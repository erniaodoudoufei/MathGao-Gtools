# MathGao-Gtools v1.1.18（源码发布）

- 待办工具栏展开后，原位置显示 ▴；加粗、倾斜或选色后仍能点击它手动收起。样式保存不再重建整张列表，位置和样式保留。
- 修复待办排序测试的 CI 编译报错，并确保构建使用 .NET 8 / C# 12。
- 保留原地编辑、排序、删除撤销和整块待办折叠，不修改数据库或设置格式。
- 自 v1.1.13 以来也增加了待办一键置顶、文字样式、标题栏农历与英文星期，以及原地编辑。

## 下载与验收

本次 Release 只提供 GitHub 自动生成的源码 ZIP 与 tar.gz。由于所用 OCR 原生组件的再分发授权尚未确认，不上传 Windows 运行包。源码构建使用稳定版 .NET 8 SDK 与 C# 12，运行需要 .NET 8 Desktop Runtime（x64）。

本地 261 项测试通过，Release 构建零警告、零错误，NuGet 已知漏洞为零，隔离启动与实际鼠标交互测试通过。真实浏览器复制及微信长截图仍待验收，见 [验收记录](https://github.com/erniaodoudoufei/MathGao-Gtools/blob/v1.1.18/TODO-TOOLS-ACCEPTANCE-v1.1.18.md)。
