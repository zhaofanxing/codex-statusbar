# Validate a build artifact before granting it a public Release URL.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Commit,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
if ($Tag -cnotmatch '^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'Invalid release tag.'
}
if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'A full source commit SHA is required.' }
$directory = [IO.Path]::GetFullPath($PackageDirectory, $root)
$assets = Join-Path $directory 'assets'
$manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw | ConvertFrom-Json
$version = $Tag.Substring(1)
if ($manifest.Tag -cne $Tag -or $manifest.Version -cne $version -or $manifest.SourceCommit -cne $Commit -or
    $manifest.Prerelease -isnot [bool] -or $manifest.Prerelease -ne $version.Contains('-') -or $manifest.Checks -lt 1) {
    throw 'Release manifest does not match the requested tag, source commit or test result.'
}
$archiveName = "CodexStatusbar-$Tag-win-x64.zip"
$expected = @('CodexStatusbar.exe', $archiveName, 'start-monitor.bat', 'LICENSE',
    'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md', 'SHA256SUMS.txt')
if (@($manifest.Assets).Count -ne $expected.Count -or
    @(Get-ChildItem -LiteralPath $assets -File -Force -Recurse).Count -ne $expected.Count) { throw 'Unexpected release asset count.' }
$byName = @{}
foreach ($asset in $manifest.Assets) {
    if ($expected -cnotcontains $asset.Name -or $byName.ContainsKey($asset.Name) -or
        $asset.SHA256 -cnotmatch '^[0-9a-f]{64}$' -or $asset.Size -lt 1) { throw 'Invalid release asset entry.' }
    $path = Join-Path $assets $asset.Name
    if ((Get-Item -LiteralPath $path).Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.SHA256) {
        throw "Release asset checksum mismatch: $($asset.Name)"
    }
    $byName[$asset.Name] = $asset
}
foreach ($name in @('LICENSE', 'LICENSE-codex-token-overlay', 'THIRD-PARTY-NOTICES.md')) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $byName[$name].SHA256) {
        throw "Upstream license or attribution changed in the package: $name"
    }
}
$checksumLines = @(Get-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt'))
if ($checksumLines.Count -ne ($expected.Count - 1)) { throw 'Incomplete SHA256SUMS.txt.' }
foreach ($name in $expected | Where-Object { $_ -ne 'SHA256SUMS.txt' }) {
    if ($checksumLines -cnotcontains "$($byName[$name].SHA256)  $name") { throw "Checksum entry missing: $name" }
}
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $assets $archiveName))
try {
    $requiredEntries = @('CodexStatusbar.exe', 'start-monitor.bat', 'LICENSE', 'LICENSE-codex-token-overlay',
        'THIRD-PARTY-NOTICES.md', 'VERSION', 'SHA256.txt', 'README.md', 'fixtures/fixture-modern.jsonl', 'fixtures/fixture-legacy.jsonl')
    if (@($zip.Entries | Where-Object { $_.Name }).Count -ne $requiredEntries.Count) { throw 'Unexpected ZIP content count.' }
    foreach ($name in $requiredEntries) {
        if ($null -eq $zip.GetEntry($name)) { throw "Required ZIP entry missing: $name" }
    }
    foreach ($name in @('CodexStatusbar.exe', 'start-monitor.bat', 'LICENSE', 'LICENSE-codex-token-overlay',
        'THIRD-PARTY-NOTICES.md', 'fixtures/fixture-modern.jsonl', 'fixtures/fixture-legacy.jsonl')) {
        $stream = $zip.GetEntry($name).Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        $expectedHash = if ($byName.ContainsKey($name)) { $byName[$name].SHA256 } else {
            (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        if ($hash -cne $expectedHash) { throw "ZIP entry checksum mismatch: $name" }
    }
    $reader = [IO.StreamReader]::new($zip.GetEntry('VERSION').Open())
    try { if ($reader.ReadToEnd().Trim() -cne $version) { throw 'ZIP VERSION does not match the tag.' } }
    finally { $reader.Dispose() }
}
finally { $zip.Dispose() }
if ($ValidateOnly) { Write-Host "Release validation passed: $Tag ($Commit)"; return }

if (-not $env:GH_TOKEN -or $env:GITHUB_REPOSITORY -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'GH_TOKEN and GITHUB_REPOSITORY are required for publishing.'
}
if ((& git -C $root rev-parse HEAD).Trim() -cne $Commit -or $LASTEXITCODE -ne 0) { throw 'Checkout commit does not match the build.' }
$repo = $env:GITHUB_REPOSITORY
$api = "https://api.github.com/repos/$repo"
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2026-03-10' }
function Invoke-ReleaseApi {
    param([string]$Path, [string]$Method = 'Get', [object]$Body = $null, [switch]$AllowMissing)
    $parameters = @{ Uri = "$api/$Path"; Method = $Method; Headers = $headers; TimeoutSec = 60; MaximumRedirection = 0 }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Depth 6; $parameters.ContentType = 'application/json; charset=utf-8' }
    try { Invoke-RestMethod @parameters }
    catch {
        if ($AllowMissing -and $_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound) { return $null }
        throw
    }
}
function Assert-TagCommit {
    $target = (Invoke-ReleaseApi -Path "git/ref/tags/$Tag").object
    for ($depth = 0; $depth -lt 5 -and $target.type -eq 'tag'; $depth++) {
        $target = (Invoke-ReleaseApi -Path "git/tags/$($target.sha)").object
    }
    if ($target.type -ne 'commit' -or $target.sha -cne $Commit) { throw 'Remote tag no longer points to the built commit.' }
}
function Assert-RemoteAsset {
    param($Remote)
    $local = $byName[$Remote.name]
    if ($null -eq $local -or $Remote.state -ne 'uploaded' -or $Remote.size -ne $local.Size -or
        $Remote.digest -cne "sha256:$($local.SHA256)") { throw "Remote asset verification failed: $($Remote.name)" }
}
Assert-TagCommit
$marker = "<!-- codex-statusbar-release:$Commit -->"
$release = Invoke-ReleaseApi -Path "releases/tags/$Tag" -AllowMissing
if ($null -ne $release) {
    if (-not $release.draft) { throw 'This version is already published; create a new version tag.' }
    if ($release.target_commitish -cne $Commit -or -not $release.body.Contains($marker)) {
        throw 'An unrelated release draft already uses this tag; it was not modified.'
    }
}
else {
    $notes = (Get-Content -LiteralPath (Join-Path $directory 'release-notes.md') -Raw) + "`n$marker"
    $release = Invoke-ReleaseApi -Path 'releases' -Method Post -Body @{
        tag_name = $Tag; target_commitish = $Commit; name = "CodexStatusbar $Tag - Windows x64"
        body = $notes; draft = $true; prerelease = $manifest.Prerelease
    }
}
# A retry may reuse matching assets in this workflow's draft, but never deletes or replaces assets.
foreach ($name in $expected) {
    $remote = @($release.assets | Where-Object { $_.name -ceq $name })
    if ($remote.Count) { Assert-RemoteAsset $remote[0]; continue }
    & gh release upload $Tag (Join-Path $assets $name) --repo $repo
    if ($LASTEXITCODE -ne 0) { throw "Asset upload failed: $name" }
}
$release = Invoke-ReleaseApi -Path "releases/$($release.id)"
if (-not $release.draft -or $release.tag_name -cne $Tag -or $release.target_commitish -cne $Commit -or
    $release.prerelease -ne $manifest.Prerelease -or @($release.assets).Count -ne $expected.Count -or
    -not $release.body.Contains($marker)) { throw 'Release draft changed during the upload.' }
foreach ($remote in $release.assets) { Assert-RemoteAsset $remote }
Assert-TagCommit
$published = Invoke-ReleaseApi -Path "releases/$($release.id)" -Method Patch -Body @{ draft = $false; prerelease = $manifest.Prerelease }
if ($published.draft -or $published.prerelease -ne $manifest.Prerelease) { throw 'Release publication did not complete.' }
Assert-TagCommit
Write-Host "Published: $($published.html_url)"
if ($env:GITHUB_STEP_SUMMARY) { "Published [$Tag]($($published.html_url)); $($expected.Count) assets verified." >> $env:GITHUB_STEP_SUMMARY }
