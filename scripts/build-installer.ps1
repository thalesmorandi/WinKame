# WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
# SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.
# Builds artifacts\WinKame-Setup-<version>.exe:
#   1. downloads the bundled FFmpeg (once),
#   2. publishes WinKame self-contained for x64 (no .NET install needed on the target PC),
#   3. compiles installer\WinKame.iss with Inno Setup.
# Requires Inno Setup 6:  winget install JRSoftware.InnoSetup
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $csproj = [IO.File]::ReadAllText((Join-Path $root "src\WinKame\WinKame.csproj"))
    $Version = [regex]::Match($csproj, "<Version>([^<]+)</Version>").Groups[1].Value
}
Write-Host "WinKame $Version"

& (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1")

$publish = Join-Path $root "artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src\WinKame -c Release -r win-x64 --self-contained true -o $publish -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc /Qp "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root "installer\WinKame.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Join-Path $root "artifacts\WinKame-Setup-$Version.exe"
Write-Host ("Installer: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
