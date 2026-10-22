# WinKame

Zero-click file converter for Windows 10 and 11. Drag any file, tap a key, and drop it on a slice of the wheel:

| Key | Wheel |
| --- | --- |
| <kbd>Shift</kbd> | Convert formats |
| <kbd>Ctrl</kbd> | Tools (compress, crop, trim, split, PDF pages…) |
| <kbd>Alt</kbd> | File actions (copy, move, path, terminal, shelf, ZIP…) |
| <kbd>Space</kbd> | Your custom actions |

Everything runs offline on the PC. The interface is in English and Portuguese and follows the Windows language.

## Layout

```
src/WinKame.Core     Formats, conversion engines, settings, localization (no UI)
src/WinKame          WPF app: drag watcher, wheel, dialogs, tray, Explorer integration
tests/WinKame.Tests  xUnit tests
installer/           Inno Setup script (per-user install, no admin)
scripts/             fetch-ffmpeg.ps1, build-installer.ps1
site/                Landing page (static HTML/CSS/JS)
```

## Build and run

Requires the .NET 9 SDK.

```powershell
.\scripts\fetch-ffmpeg.ps1          # downloads the bundled FFmpeg into src\WinKame\tools\ffmpeg (once)
dotnet build
dotnet test
dotnet run --project src\WinKame
```

Useful environment variables while developing:

- `WINKAME_HOME`: folder for settings and logs (keeps a dev copy apart from the installed one)
- `WINKAME_DEBUG=1`: writes `debug.log`
- `WINKAME_LANG=pt|en`: forces the interface language
- `WINKAME_NO_OFFICE=1`: never uses Microsoft Office for documents

Command line:

```powershell
WinKame.exe --convert file1.png file2.png --to webp
WinKame.exe --snapshot settings out.png             # renders a window to PNG off-screen
```

## Installer

Requires Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
.\scripts\build-installer.ps1       # -> artifacts\WinKame-Setup-<version>.exe
```

The version comes from `<Version>` in `src/WinKame/WinKame.csproj`. The installer is not code-signed yet.

## Site

`site/` is a static page with no build step. The download link and version are set in `CONFIG` at the top of `site/js/main.js`. Preview it with:

```powershell
python -m http.server 8765 --directory site
```

## Licenses

The bundled FFmpeg build is GPLv3. Other third-party licenses are listed in `THIRD-PARTY-NOTICES.txt`.
