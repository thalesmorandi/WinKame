# Microsoft Store package (MSIX)

The Store build is the same app as the installer, packaged as MSIX. Microsoft signs it when it is published, so
people who install from the Store don't see the SmartScreen warning.

What is different from the `.exe` installer:

- **Start with Windows** uses the package's startup task (on by default, like the installer). People can turn it off
  in WinKame's settings or in Windows Settings › Apps › Startup.
- **No Explorer context menu and no Send To.** A packaged app's registry and Send To folder are private to it, so
  Explorer would never see them. The settings hide those options in the Store build. The wheel works the same.
- Settings and logs live in the package's private copy of `%APPDATA%\WinKame`; Windows removes them on uninstall.

## Build

```powershell
./scripts/build-msix.ps1 -Version 0.1.1 -IdentityName <Package/Identity/Name> -Publisher "<Package/Identity/Publisher>"
```

Output: `artifacts/WinKame-<version>-x64.msix`. It needs the Windows SDK (MakeAppx, MakePri).

To try it on your own PC without the Store, turn on Developer Mode (Settings › System › For developers) and run:

```powershell
Add-AppxPackage -Register artifacts\msix-layout\AppxManifest.xml
```

Remove it with `Get-AppxPackage *WinKame* | Remove-AppxPackage`.

## First submission (once)

1. Sign in to [Partner Center](https://partner.microsoft.com/dashboard/apps-and-games/overview) with a Microsoft
   account and register as an individual developer (free).
2. **Apps and games › New product › MSIX or PWA app**, reserve the name **WinKame**.
3. Open **Product management › Product identity** and copy `Package/Identity/Name`, `Package/Identity/Publisher` and
   `Package/Properties/PublisherDisplayName`.
4. In GitHub, **Settings › Secrets and variables › Actions › Variables**, add:
   - `MSIX_IDENTITY_NAME` = Package/Identity/Name
   - `MSIX_PUBLISHER` = Package/Identity/Publisher (starts with `CN=`)
   - `MSIX_PUBLISHER_DISPLAY_NAME` = Package/Properties/PublisherDisplayName
5. Run the **Release** workflow again for the latest tag (Actions › Release › Run workflow). The run keeps the package
   as the artifact **WinKame-msix-store**; download it and unzip it.
6. Start a submission and fill in:
   - **Packages:** upload the `.msix`.
   - **Properties:** category Utilities & tools; privacy policy URL
     `https://github.com/thalesmorandi/WinKame/blob/main/CODE_SIGNING_POLICY.md#privacy` (or the site's page).
   - **Age ratings:** the questionnaire (no user content, no purchases, no network features).
   - **Store listings:** English and Portuguese (Brazil) description and screenshots.
   - **Submission options › Notes for certification:** explain the `runFullTrust` capability, for example:
     *"WinKame is a desktop file converter. It uses low-level mouse and keyboard hooks only to notice when the user is
     dragging a file and taps a key, then opens a conversion wheel at the cursor. No keystrokes are recorded or sent
     anywhere; all conversions run locally (FFmpeg, ImageMagick, Office if installed)."*
7. Submit. Certification usually takes a few days.

For later versions, push the tag as usual, download **WinKame-msix-store** from that Release run and upload it in a
new submission.
