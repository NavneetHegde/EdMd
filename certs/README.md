# Code-signing certificate (EdMd)

This folder holds the certificate used to sign the production MSIX
(`src/EdMd.Installer/Output/EdMd-*.msix`). **Do not commit the `.pfx` or the
password** — the private key must stay private. Keep an offline backup of both;
losing them means you can no longer ship trusted upgrades (see below).

## Current cert

| Field | Value |
|-------|-------|
| PFX (private key)  | `edmd-codesign.pfx` |
| CER (public key)   | `edmd-codesign.cer` |
| Subject / Publisher| `CN=EdMd` |
| Thumbprint         | `6DB652B6B616CE004035DAB4BEA4891368E16918` |
| Type               | Self-signed (code signing), **not** CA-issued |
| Created / Expires  | 2026-07-01 / **2029-07-01** |
| Password           | stored separately (password manager) — **not** in this repo |

## How it's used

Production build (from `src/EdMd.Installer/`):

```powershell
./build.ps1 -PfxPath ..\..\certs\edmd-codesign.pfx -PfxPassword '<pwd>' -Version 1.0.0.0
```

`build.ps1` derives the package `Publisher` from this cert's Subject and injects it
into the manifest — the two **must** match or Windows refuses to install.

## Two rules that bite

1. **Same cert for every upgrade.** `Add-AppxPackage` only treats a new build as an
   in-place upgrade if it's signed by the *same* Publisher. Re-issue or lose this cert
   and existing installs can't upgrade — users must uninstall first. Bump `-Version`
   each release.

2. **Self-signed ⇒ each machine must trust it first** (elevated PowerShell), because
   the cert doesn't chain to a public root:

   ```powershell
   Import-Certificate -FilePath edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\Root
   Import-Certificate -FilePath edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```

## Going fully public

For trust-free distribution (no per-machine import), replace this with a cert from a
public CA (DigiCert, Sectigo, …) or ship via the Microsoft Store. Run the same
`build.ps1 -PfxPath …` command with the CA-issued `.pfx`. For Store submission, also
set `Identity/@Name` in `Package.appxmanifest` to your reserved Partner Center name and
pass `-Publisher` to match it.
