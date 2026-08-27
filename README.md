# MathGao-Gtools

MathGao-Gtools 是一个面向 Windows 的开源桌面效率工具。仓库和发布包使用 **MathGao-Gtools** 名称；为保持升级兼容，程序入口仍叫 `Gtools.exe`，本机数据目录仍为 `%LOCALAPPDATA%\Gtools`。

源码采用 [MIT](LICENSE) 许可证。第三方组件按各自许可证分发，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [ThirdPartyLicenses/manifest.json](ThirdPartyLicenses/manifest.json)。

## 二进制发布状态

运行环境：Windows x64 和 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)。

`v1.1.6` 的源码已经公开。Windows ZIP 暂不公开上传：`PaddleOCRSharp 6.2.0` 携带的第三方 `PaddleOCR.dll` 没有可供本项目确认的再分发授权。项目不会在授权边界不明确时把该二进制发布给公众；待取得明确授权或替换运行时后，再补充 ZIP 与 SHA-256。详情见 [第三方组件清单](ThirdPartyLicenses/manifest.json) 和 [公开验收摘要](ACCEPTANCE-STATUS.md)。

当前可以从 [GitHub Tags](https://github.com/erniaodoudoufei/MathGao-Gtools/tags) 获取源码标签，也可以按下文从源码构建。若以后提供官方 ZIP，请完整解压到同一个目录，不要只复制 `Gtools.exe`；OCR 模型、原生库和两个 Worker 必须保持原有相对位置。

本项目当前没有代码签名证书。未来公开 Windows 包时，Windows SmartScreen 可能显示“未知发布者”；届时请先确认文件来自本仓库 GitHub Release 且 SHA-256 一致，再决定是否继续运行。更新时先退出 Gtools，再用新版本完整目录替换程序文件；用户数据不在程序目录中，不会随替换丢失。

## 功能

- `Alt+B`：框选屏幕并使用本地 PaddleOCR 识别，结果自动复制，可在提示中打开校对。
- `Alt+S`：框选、OCR、自动判断中英方向，再通过 Google 网页通道、有道网页通道或用户配置的 OpenAI 兼容接口翻译。只有 OCR 后的文字会发送给翻译服务。
- 英语生词本：可选的有道词典和 dictionaryapi.dev 在线查词、标签筛选、CSV 导出及抽卡复习。
- `Alt+Shift+S`：手动或自动采集滚动区域，由独立 Worker 匹配和拼接长截图。
- `Ctrl+~`：本地 SQLite 剪贴板历史，支持搜索、收藏、删除、暂停和恢复后粘贴。
- 性能悬浮条与待办：显示网络、CPU、内存，待办保存在本机 SQLite；两者可分别关闭。

默认启用 OCR、翻译、生词本、长截图、剪贴板和待办；性能悬浮条可在设置中开启。MathGao-Gtools 不修改或卸载 Shot、STranslate、Ditto、AltSnap、TrafficMonitor 等其他软件。

## 长截图 1.1.5

每次只允许一个长截图会话；重复按快捷键不会创建第二个任务。Esc、关闭采集窗、异常和超时都按取消处理，Worker 和临时帧会被清理。

手动模式不会移动鼠标。选区隐藏并稳定后才采集首帧；点击采集后会等待画面连续两次稳定，最长约 1.2 秒。采集窗提供“采集并完成”和“直接完成”。检测到反向滚动、前进不足 6px、单次前进超过选区高度 86% 或达到最大帧数时会明确提示，不会静默截断。

只有可信的位移提示才会用于缩小 Worker 搜索范围。匹配置信度低于 `0.62` 时预览会显著标记接缝；没有可信提示且找不到有效重叠时会停止生成，避免输出看似正常的错图。手动模式可明确选择“仍然采集并标记检查”。拼接期间进度窗会保留并显示当前阶段。

预览只加载约 1200px 宽的内存缩略图，原始 PNG 保留在磁盘；首尾裁剪由 Worker 完成，主进程不会为裁剪完整解码超长 PNG。输出名包含毫秒和短请求编号，连续生成不会互相覆盖。

### 已知限制

- 微信版本变化、动态表情或内容高度变化可能降低匹配可靠性；低置信度接缝需要人工检查。
- 懒加载、无限滚动容器和虚拟列表可能在滚动后替换已有内容。
- 自动滚动不能保证适配任意软件；微信聊天建议从手动模式开始。
- 选区应避开标题栏、联系人列表、输入框和滚动条边缘；整屏图片、大片空白或重复气泡可能缺少可辨识特征。
- 本版未引入 Windows Graphics Capture、ORB/光流、流式超大图或新的微信专用匹配参数。

## 历史剪贴板 1.1.6

历史窗口先绘制工具栏、表头和加载提示，再从后台读取轻量列表；列表查询不再加载完整文字、HTML、RTF 或图片 BLOB。图片缩略图只在对应行滚动到可见区域时按需读取和解码，因此即使数据库包含大量截图，也不会在按下 `Ctrl+~` 后长时间显示空白窗口。

搜索输入采用短暂防抖，收藏、删除、清空和恢复内容均在需要时后台读取；连续按快捷键只会激活同一个历史窗口。复制图片时，界面线程只制作可跨线程使用的像素快照，PNG 压缩、哈希和 SQLite 写入按顺序在后台完成，降低复制大图造成的界面停顿。

## 数据与隐私

- 配置和 SQLite 数据库：`%LOCALAPPDATA%\Gtools`
- 长截图：当前用户“图片”目录下的 `Gtools\LongShots`
- 临时长截图帧：当前用户临时目录，完成、取消或异常后清理
- OCR：默认完全本地运行；离线模型和 Paddle CPU Runtime 位于 OCR Worker 目录，按需启动
- OpenAI 兼容接口 Key：使用当前 Windows 用户的 DPAPI 加密后保存在本机
- 翻译：Google/有道为非官方网页通道，可能失效；仅发送 OCR 后的文字，不发送截图
- 生词查词：启用自动查词时会把单词发送给有道词典和 dictionaryapi.dev，可在设置中关闭
- 剪贴板、待办和生词本：保存在本机 SQLite，不会由 MathGao-Gtools 主动上传

## 从源码构建

需要 Windows x64 和 .NET 8 SDK：

```powershell
.\build.ps1 -Configuration Release
```

普通 `-Publish` 会在检测到未解决的第三方再分发授权时主动停止。只有在你已自行确认相关权利、且仅用于本机评估时，才可显式运行：

```powershell
.\build.ps1 -Configuration Release -Publish -AllowUnresolvedRedistribution
```

该开关不会授予任何许可证或再分发权，生成的验收摘要会标记为 `local-evaluation-only-unresolved-license`，不得据此公开上传构建产物。

也可以分别执行：

```powershell
dotnet restore .\Gtools.sln
dotnet build .\Gtools.sln -c Release --no-restore
dotnet test .\tests\Gtools.Tests\Gtools.Tests.csproj -c Release --no-build
dotnet list .\Gtools.sln package --vulnerable --include-transitive
```

本地评估目录为 `artifacts\Gtools-1.1.6`，入口仍为 `Gtools.exe`。构建产物较大是因为包含离线 OCR 模型和 CPU Runtime；它们不会常驻内存。CI 只执行 Release 构建、测试和漏洞扫描，不生成 Windows ZIP。

诊断启动可使用 `Gtools.exe --test-profile <目录> --diagnostics --show-dashboard`。测试配置不会读写正式 `%LOCALAPPDATA%\Gtools`，也不会修改开机启动设置。请勿公开提交原始诊断日志；日志可能包含本机路径和运行环境信息。

## 安全与反馈

安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。普通缺陷和功能建议可提交 GitHub Issue。版本变化见 [CHANGELOG.md](CHANGELOG.md)，脱敏后的发布验收摘要见 [ACCEPTANCE-STATUS.md](ACCEPTANCE-STATUS.md)。
