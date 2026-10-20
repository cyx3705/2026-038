# 验证记录

日期：2026-10-11（Asia/Shanghai）。

环境：Windows x64，Solid Edge 2020 `220.00.00.104`，Python `3.12.10`，MCP SDK `1.30.0`，pywin32 `312`。依赖完整版本在 `requirements.lock.txt` / `requirements-dev.lock.txt`。

## 实机结果

使用 `scripts/live_smoke.py` 启动真实 stdio MCP 进程，通过 `ClientSession` 调用，未用模拟 CAD 后端。全部 23 个工具至少成功调用一次，其中 `se_disconnect` 在清理阶段验证，其余 22 个工具保留在逐步 JSON 记录中。

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

实体体积断言采用相对误差 `1e-7`，避免浮点舍入误差。原生零件和 STEP 均在 `examples/demo/`；STEP 本次验证了成功导出与文件标头，未用第二款 CAD 软件做独立导入验证。

## 自动化结果

`pytest -q`：15 项通过。`ruff check .` 和 `ruff format --check .` 通过。安装脚本、项目独立虚拟环境和 `--doctor` 已运行成功。

## 2020 版适配事实

1. 本地 `fwksupp.tlb` 明确规定：直线关键点索引 `igLineStart=0`、`igLineEnd=1`、`igLineMiddle=2`。使用另一套 1/2 关键点标志会移动错误位置，已通过实机矩形尺寸发现并修正。
2. 带类型信息的 pywin32 COM 方法对 SAFEARRAY 接受普通 tuple/list；显式套 VARIANT 会触发类型错误。`GetRange` 的输出从返回 tuple 中读取。
3. `SaveAsImage("*.png")` 在本机生成的是追加 `.tif` 的文件。服务改用临时 BMP，随后转换 PNG，并验证生成文件。
4. 新建拉伸特征的深度未自动出现于本机变量查询；因此提供基于 `ApplyDirection1Extent` 的 `se_set_extrusion_depth`。

未验证：其他 Solid Edge 版本、非默认模板下的所有坐标方向、所有错误/模态窗口情形、所有用户变量单位和公式组合。COM 无事务回滚；客户端应在写入错误后检查真实文档状态。
