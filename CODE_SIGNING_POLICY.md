# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

WinKame is free software under the [GPL-3.0-or-later](LICENSE), developed in the open at
[github.com/thalesmorandi/WinKame](https://github.com/thalesmorandi/WinKame).

## What is signed

Only binaries built from this repository's source code are signed:

| File | Where it comes from |
| --- | --- |
| `WinKame-Setup-x64.exe` | The installer, built by Inno Setup from [`installer/WinKame.iss`](installer/WinKame.iss) |
| `WinKame.exe`, `WinKame.dll`, `WinKame.Core.dll` | The app, built from [`src/`](src) |

Third-party components shipped with WinKame (the .NET runtime, FFmpeg, ImageMagick and the other libraries listed in
[`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt)) are included as published by their own projects and are **not**
signed by WinKame.

The Microsoft Store build (MSIX, see [`packaging/msix`](packaging/msix/README.md)) is built by the same workflow from
the same signed app files and is signed by Microsoft when it is published in the Store.

## How releases are built and signed

1. A release starts when a maintainer pushes a version tag (`vX.Y.Z`).
2. GitHub Actions builds everything from that tag on GitHub-hosted runners, using
   [`.github/workflows/release.yml`](.github/workflows/release.yml). No binary is built on a personal computer.
3. The workflow sends the unsigned files to SignPath, which checks that they come from this repository's workflow.
4. A maintainer with the approver role reviews and **manually approves every signing request**.
5. The signed files are published on the [Releases page](https://github.com/thalesmorandi/WinKame/releases),
   together with `SHA256SUMS.txt`.

The signing key is held by SignPath in a hardware security module. Maintainers never have access to it.

## Team roles

| Role | Members |
| --- | --- |
| Authors (commit without further review) | [Thales de Oliveira Morandi](https://github.com/thalesmorandi) |
| Reviewers (review every change from other contributors) | [Thales de Oliveira Morandi](https://github.com/thalesmorandi) |
| Approvers (approve each signing request) | [Thales de Oliveira Morandi](https://github.com/thalesmorandi) |

All team members use multi-factor authentication for GitHub and SignPath.
Pull requests from outside contributors are merged only after review by a reviewer.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user
or the person installing or operating it.

Everything WinKame converts is processed on the user's computer. It has no telemetry, no accounts and no automatic
update checks. Network access only happens when the user asks for it, for example:

- **Install with winget** (Settings › Tools): runs Windows Package Manager to download an optional tool
  (LibreOffice or Ghostscript). See the [winget privacy statement](https://learn.microsoft.com/windows/package-manager/winget/#privacy).
- **Share** (file actions wheel): opens the Windows share sheet; what happens next depends on the app the user picks.
- **Download the WebView2 Runtime** (Settings › Tools, only when it is missing): opens Microsoft's download page in the browser.

The PDF engine (Microsoft Edge WebView2) runs with scripts disabled and every network request blocked. WebView2 is
part of Windows; any diagnostic data it sends to Microsoft follows the Windows settings and the
[Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement).

## Verifying a download

Right-click the file › **Properties** › **Digital Signatures**: the signer is **SignPath Foundation**.
Or in PowerShell:

```powershell
Get-AuthenticodeSignature .\WinKame-Setup-x64.exe
Get-FileHash .\WinKame-Setup-x64.exe -Algorithm SHA256   # compare with SHA256SUMS.txt
```

## Reporting a problem

If a signed file behaves suspiciously, don't run it. Open an issue at
[github.com/thalesmorandi/WinKame/issues](https://github.com/thalesmorandi/WinKame/issues) with the file name,
its SHA-256 hash and where you downloaded it. You can also report it to SignPath Foundation at
[signpath.org](https://signpath.org).
