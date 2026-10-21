# 验证记录

日期：2026-10-21（Asia/Shanghai），0.5.0（草图升级：关系、驱动尺寸、简写与拐角、椭圆/样条/点/构造线、基准与引用、建面、旋转、尺寸修改与草图编辑）实测。外壳仍是 0.4.0：本次只换了指令程序集，运行中 `se_load` 热载入，未重新发布外壳、未重连。0.2.0（C# 首版）与 Python 版（0.1.0，2026-10-11）的同一组检查数值与本次逐项一致。

环境：Windows x64，Solid Edge 2020 `220.00.00.104`，.NET `8.0.31`，MCP C# SDK `ModelContextProtocol.Core 1.4.0`。依赖版本写在各 `.csproj`。

## 实机结果

使用 `scripts/LiveSmoke` 启动真实 stdio MCP 进程，通过官方 `McpClient` 调用，未用模拟 CAD 后端。客户端看到 4 个工具；全部 30 条内部指令经 `se_call` 至少成功调用一次，其中 `disconnect` 在清理阶段验证，其余 29 条保留在逐步 JSON 记录中。

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
| 草图读回 | 80×20 长圆形（半圆端）+ φ8 孔 | 读回偏差 0，净面积 1706.81415 mm²（= 1600 + 50π − 16π） |
| 草图拉伸 | 同一草图拉伸 10 mm | 体积 17068.141502 mm³ = 净面积 × 10，孔随整组轮廓拉伸 |
| 草图拒绝 | 自交（蝴蝶结）描述 | 返回错误，零件中不留草图 |
| 草图查看 | 拉伸后按序号 `view_sketch`，`png=true` | 结构与 SVG 一致，PNG 目视正确 |
| 完全约束草图 | 圆角矩形 + 两孔，相对 `@x_axis`/`@y_axis` 定位，8 个尺寸（5 个命名） | `fully_constrained=true`，读回偏差 0，8 个尺寸的 Solid Edge 值与几何量一致 |
| 尺寸驱动 | `W` 60→80、`H` 40→30、`Pitch` 40→50、`Rf` 5→3 | 左下角仍在 (−30, −20)，全部尺寸与关系成立 |
| 草图编辑 | 删孔、加长圆、关于竖线镜像孔，重画 | `profile_id` 与变量名不变，丢弃 3 条失效的关系/尺寸 |
| 编辑后拉伸 | 10 mm | 体积 = 净面积 × 10（空零件上的首个特征按精确值核对） |
| 面上建面 | 顶面偏移 0 建面，画凸台、引用实体边 | 平面原点 (10, −5, 10)；`@y_axis` 读到局部 x = −10；凸台体积 = π·4²·5 |
| 引用 | 投影孔边作同心参照；包含另一孔边成为轮廓 | 关系成立；包含的边作为孔参与净面积 |
| 旋转 | L 形截面带 R3 内圆角，绕草图线 360° | 22574.01138621982 mm³ = 2π × 面积 × 形心半径 |
| 曲线草图 | 六边形 + 30° 斜椭圆孔 + 闭合样条 + 点 + 构造线；对称、`@origin` 重合、与基准共线、两向夹角 | 读回偏差 0，关系与尺寸全部成立 |
| 程序集热换 | `se_unload` 卸载整个指令程序集后 `se_load` 重新载入 | 30 条指令恢复，原文档 ID 查得体积一致 |

实体体积断言采用相对误差 `1e-7`，避免浮点舍入误差。原生零件和 STEP 均在 `examples/demo/`；STEP 本次验证了成功导出与文件标头，未用第二款 CAD 软件做独立导入验证。

## 自动化结果

`dotnet build`（告警即错误）零告警，`dotnet test`：33 项通过，其中包括：被替换的指令程序集所在加载上下文能被 GC 回收；载入中的 DLL 文件可被覆盖（不锁文件）。`scripts/setup.ps1 -Dev`（发布外壳到 `obj/app`、构建指令到 `obj/commands`、生成客户端配置）和 `--doctor` 已运行成功。项目合同 `-Instantiation`：PASS。

## 2020 版适配事实

1. 本地 `fwksupp.tlb` 明确规定：直线关键点索引 `igLineStart=0`、`igLineEnd=1`、`igLineMiddle=2`。使用另一套 1/2 关键点标志会移动错误位置，已通过实机矩形尺寸发现并修正。
2. 签名以 `Part.tlb` / `geometry.tlb` 为准，C# `dynamic` 按类型信息封送：
   - `ProfileArray` 是 `SAFEARRAY(IDispatch*)*`，必须以 `ref` 传 `DispatchWrapper[]`；`object[]` 会被拒（“Could not convert argument 2”）。
   - `Body.GetRange` 的两个 `SAFEARRAY(double)*` 以 `ref Array`（`double[3]`）传入并原地回填。
   - `ApplyDirection1Extent` 的 `KeyPointFlags` 声明为 `KeyPointExtentConstants*`，`dynamic` 绑定器无法转换该签名；改用 `Type.InvokeMember` 直接走 IDispatch，第 5 参按引用、关键点传 `DispatchWrapper(null)`。
3. `SaveAsImage("*.png")` 在本机生成的是追加 `.tif` 的文件。服务改用临时 BMP，随后转换 PNG，并验证生成文件。
4. 新建拉伸特征的深度未自动出现于本机变量查询；因此提供基于 `ApplyDirection1Extent` 的 `set_extrusion_depth`。
5. `Profile.End(igProfileClosed | igProfileNoSelfIntersect | igProfileAllowNested)` 会把嵌套的闭环（孔）拆到同一 `ProfileSet` 的独立 `Profile` 中；读回要遍历整组，拉伸要把整组轮廓一起传入，否则孔被漏掉。
6. 草图元素签名（`fwksupp.tlb`）：圆弧关键点 `igArcStart=1`、`igArcEnd=2`（直线 0/1）；`GetStartPoint/GetEndPoint/GetCenterPoint` 以 `out double` 读回可用；`Name` 属性可直接读取（如 `Line2d 318`）。圆弧用 `AddByStartAlongEnd` 三点创建，避免依赖方向约定。

7. 草图尺寸（`Dimensions.Add*BetweenObjects`）的 keyPoint 参数是开关，不是 `KeypointIndexConstants`：非零时吸附到拾取点最近的关键点，0 时按元素本身。按"索引"传会测错点：两线夹角得到 19.1° 而非 120°；到基准投影线的"整线"距离吸附到投影线中点，而投影线长度随草图范围变化，改尺寸后尺寸值不变、几何却漂移。现在关键点一律传 1 和精确坐标；整条线取起点关键点，沿与它垂直的 x 或 y 方向测（`Dimensions.AxisMode=3` + `Axis` 设为基准投影线）；斜的整线不支持，描述检查时就报错。
8. 两线夹角（`AddAngleBetweenObjects`）按两条线起点到终点的方向测，拾取点不影响；`AngleCompliment` 只在 x 与 360−x 之间切换，补角（180−x）无法得到。描述里的角度采用同一定义（0–180°），量出 360−x 时自动切换 `AngleCompliment`。
9. `Variables.PutName` 起一个已存在的名字时，Solid Edge 在调用中弹框，表现为 `RPC_E_CANTCALLOUT_INEXTERNALCALL`。画之前检查重名；`edit_sketch` 先把旧草图的变量改成临时名，新草图核对通过后才删旧草图，失败时改回。
10. 轮廓 `End()` 之后：仍能加元素并再次 `End()`；改尺寸值后必须 `doc.Recompute()` 几何才跟着动；`Offset2d` 静默不生效。所以编辑一律在描述上算好再整张重画。
11. `ProjectRefPlane`、`ProjectEdge` 的结果不进入 `Lines2d` 等集合，可作关系和尺寸的参照；`IncludeEdge` 会在集合里新增一个真实元素。从实体面建的平面，原点在面上某点（实测 (10, −5, 10)），不是全局原点的投影。
12. `Point2d` 的 `IsConstructionElement` 恒为真；`ProfileSet.IsUnderDefined` 给出是否完全约束；`BSplineCurve2d.GetNode` 读回经过的点，`Ellipse2d.GetMajorAxis` 返回半长轴向量。
13. `Models.AddFiniteRevolvedProtrusion(n, ref profiles, refAxis, side, angle)` 中的 `refAxis` 是 `Profile.SetAxisOfRevolution(line)` 的返回值；轴线留在 `Lines2d` 里、不是构造线。

未验证：其他 Solid Edge 版本、非默认模板下的所有坐标方向、所有错误/模态窗口情形、所有用户变量单位和公式组合。COM 无事务回滚；客户端应在写入错误后检查真实文档状态。
