# MathGao-Gtools

MathGao-Gtools 是一个面向 Windows 的开源桌面效率工具。仓库和发布包使用 **MathGao-Gtools** 名称；为保持升级兼容，程序入口仍叫 `Gtools.exe`，本机数据目录仍为 `%LOCALAPPDATA%\Gtools`。

源码采用 [MIT](LICENSE) 许可证。第三方组件按各自许可证分发，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 [ThirdPartyLicenses/manifest.json](ThirdPartyLicenses/manifest.json)。

## 二进制发布状态

运行环境：Windows x64 和 [.NET 8 Desktop Runtime（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)。

`v1.1.9` 已公开；当前本地版本 `v1.1.11` 优化图片剪贴板延迟，并包含 `v1.1.10` 的长截图改造。真实微信长截图验收仍待完成，完成后再发布源码标签。Windows ZIP 暂不公开上传：`PaddleOCRSharp 6.2.0` 携带的第三方 `PaddleOCR.dll` 没有可供本项目确认的再分发授权。项目不会在授权边界不明确时把该二进制发布给公众；待取得明确授权或替换运行时后，再补充 ZIP 与 SHA-256。详情见 [第三方组件清单](ThirdPartyLicenses/manifest.json) 和 [公开验收摘要](ACCEPTANCE-STATUS.md)。

当前可以从 [GitHub Tags](https://github.com/erniaodoudoufei/MathGao-Gtools/tags) 获取源码标签，也可以按下文从源码构建。若以后提供官方 ZIP，请完整解压到同一个目录，不要只复制 `Gtools.exe`；OCR 模型、原生库和两个 Worker 必须保持原有相对位置。

本项目当前没有代码签名证书。未来公开 Windows 包时，Windows SmartScreen 可能显示“未知发布者”；届时请先确认文件来自本仓库 GitHub Release 且 SHA-256 一致，再决定是否继续运行。更新时先退出 Gtools，再用新版本完整目录替换程序文件；用户数据不在程序目录中，不会随替换丢失。

## 功能

- `Alt+B`：框选屏幕并使用本地 PaddleOCR 识别，结果自动复制，可在提示中打开校对。
- `Alt+S`：框选、OCR、自动判断中英方向，再通过 Google 网页通道、有道网页通道或用户配置的 OpenAI 兼容接口翻译。只有 OCR 后的文字会发送给翻译服务。
- 英语生词本：可选的有道词典和 dictionaryapi.dev 在线查词、标签筛选、CSV 导出及抽卡复习。
- `Alt+Shift+S`：手动滚动微信聊天，逐帧检查并确认接缝，由独立 Worker 生成长图。
- `Ctrl+~`：本地 SQLite 剪贴板历史，支持搜索、收藏、删除、暂停和恢复后粘贴。
- 性能悬浮条与待办：显示网络、CPU、内存，待办保存在本机 SQLite；两者可分别关闭。

默认启用 OCR、翻译、生词本、长截图、剪贴板和待办；性能悬浮条可在设置中开启。MathGao-Gtools 不修改或卸载 Shot、STranslate、Ditto、AltSnap、TrafficMonitor 等其他软件。

## 悬浮窗位置 1.1.8

待办与性能条共用一个桌面层窗口。设置中的位置锚点对应最下面性能条的左上角，因此展开或折叠待办时性能条不会跳动。启用“锁定位置”只会禁止拖动；重新启动、Explorer 桌面宿主重建以及窗口重新显示时，仍会恢复上次保存的位置。自动布局不会覆盖保存值，只有解锁后主动拖动结束才会记录新位置。

## 截图翻译 1.1.9

翻译窗口采用浅蓝原文和浅绿译文双卡片布局。英文使用 Segoe UI，中文使用 Microsoft YaHei UI，正文为约 17px 并增加行距；文本仍可编辑、选择、复制、交换语言和加入生词本。主面板会显示正在运行的版本，设置页还会显示完整文件版本和 `Gtools.exe` 路径，可用来识别误打开的旧目录副本。

Google 通道直接请求 `translate.google.com` 的免密钥非官方网页端点，并不是 Google Cloud 提供兼容承诺的正式 Translation API。请求跟随当前系统代理或 TUN，不使用第三方公共中转，也不会绕过代理。它只接收 OCR 后的文字，不接收截图，但未来仍可能因端点、返回格式、频率限制或代理出口变化而失效。正式接口的要求可参考 [Google Cloud Translation 身份验证](https://docs.cloud.google.com/translate/docs/authentication) 和 [Basic REST translate](https://docs.cloud.google.com/translate/docs/reference/rest/v2/translate)。

为减少限流，相同翻译会合并，成功结果只在内存缓存15分钟且最多64条；实际请求至少相隔900ms。超时和服务端异常只进行一次受控重试，HTTP 429 会遵守服务端冷却时间。OCR 完成后会先显示原文，Google 失败不会关闭结果窗；更换代理节点后可主动重试，也可由用户明确点击改用有道，Gtools 不会自动转发文字。

设置页的“测试 Google 通道”只发送固定合成文字，可在更换代理节点后立即检查。HTTP 429 表示当前代理出口已被网页通道限流，并不代表代理没有连接；403通常表示出口或通道策略拒绝；超时和连接失败再检查节点、DNS与网络。需要稳定的自有通道时，可配置 `openai-compatible` 地址、模型和 Key。正式 Google Cloud Translation API 需要单独的 Google Cloud 项目、身份验证和配额/计费配置，不能只替换当前 URL。

## 微信长截图 1.1.10

本版撤下自动滚动。按以下步骤逐帧采集，算法只提供对齐建议，最终图片严格采用你确认过的接缝。

1. 按 `Alt+Shift+S`，框选消息区域并确认第一帧。避开标题、输入框、滚动条和“新消息”提示；必要时关闭提示后重新框选。
2. 按设置中的方向手动滚动，每次保留至少半屏重叠，点击“采集下一帧”。画面需连续稳定300ms，3秒内未稳定则重试。
3. 检查接缝实际成图，橙线标记拼接处，支持原始尺寸查看。点击“确认加入”后才会加入长图。
4. 如需手工对齐，在两帧中各点一次同一行文字，点击“更新预览”，用 ±1px、接缝线位置及半透明叠加检查微调。
5. 对不齐可“丢弃本帧并重采”；上一帧已确认也可撤销。最后点击“完成并预览”，只导出已确认内容。

固定提示或动画应先避开；完全无重叠、重复画面、反向滚动、内容变化过大时无法确认。没有足够定位信息时请回退后重采，不能通过“仍然采集”强行导出。窗口移动、大小/DPI变化或被其他窗口遮挡时会暂停。

只允许一个采集会话。Esc或关闭采集/校对窗口取消任务，退出分析Worker后清理临时帧。导出支持完整PNG、分段PNG及Worker首尾裁剪；总览限制400万像素，原图保留在磁盘，文件名含毫秒及唯一编号。

### 本轮验收与限制

- 自动测试使用合成编号聊天，整图与原始长图逐像素比对，不再把“生成了文件”视为成功。
- 真实微信桌面验收仍待完成，具体状态见 [长截图验收报告](LONGSHOT-ACCEPTANCE-v1.1.10.md)。
- QQ、Generic 配置仍可读取，但本轮只验收微信；网页、PDF、OneNote及任意软件自动滚动不在本轮范围。
- 懒加载、动态图像及内容重新排版可能需要重采；没有共同内容的两张截图不能自动补出中间丢失的消息。

## 历史剪贴板 1.1.11

历史窗口先绘制工具栏、表头和加载提示，再从后台读取轻量列表；列表查询不再加载完整文字、HTML、RTF 或图片 BLOB。图片缩略图只在对应行滚动到可见区域时按需读取和解码，因此即使数据库包含大量截图，也不会在按下 `Ctrl+~` 后长时间显示空白窗口。

搜索输入采用短暂防抖，收藏、删除、清空和恢复内容均在需要时后台读取；连续按快捷键只会激活同一个历史窗口。图片读取使用独立 STA 线程，在系统剪贴板锁内复制当前格式的数据；PNG 已有编码直接复用，Bitmap/DIB 则在后台转换后入库。读取被占用时有限重试，连续通知按序号去重，已捕获内容按顺序保存。

面板底部会显示“已捕获，正在保存 N 条”或失败原因。保存后自动刷新，并保留搜索、所选记录和滚动位置；持续复制不会把刷新无限推迟。正常退出最多等待三秒排空已捕获内容，遇到失败或超时会提示。系统剪贴板已被覆盖且尚未读取的内容无法补回。

合成测试、阶段耗时及真实浏览器/聊天软件验收状态见 [剪贴板验收记录](CLIPBOARD-ACCEPTANCE-v1.1.11.md)。手工复现素材在 `tools/clipboard-browser-test.html`，只包含两张 2048×2048 合成图片。

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

本地评估目录为 `artifacts\Gtools-1.1.11`，入口仍为 `Gtools.exe`。构建产物较大是因为包含离线 OCR 模型和 CPU Runtime；它们不会常驻内存。CI 只执行 Release 构建、测试和漏洞扫描，不生成 Windows ZIP。

诊断启动可使用 `Gtools.exe --test-profile <目录> --diagnostics --show-dashboard`。测试配置不会读写正式 `%LOCALAPPDATA%\Gtools`，也不会修改开机启动设置。请勿公开提交原始诊断日志；日志可能包含本机路径和运行环境信息。

## 安全与反馈

安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。普通缺陷和功能建议可提交 GitHub Issue。版本变化见 [CHANGELOG.md](CHANGELOG.md)，脱敏后的发布验收摘要见 [ACCEPTANCE-STATUS.md](ACCEPTANCE-STATUS.md)。

本地合成聊天交互验收入口：`Gtools.exe --test-profile <独立目录> --longshot-demo --diagnostics`。仅在明确指定独立测试配置时启用，不读取实际聊天数据。
