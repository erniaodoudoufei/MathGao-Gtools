# MathGao-Gtools v1.1.13

待办浮窗可以撤销误删：删除单条或清除已完成之后，标题栏右上角“清除已完成”按钮左边会出现撤销按钮 ↶，点一下就把刚删掉的条目放回去。

- 恢复的条目回到原来的位置，勾选状态和时间都保持不变。
- 可以连续点，按相反顺序逐步撤回，最多保留最近 20 步；一次“清除已完成”算一步。
- 鼠标悬停在按钮上会提示下一步撤销什么，例如“撤销：删除「向量」”。
- 没有可撤销的操作时按钮自动隐藏；操作后标题栏会短暂提示“已删除，可撤销”。
- 只有按钮，不占用 Ctrl+Z，避免和其他软件的快捷键冲突。
- 撤销历史只在本次运行期间保留，退出程序后清空。

Release 构建零警告、零错误；180 项自动测试通过，其中新增 9 项撤销测试；NuGet 直接及传递依赖已知漏洞为零。2026-10-02 的桌面验收记录还包含 UI 自动化驱动真实悬浮窗按钮完成删除、清除、连续撤销的流程，每一步核对数据库。测试全部使用隔离合成数据，不读取正式待办。

本版正式发布源码，GitHub 页面下方提供自动生成的 Source code ZIP/tar.gz。从源码构建需要 Windows x64、.NET 8 SDK 和 PowerShell 7：

```powershell
pwsh -File .\build.ps1 -Configuration Release
```

本地完整运行目录入口仍为 `Gtools.exe`，需 .NET 8 Desktop Runtime（x64）；不要只复制单个 EXE。

详见 [待办撤销验收记录](https://github.com/erniaodoudoufei/MathGao-Gtools/blob/v1.1.13/TODO-UNDO-ACCEPTANCE-v1.1.13.md)。浏览器/聊天软件实际复制验收与长截图真实微信验收仍未完成。

现有 `PaddleOCRSharp 6.2.0` 携带的第三方 `PaddleOCR.dll` 公开再分发授权仍未确认，因此本版继续只发布源码，不上传 Windows 运行包 ZIP。源码下载包不包含离线 OCR 模型、原生运行库或本机用户数据。
