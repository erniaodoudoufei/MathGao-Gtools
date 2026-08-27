# Security policy

## Supported versions

安全修复只面向当前最新的 `1.1.x` Release。旧版本可能不再接收修复；报告问题前请先确认最新版本是否仍可复现。

## Reporting a vulnerability

请不要在公开 Issue、讨论区、截图、验收报告或日志中披露未修复漏洞、API Key、剪贴板内容、词典/翻译请求正文、用户名或本机路径。

请通过 GitHub 仓库的 **Security → Report a vulnerability** 私下提交报告：

https://github.com/erniaodoudoufei/MathGao-Gtools/security/advisories/new

报告尽量包含：受影响版本、Windows/.NET 版本、最小复现步骤、预期与实际结果、影响判断，以及已脱敏的必要日志片段。不要上传真实剪贴板数据库、用户配置、OCR 截图或完整诊断目录。

维护者会尽力在 7 天内确认收到，并在复现和评估后给出修复计划。修复发布前请保持细节私密；如需公开致谢，会先征得报告者同意。

## Security boundaries

- OCR 默认在本机 Worker 中运行；翻译和自动查词属于可选网络功能，隐私边界见 README。
- OpenAI 兼容接口 Key 使用当前 Windows 用户的 DPAPI 加密，但有权以同一 Windows 用户身份运行的进程仍可能访问该用户可访问的数据。
- 本项目尚未进行代码签名。Windows 二进制附件当前因第三方再分发授权未确认而暂缓公开；未来附件可用时，只从本仓库 GitHub Release 下载，并在运行前核对随附 SHA-256。
- 第三方二进制和模型受其上游许可证及安全维护周期约束，清单见 `ThirdPartyLicenses/manifest.json`。
