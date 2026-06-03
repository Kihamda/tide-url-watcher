# Tide

**The calm update radar for the web.**

Tide is a native Windows app for noticing changes on the sites you care about.
It watches RSS, Atom, and ordinary webpages, then gathers new stories into a
quiet local inbox so you do not need to keep another row of browser tabs open.

Screenshot placeholder: add a first-run inbox screenshot after the next visual
polish pass.

## What It Does

- Watches RSS, Atom, and normal website URLs
- Runs quietly in the background while the app is open
- Keeps a tray icon with open, refresh, pause, settings, and quit commands
- Shows Windows local app notifications for new stories
- Supports unread, saved, source, search, today, seven-day, failed-source,
  notified, and muted filters
- Lets each source be enabled, disabled, muted, or refreshed on its own
- Records last success, last failure, error text, ETag, and Last-Modified per
  source
- Stores everything locally in portable JSON under the adjacent `Data` folder
- Uses atomic JSON saves with `.tmp` writes and `.bak` recovery
- Provides import, export, logs, startup registration, quiet hours, and refresh
  interval settings

## Product Shape

Tide is built as a local-first update radar, not a social feed. It avoids
cloud sync, accounts, push infrastructure, Electron, WebView, and embedded
browser runtimes. It performs direct HTTP GET requests to the sites you register
and stores article metadata, source settings, read state, saved state, and logs
on your machine.

## Background Behavior

By default Tide:

- Refreshes automatically every 30 minutes while running
- Refreshes once after startup
- Keeps running when the window is closed by hiding to the tray
- Can start with Windows through the settings screen
- Can start directly in the tray when launched at startup
- Prevents most duplicate app launches and routes later launches back to the
  existing window

The tray menu includes:

- Tideを開く
- 今すぐ更新
- 通知を一時停止
- 次回更新まで停止
- 設定
- 終了

Use the tray menu's **終了** item when you want to fully quit the process.

## Notifications

Tide uses Windows local app notifications through Windows App SDK. New stories
are summarized instead of sending one notification per article. Notification
clicks return to Tide, and single-story notifications can open the article.

Notification controls include:

- Global notification on/off
- Per-source notification on/off
- Quiet hours
- Pause notifications for one hour
- Pause until the next refresh
- First-run suppression so adding a source does not notify old existing posts

Known notification limits:

- These are local notifications, not cloud push notifications.
- Admin or restricted notification contexts may prevent registration.
- If notification registration fails, Tide continues to update the in-app inbox
  and reports the issue in the UI/log.

## Data Safety

Portable data lives beside the executable:

```text
Data/
  watcher-data.json
  watcher-data.json.bak
  watcher-data.json.tmp
  app.log
  startup.log
```

Saves write to `watcher-data.json.tmp`, flush to disk, then atomically replace
the main JSON while keeping a backup. On load, Tide tries the main JSON first,
then the backup, then starts with an empty snapshot and a visible warning if
both are unusable.

Preserved state includes:

- Sources and feed URLs
- Source enabled/notification settings
- Last success, last failure, ETag, Last-Modified, and failure counts
- Stories
- Read/saved state plus timestamps
- Notification state
- Auto-refresh, tray, startup, quiet-hours, and retention settings

Saved articles are protected during retention cleanup; old unsaved stories are
pruned first.

## Privacy

- Registered URLs stay local.
- Article metadata, read state, saved state, and settings stay local.
- Tide does not require an account.
- Tide does not sync to an external server.
- Tide sends HTTP requests only to the sites you register and feed candidates
  discovered from those sites.
- Logs avoid storing full article bodies.

## Install

Download the latest `Tide-Windows-x64.zip` from GitHub Releases, extract it into
a writable folder, and run `Tide.App.exe`.

The current release is unpackaged and framework-dependent, so the target machine
needs the Windows App SDK runtime and .NET runtime expected by the build.

## Develop

Requirements:

- Visual Studio 2026 with WinUI application development tools
- .NET SDK 10
- Windows SDK 10.0.26100 or later

Build:

```powershell
dotnet restore Tide.slnx
dotnet build Tide.slnx -c Release
```

Run:

```powershell
dotnet run --project src/Tide.App/Tide.App.csproj
```

Test:

```powershell
dotnet run --project tests/Tide.Core.Tests/Tide.Core.Tests.csproj -c Release
```

## CI And Release

PR CI runs:

```powershell
dotnet restore Tide.slnx
dotnet build Tide.slnx -c Release --no-restore
dotnet run --project tests/Tide.Core.Tests/Tide.Core.Tests.csproj -c Release --no-restore
```

Merges to `main` publish a `win-x64` build, copy this README into the package,
exclude the runtime `Data` folder, upload a ZIP artifact, and create a GitHub
Release.

## Troubleshooting

- **No notifications:** Check Windows notification settings. If Tide is running
  elevated or in a restricted environment, local notification registration may
  fail.
- **Data is not saving:** Make sure the extracted app folder is writable.
- **A site has no stories:** RSS/Atom is preferred. Plain website detection is
  best-effort and may miss JavaScript-rendered pages.
- **A source keeps failing:** Open Source管理 to view the last error, disable the
  source, or refresh only that source.
- **The app disappeared after close:** Tide is in the tray. Use the tray menu to
  reopen or quit.

## Known Limitations

- HTML fallback is best-effort and does not execute JavaScript.
- Cloud push notifications are not implemented.
- Startup registration uses the current executable path, so moving the portable
  folder may require toggling the setting off and on again.
- The tray icon uses a system fallback icon until branded icon assets are added.
- Full UI automation tests are not included yet; the core suite covers parsing,
  storage, merge behavior, settings, source enablement, and notification
  planning.

## Roadmap

- Branded app/tray icon assets
- Stronger onboarding for first launch
- Richer source preview and favicon handling
- Better keyboard selection model for story cards
- More notification actions
- Optional per-source retention limits
- UI automation smoke tests
