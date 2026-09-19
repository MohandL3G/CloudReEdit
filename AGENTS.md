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
`%AppData%\CloudRedirect\` (`config.json` + DPAPI-encrypted `s3_credentials.json`;
`google_tokens.json` / `onedrive_tokens.json` for those providers).

## Layout

- `ui/` — the entire WPF app (`CloudReEdit.csproj`). Only two NuGet deps:
  WPF-UI and `System.Security.Cryptography.ProtectedData` — no AWS SDK, no JSON
  library beyond `System.Text.Json`.
- `ui/Pages/` — `AccountsPage`, `GamesPage`, `PlaytimeEditorPage`.
- `ui/Services/` — app logic, ports of CloudRedirect behavior. Save pipeline lives
  in `SaveTransaction.cs` (state machine), `SaveReportModel.cs` (report types),
  and `BackupManifest.cs` (backup metadata).
- `ui/Services/Cloud/` — cloud providers + `CloudProviderFactory`.
- `tests/CloudReEdit.Tests/` — xunit project; pure-model/VDF tests compile
  `PlaytimeModel.cs`, `VdfEditor.cs`, `FileUtils.cs`, `SaveTransaction.cs`, and
  the cloud providers directly so they run as `net8.0` without the WPF app.
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
- Nullable reference types are enabled but the csproj `NoWarn` suppresses CS86xx warnings — the code freely uses `string?` and `!`.

## Cloud storage semantics (important)

- `per_device` buckets are the source of truth; totals are derived (parser floors to `uint`).
- Merge is max-wins across `localconfig.vdf` and the cloud blob — all copies must be written together.
- `__migrated_localconfig` is the reconcile balance bucket; shrinking drains it before real devices.
- CloudRedirect day history lives in `per_device.<device>.days` (unix day index -> minutes). Preserve it
  verbatim through parse/serialize; never scale, prune, or create day buckets during lifetime edits.
- Day-tracked apps (any device carries a `days` property, even an empty one) derive `minutes_2weeks` from
  a rolling 14-day window of day buckets, capped at the lifetime total. Legacy/no-day apps retain the
  existing lifetime-as-two-week behavior.
- Writable providers: `s3` / `r2` (`S3Provider`, native SigV4, path-style `/{bucket}/{key}`) and
  `folder` (`FolderProvider`, when `config.json` sets `sync_path`). `gdrive`, `onedrive`, and
  `local` are read-only (`UnsupportedProvider`, `CanWrite == false`).
- Credentials JSON fields: `access_key_id`, `secret_access_key`, `bucket`, `key_prefix`, `endpoint`, `region`.
  `key_prefix` is normalized: strip leading `/`, ensure trailing `/`. Object key = `{prefix}{accountId}/0/stats.json`.
  Cloud blob is a JSON object mapping `appId` (string) -> app stats.
- `VdfEditor` patches only the target app block and preserves line endings (`\n` / `\r\n`) and every other byte — don't rewrite the whole VDF.

## Save protocol (important)

`PlaytimeService.SaveAsync` delegates the whole save to `SaveTransaction` via a
portable `SaveScope { AccountId, AppId, Source }`. Order is fixed; never reorder
copies or skip a phase.

1. **Pre-flight — no mutations.** Steam must be closed. Read local JSON, VDF, and
   (writable providers) the cloud blob + ETag without writing. VDF must have the
   app block (`AppBlockExists`) — a missing `localconfig.vdf` file is tolerated
   (skipped, not failed). Malformed any copy → abort (`NotSaved`) before any write.
2. **Backup.** Every save writes byte-exact copies of all existing copies plus
   `manifest.json` (atomic) into a fresh, unique
   `%AppData%\CloudReEdit\backups\{timestamp}_{acct}-{app}` directory. Absent
   copies are recorded as absent in the manifest, never invented. Backup failure
   aborts before any write (`BackupFailed`).
3. **Write order: VDF → local → cloud.** Each phase patches in place then verifies
   semantically read-back (`PlaytimeModel.SemanticallyEquals`; VDF re-reads
   `Playtime`/`LastPlayed`, and `Playtime2wks` only when the key exists — a VDF
   that never had it is not expected to gain it). Final outcome is the worst copy
   outcome; never "nothing changed" when any copy changed.
4. **Cloud writes are conditional.** Existing blob → `If-Match: {preflightETag}`
   (`412` → `CloudChangedConcurrentlyException` → stop, never overwrite the
   concurrent writer). Absent blob → `If-None-Match: *` (create-only). The uploaded
   blob is rebuilt from the pre-flight parsed account model (only the target app's
   `playtime` replaced; sibling apps preserved but re-serialized), never a blind
   re-upload of the pre-flight raw bytes.
5. **Truthful reporting.** After a cloud write error, re-read the blob to classify:
   contains target → committed (`SavedUnverified`, concurrent change preserved);
   byte-identical to the pre-write snapshot (or still absent) → the write didn't
   land — roll back earlier copies: `CloudChangedConcurrently` only when the write
   was explicitly rejected by a precondition (`412`), otherwise `CloudWriteFailed`;
   anything else → a newer concurrent writer owns it (`CloudDiverged`; if the
   re-read fails, `CloudStateUnknown` — never assert commit). Old cloud value is
   never restored over a concurrent writer.

## Testing

- Run tests with `dotnet test tests/CloudReEdit.Tests/CloudReEdit.Tests.csproj` from the repo root.
- The test project targets `net8.0` (not `net8.0-windows`) and compiles the pure model/VDF sources
  directly via `<Compile Include>` links, so tests run anywhere without the WPF app or a Windows host.
- Never write to real Steam files or the live cloud blob during a test without an explicit user request —
  prefer in-memory dry-runs and temp copies of the VDF.
- Cloud sequencing tests use `FakeCloud` (in-memory `ICloudProvider` with scripted
  PUT/create failures) for deterministic classification; `S3ProviderTests` use a
  `StubHandler` `HttpMessageHandler` that snapshots the request body before the
  request is disposed. Both live in the test project.
- Steam must be closed before any real save test.
- Every save backs up all three copies to `%AppData%\CloudReEdit\backups\<timestamp>\` before writing — watch for these during tests.

## Releases

- Publish via the GitHub Actions `workflow_dispatch` workflow; it creates a tag `v<version>`
  and attaches the built exe to a release.
- Commit only when the user asks.
