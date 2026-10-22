# WinKame, a morandi.cloud product. Copyright (C) 2026 Thales de Oliveira Morandi.
# SPDX-License-Identifier: GPL-3.0-or-later. See LICENSE and NOTICE.
# Downloads the FFmpeg build that WinKame ships with and places it in src/WinKame/tools/ffmpeg.
# The binary is not kept in git; run this once before building a release.
#
# Source: gyan.dev "essentials" build (GPLv3), the Windows build linked from ffmpeg.org,
# published on GitHub at GyanD/codexffmpeg.
param(
    [string]$Version = "9.0.2",
    [string]$Sha256 = "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "src\WinKame\tools\ffmpeg"
$name = "ffmpeg-$Version-essentials_build"
$url = "https://github.com/GyanD/codexffmpeg/releases/download/$Version/$name.zip"

if (Test-Path (Join-Path $dest "ffmpeg.exe")) {
    Write-Host "FFmpeg already present in $dest"
    exit 0
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("winkame-ffmpeg-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp "$name.zip"
    Write-Host "Downloading $url"
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($Sha256 -ne "" -and $hash -ne $Sha256) {
        throw "SHA-256 mismatch: expected $Sha256, got $hash"
    }
    Write-Host "SHA-256 $hash"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        New-Item -ItemType Directory -Force $dest | Out-Null
        foreach ($entry in $archive.Entries) {
            $file = switch -Wildcard ($entry.FullName) {
                "*/bin/ffmpeg.exe" { "ffmpeg.exe" }
                "*/LICENSE" { "LICENSE.txt" }
                "*/README.txt" { "README.txt" }
                default { $null }
            }
            if ($file) {
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $dest $file), $true)
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "FFmpeg $Version ready in $dest"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
