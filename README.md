# CloudReEdit

> **WARNING: Cloud writes have only been tested against a self-hosted RustFS
> server (S3-compatible).** Other S3-compatible services — AWS S3, Cloudflare
> R2, MinIO, … — are expected to work (the request signing is the same code) but
> have **not** been tested. Google Drive, OneDrive, and local-only providers
> are treated as **read-only** — cloud writes for them are not implemented and
> have **not** been tested. If you use one of those, your edits will not reach
> the cloud copy and Steam may overwrite them.

> **Disclaimer:** This project was created using **OpenCode**, an open-source AI
> coding assistant.

A small desktop app to edit Steam playtime stats stored by
[CloudRedirect](https://github.com/Selectively11/CloudRedirect).

CloudRedirect stores playtime in **three places**, and editing only one of them
gets overwritten the next time Steam reconciles. CloudReEdit reads all three and
writes them consistently in a single save.

## How to use

> **Prerequisite:** CloudRedirect must already be **installed, configured with a
> storage, and have synced at least once** so its config (`config.json`), the
> credentials file, and the per-app stats JSON files exist on disk. CloudReEdit
> does not create or configure storage — it only reads and edits what
> CloudRedirect already produced. Use an **S3-compatible bucket** for cloud
> writes to actually work (self-hosted RustFS is the setup that was tested).

1. **Install & configure CloudRedirect** first with an S3-compatible bucket, and
   let it sync at least once. Close Steam before continuing.
2. **Run CloudReEdit** (`CloudReEdit.exe` — single file, requires .NET 8 Desktop
   Runtime).
3. **Accounts page** — the Steam install path and the cloud provider (read from
   CloudRedirect's `config.json`) are auto-detected; there's no provider picker.
   Pick your account. The app shows whether cloud writes are supported for it,
   and warns (without blocking anything) when the configured endpoint is plain
   `http://`, i.e. unencrypted.
4. **Games page** — pick a game from the list (names are fetched from the Steam
   store) and click edit.
5. **Playtime editor** — set the total in **minutes** or **hours**, optionally
   adjust the **per-device breakdown** and **last played** date, then click
   **Save playtime**.
   - If Steam is running, saving first asks whether to close it — **close and
     reopen**, **close only**, or **cancel** — with an *"always do this"*
     checkbox so you only decide once (the choice lives under **Settings**).
   - Steam must actually be closed: if it does not close in time, nothing is
     written.
   - Right before the first write the app checks Steam once more; if Steam
     started again while the save was being prepared, the save aborts with
     nothing changed.
   - Before writing, it backs up all three copies to
     `%AppData%\CloudReEdit\backups\<timestamp>\`.
   - If the total differs from the sum of devices, the difference is stored in
     the `__migrated_localconfig` balance bucket on save.
6. **Restart Steam** if the save left it closed, and verify the playtime stuck in
   your library.

### Settings page

**Settings → Steam behaviour when saving** decides what happens to Steam once a
save finishes: **Ask** (default — prompt every time), **Always open**, **Never
open**, or **Last state** (put it back the way it was). Preferences are saved to
`%AppData%\CloudReEdit\settings.json`.

### Editor page

The editor prompts **Save / Discard / Cancel** when you leave it (or close the
window) with unsaved changes. It also shows where the current **log file** lives
with an *Open* button, and (once backups exist) where the backup folder is, how
many backup folders there are and how big they are.

## Features

- Browse every CloudRedirect account and app on your machine.
- Edit playtime as **minutes or hours**, with a full per-device breakdown.
- Adjust **last played** date.
- Keeps `localconfig.vdf`, the local stats JSON, and the cloud blob in sync.
- Cloud writes for **S3-compatible buckets** (signed requests, same as
  CloudRedirect; tested against a self-hosted RustFS server).
- Read-only cloud mode for Google Drive / OneDrive / local providers.
- Automatic backups before every save: `%AppData%\CloudReEdit\backups\<timestamp>\`,
  pruned automatically (5 newest per account + game are kept).
- Closes Steam before saving — with a prompt you can remember — and aborts with
  nothing written if Steam is or becomes running.
- Warns when the configured storage endpoint is plain `http://` (unencrypted).
- Rolling daily log file under `%AppData%\CloudReEdit\logs\`.
- Shows its version in the sidebar footer (e.g. `CloudReEdit 0.2.7-pre-release`).

## How playtime is stored

CloudRedirect stores playtime as `per_device` buckets plus derived totals. The
DLL recomputes totals as the exact sum of the buckets, and merges the
local `localconfig.vdf` and cloud copies with **max-wins** semantics. Because of
that, editing a total without touching the buckets gets recapped upward.
CloudReEdit therefore edits the buckets: any difference between the total and
the device sum is kept in a `__migrated_localconfig` balance bucket (the same
one CloudRedirect uses), and shrinking first drains that balance before scaling
real devices.

## Save safety & recovery

Saves are designed to leave no half-written state behind:

- Each local copy is written **atomically** (write to a temp file, then move it
  over the original), so a crash mid-save never leaves a truncated JSON or a
  half-edited VDF.
- Copies are written in a fixed order (VDF → local → cloud). If a later copy
  fails, the earlier ones are automatically restored to their exact pre-save
  byte state and the save reports the failure — it never claims success.
- Steam is checked **twice**: once before anything is read, and again right
  before the first write. If Steam is running at either point the save aborts
  with nothing changed ("Steam started while saving; close it and try again").
- Cloud writes are **conditional**: the app sends the pre-save ETag with the
  upload and aborts if it no longer matches, so it never silently overwrites a
  concurrent change made by another device or a Steam sync.

Every save that reaches the write phase first backs up all existing copies to
`%AppData%\CloudReEdit\backups\{timestamp}_{accountId}-{appId}\`, together with
a `manifest.json` (hashes and whether each copy existed). Copies that were
absent are recorded as absent — they are never invented.

To recover, copy the saved files from the backup folder back over the live
ones. Note:

- There is **no in-app restore** feature; recovery is manual.
- `cloud_stats.json` holds the pre-save cloud blob, but the app never re-uploads
  backups — restoring the cloud copy requires your own S3-compatible tooling.
- Backups **are** cleaned up automatically: after every save (and once at
  startup) old backup folders are pruned, keeping the **5 newest per account +
  game**. The backup created by the save that triggered the pruning is never
  removed by it, and folders that don't follow the app's backup naming scheme
  are left alone.

## Privacy & credentials

- CloudReEdit reads CloudRedirect's `config.json` but never writes to it.
- For S3 / R2 it reads the DPAPI-encrypted `s3_credentials.json` to sign
  requests; the decrypted credentials are **never written back to disk**.
  Google Drive / OneDrive / local providers are never used for writes.
- Game names and header images are fetched from Steam's **public store APIs** and
  cached under `%AppData%\CloudRedirect\store_cache.json` and `store_images\`.
  Store responses are only accepted over HTTPS from Steam's own hosts (even
  after a redirect), and downloaded images are verified to be real JPEG/PNG/WebP
  data before they are cached.
- The only other network traffic is reading and writing your cloud stats blob
  (`{prefix}{accountId}/0/stats.json`) on S3 / R2. If the endpoint in
  CloudRedirect's credentials is plain `http://`, that traffic is unencrypted —
  the app shows a warning next to the provider status (Accounts page and the
  editor's information bar) and writes one to the log, but does not block
  saving.
- Everything the app does is written to a rolling log at
  `%AppData%\CloudReEdit\logs\CloudReEdit-yyyyMMdd.log` (one file per day, the
  7 newest kept, each capped at 5 MB). Credentials are never logged.
- No telemetry, analytics, or data logging anywhere in the app.

## Limitations

- **Read-only providers** (GDrive / OneDrive / local): only the local VDF and
  stats JSON are edited. The cloud copy keeps its old values, and Steam (or
  another CloudRedirect device) can re-assert them later.
- **No CloudRedirect config**: the app still edits and syncs the two local copies
  (VDF + stats JSON); there is simply no cloud copy to update.
- **Broken config**: if `config.json` exists but is invalid, all saves are
  blocked ("No files were changed") until the config is fixed.
- A missing `localconfig.vdf` is skipped rather than fatal; a missing local stats
  JSON is created on save.
- Only the lifetime total, the per-device split, and last-played are editable.
  CloudRedirect's per-day history — and the "recent playtime" derived from it —
  is preserved exactly as-is and never edited.

## Requirements

- **CloudRedirect** already installed and configured with storage (an
  S3-compatible bucket for cloud writes).
- **Running the app**: .NET 8 Desktop Runtime (Windows).
- **Building from source**: .NET 8 SDK (or newer), Windows.

## Build

For development, build the `net8.0-windows` app directly:

```powershell
dotnet build ui/CloudReEdit.csproj
```

Run the unit tests from the repo root (pure model/VDF tests, no Windows host
needed):

```powershell
dotnet test tests/CloudReEdit.Tests/CloudReEdit.Tests.csproj
```

On a non-Windows machine, add `-p:EnableWindowsTargeting=true` to the build
above.

To publish a single-file, framework-dependent exe (no extra DLLs next to it):

```powershell
dotnet publish ui/CloudReEdit.csproj -c Release
```

Output: `ui\bin\Release\net8.0-windows\win-x64\publish\CloudReEdit.exe`

> The exe bundles the app into one file but still requires the .NET 8 Desktop
> Runtime on the target machine. To also bundle the runtime (bigger exe, no
> install needed), publish with `--self-contained true`.

## Release via GitHub Actions

The repo ships a `workflow_dispatch` workflow (`.github/workflows/build-release.yml`).
On GitHub:

1. Open **Actions** → **Build & Release** → **Run workflow**.
2. Set the inputs:

   | Input | Default | What it does |
   |---|---|---|
   | `version` | `0.2.7` | Version to tag the release with (`v<version>`). |
   | `release_notes` | *(empty)* | Optional plain-text notes; when empty, GitHub's generated notes are used. |
   | `release` | checked | Publish the GitHub release. Uncheck to only build the exe and upload it as a build artifact. |
   | `prerelease` | unchecked | Mark the release as a pre-release. |

3. Run it. The workflow runs the unit tests, publishes the single-file exe,
   uploads it as the `CloudReEdit` artifact, and — when `release` is checked —
   creates the release `v<version>` titled `CloudReEdit v<version>` with the exe
   attached.

The `version` input is manual, not read from the csproj — keep it in sync with
the `<VersionPrefix>` in `ui/CloudReEdit.csproj`, and pick a version that does
not already have a `v<version>` tag or release. The version shown inside the app
comes from `<VersionPrefix>` + `<VersionSuffix>` in the csproj (currently
`0.2.7-pre-release`; drop the `<VersionSuffix>` line for the next stable
release).

## Disclaimer

Playtime edits are cosmetic. Editing files while Steam is running (or a cloud
sync is in progress) can result in Steam overwriting your changes — close Steam
before saving, and rely on the backups in `%AppData%\CloudReEdit\backups` if
anything looks off. This tool was only tested against a self-hosted RustFS
(S3-compatible) server; use it at your own risk.
