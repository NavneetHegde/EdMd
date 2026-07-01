# EdMd.Installer (MSIX)

Builds a signed **MSIX** package for EdMd that installs the app and registers
`.md` / `.markdown` file associations.

This is a script-driven package (Windows SDK `MakeAppx` + `SignTool`), not a Visual
Studio `.wapproj`, so it builds from the command line with no IDE.

## Prerequisites

- **.NET SDK** (matches the app — .NET 10).
- **Windows 10/11 SDK** — provides `MakeAppx.exe` and `SignTool.exe`. `build.ps1`
  auto-discovers them under `C:\Program Files (x86)\Windows Kits\10\bin\…\x64\`,
  preferring stable SDKs over Insider/preview builds (whose `MakeAppx` has a bug that
  rejects valid full-trust manifests).
- A **code-signing certificate** (`.pfx`) for distribution. Windows will not install an
  unsigned MSIX.

## Build (production)

```powershell
./build.ps1 -PfxPath C:\certs\edmd.pfx -PfxPassword '<password>' -Version 1.0.0.0
```

Output: `Output/EdMd-1.0.0.0.msix`.

### How Publisher matching works

An MSIX only installs if `Identity/Publisher` in the manifest **exactly equals** the
signing certificate's Subject. You don't edit the manifest for this — `build.ps1` reads
the Subject from your `.pfx` and injects it into the staged `AppxManifest.xml` before
packing, then signs with a SHA-256 timestamp (`-TimestampUrl`, default DigiCert).

For **Microsoft Store** submission, also set `Identity/@Name` in `Package.appxmanifest`
to the identity reserved in Partner Center, and pass `-Publisher` matching it.

## Build (local dev, self-signed)

```powershell
./build.ps1 -Sign
```

Signs with a throwaway `CN=EdMd Dev` cert and exports `Output/EdMd-Dev.cer`. Trust it
once (elevated PowerShell) so the package will install:

```powershell
Import-Certificate -FilePath Output\EdMd-Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

## Install / uninstall

```powershell
Add-AppxPackage    Output\EdMd-1.0.0.0.msix
Get-AppxPackage    *EdMd*            # find the full PackageFullName
Remove-AppxPackage EdMd.MarkdownEditor_1.0.0.0_x64__<hash>
```

After install, EdMd appears in the Start menu, and double-clicking a `.md`/`.markdown`
file (or right-click → Open with → EdMd) launches it with that file. The path is passed
to the app as `"%1"` via the manifest's file-type association and read by
`MainWindow.OnNavigationCompleted` from `Environment.GetCommandLineArgs()`.

## Verify a signature

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe" verify /pa /v Output\EdMd-1.0.0.0.msix
```

## Layout

| File | Purpose |
|------|---------|
| `build.ps1` | Publish → stage → pack → sign. Params: `-PfxPath`, `-PfxPassword`, `-Publisher`, `-Version`, `-TimestampUrl`, `-Sign`. |
| `Package.appxmanifest` | App identity, visual assets, `.md`/`.markdown` association, `runFullTrust`. Publisher/Version are placeholders overwritten at build time. |
| `Assets/` | Tile/logo PNGs (Square44/150, Wide310x150, StoreLogo). |
| `obj/stage/`, `Output/` | Build intermediates and the finished `.msix` (git-ignore these). |

## Notes

- The app is published **self-contained** (`--self-contained`), so target machines need
  no .NET runtime. The **WebView2 Evergreen runtime** is still assumed present (it is on
  most Windows 10/11 machines; Windows offers to install it otherwise).
- Bump `-Version` for every release you distribute; `Add-AppxPackage` treats a higher
  version as an in-place upgrade.
