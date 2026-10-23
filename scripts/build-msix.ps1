# WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
# SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.
# Builds artifacts\WinKame-<version>-x64.msix for the Microsoft Store:
#   1. publishes WinKame self-contained for x64 (or reuses artifacts\publish with -SkipPublish),
#   2. lays it out with packaging\msix (manifest + logos) and indexes the logos with MakePri,
#   3. packs it with MakeAppx.
# The package is unsigned on purpose: the Store signs it. To install it locally, turn on Developer Mode and run
#   Add-AppxPackage -Register artifacts\msix-layout\AppxManifest.xml
# Identity values must match Partner Center (Product management > Product identity) for Store uploads.
# Requires the Windows 10/11 SDK (MakeAppx.exe, MakePri.exe), present on GitHub's windows-latest runners.
param(
    [string]$Version = "",
    [string]$IdentityName = "WinKame.Dev",
    [string]$Publisher = "CN=WinKame Dev",
    [string]$PublisherDisplayName = "morandi.cloud",
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $csproj = [IO.File]::ReadAllText((Join-Path $root "src\WinKame\WinKame.csproj"))
    $Version = [regex]::Match($csproj, "<Version>([^<]+)</Version>").Groups[1].Value
}
# The Store wants four parts and a zero revision: 0.1.1 -> 0.1.1.0
$parts = @($Version.Split('-')[0].Split('.') | ForEach-Object { [int]$_ })
while ($parts.Count -lt 3) { $parts += 0 }
$packageVersion = "{0}.{1}.{2}.0" -f $parts[0], $parts[1], $parts[2]
Write-Host "WinKame $Version (package $packageVersion)"

& (Join-Path $PSScriptRoot "fetch-ffmpeg.ps1")

$publish = Join-Path $root "artifacts\publish"
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publish "WinKame.exe"))) { throw "artifacts\publish is empty; run without -SkipPublish" }
} else {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish src\WinKame -c Release -r win-x64 --self-contained true -o $publish -p:Version=$Version -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

$sdkBin = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin") -Directory -Filter "10.*" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName "x64" } |
    Where-Object { Test-Path (Join-Path $_ "makeappx.exe") } |
    Select-Object -First 1
if (-not $sdkBin) { throw "Windows SDK not found (MakeAppx.exe). Install it with: winget install Microsoft.WindowsSDK.10.0.26100" }
$makeappx = Join-Path $sdkBin "makeappx.exe"
$makepri = Join-Path $sdkBin "makepri.exe"

$src = Join-Path $root "packaging\msix"
$layout = Join-Path $root "artifacts\msix-layout"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $publish "*") $layout -Recurse
Copy-Item (Join-Path $src "Assets") $layout -Recurse

$manifest = [IO.File]::ReadAllText((Join-Path $src "AppxManifest.xml"))
$manifest = $manifest.Replace('$IdentityName$', $IdentityName).Replace('$Publisher$', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($PublisherDisplayName)).Replace('$Version$', $packageVersion)
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object Text.UTF8Encoding $false))

# Index only the logos (not the hundreds of .NET files) so Windows picks the right size for each place.
$pri = Join-Path $root "artifacts\msix-pri"
if (Test-Path $pri) { Remove-Item $pri -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $pri "res") | Out-Null
Copy-Item (Join-Path $src "Assets") (Join-Path $pri "res") -Recurse
$priConfig = Join-Path $pri "priconfig.xml"
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakePri createconfig failed" }
# Keep every logo size in the main package (the default config moves them to resource packs, which need a bundle).
$cfg = [xml][IO.File]::ReadAllText($priConfig)
$cfg.SelectNodes("//packaging") | ForEach-Object { [void]$_.ParentNode.RemoveChild($_) }
$cfg.Save($priConfig)
& $makepri new /pr (Join-Path $pri "res") /cf $priConfig /mn (Join-Path $layout "AppxManifest.xml") /of (Join-Path $layout "resources.pri") /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakePri failed" }

$msix = Join-Path $root "artifacts\WinKame-$Version-x64.msix"
& $makeappx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed" }
Write-Host ("MSIX: {0} ({1:N0} MB)" -f $msix, ((Get-Item $msix).Length / 1MB))
