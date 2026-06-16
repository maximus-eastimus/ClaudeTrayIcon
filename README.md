# Claude Tray Icon

A lightweight system-tray app that shows your **Claude subscription usage** at a glance — your 5-hour session limit and your 7-day weekly limit — with reset timers, automatic OAuth-token refresh, and a history of recent checks.

Built on **.NET 10 + [Eto.Forms](https://github.com/picoe/Eto)**, which uses each OS's **native** tray widget — on Windows the real WinForms `NotifyIcon`, on macOS `NSStatusItem`, on Linux GTK — so the menu behaves natively on each platform. **Windows and Linux** builds are available today; **macOS** is in progress (see status below).

## The tray icon

A small box split into two vertical bars that fill from the bottom:

| Bar | Meaning | Color |
|-----|---------|-------|
| Left | Current **5-hour session** usage | Blue |
| Right | **7-day weekly** usage | Green |

Lighter when usage is low, darker as it approaches the limit (with a floor so it stays visible on dark themes).

## Menu (right-click the icon)

- Session (5h) and Week (7d): % and "resets in …"
- Per-model weekly usage (Opus / Sonnet / …) when your plan reports it
- **Last updated** → submenu with the last 10 checks, marked **🟢 success / 🔴 error** with the return code (`200`, `401`, …) and time
- **Refresh now**, **Start at login** (toggle), **Quit**

> Native OS menus don't support per-item text color cross-platform, so success/failure is shown with 🟢/🔴 markers instead of green/red text.

## Download

Grab a ready-to-run build from the **[Releases page](https://github.com/maximus-eastimus/ClaudeTrayIcon/releases/latest)** — pick the zip for your platform (`win-x64`, `linux-x64`), unzip, and run `ClaudeTrayIcon`. Each is a self-contained single file; no .NET install needed. Builds are produced automatically by GitHub Actions. (macOS build is in progress.)

## Install / build

Requires the **[.NET 10 SDK](https://dotnet.microsoft.com/download)** to build. The output is a self-contained single executable — your users don't need to install anything.

```bash
# Windows (PowerShell)
./build.ps1                  # win-x64

# Linux
./build.sh linux-x64
```

The binary lands in `publish/<rid>/`. During development you can run it directly with `dotnet run --project src/ClaudeTrayIcon -f net10.0-windows` (Windows) or `-f net10.0` (Linux).

It adds itself to login startup on first run (toggle from the menu → **Start at login**):
- **Windows** — `HKCU\…\Run` registry value
- **macOS** — `~/Library/LaunchAgents/com.claudetrayicon.plist`
- **Linux** — `~/.config/autostart/claudetrayicon.desktop`

## How authentication works

When you sign into **Claude Code** with a Pro/Max subscription, it stores an OAuth token. This app reads it and calls a **read-only** usage endpoint (`GET /api/oauth/usage`) with `Authorization: Bearer …`. It is **not** an API key, consumes no tokens/credits, and nothing leaves your machine except that request to Anthropic.

Where the token is read from, per platform:
- **Windows / Linux** — `~/.claude/.credentials.json`
- **macOS** — the login **Keychain** (item `Claude Code-credentials`), with `~/.claude/.credentials.json` as a fallback

### Token self-refresh
The token lasts ~8 hours and is normally refreshed by Claude Code. If Claude Code isn't running, this app refreshes it itself — but only when it's expired/near-expiry, re-reading first, with a guarded write-back (a malformed response can never corrupt your login) and a 2-minute back-off on `429`. On macOS the refreshed token is written back to the Keychain.

> You must sign into Claude Code **once** to create the login; the app refreshes an existing token, it can't create one. The Claude **Desktop** chat app uses a separate auth store and is not used here.

App state (history, logs, first-run marker) lives in:
- Windows: `%LOCALAPPDATA%\ClaudeTrayIcon\`
- macOS: `~/Library/Application Support/ClaudeTrayIcon/`
- Linux: `$XDG_DATA_HOME` (or `~/.local/share`)`/ClaudeTrayIcon/`

## Platform status / notes
- **Windows** — done. Right-click menu verified working (it's the native WinForms `NotifyIcon`).
- **Linux** — built (GTK backend), but **not yet runtime-verified**. Needs GTK at runtime. Note: the GTK tray uses the legacy `StatusIcon` API, so on **GNOME** (Ubuntu's default) the icon won't show without the *"AppIndicator and KStatusNotifierItem Support"* extension; it works out of the box on KDE/XFCE/MATE/Cinnamon.
- **macOS** — in progress. Needs the Eto macOS backend wired up and a Mac to verify; the credential read/write already targets the macOS **Keychain** (item `Claude Code-credentials`, with `~/.claude/.credentials.json` as a fallback).
- The **session % / weekly %** come only from the subscription's OAuth token — an Anthropic API key cannot provide them (it meters the separate pay-as-you-go developer API).

## Legacy Windows-only version

The original ~28 KB **WinForms** build (no .NET SDK needed — compiles with the C# compiler built into Windows) is preserved under [`legacy-windows/`](legacy-windows/). It's Windows-only but has zero build dependencies.
