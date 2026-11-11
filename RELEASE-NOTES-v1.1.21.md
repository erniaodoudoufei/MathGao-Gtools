# MathGao-Gtools v1.1.21（源码发布）

- 待办按颜色分组：红色固定第一，其他组可调序。选色新建后排在同色组首；勾选移到列表底部，不改颜色和样式。
- 直接拖动任务文字：同色移动单条，跨色移动整组，不改色或拆组。保留组内一键置顶。
- 行内 `⋯ / ▴` 点击区增至 28×21 DIP；悬停直接点 `B` 加粗，展开后第一项 `✎` 用于编辑，双击文字仍可编辑。

本次仅提供 GitHub 自动生成的源码 ZIP 与 tar.gz。OCR 原生组件的再分发授权尚未确认，不上传 Windows 运行包。构建使用稳定版 .NET 8 SDK 与 C# 12，运行需要 .NET 8 Desktop Runtime（x64）。

本地 276 项测试通过，Release 构建零警告、零错误，NuGet 已知漏洞为零，隔离启动通过。真实应用复制及微信长截图仍待验收，见 [验收摘要](https://github.com/erniaodoudoufei/MathGao-Gtools/blob/v1.1.21/ACCEPTANCE-STATUS.md)。
