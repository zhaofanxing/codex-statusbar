# Build and verify the exact files later attached to a GitHub Release.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$OutputDirectory = 'dist/release'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
$versionPattern = '^v(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)$'
if ($Tag -cnotmatch $versionPattern) { throw 'Expected vMAJOR.MINOR.PATCH, optionally followed by -prerelease.' }
$version = $Matches.version
$numericVersion = $version.Split('-')[0]
if (@($numericVersion.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
    throw 'Windows version components must not exceed 65534.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The output directory must be inside this repository.'
}
if (Test-Path -LiteralPath $output) { throw "Use a new, empty output path: $output" }

$required = @('LICENSE', 'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md',
    'release/start-monitor.bat', 'fixtures/fixture-modern.jsonl', 'fixtures/fixture-legacy.jsonl')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) { throw "Required file missing: $name" }
}
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the source commit.' }
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^10\.') { throw '.NET 10 SDK is required.' }

# Each invocation owns a fresh work directory; never remove a user's existing build output.
$work = Join-Path (Split-Path -Parent $output) ('release-work-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$package = Join-Path $work 'package'
$assets = Join-Path $output 'assets'
New-Item -ItemType Directory -Path $publish, $package, $assets | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$project = Join-Path $root 'src/CodexStatusbar/CodexStatusbar.csproj'
$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-o', $publish, "-p:Version=$version", "-p:InformationalVersion=$version",
    "-p:AssemblyVersion=$numericVersion.0", "-p:FileVersion=$numericVersion.0")
& dotnet @publishArgs | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
$exe = Join-Path $package 'CodexStatusbar.exe'
Copy-Item -LiteralPath (Join-Path $publish 'CodexStatusbar.exe') -Destination $exe
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -cne $version) {
    throw 'The EXE informational version does not match the tag.'
}
foreach ($name in @('LICENSE', 'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $package $name)
}
$launcher = Get-Content -LiteralPath (Join-Path $root 'release/start-monitor.bat') -Raw
$launcher = [regex]::Replace($launcher, '(?m)^rem  CodexStatusbar \S+ - launcher\r?$', "rem  CodexStatusbar $version - launcher")
$launcher = $launcher -replace '\r?\n', "`r`n"
[IO.File]::WriteAllText((Join-Path $package 'start-monitor.bat'), $launcher, $utf8)
[IO.File]::WriteAllText((Join-Path $package 'VERSION'), "$version`n", $utf8)
New-Item -ItemType Directory -Path (Join-Path $package 'fixtures') | Out-Null
foreach ($name in @('fixture-modern.jsonl', 'fixture-legacy.jsonl')) {
    Copy-Item -LiteralPath (Join-Path $root "fixtures/$name") -Destination (Join-Path $package "fixtures/$name")
}

# Run the packaged GUI-subsystem executable headlessly with explicit output handles and a timeout.
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $package
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--self-test')
$start.ArgumentList.Add((Join-Path $package 'fixtures'))
$process = [Diagnostics.Process]::Start($start)
try {
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Self-test timed out.' }
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $work 'self-test.log'), $stdout + $stderr, $utf8)
    Write-Host $stdout
    if ($process.ExitCode -ne 0 -or $stdout -notmatch 'RESULT: PASS' -or
        $stdout -notmatch 'checks: (?<checks>[0-9]+)\s+failures: 0') { throw 'Packaged EXE self-test failed.' }
    $checks = [int]$Matches.checks
}
finally { $process.Dispose() }

$exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$exeSize = (Get-Item -LiteralPath $exe).Length
$checksumInfo = @"
CodexStatusbar $version - SHA-256

$exeHash  CodexStatusbar.exe

Size: $exeSize bytes
Runtime: self-contained win-x64 single file
Build SDK: $sdkVersion
Source commit: $commit
Self-test: $checks checks, 0 failures (CodexStatusbar.exe --self-test fixtures)
"@
[IO.File]::WriteAllText((Join-Path $package 'SHA256.txt'), $checksumInfo + "`n", $utf8)
$readme = @"
# CodexStatusbar $version

Windows x64 自包含版本，无需安装 .NET 运行时。解压后双击 start-monitor.bat 或 CodexStatusbar.exe。
需要同一用户下的 Codex Desktop；通过系统托盘调整位置、主题及开机启动。
首次运行默认注册当前用户的开机启动，可在托盘菜单中取消。

自测：CodexStatusbar.exe --self-test fixtures
本次验证：$checks 项检查，0 项失败。EXE 校验值见 SHA256.txt。

本仓库基于 https://github.com/dongyue-19/codex-statusbar 修改维护。
基础代码来自 codex-token-overlay；设计参考 codex-monitor-hud（现名 agent-monitor-hud）；
TPS 方法参考 codex-model-benchmarks。完整来源与复用范围见 THIRD-PARTY-NOTICES.md。
请保留 LICENSE、LICENSE-codex-token-overlay 和 THIRD-PARTY-NOTICES.md。
项目主页：https://github.com/zhaofanxing/codex-statusbar
"@
[IO.File]::WriteAllText((Join-Path $package 'README.md'), $readme + "`n", $utf8)
$archiveName = "CodexStatusbar-$Tag-win-x64.zip"
$archive = Join-Path $assets $archiveName
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $archive -CompressionLevel Optimal

# Verify every ZIP entry against its source, including byte-for-byte upstream license preservation.
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $packageFiles = @(Get-ChildItem -LiteralPath $package -File -Recurse)
    $entries = @($zip.Entries | Where-Object { $_.Name })
    if ($entries.Count -ne $packageFiles.Count) { throw 'ZIP file count does not match the package.' }
    foreach ($file in $packageFiles) {
        $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
        $entry = $zip.GetEntry($relative)
        if ($null -eq $entry) { throw "ZIP entry missing: $relative" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw "ZIP content mismatch: $relative"
        }
    }
}
finally { $zip.Dispose() }
foreach ($name in @('CodexStatusbar.exe', 'start-monitor.bat', 'LICENSE', 'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $package $name) -Destination (Join-Path $assets $name)
}
$assetNames = @('CodexStatusbar.exe', $archiveName, 'start-monitor.bat', 'LICENSE', 'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md')
$hashLines = foreach ($name in $assetNames) {
    "{0}  {1}" -f (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant(), $name
}
[IO.File]::WriteAllText((Join-Path $assets 'SHA256SUMS.txt'), ($hashLines -join "`n") + "`n", $utf8)
$assetNames += 'SHA256SUMS.txt'
$manifest = [ordered]@{
    Tag = $Tag; Version = $version; SourceCommit = $commit; Prerelease = $version.Contains('-'); Checks = $checks
    Assets = @($assetNames | ForEach-Object {
        $file = Join-Path $assets $_
        [ordered]@{ Name = $_; Size = (Get-Item -LiteralPath $file).Length; SHA256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
[IO.File]::WriteAllText((Join-Path $output 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5) + "`n", $utf8)
$notes = @"
Windows x64 自包含版本，无需安装 .NET 运行时。

- 推荐下载 $archiveName，解压后启动 start-monitor.bat 或 CodexStatusbar.exe。
- 同时提供独立 EXE、启动脚本、许可证、第三方声明和 SHA256SUMS.txt。
- 版本来自标签 $Tag；源码提交：$commit。
- 对发布包内 EXE 执行自测：$checks 项检查，0 项失败。
- ZIP 包含两份许可证、第三方声明、使用说明、版本信息及完整自测样例。

本仓库基于 https://github.com/dongyue-19/codex-statusbar 修改维护。
基础代码来自 https://github.com/soleillevant0125/codex-token-overlay；
设计参考 https://github.com/LH-03/agent-monitor-hud（原 codex-monitor-hud）；
TPS 方法参考 https://github.com/zakmandhro/codex-model-benchmarks。
原作者版权和 MIT 许可证完整保留，详见随附 THIRD-PARTY-NOTICES.md。

EXE SHA-256：$exeHash
"@
[IO.File]::WriteAllText((Join-Path $output 'release-notes.md'), $notes + "`n", $utf8)
[pscustomobject]@{ Tag = $Tag; Version = $version; SourceCommit = $commit; Directory = $output; Checks = $checks }
