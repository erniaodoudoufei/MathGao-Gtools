# Third-party notices

MathGao-Gtools 自身源码采用 MIT 许可证。发布包还包含第三方 NuGet 组件、原生库和离线 OCR 模型；它们不因本项目使用 MIT 而改变各自的许可证或权利归属。

机器可读的逐项清单、锁定版本、上游来源、对应发布文件和许可证文件位于 [`ThirdPartyLicenses/manifest.json`](ThirdPartyLicenses/manifest.json)。发布 ZIP 必须原样包含整个 `ThirdPartyLicenses` 目录。

主要组件包括：

- Microsoft.Data.Sqlite、部分 .NET 运行库和 SkiaSharp 托管层：MIT
- SQLitePCLRaw：Apache-2.0；SQLite：公共领域
- OpenCvSharp 4.10.0.20240616 与 OpenCV 4.10：Apache-2.0
- Skia 原生组件：BSD-3-Clause，并附 SkiaSharp 上游完整第三方 notices
- PaddleOCRSharp 6.2.0 托管层、Paddle Runtime 3.3.0.1、PaddlePaddle/PaddleOCR 和离线模型：上游标注 Apache-2.0
- Paddle Runtime 内的 Intel 数学/OpenMP 二进制：Intel Simplified Software License；oneDNN/oneTBB：Apache-2.0；yaml-cpp：MIT
- Paddle Runtime 内的 Microsoft Visual C++ Runtime：受 Microsoft Visual C++ Runtime 2015–2022 条款约束

`PaddleOCRSharp` 随包携带的 `PaddleOCR.dll` 是上游说明中的“第三方免费社区版”原生组件。PaddleOCRSharp 自带许可证明确表示其 Apache-2.0 只覆盖 C# 源码，并不授予该第三方 DLL 的许可。因此清单对该文件使用 `NOASSERTION`，正式公开再分发前需要发布者另行确认授权；本 notices 不是对该 DLL 授权状态的法律判断。

长截图不使用 OpenCV 的 FFmpeg Video I/O 插件。`opencv_videoio_ffmpeg*_64.dll` 不得进入发布包；清单将它列为明确排除项，避免引入本产品不需要的 FFmpeg 许可范围。

Gtools 的实现没有复制 Ditto、TrafficMonitor、Shot、STranslate、AltSnap 或微信的源码或品牌素材。提及这些名称仅用于说明兼容边界，不代表从属、授权或背书关系。
