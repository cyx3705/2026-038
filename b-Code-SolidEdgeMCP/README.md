# Solid Edge AI MCP

用自然语言让 AI 操作 Siemens Solid Edge 的 C# MCP 服务。第一版专注有序建模零件（`.par`），采用官方 MCP C# SDK（`ModelContextProtocol.Core`）和 COM 后期绑定，所有 COM 对象均留在独立 STA 线程。

MCP 接口固定为 4 个外壳工具；具体操作是内部指令，编译在单独的指令程序集里，服务运行中可以热换、逐条开关，客户端不用重连。

**4 个 MCP 工具 · 25 条内部指令 · 运行时热换 · stdio MCP · 毫米输入 · Windows · .NET 8 · MIT**

已在 **Solid Edge 2020 / 220.00.00.104、.NET 8.0.31 x64** 上通过真实 MCP 客户端完成建模、改厚度、切孔、截图、保存、重新打开和 STEP 导出。其他 Solid Edge 版本尚未验证。

![实机生成的带孔板](examples/demo/plate-with-hole.png)

## 快速使用

要求：Windows、已安装并授权的 Solid Edge、.NET 8 SDK（只运行时需 .NET 8 桌面运行时）。

在项目目录打开 PowerShell：

```powershell
.\scripts\setup.ps1
```

脚本把外壳发布到 `obj/app/SolidEdgeMcp.exe`，把指令程序集构建到 `obj/commands/SolidEdgeMcp.Commands.dll`，生成实际路径的客户端配置，并检查 Solid Edge 的 COM 注册。项目搬家后重新运行脚本生成路径。客户端正在运行服务时外壳 exe 被占用，重新发布外壳需先停掉客户端里的 MCP 服务；只改指令则不需要（见下文“运行时修改指令”）。

如 PowerShell 的执行策略阻止本地脚本，可仅对当前脚本进程运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1
```

### 接入 AI 客户端

- **Codex**：将 `config/codex.toml` 的配置段合并到自己的 `config.toml`，重新加载 MCP 服务。
- **Claude Desktop / Cursor / 其他支持 stdio 的客户端**：使用 `config/mcp.json` 中 `mcpServers.solid-edge` 的配置。
- 配置文件已经填写 `SolidEdgeMcp.exe` 的绝对路径；不要把 JSON 文件整体粘贴进 TOML。
- 服务进程由客户端启动。无需先打开常驻终端，也无需 API Key。

Codex 的工具超时配置为 150 秒，长于服务内部的 120 秒 COM 超时。字段依据 [Codex 官方 MCP 文档](https://developers.openai.com/codex/mcp) 核对。

可给 AI 的第一条指令：

> 用 se_find 看一下有哪些指令，然后连接 Solid Edge，新建一个零件，生成 100×60×20 毫米的长方体；在中心切一个直径 12 毫米的通孔，显示等轴测视图，保存为 plate.par 并导出 plate.step。检查模型体积后把预览图给我。

检查环境或手动启动：

```powershell
.\obj\app\SolidEdgeMcp.exe --doctor
.\scripts\start.ps1
```

`start.ps1` 等待客户端的 JSON-RPC 输入，终端没有普通输出是正常的。日志走 stderr，stdout 只用于 MCP 协议。

## MCP 接口

客户端只看到 4 个工具，列表永不变化：

| 工具 | 作用 |
| --- | --- |
| `se_find` | 不带参数：列出全部内部指令（名称、类别 read/create/write、开关状态、说明）和程序集版本。`command=<名>`：返回该指令的参数 JSON Schema |
| `se_call` | `command=<名>`、`arguments={...}`：执行一条已开启的指令。参数按该指令的 Schema 校验，未知参数名直接报错 |
| `se_load` | 不带参数：从磁盘重新载入指令程序集（热换）。`command=<名>`：打开该指令的开关 |
| `se_unload` | `command=<名>`：关闭该指令的开关。不带参数：卸载整个指令程序集 |

还提供 `solidedge://guide` 资源，供客户端读取操作约定。

## 内部指令

| 类别 | 指令 |
| --- | --- |
| 连接 | `status`、`connect`、`disconnect` |
| 文档 | `list_documents`、`get_document`、`new_part`、`open_document`、`close_document` |
| 草图 | `sketch`、`view_sketch`、`reference_planes`、`create_rectangle`、`create_circle` |
| 实体 | `create_box`、`create_cylinder`、`extrude` |
| 查询和编辑 | `geometry`、`features`、`list_variables`、`set_variable`、`set_extrusion_depth` |
| 文件 | `save_document`、`export_step` |
| 视图 | `set_view`、`capture_viewport` |

指令可以调用其他指令（如 `create_box` = `create_rectangle` + `extrude`）。关掉的指令在嵌套调用里同样不可用。

### 草图：`sketch` 与 `view_sketch`

`sketch` 的输入是声明式的元素列表（平面局部坐标，mm），完整格式由 `se_find command=sketch` 给出：

```json
{"document_id": "…", "plane_index": 1, "entities": [
  {"id": "L1", "type": "line",   "start": [0, 0],  "end": [80, 0]},
  {"id": "A1", "type": "arc",    "center": [80, 10], "start": [80, 0], "end": [80, 20]},
  {"id": "L2", "type": "line",   "start": [80, 20], "end": [0, 20]},
  {"id": "L3", "type": "line",   "start": [0, 20],  "end": [0, 0]},
  {"id": "H1", "type": "circle", "center": [40, 10], "radius": 4}
]}
```

- 圆弧两种写法：`center+start+end`（默认逆时针，`clockwise=true` 反向）或 `center+radius+start_angle+end_angle`（度）。
- 重合的端点自动加关键点重合约束；所有链必须闭合，内部闭环自动成为孔。
- 先在本地检查描述：开口端点、分叉、交叉、重叠。有缺陷时什么都不画，错误里带上问题清单和描述的 SVG。
- 画完从 Solid Edge 逐个读回，与描述比对（坐标偏差、净面积），不一致就删除草图并报错。
- 返回：读回的元素（含 Solid Edge 名称）、各环的内外角色/方向/面积/形心、净面积（拉伸体积 = 净面积 × 深度）、包围盒、问题清单，以及按读回几何生成的 **SVG**（y 向上，单引号属性，可直接在 JSON 里阅读）。默认不出 PNG，单步调用保持轻量。
- 返回的 `profile_id` 可直接交给 `extrude`。Solid Edge 会把孔拆成同一草图集里的独立轮廓，`extrude` 会把整组轮廓一起拉伸。

`view_sketch` 查看已有草图：不带参数列出零件的全部草图（序号、元素数、闭环数、净面积、问题数）；`sketch_index` 或 `profile_id` 返回与 `sketch` 相同的结构和 SVG；`png=true` 时另附 PNG 图片。拉伸用掉的草图也能按序号查看。

### 运行时修改指令

指令源码在 `src/SolidEdgeMcp.Commands/`，按类别分文件，每条指令是一个继承 `SeCommand` 的类。服务运行中：

1. 修改或新增指令类。
2. `dotnet build .\src\SolidEdgeMcp.Commands`（输出到 `obj/commands/`）。
3. 让 AI 调用 `se_load`。返回新增和移除的指令名；文件内容没变时返回 `unchanged`。

外壳把程序集读进内存后载入可回收的加载上下文，不锁文件，所以服务运行时可以随时重新构建。载入失败（例如构建残缺）时保留正在运行的旧版本，错误显示在 `se_find` 的 `last_load_error`。Solid Edge 连接、文档 ID 和未用的轮廓都保存在外壳里，热换后仍然有效。

新指令要先不对 AI 开放，可在类里重写 `EnabledByDefault => false`，再用 `se_load command=<名>` 打开。

### 开关

每条指令的开关默认取 `EnabledByDefault`（现有 23 条都为开）。`se_load`/`se_unload` 带指令名切换的状态写入 `config/commands.json`（本机生成，不入库），服务重启后保持。可用 `--switches <文件>` 或环境变量 `SOLIDEDGE_MCP_SWITCHES` 指定别的位置；指令程序集位置可用 `--commands <dll>` 或 `SOLIDEDGE_MCP_COMMANDS` 指定。

### 外壳与指令的分界

外壳只认一个很小的契约（`src/SolidEdgeMcp/Commands.cs`）：每条指令给出名称、说明、类别、参数的 JSON Schema，以及 `Execute(原始 JSON 参数)`。参数规格与校验（`ToolParameter`，含数组/对象参数 `Structured`）、`SeCommand` 基类、文档/轮廓等内核操作（`EdgeOps`）和公用小函数都在指令工程的 `Kit/` 里，随指令一起热换。

外壳只保留不会轻易变的部分：MCP 收发与 4 个工具、COM 单线程、热换与开关、跨热换的共享状态（COM 连接、文档 ID、轮廓，以及留给新指令的通用 `State` 字典）。需要重新发布外壳并重连的，只剩改动这些部分本身。

### 参数约定

- 建模长度使用 **mm**，体积返回 **mm³**。角度变量使用 **deg**。其他变量的 `raw` 值采用 COM 原生单位，并返回 `units_type`，不隐式转换。
- 下文的名称都是内部指令，经 `se_call` 调用。
- 文档操作必须传 `document_id`。ID 由新建、打开或列举指令返回，断开连接、关闭文档或服务重启后失效；指令热换不影响 ID。切换活动文档不会改变 ID 所指向的零件。
- 草图坐标是指定基准面内的局部坐标。基准面索引从 **1** 开始；调用 `reference_planes` 查询实际名称。默认模板的第 1 面在本机是 XY 面。
- `create_box` 的宽、深沿平面局部轴，高沿平面法向；`create_cylinder` 同样从平面原点沿法向拉伸。两者只用于空零件。
- 在已有实体上加料或切除：先建矩形/圆轮廓，再 `extrude(operation="add" / "cut")`；轮廓使用后失效。第一版只支持有序、单实体零件。
- 修改拉伸厚度使用 `set_extrusion_depth`，`feature_name` 来自 `features`。2020 版不一定将拉伸深度自动列入变量表。
- `set_variable` 要求名字和单位与查询结果一致。写入会改变原有变量驱动值；有关联公式的零件应先查看返回的 `formula`。
- `set_view` 支持 `Iso`、`Front`、`Top`、`Right`。截图指令会保存 PNG，并将 MCP 图像内容直接返回给 AI。

## 输出与错误行为

客户端配置默认把 CAD 文件写入项目内 `data/`。也可使用 `--output-dir` 或环境变量 `SOLIDEDGE_MCP_OUTPUT_DIR` 指定根目录；命令行参数优先。

- 打开文件需提供已有 `.par` 的绝对路径；保存、导出、截图必须位于输出根目录内。
- 已有文件（包括原位保存）必须传 `overwrite=true`。外部打开的零件可另存到输出目录，不能直接覆盖目录外的原件。
- 关闭含未保存修改的文档会失败，除非已保存或明确传 `discard_changes=true`。
- `disconnect` 和 MCP 退出只释放连接，不退出 Solid Edge，不自动保存或关闭用户文档。
- 建模不只检查 COM 是否返回：拉伸和切除后读取实体体积，确认加料/减料确实发生；深度和变量修改后读取实际值。保存/导出检查文件非空，STEP 导出保留原生文件路径。
- COM 操作不具备事务回滚。操作报错可能已留下草图或部分特征；超时后操作也可能继续完成。先检查模型，不能自动重复写入。工作线程忙时拒绝新请求，避免任务堆积。
- `features` 返回 Solid Edge 原始状态码：`1216476310` 为 OK，`1216476311` 为失败，`1216476312` 为警告。修改参数后应检查下游特征。
- 本服务使用本机用户权限，只执行指令程序集里编译好的指令；未开放任意脚本、Shell 或任意成员执行入口，也未开放 HTTP 监听端口。注意：`se_load` 会载入配置路径上的 DLL 并执行其中的指令，该 DLL 与外壳同等可信，别把 `--commands` 指向来历不明的文件。

## 验证

```powershell
dotnet build .\SolidEdgeMcp.sln
dotnet test .\SolidEdgeMcp.sln --no-build
```

构建开启告警即错误（`Directory.Build.props`）。26 项自动化测试覆盖：草图解析与两种圆弧写法、开口/分叉/交叉/重叠检测、带圆弧和孔的精确面积与形心、SVG/PNG 生成、真实 stdio 握手与固定 4 工具、`se_find` 指令列表与 Schema、非法参数/未知参数/未知指令和错误响应、开关切换与持久化（含嵌套调用、默认关闭的指令）、程序集热换不锁文件、残缺构建保留旧版本、被替换的程序集可回收、输出路径和覆盖保护、文档 ID 失效、跨文档轮廓拒绝、未保存文档保护、COM 单线程（STA）生命周期、超时不重试写入、非有限数值不出线程、线程退出清理等。

有 Solid Edge 的 Windows 机器可运行实机测试：

```powershell
dotnet run --project .\scripts\LiveSmoke -- --keep-open
```

它创建独立测试零件和带时间戳的输出目录；只关闭本次创建的文档。草图部分画一个带孔长圆形，核对读回零偏差、净面积、拉伸体积 = 净面积 × 深度，并确认自交草图被拒且不留残余；草图的 SVG/PNG 存在输出目录。中途在文档打开的状态下关、开一条指令，再卸载并重新载入整个指令程序集，确认原文档 ID 仍可用；开关文件写在本次输出目录，不动 `config/`。`--keep-open` 在成功后保留演示板。没有该选项时测试结束会关闭自己的文档。

实机记录见 [VALIDATION.md](VALIDATION.md)，完整调用结果见 [verification.json](examples/demo/verification.json)。本次检查通过 4 个外壳工具和全部 25 条指令的调用，不代表全部参数组合或全部 Solid Edge 版本均已测试。

## 项目结构

```text
SolidEdgeMcp.sln
src/SolidEdgeMcp/             外壳（发布为 obj/app/SolidEdgeMcp.exe）
  SolidEdgeServer.cs    固定的 4 个 MCP 工具、资源、低层处理器与错误响应
  CommandRegistry.cs    指令程序集的热换、开关持久化与嵌套调用
  Commands.cs           外壳与指令的最小契约：ISeCommand、CommandContext、CommandOutput
  SolidEdgeBackend.cs   跨热换保留的共享状态（COM 连接、文档 ID、轮廓、State）
  ComWorker.cs          STA 线程、超时与退出处理
  ComInterop.cs         GetActiveObject、COM 身份比较、IDispatch 直调
  OutputPaths.cs        输出目录、覆盖保护和文件验证
  Program.cs            stdio 启动、默认路径与环境诊断（--doctor）
src/SolidEdgeMcp.Commands/    内部指令（构建到 obj/commands/，运行时热换）
  Kit/                  SeCommand 基类、参数规格与校验（ToolParameter）、内核操作（EdgeOps）、公用函数
  Connection.cs Documents.cs Sketch.cs Solids.cs Query.cs Files.cs View.cs
  Sketching/            草图描述解析、成环与面积形心、交叉检测、SVG/PNG、COM 绘制与读回
tests/SolidEdgeMcp.Tests/     不需要启动 Solid Edge 的回归测试（xunit）
scripts/              安装、启动脚本；LiveSmoke/ 为实机冒烟客户端
config/               当前机器的客户端配置与指令开关（生成，不入库）
examples/demo/        实际生成的零件、STEP、PNG 和验证记录
```

当前不包含装配约束、工程图、钣金、倒角/圆角、放样/扫掠或同步建模工具。后续在 `src/SolidEdgeMcp.Commands/` 增加指令类（明确参数、写入后回读确认），构建后 `se_load` 即可使用，并补相应实机验证。

参考项目、提交号和许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
