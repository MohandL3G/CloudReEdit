# CloudReEdit

> **WARNING: This project has only been tested with S3-compatible storage
> (AWS S3 / Cloudflare R2).** Google Drive, OneDrive, and local-only providers
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
> CloudRedirect already produced. Use an **S3-compatible bucket** (S3 / R2) for
> cloud writes to actually work.

1. **Install & configure CloudRedirect** first with an S3-compatible bucket, and
   let it sync at least once. Close Steam before continuing.
2. **Run CloudReEdit** (`CloudReEdit.exe` — single file, requires .NET 8 Desktop
   Runtime).
3. **Accounts page** — the Steam install path is auto-detected. Pick your cloud
   provider and account. The app shows whether cloud writes are supported.
4. **Games page** — pick a game from the list (names are fetched from the Steam
   store) and click edit.
5. **Playtime editor** — set the total in **minutes** or **hours**, optionally
   adjust the **per-device breakdown** and **last played** date, then click
   **Save playtime**.
   - The app blocks saving while Steam is running.
   - Before writing, it backs up all three copies to
     `%AppData%\CloudReEdit\backups\<timestamp>\`.
   - If the total differs from the sum of devices, the difference is stored in
     the `__migrated_localconfig` balance bucket on save.
6. **Restart Steam** and verify the playtime stuck in your library.

## Features

- Browse every CloudRedirect account and app on your machine.
- Edit playtime as **minutes or hours**, with a full per-device breakdown.
- Adjust **last played** date.
- Keeps `localconfig.vdf`, the local stats JSON, and the cloud blob in sync.
- Cloud writes for **S3 / R2** (signed requests, same as CloudRedirect).
- Read-only cloud mode for Google Drive / OneDrive / local providers.
- Automatic backups before every save: `%AppData%\CloudReEdit\backups\<timestamp>\`.
- Blocks saving while Steam is running, to avoid Steam overwriting your edits.

## How playtime is stored

CloudRedirect stores playtime as `per_device` buckets plus derived totals. The
DLL recomputes totals as the exact sum of the buckets, and merges the
local `localconfig.vdf` and cloud copies with **max-wins** semantics. Because of
that, editing a total without touching the buckets gets recapped upward.
CloudReEdit therefore edits the buckets: any difference between the total and
the device sum is kept in a `__migrated_localconfig` balance bucket (the same
one CloudRedirect uses), and shrinking first drains that balance before scaling
real devices.

## Requirements

- **CloudRedirect** already installed and configured with storage (S3 / R2 for
  cloud writes).
- **Running the app**: .NET 8 Desktop Runtime (Windows).
- **Building from source**: .NET 8 SDK (or newer), Windows.

## Build

Produces a single-file, framework-dependent exe (no extra DLLs next to it):

```powershell
dotnet publish ui/CloudReEdit.csproj -c Release
```

Output: `ui\bin\Release\net8.0-windows\win-x64\publish\CloudReEdit.exe`

> The exe bundles the app into one file but still requires the .NET 8 Desktop
> Runtime on the target machine. To also bundle the runtime (bigger exe, no
> install needed), publish with `--self-contained true`.

## Release via GitHub Actions

The repo ships a `workflow_dispatch` workflow. On GitHub:

1. Open **Actions** → **Build & Release** → **Run workflow**.
2. Optionally set the version (defaults to `0.1.0`) and release notes.
3. Run it. The workflow builds the exe, uploads it as a build artifact, and
   publishes it to a GitHub Release tagged `v<version>`.

## Disclaimer

Playtime edits are cosmetic. Editing files while Steam is running (or a cloud
sync is in progress) can result in Steam overwriting your changes — close Steam
before saving, and rely on the backups in `%AppData%\CloudReEdit\backups` if
anything looks off. This tool was only tested against S3-compatible storage; use
it at your own risk.
