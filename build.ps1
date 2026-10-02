param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SkipAcceptance,
    [switch]$AllowUnresolvedRedistribution
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath $PSScriptRoot).Path

function Get-ValidatedChildPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowRoot
    )

    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd([System.IO.Path]::DirectorySeparatorChar,[System.IO.Path]::AltDirectorySeparatorChar)
    $candidate = [System.IO.Path]::GetFullPath($Path)
    $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
    $inside = $candidate.StartsWith($prefix,[System.StringComparison]::OrdinalIgnoreCase)
    if ((-not $inside) -and -not ($AllowRoot -and $candidate.Equals($rootPath,[System.StringComparison]::OrdinalIgnoreCase))) {
        throw "路径越出允许范围：$candidate（根目录：$rootPath）"
    }
    return $candidate
}

function Remove-ValidatedItem {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Path,
        [switch]$Recurse
    )

    $validated = Get-ValidatedChildPath -Root $Root -Path $Path
    if (Test-Path -LiteralPath $validated) {
        Remove-Item -LiteralPath $validated -Force -Recurse:$Recurse
    }
}

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Message)
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

function Test-ThirdPartyLicenseBundle {
    param(
        [Parameter(Mandatory)][string]$BundleRoot,
        [switch]$AllowUnresolved
    )

    $root = (Resolve-Path -LiteralPath $BundleRoot).Path
    $manifestPath = Join-Path $root 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "第三方许可证清单不存在：$manifestPath"
    }

    try {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    }
    catch {
        throw "第三方许可证清单不是有效 JSON：$manifestPath。$($_.Exception.Message)"
    }

    if ($manifest -is [System.Array]) { $entries = @($manifest) }
    elseif ($null -ne $manifest.components) { $entries = @($manifest.components) }
    elseif ($null -ne $manifest.packages) { $entries = @($manifest.packages) }
    else { throw '第三方许可证清单必须是数组，或包含 components/packages 数组。' }

    if ($entries.Count -eq 0) { throw '第三方许可证清单不能为空。' }
    foreach ($entry in $entries) {
        $licenseFile = [string]$entry.licenseFile
        if ([string]::IsNullOrWhiteSpace($licenseFile)) {
            throw '第三方许可证清单中的每一项都必须提供 licenseFile。'
        }
        if ([System.IO.Path]::IsPathRooted($licenseFile)) {
            throw "许可证文件必须使用相对路径：$licenseFile"
        }

        $licensePath = Get-ValidatedChildPath -Root $root -Path (Join-Path $root $licenseFile)
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "许可证文件不存在：$licenseFile"
        }
        if ((Get-Item -LiteralPath $licensePath).Length -eq 0) {
            throw "许可证文件为空：$licenseFile"
        }
        $noticeFile = [string]$entry.noticeFile
        if (-not [string]::IsNullOrWhiteSpace($noticeFile)) {
            $noticePath = Get-ValidatedChildPath -Root $root -Path (Join-Path $root $noticeFile)
            if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or (Get-Item -LiteralPath $noticePath).Length -eq 0) {
                throw "第三方 notice 不存在或为空：$noticeFile"
            }
        }
    }
    $blocked = @($entries | Where-Object { [bool]$_.publicRedistributionBlocked })
    if ($blocked.Count -gt 0 -and -not $AllowUnresolved) {
        throw "发布被第三方再分发授权阻断：$($blocked.name -join ', ')。仅可在不公开上传的本地验收中显式使用 -AllowUnresolvedRedistribution。"
    }
    return $entries.Count
}

function Copy-VerifiedDirectory {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }

    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        $relative = [System.IO.Path]::GetRelativePath($sourceRoot,$sourceFile.FullName)
        $targetFile = Join-Path $Destination $relative
        if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf)) {
            throw "许可证文件复制失败：$relative"
        }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) { throw "许可证文件复制后校验失败：$relative" }
    }
}

$propsPath = Join-Path $workspace 'Directory.Build.props'
[xml]$buildProps = Get-Content -Raw -LiteralPath $propsPath
$version = [string]$buildProps.Project.PropertyGroup.Version
$fileVersion = [string]$buildProps.Project.PropertyGroup.FileVersion
$assemblyVersion = [string]$buildProps.Project.PropertyGroup.AssemblyVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props 中的版本号无效：$version" }
$expectedFileVersion = "$version.0"
if ($fileVersion -ne $expectedFileVersion -or $assemblyVersion -ne $expectedFileVersion) {
    throw "Version、FileVersion 与 AssemblyVersion 不一致：$version / $fileVersion / $assemblyVersion"
}

$solution = Join-Path $workspace 'Gtools.sln'
$testProject = Join-Path $workspace 'tests\Gtools.Tests\Gtools.Tests.csproj'

dotnet restore $solution
Assert-LastExitCode 'dotnet restore failed.'
dotnet build $solution -c $Configuration --no-restore -warnaserror
Assert-LastExitCode 'dotnet build failed.'
dotnet test $testProject -c $Configuration --no-build
Assert-LastExitCode 'dotnet test failed.'

$auditOutput = dotnet list $solution package --vulnerable --include-transitive --format json
Assert-LastExitCode 'NuGet 漏洞扫描命令失败。'
$auditText = $auditOutput -join [Environment]::NewLine
$null = $auditText | ConvertFrom-Json
if ($auditText -match '(?i)"vulnerabilities"\s*:') {
    throw "检测到 NuGet 漏洞：`n$auditText"
}
if (-not $Publish) { exit 0 }

$artifactRoot = Get-ValidatedChildPath -Root $workspace -Path (Join-Path $workspace 'artifacts')
$publishRoot = Get-ValidatedChildPath -Root $artifactRoot -Path (Join-Path $artifactRoot "Gtools-$version")
$acceptanceRoot = Get-ValidatedChildPath -Root $artifactRoot -Path (Join-Path $artifactRoot "Acceptance-$version")
$zipName = "MathGao-Gtools-v$version-win-x64.zip"
$zip = Get-ValidatedChildPath -Root $artifactRoot -Path (Join-Path $artifactRoot $zipName)
$checksum = Get-ValidatedChildPath -Root $artifactRoot -Path "$zip.sha256"

Remove-ValidatedItem -Root $artifactRoot -Path $publishRoot -Recurse
Remove-ValidatedItem -Root $artifactRoot -Path $zip
Remove-ValidatedItem -Root $artifactRoot -Path $checksum
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

dotnet publish (Join-Path $workspace 'src\Gtools.App\Gtools.App.csproj') -c $Configuration -r win-x64 --self-contained false -o $publishRoot
Assert-LastExitCode 'Gtools publish failed.'
foreach ($worker in @('Gtools.OcrWorker','Gtools.LongShotWorker')) {
    $target = Join-Path $publishRoot "Workers\$worker"
    dotnet publish (Join-Path $workspace "src\$worker\$worker.csproj") -c $Configuration -r win-x64 --self-contained false -o $target
    Assert-LastExitCode "$worker publish failed."
}

# LongShot 仅处理静态图像；OpenCV 的视频 FFmpeg 组件不随发布包分发。
$ffmpegFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'opencv_videoio_ffmpeg*_64.dll')
foreach ($ffmpegFile in $ffmpegFiles) {
    $validatedFfmpeg = Get-ValidatedChildPath -Root $publishRoot -Path $ffmpegFile.FullName
    Remove-Item -LiteralPath $validatedFfmpeg -Force
}
if (Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'opencv_videoio_ffmpeg*.dll') {
    throw '发布包仍包含不需要的 OpenCV FFmpeg DLL。'
}

# 公开运行包不分发可能携带本机构建路径的调试符号。
foreach ($symbolFile in Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter '*.pdb') {
    $validatedSymbol = Get-ValidatedChildPath -Root $publishRoot -Path $symbolFile.FullName
    Remove-Item -LiteralPath $validatedSymbol -Force
}

foreach ($document in @('README.md','CHANGELOG.md','ACCEPTANCE-STATUS.md',"RELEASE-NOTES-v$version.md",'THIRD-PARTY-NOTICES.md','LICENSE')) {
    $sourceDocument = Join-Path $workspace $document
    if (-not (Test-Path -LiteralPath $sourceDocument -PathType Leaf)) { throw "发布文档不存在：$document" }
    Copy-Item -LiteralPath $sourceDocument -Destination $publishRoot -Force
}

foreach ($reportName in @('LONGSHOT-ACCEPTANCE-v1.1.10.md','CLIPBOARD-ACCEPTANCE-v1.1.11.md','CLIPBOARD-ACCEPTANCE-v1.1.12.md','TODO-UNDO-ACCEPTANCE-v1.1.13.md',"CLIPBOARD-ACCEPTANCE-v$version.md") | Select-Object -Unique) {
    $featureReport = Join-Path $workspace $reportName
    if (Test-Path -LiteralPath $featureReport -PathType Leaf) {
        Copy-Item -LiteralPath $featureReport -Destination $publishRoot -Force
    }
}

$thirdPartySource = Join-Path $workspace 'ThirdPartyLicenses'
$licenseCount = Test-ThirdPartyLicenseBundle -BundleRoot $thirdPartySource -AllowUnresolved:$AllowUnresolvedRedistribution
$thirdPartyDestination = Join-Path $publishRoot 'ThirdPartyLicenses'
Copy-VerifiedDirectory -Source $thirdPartySource -Destination $thirdPartyDestination
$publishedLicenseCount = Test-ThirdPartyLicenseBundle -BundleRoot $thirdPartyDestination -AllowUnresolved:$AllowUnresolvedRedistribution
if ($publishedLicenseCount -ne $licenseCount) { throw '发布包内的第三方许可证数量与源码清单不一致。' }

$brand = Join-Path $publishRoot 'Brand'
New-Item -ItemType Directory -Path $brand -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'assets\icon\Gtools.svg') -Destination $brand -Force
Copy-Item -LiteralPath (Join-Path $workspace 'assets\icon\png') -Destination $brand -Recurse -Force

$requiredExecutables = @(
    (Join-Path $publishRoot 'Gtools.exe'),
    (Join-Path $publishRoot 'Workers\Gtools.OcrWorker\Gtools.OcrWorker.exe'),
    (Join-Path $publishRoot 'Workers\Gtools.LongShotWorker\Gtools.LongShotWorker.exe')
)
foreach ($executable in $requiredExecutables) {
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "发布程序缺失：$executable" }
    $actualVersion = (Get-Item -LiteralPath $executable).VersionInfo.FileVersion
    if ([version]$actualVersion -ne [version]$expectedFileVersion) {
        throw "发布程序版本错误：$executable（实际 $actualVersion，应为 $expectedFileVersion）"
    }
}

# 在移除 FFmpeg 后，从最终发布目录启动 LongShot Worker 完成 PNG 拼接冒烟测试。
$previousPackageRoot = $env:GTOOLS_PACKAGE_ROOT
try {
    $env:GTOOLS_PACKAGE_ROOT = $publishRoot
    dotnet test $testProject -c $Configuration --no-build --filter 'FullyQualifiedName=Gtools.Tests.PublishedPackageTests.PublishedLongShotWorker_StitchesPngWithoutFfmpeg'
    Assert-LastExitCode '发布目录 LongShot Worker 冒烟测试失败。'
}
finally {
    if ($null -eq $previousPackageRoot) { Remove-Item Env:\GTOOLS_PACKAGE_ROOT -ErrorAction SilentlyContinue }
    else { $env:GTOOLS_PACKAGE_ROOT = $previousPackageRoot }
}

$acceptanceSummaryPath = Join-Path $publishRoot 'acceptance-summary.json'
if (-not $SkipAcceptance) {
    Remove-ValidatedItem -Root $artifactRoot -Path $acceptanceRoot -Recurse
    New-Item -ItemType Directory -Path $acceptanceRoot -Force | Out-Null
    $reportPath = Join-Path $acceptanceRoot 'acceptance-report.json'
    $arguments = @('--self-test','--test-profile',('"' + $acceptanceRoot + '"'),'--diagnostics','--report',('"' + $reportPath + '"'))
    $acceptance = Start-Process -FilePath (Join-Path $publishRoot 'Gtools.exe') -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($acceptance.ExitCode -ne 0) {
        throw "Gtools 自动验收失败，退出码 $($acceptance.ExitCode)。原始报告保留于隔离测试目录。"
    }
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw 'Gtools 自动验收未生成报告。' }
    $rawReport = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if (-not [bool]$rawReport.passed) { throw 'Gtools 自动验收报告未通过。' }

    $summaryChecks = @($rawReport.checks | ForEach-Object {
        [ordered]@{
            id = [string]$_.id
            name = [string]$_.name
            passed = [bool]$_.passed
            result = if ([bool]$_.passed) { 'Passed' } else { 'Failed' }
        }
    })
    $summary = [ordered]@{
        product = 'MathGao-Gtools'
        application = 'Gtools'
        version = $version
        generatedAt = [DateTimeOffset]::Now.ToUniversalTime().ToString('o')
        profile = 'isolated-test-profile'
        passed = $true
        checks = $summaryChecks
        workers = @('Gtools.OcrWorker','Gtools.LongShotWorker')
        nugetVulnerabilities = 0
        redistributionStatus = if ($AllowUnresolvedRedistribution) { 'local-evaluation-only-unresolved-license' } else { 'cleared-for-public-release' }
        knownLimitations = @($rawReport.knownLimitations | ForEach-Object { [string]$_ })
    }
}
else {
    $summary = [ordered]@{
        product = 'MathGao-Gtools'
        application = 'Gtools'
        version = $version
        generatedAt = [DateTimeOffset]::Now.ToUniversalTime().ToString('o')
        profile = 'acceptance-skipped'
        passed = $false
        checks = @()
        workers = @('Gtools.OcrWorker','Gtools.LongShotWorker')
        nugetVulnerabilities = 0
        redistributionStatus = if ($AllowUnresolvedRedistribution) { 'local-evaluation-only-unresolved-license' } else { 'cleared-for-public-release' }
        knownLimitations = @('本地发布使用了 -SkipAcceptance；该包不具备正式发布验收资格。')
    }
}

$summaryText = $summary | ConvertTo-Json -Depth 8
foreach ($sensitiveValue in @($workspace,$env:USERPROFILE,$env:USERNAME)) {
    if (-not [string]::IsNullOrWhiteSpace($sensitiveValue) -and $summaryText.Contains($sensitiveValue,[System.StringComparison]::OrdinalIgnoreCase)) {
        throw '脱敏验收摘要仍包含本机路径或用户名。'
    }
}
if ($summaryText -match '(?i)"(?:diagnosticsLog|processId|pid)"\s*:|[A-Za-z]:\\\\') {
    throw '脱敏验收摘要仍包含诊断路径或进程标识。'
}
[System.IO.File]::WriteAllText($acceptanceSummaryPath,$summaryText,[System.Text.UTF8Encoding]::new($false))

$forbiddenFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Name -match '(?i)WindowWorker|window-control|acceptance-report|\.jsonl$|\.pdb$|opencv_videoio_ffmpeg'
})
if ($forbiddenFiles.Count -gt 0) {
    throw "发布包包含禁止文件：$($forbiddenFiles.FullName -join ', ')"
}
if (-not (Test-Path -LiteralPath $acceptanceSummaryPath -PathType Leaf)) { throw '发布包缺少脱敏验收摘要。' }

Compress-Archive -LiteralPath $publishRoot -DestinationPath $zip -Force
if (-not (Test-Path -LiteralPath $zip -PathType Leaf) -or (Get-Item -LiteralPath $zip).Length -eq 0) {
    throw '发布 ZIP 未生成或为空。'
}
$zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = "$zipHash  $zipName`n"
[System.IO.File]::WriteAllText($checksum,$checksumText,[System.Text.UTF8Encoding]::new($false))

Write-Host "Published: $publishRoot"
Write-Host "Package: $zip"
Write-Host "SHA256: $checksum"
