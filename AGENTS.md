# AGENTS.md

Guidance for AI coding assistants and human contributors working in this repo.

## Project overview

CloudReEdit is a WPF (.NET 8, `net8.0-windows`) desktop app that edits Steam
playtime stats stored by [CloudRedirect](https://github.com/Selectively11/CloudRedirect).
It keeps the three copies CloudRedirect maintains in sync:

1. `localconfig.vdf` under the Steam `userdata` folder
2. the local stats JSON at `{Steam}\cloud_redirect\stats\{accountId}\{appId}.json`
3. the cloud account blob at `{prefix}{accountId}/0/stats.json`

CloudRedirect's config and credentials are read (never modified) from
`%AppData%\CloudRedirect\` (`config.json` + DPAPI-encrypted `s3_credentials.json`).

## Layout

- `ui/` — the entire WPF app (`CloudReEdit.csproj`).
- `ui/Pages/` — `AccountsPage`, `GamesPage`, `PlaytimeEditorPage`.
- `ui/Services/` — app logic, ports of CloudRedirect behavior.
- `ui/Services/Cloud/` — cloud providers (`S3Provider` native SigV4, `FolderProvider`,
  `UnsupportedProvider` read-only stand-in) + `CloudProviderFactory`.
- `.github/workflows/build-release.yml` — `workflow_dispatch` CI build + release.

## Build

- Target: `net8.0-windows` (buildable with the .NET 10 SDK; runtime needed is .NET 8 Desktop).
- Build: `dotnet build ui/CloudReEdit.csproj`
- Single-file exe: `dotnet publish ui/CloudReEdit.csproj -c Release`
  (framework-dependent, ~6.8 MB, output under `ui/bin/Release/net8.0-windows/win-x64/publish/`).
- Publish settings (single-file, self-contained=false) are defaults in the csproj — don't override per build.

## Conventions

- NO code comments unless requested; keep code style consistent with existing files.
- WPF-UI 4.2.0 for controls; plain WPF types (e.g. `DataGridTextColumn`) are NOT in the `ui:` namespace.
- `System.Security.Cryptography.ProtectedData` is used for DPAPI decryption of cloud credentials.
- Don't touch the WPF-UI XAML namespace (`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`).

## Cloud storage semantics (important)

- `per_device` buckets are the source of truth; totals are derived (parser floors to `uint`).
- Merge is max-wins across `localconfig.vdf` and the cloud blob — all copies must be written together.
- `__migrated_localconfig` is the reconcile balance bucket; shrinking drains it before real devices.
- S3 / R2 are the only writable providers. gdrive / onedrive / local are read-only (`UnsupportedProvider`).
- Credentials JSON fields: `access_key_id`, `secret_access_key`, `bucket`, `key_prefix`, `endpoint`, `region`.
  `key_prefix` is normalized: strip leading `/`, ensure trailing `/`. Object key = `{prefix}{accountId}/0/stats.json`.
  Cloud blob is a JSON object mapping `appId` (string) -> app stats.

## Testing

- No test project in the repo. Verification is done via `dotnet build` and headless
  console harnesses (throwaway projects under the OS temp dir referencing `ui/CloudReEdit.csproj`).
- Never write to real Steam files or the live cloud blob during a test without an explicit user request —
  prefer in-memory dry-runs and temp copies of the VDF.
- Steam must be closed before any real save test.

## Releases

- Publish via the GitHub Actions `workflow_dispatch` workflow; it creates a tag `v<version>`
  and attaches the built exe to a release.
- Commit only when the user asks.
