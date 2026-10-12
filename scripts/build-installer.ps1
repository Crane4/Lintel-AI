<#
  Builds the single-file exe and wraps it in an installer.
  Usage:  powershell -ExecutionPolicy Bypass -File scripts/build-installer.ps1
  Needs:  .NET 8 SDK and Inno Setup 6 (ISCC.exe).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$csproj = Join-Path $root 'src/LintelAI/LintelAI.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $csproj" }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php' }

Write-Host "Publishing Lintel AI $version ..."
dotnet publish src/LintelAI -p:PublishProfile=win-x64 -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host 'Building installer ...'
& $iscc "/DAppVersion=$version" 'installer/LintelAI.iss'
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

Write-Host "Done: artifacts/installer/LintelAI-Setup-$version.exe"
