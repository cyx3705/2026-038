# SolidEdgeMCP

OneHistory 项目 `2026-038`：通过 C#（.NET 8）MCP 服务控制 Siemens Solid Edge。

项目入口：

- [`project.manifest.json`](./project.manifest.json)：项目身份、活动目录、依赖和验证命令。
- [`AGENTS.md`](./AGENTS.md)：AI 工作合同和修改边界。
- [`b-Office/现行约定.md`](./b-Office/现行约定.md)：本项目长期约定。
- [`b-Code-SolidEdgeMCP/README.md`](./b-Code-SolidEdgeMCP/README.md)：安装、MCP 接入、工具和实机验证说明。

业务源码集中在 `b-Code-SolidEdgeMCP`。MCP 接口固定为查询、调用、载入、卸载 4 个工具，具体操作是可在运行中热换、逐条开关的内部指令。当前支持有序单实体 `.par` 零件：连接、创建草图、拉伸、切除、参数修改、保存、STEP 导出和视图截图。当前实机验证环境是 Solid Edge 2020、.NET 8、Windows x64。

## 常用命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code-SolidEdgeMCP\scripts\setup.ps1 -Dev
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-SolidEdgeMCP.ps1
```

实机测试需要本机安装并授权 Solid Edge：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-SolidEdgeMCP.ps1 -Live
```

根目录遵循 OneHistory 的 `a-*`、`b-*`、`z-*` 分级；本项目源码组件使用 `b-Code-SolidEdgeMCP`。

## 保留内容

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
