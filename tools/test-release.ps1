# Exercise release rejection paths without credentials or network requests.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = [IO.Path]::GetFullPath($PackageDirectory, $root)
if (-not $source.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test packages must be inside the repository.'
}
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Raw | ConvertFrom-Json
$publisher = Join-Path $PSScriptRoot 'publish-release.ps1'
$arguments = @{ Tag = $manifest.Tag; Commit = $manifest.SourceCommit; ValidateOnly = $true }
& $publisher -PackageDirectory $source @arguments
$checks = 1
function Expect-Failure {
    param([scriptblock]$Action, [string]$Pattern)
    try { & $Action }
    catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        Write-Host "PASS: rejected $Pattern"
        return
    }
    throw "Expected a rejection: $Pattern"
}
Expect-Failure { & $publisher -PackageDirectory $source -Tag v999.0.0 -Commit $manifest.SourceCommit -ValidateOnly } 'manifest does not match'
$checks++
Expect-Failure { & (Join-Path $PSScriptRoot 'prepare-release.ps1') -Tag 'v1.2.3;invalid' } 'Expected vMAJOR'
$checks++

$testRoot = Join-Path (Split-Path -Parent $source) ('release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $copy = Join-Path $testRoot 'package'
    Copy-Item -LiteralPath $source -Destination $copy -Recurse
    $copyManifestPath = Join-Path $copy 'manifest.json'
    $utf8 = [Text.UTF8Encoding]::new($false)
    function Update-TestManifest {
        $copyManifest = Get-Content -LiteralPath $copyManifestPath -Raw | ConvertFrom-Json
        foreach ($asset in $copyManifest.Assets) {
            $file = Join-Path $copy "assets/$($asset.Name)"
            $asset.Size = (Get-Item -LiteralPath $file).Length
            $asset.SHA256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        [IO.File]::WriteAllText($copyManifestPath, ($copyManifest | ConvertTo-Json -Depth 5), $utf8)
    }
    # A matching transport hash is insufficient if an original upstream license was modified.
    [IO.File]::AppendAllText((Join-Path $copy 'assets/LICENSE'), "`nmodified", $utf8)
    Update-TestManifest
    Expect-Failure { & $publisher -PackageDirectory $copy @arguments } 'Upstream license or attribution changed'
    $checks++
    Copy-Item -LiteralPath (Join-Path $source 'assets/LICENSE') -Destination (Join-Path $copy 'assets/LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination $copyManifestPath -Force

    # Recompute the outer hashes: an omitted ZIP license must still block publication.
    $zipName = "CodexStatusbar-$($manifest.Tag)-win-x64.zip"
    $zipPath = Join-Path $copy "assets/$zipName"
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
    try { $zip.GetEntry('LICENSE').Delete() }
    finally { $zip.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines = Get-Content -LiteralPath (Join-Path $copy 'assets/SHA256SUMS.txt')
    $lines = $lines | ForEach-Object { if ($_.EndsWith("  $zipName")) { "$zipHash  $zipName" } else { $_ } }
    [IO.File]::WriteAllText((Join-Path $copy 'assets/SHA256SUMS.txt'), ($lines -join "`n") + "`n", $utf8)
    Update-TestManifest
    Expect-Failure { & $publisher -PackageDirectory $copy @arguments } 'ZIP content count|Required ZIP entry'
    $checks++
}
finally {
    # Only remove this invocation's freshly created directory, after checking its absolute scope.
    $cleanup = [IO.Path]::GetFullPath($testRoot)
    if (-not $cleanup.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $cleanup) -notmatch '^release-tests-[0-9a-f]{32}$') { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $cleanup -Recurse -Force
}

# Shadow the only network entry points in this test scope. No real token or API is used.
$previousToken = $env:GH_TOKEN
$previousRepo = $env:GITHUB_REPOSITORY
$previousSummary = $env:GITHUB_STEP_SUMMARY
function gh {
    if ($model.Mode -ne 'first-release' -or $args[0] -ne 'release' -or $args[1] -ne 'upload' -or
        $args[2] -cne $manifest.Tag -or -not (Test-Path -LiteralPath $args[3])) { throw 'Unexpected test gh invocation.' }
    $model.Uploads++
}
function Invoke-RestMethod {
    param($Uri, $Method, $Headers, $TimeoutSec, $MaximumRedirection, $Body, $ContentType)
    if (-not $Uri.StartsWith('https://api.github.com/repos/release-test/release-test/')) { throw 'Unexpected test API address.' }
    if ($Uri -match '/git/ref/tags/') {
        $model.TagReads++
        $sha = if ($model.Mode -eq 'moved-tag' -and $model.TagReads -gt 1) { '0' * 40 } else { $manifest.SourceCommit }
        return [pscustomobject]@{ object = [pscustomobject]@{ type = 'commit'; sha = $sha } }
    }
    if ($model.Mode -eq 'first-release' -and $Uri -match '/releases/tags/') {
        $missing = [Exception]::new('Mock HTTP 404')
        $missing | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = [Net.HttpStatusCode]::NotFound })
        throw $missing
    }
    $remoteAssets = @($manifest.Assets | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; size = $_.Size; state = 'uploaded'; digest = "sha256:$($_.SHA256)" }
    })
    if ($model.Mode -eq 'bad-digest') { $remoteAssets[0].digest = 'sha256:' + ('0' * 64) }
    $release = [pscustomobject]@{
        id = 123; tag_name = $manifest.Tag; target_commitish = $manifest.SourceCommit
        draft = ($model.Mode -ne 'published'); prerelease = $manifest.Prerelease
        body = "<!-- codex-statusbar-release:$($manifest.SourceCommit) -->"; assets = $remoteAssets
        html_url = 'https://github.com/release-test/release-test/releases/tag/test'
    }
    if ($Method -eq 'Post') {
        $request = $Body | ConvertFrom-Json
        if ($model.Mode -ne 'first-release' -or -not $request.draft -or $request.tag_name -cne $manifest.Tag -or
            $request.target_commitish -cne $manifest.SourceCommit -or $request.prerelease -ne $manifest.Prerelease) {
            throw 'Invalid draft creation request.'
        }
        $model.Created = $true
        $release.assets = @()
    }
    elseif ($Method -eq 'Patch') {
        if ($model.Mode -eq 'first-release' -and $model.Uploads -ne $manifest.Assets.Count) { throw 'Draft published before all uploads.' }
        $model.Published = $true
        $release.draft = $false
    }
    elseif ($Method -ne 'Get') { throw 'Unexpected test API mutation.' }
    return $release
}
try {
    $env:GH_TOKEN = 'release-test-placeholder'
    $env:GITHUB_REPOSITORY = 'release-test/release-test'
    $env:GITHUB_STEP_SUMMARY = $null
    foreach ($case in @(
        @{ Mode = 'bad-digest'; Error = 'Remote asset verification failed' },
        @{ Mode = 'moved-tag'; Error = 'Remote tag no longer points' },
        @{ Mode = 'published'; Error = 'already published' }
    )) {
        $model = [pscustomobject]@{ Mode = $case.Mode; Published = $false; TagReads = 0 }
        Expect-Failure { & $publisher -PackageDirectory $source -Tag $manifest.Tag -Commit $manifest.SourceCommit } $case.Error
        if ($model.Published) { throw 'An invalid release was published.' }
        $checks++
    }
    $model = [pscustomobject]@{ Mode = 'valid'; Published = $false; TagReads = 0 }
    & $publisher -PackageDirectory $source -Tag $manifest.Tag -Commit $manifest.SourceCommit
    if (-not $model.Published -or $model.TagReads -ne 3) { throw 'The valid release did not pass all publication checks.' }
    $checks++
    $model = [pscustomobject]@{ Mode = 'first-release'; Published = $false; TagReads = 0; Created = $false; Uploads = 0 }
    & $publisher -PackageDirectory $source -Tag $manifest.Tag -Commit $manifest.SourceCommit
    if (-not $model.Created -or -not $model.Published -or $model.Uploads -ne 7 -or $model.TagReads -ne 3) {
        throw 'First-time release creation and upload checks did not complete.'
    }
    $checks++
}
finally {
    $env:GH_TOKEN = $previousToken
    $env:GITHUB_REPOSITORY = $previousRepo
    $env:GITHUB_STEP_SUMMARY = $previousSummary
}
Write-Host "Release validation tests: $checks passed."
