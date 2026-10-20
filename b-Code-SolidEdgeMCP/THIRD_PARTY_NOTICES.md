# 参考来源与许可

本项目是独立的 Python 实现，重点适配本机 Solid Edge 2020；并非上游项目的完整复制版。

## Python COM 与 MCP 参考

- 项目：<https://github.com/tylerwagler/SolidEdge-MCP>
- 下载参考的提交：`49384d030c087c643ca3332eb2134bffe393e84c`
- 许可：MIT，原始声明见 `third_party/SolidEdge-MCP-LICENSE.txt`。
- 参考内容：STA 线程隔离、COM 接口签名、枚举常量、变量查询、STEP SaveCopyAs，以及通过真实几何确认建模结果的方法。
- 本地实测修正：直线约束使用 `KeypointIndexConstants` 的 `igLineStart=0` / `igLineEnd=1`；SAFEARRAY 使用 pywin32 类型信息自动封送；Solid Edge 2020 截图采用原生 BMP 再转换 PNG。

另检索并下载了 <https://github.com/1337332551-dot/solidedge-mcp-server>，提交 `2ed941e8828ffe626699f2750ca57ed66a7f43bb`。它的 .NET 服务设计用于比较工具边界，未将其代码纳入本项目。

## 接入文档

Codex MCP 配置字段根据官方文档核对（2026-10-11）：
<https://developers.openai.com/codex/mcp>

Solid Edge 为 Siemens 的产品。本服务通过本机已安装且已授权的 Solid Edge 工作，不包含 CAD 软件或许可证。
