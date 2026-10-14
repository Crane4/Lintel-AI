<#
  Builds a Velopack release: the Setup.exe people install with, and the packages the app updates itself from.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts/release.ps1            # build only, nothing is uploaded
    $env:GITHUB_TOKEN = '<token with repo scope>'
    powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Upload    # also create a DRAFT GitHub release
    powershell -ExecutionPolicy Bypass -File scripts/release.ps1 -Upload -Publish   # ... and publish it

  The version is the <Version> in src/LintelAI/LintelAI.csproj. Bump it first: a release can never
  reuse a version that already exists, or installed copies will not see it as newer.

  Needs: the .NET 8 SDK. The packager (vpk) is a pinned local tool, restored automatically.
#>
param(
    [switch]$Upload,
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$repoUrl = 'https://github.com/Crane4/Lintel-AI'
$csproj = Join-Path $root 'src/LintelAI/LintelAI.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj" }

$publishDir = Join-Path $root 'artifacts/win-x64'
$outDir = Join-Path $root 'artifacts/velopack'

if ($Upload -and -not $env:GITHUB_TOKEN) {
    throw 'Set $env:GITHUB_TOKEN to a GitHub token with "repo" scope before using -Upload.'
}

Write-Host "Restoring the packager ..."
dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

Write-Host "Publishing Lintel AI $version ..."
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src/LintelAI -p:PublishProfile=win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

New-Item -ItemType Directory -Force $outDir | Out-Null

# The previous release lets Velopack build a small "delta" package instead of making everyone
# download the whole app again. The first Velopack release has none, so a failure here is fine.
Write-Host 'Fetching the previous release (for delta packages) ...'
dotnet vpk download github --repoUrl $repoUrl --outputDir $outDir 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Host '  none found, building a full package only.' }

Write-Host "Packing $version ..."
dotnet vpk pack `
    --packId LintelAI `
    --packVersion $version `
    --packDir $publishDir `
    --runtime win-x64 `
    --mainExe LintelAI.exe `
    --packTitle 'Lintel AI' `
    --packAuthors 'Crane4' `
    --icon (Join-Path $root 'src/LintelAI/Assets/icon.ico') `
    --outputDir $outDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Host ''
Write-Host "Built into $outDir :"
Get-ChildItem $outDir | Where-Object { $_.Name -like "*$version*" -or $_.Name -like 'releases.*' -or $_.Name -like '*Setup*' } |
    ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }

if ($Upload) {
    Write-Host ''
    Write-Host "Uploading to $repoUrl as v$version ..."
    $args = @(
        'vpk', 'upload', 'github',
        '--repoUrl', $repoUrl,
        '--outputDir', $outDir,
        '--token', $env:GITHUB_TOKEN,
        '--tag', "v$version",
        '--releaseName', "Lintel AI $version"
    )
    if ($Publish) { $args += '--publish' }

    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }

    if ($Publish) { Write-Host "Published v$version." }
    else { Write-Host "Created a DRAFT release v$version. Review it on GitHub, then publish it." }
}
else {
    Write-Host 'Nothing was uploaded. Add -Upload to create the GitHub release.'
}
