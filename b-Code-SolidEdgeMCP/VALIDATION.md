# 验证记录

日期：2026-10-21（Asia/Shanghai），0.3.0（固定 4 个 MCP 工具 + 可热换的内部指令程序集）实测。0.2.0（C# 首版）与 Python 版（0.1.0，2026-10-11）的同一组检查数值与本次逐项一致。

环境：Windows x64，Solid Edge 2020 `220.00.00.104`，.NET `8.0.31`，MCP C# SDK `ModelContextProtocol.Core 1.4.0`。依赖版本写在各 `.csproj`。

## 实机结果

使用 `scripts/LiveSmoke` 启动真实 stdio MCP 进程，通过官方 `McpClient` 调用，未用模拟 CAD 后端。客户端看到 4 个工具；全部 23 条内部指令经 `se_call` 至少成功调用一次，其中 `disconnect` 在清理阶段验证，其余 22 条保留在逐步 JSON 记录中。

| 检查 | 预期 | 实测 |
| --- | --- | --- |
| 方块尺寸 | 100 × 60 × 20 mm | 一致 |
| 方块体积 | 120000 mm³ | 119999.99999999999 mm³ |
| 拉伸深度修改 | 20 → 25 mm | 回读 25 mm，体积 150000 mm³ |
| 中心通孔 | 半径 6 mm，深 25 mm | 7 个面，体积 147172.5666117692 mm³ |
| 圆柱体积 | π × 10² × 30 mm³ | 9424.77796076938 mm³ |
| 圆柱上矩形加料 | 原体积 + 5 × 5 × 10 mm³ | 9674.77796076938 mm³ |
| 文档准确定位 | 两个文档切换后仍查询原板 | 体积一致 |
| 保存再打开 | 原生 `.par` 能重新打开并保持几何 | 一致 |
| STEP | 文件非空，具有 ISO-10303-21 标头 | 通过 |
| 覆盖保护 | 未声明覆盖时拒绝第二次导出 | 通过 |
| 视图 | Iso / Front / Top / Right | 全部调用通过 |
| 预览 | PNG，MCP 返回 image 类型内容 | 已生成并目视检查 |
| 指令开关 | 文档打开时关掉 `geometry` 后调用被拒，再打开恢复 | 通过 |
| 程序集热换 | `se_unload` 卸载整个指令程序集后 `se_load` 重新载入 | 23 条指令恢复，原文档 ID 查得体积一致 |

实体体积断言采用相对误差 `1e-7`，避免浮点舍入误差。原生零件和 STEP 均在 `examples/demo/`；STEP 本次验证了成功导出与文件标头，未用第二款 CAD 软件做独立导入验证。

## 自动化结果

`dotnet build`（告警即错误）零告警，`dotnet test`：21 项通过，其中包括：被替换的指令程序集所在加载上下文能被 GC 回收；载入中的 DLL 文件可被覆盖（不锁文件）。`scripts/setup.ps1 -Dev`（发布外壳到 `obj/app`、构建指令到 `obj/commands`、生成客户端配置）和 `--doctor` 已运行成功。项目合同 `-Instantiation`：PASS。

## 2020 版适配事实

1. 本地 `fwksupp.tlb` 明确规定：直线关键点索引 `igLineStart=0`、`igLineEnd=1`、`igLineMiddle=2`。使用另一套 1/2 关键点标志会移动错误位置，已通过实机矩形尺寸发现并修正。
2. 签名以 `Part.tlb` / `geometry.tlb` 为准，C# `dynamic` 按类型信息封送：
   - `ProfileArray` 是 `SAFEARRAY(IDispatch*)*`，必须以 `ref` 传 `DispatchWrapper[]`；`object[]` 会被拒（“Could not convert argument 2”）。
   - `Body.GetRange` 的两个 `SAFEARRAY(double)*` 以 `ref Array`（`double[3]`）传入并原地回填。
   - `ApplyDirection1Extent` 的 `KeyPointFlags` 声明为 `KeyPointExtentConstants*`，`dynamic` 绑定器无法转换该签名；改用 `Type.InvokeMember` 直接走 IDispatch，第 5 参按引用、关键点传 `DispatchWrapper(null)`。
3. `SaveAsImage("*.png")` 在本机生成的是追加 `.tif` 的文件。服务改用临时 BMP，随后转换 PNG，并验证生成文件。
4. 新建拉伸特征的深度未自动出现于本机变量查询；因此提供基于 `ApplyDirection1Extent` 的 `se_set_extrusion_depth`。

未验证：其他 Solid Edge 版本、非默认模板下的所有坐标方向、所有错误/模态窗口情形、所有用户变量单位和公式组合。COM 无事务回滚；客户端应在写入错误后检查真实文档状态。
