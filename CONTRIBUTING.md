# Contributing to EdMd

Thanks for helping improve EdMd! This guide covers the day-to-day workflow. For the
architecture and the rules that keep the app safe, read [`CLAUDE.md`](CLAUDE.md) first — it
is the source of truth for how the pieces fit together.

## Prerequisites

- **.NET 10 SDK**, the **WebView2 Runtime**, and (only for the MSIX) the **Windows 10/11 SDK**.
- See the [README "Build from source"](README.md#build-from-source) section.

## Workflow (pull requests)

1. **Branch.** Maintainers branch directly; external contributors fork. Use a topic branch:
   `git switch -c fix/save-encoding` (never commit straight to `main`).
2. **Change + test.** Keep changes focused. Run the suite locally before pushing:
   ```powershell
   dotnet test        # from repo root or src/EdMd.Tests
   ```
3. **Open a PR against `main`.** Describe *what* and *why*. Link any issue.
4. **CI runs automatically.** [`ci.yml`](.github/workflows/ci.yml) builds and tests on
   `windows-latest` for every PR — it must be **green** before merge.
5. **Review → squash-merge.** Keep `main` releasable at all times.

> Tip: enabling branch protection on `main` (require the CI check + a review) is recommended
> so nothing merges red. Ask a maintainer if you'd like it turned on.

## What to keep in mind

These come from [`CLAUDE.md`](CLAUDE.md) — PRs that break them will be asked to change:

- **The C# ⇄ JS bridge is two-sided.** Any new file/disk action must be wired in **both**
  `MainWindow.xaml.cs` (the message `switch`) *and* `wwwroot/app.js` (the `send(...)` calls),
  and implemented on **both** host objects (desktop bridge + browser File System Access).
- **Keep the security guards.** Origin pinning to `EdMd.local`, the `e.Source` check before
  disk I/O, the CSP + `script-src 'self'` (no inline `<script>`), and the token-gated loopback
  server. Don't loosen these.
- **Toast UI Editor stays vendored** under `wwwroot/vendor/toastui/` — never load it from a
  CDN (it runs in the file-writing WebView2). Upgrades re-vendor the files + bump the version
  note in `index.html`.
- **Add tests for security-sensitive logic** (see `src/EdMd.Tests/` — `LocalWebServer`,
  `AtomicFile`). GUI/JS isn't unit-tested; call out manual verification in the PR.
- **Never commit secrets.** `certs/*.pfx` and passwords stay out of git (see
  [`.gitignore`](.gitignore) and [`certs/README.md`](certs/README.md)).
- Don't "fix" the intentional `EdMd.csproj` / `AssemblyName=EdMd` naming — installer and
  manifest depend on the `EdMd.exe` output name.

## Commit messages

Short, imperative, optionally prefixed by area/type — e.g. `fix: preserve UTF-8 BOM on save`,
`ci: bump actions to current majors`. One logical change per commit where practical.

## Releasing

Cutting releases is a maintainer task driven entirely by git tags — see
[`RELEASING.md`](RELEASING.md).
