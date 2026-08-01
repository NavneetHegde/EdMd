# Releasing EdMd

Releases are **tag-driven**. Pushing a `vX.Y.Z` tag is the *only* action needed — the
[`release.yml`](.github/workflows/release.yml) workflow builds, signs, and publishes
everything. Nothing in the repo stores a version number to edit; **the tag is the single
source of truth**.

## Versioning policy (SemVer)

Version = `MAJOR.MINOR.PATCH`, chosen by the maintainer when tagging:

| Bump | Tag example | When |
|------|-------------|------|
| **PATCH** | `v0.1.0 → v0.1.1` | Bug fixes only; no new features or behavior changes. |
| **MINOR** | `v0.1.1 → v0.2.0` | New features / capabilities, backward-compatible. |
| **MAJOR** | `v0.x → v1.0.0` | First stable release, or a breaking change (e.g. to file handling or associations). |

Pre-1.0 (`0.y.z`) means the app is still stabilizing; per SemVer, a `0.x` **minor** bump may
include breaking changes. Move to `v1.0.0` when you consider the format/behavior stable.

The tag maps to a 4-part MSIX version automatically: `v0.2.0` → `0.2.0.0`. **Each release
must have a strictly higher version than the last** — `Add-AppxPackage` only treats a build
as an in-place upgrade if the version increases *and* it's signed by the same publisher.

## Cut a release

1. Make sure `main` is green (CI passing) and has everything you want to ship.
2. Tag and push:
   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. That's it. `release.yml` runs on the tag and:
   - runs the tests (a red test **blocks** the release),
   - publishes the portable single-file exe `EdMd-<ver>-win-x64.exe` and a zipped copy
     `EdMd-<ver>-win-x64.zip` (the exe + `LICENSE.txt`),
   - builds the MSIX via `src/EdMd.Installer/build.ps1`, **signs** it, and
   - creates a **GitHub Release** for the tag with the assets + auto-generated notes.

### Dry run (no public release)

To rehearse without publishing: **Actions → Release → Run workflow** (or
`gh workflow run release.yml -f version=0.2.0`). This uploads the exe + MSIX as *run
artifacts* only — no Release is created.

## Signing

The MSIX is signed with the committed **`CN=EdMd` self-signed** certificate. CI reads the key
from the secrets `SELFSIGN_PFX_BASE64` and `SELFSIGN_PFX_PASSWORD`; the public `.cer` ships
with the release so users can trust it once (the step is in the README).

Because the certificate isn't publicly trusted, that one-time import is required before the
MSIX will install. Moving to a publicly-trusted code-signing certificate would remove it: the
signing step in [`release.yml`](.github/workflows/release.yml) is a single `build.ps1` call, and
`Identity/Publisher` in the manifest must equal the new certificate's Subject exactly.

> Losing/rotating the signing cert breaks in-place upgrades for existing installs (they'd
> have to uninstall first). Keep an offline backup of `certs/edmd-codesign.pfx` + password.

## After publishing

- Check the [release page](https://github.com/NavneetHegde/EdMd/releases) has all the
  assets (`.exe`, `.zip`, `.msix`, and — in `selfsigned` mode — `.cer`).
- Smoke-test an upgrade: `Add-AppxPackage EdMd-<newver>.msix` over a previous install.
