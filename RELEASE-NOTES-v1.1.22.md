# MathGao-Gtools v1.1.22（源码发布）

- 截图翻译先把折行还原成整句再翻译：`He tried to get him to leave` / `him alone` 不再被逐行译成“他试图让他离开 / 他一个人”，而是“他试图让他别打扰他”。菜单、列表等独立短行保持逐行；窗口里的“按原始换行”可一键退回。
- Google 通道在 `translate.google.com` 与 `translate.googleapis.com` 之间自动切换，一个入口被限流（HTTP 429）时改用另一个；长文本改用 POST，不再因网址超长得到 HTTP 400。
- 可填写自己的 AI 接口（OpenAI 兼容，内置 DeepSeek 预设和连接测试）作为默认通道，或在 Google 失败时自动备用。自动备用默认开启、可关闭，从不自动使用有道；走备用通道时窗口会注明。
- 翻译窗口修复：过期结果不再覆盖新结果，译文为空时交换语言不再清空原文，剪贴板被占用时不再抛出异常。
- 新增全局异常兜底与截图热键重入保护：单个操作出错不再让整个托盘程序退出。

本次仅提供 GitHub 自动生成的源码 ZIP 与 tar.gz。OCR 原生组件的再分发授权尚未确认，不上传 Windows 运行包。构建使用稳定版 .NET 8 SDK 与 C# 12，运行需要 .NET 8 Desktop Runtime（x64）。

本地 313 项测试通过，Release 构建零警告、零错误，NuGet 已知漏洞为零，隔离启动通过。截图翻译的真实热键流程、AI 接口的真实 Key 调用、真实应用复制及微信长截图仍待验收，见 [验收摘要](https://github.com/erniaodoudoufei/MathGao-Gtools/blob/v1.1.22/ACCEPTANCE-STATUS.md)。
