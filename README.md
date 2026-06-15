# Claude Usage Tray

A tiny (~20 KB) native Windows tray app that shows your Claude plan usage at a glance.
No runtime to install — it builds and runs on stock Windows 11 using the in-box
.NET Framework 4.8.

## The tray icon

The icon is a small box split into two vertical bars that fill from the bottom:

- **Left bar (blue)** — current **5-hour session** usage
- **Right bar (green)** — **7-day (weekly)** usage

Each bar is **lighter at low usage and darker at high usage** (floored so it stays
visible on dark taskbars). At a glance: taller + darker = closer to your limit.

## Hover & click

- **Hover** → tooltip with session %, week %, their reset countdowns, and your plan.
- **Left-click or right-click** → menu with the full breakdown:
  - Session (5h) and Week (7d): % and "resets in …"
  - Per-model weekly usage (Opus / Sonnet / etc.) when your plan reports it
  - Extra-usage %, if enabled
  - **Last updated** — hover it for a **history submenu** of the last 10 poll
    attempts (newest first), each showing ✓/✗, the return code (`200`, `401`,
    `429`, `no login`, `error`), and the date/time. This is the quickest way to
    see the state of authentication at a glance.
  - **Refresh now**, **Start with Windows** (toggle), **Quit**

The attempt history persists to `%LOCALAPPDATA%\ClaudeUsageTray\history.json`,
so it survives restarts and login autostart.

## How it gets data

Every **5 minutes** it re-reads your OAuth token from
`%USERPROFILE%\.claude\.credentials.json` (so it always picks up Claude Code's
auto-refreshed token) and calls `GET https://api.anthropic.com/api/oauth/usage`.
Nothing is stored or sent anywhere else; the token never leaves your machine.

If you're not logged into Claude Code, or the API call fails, the icon keeps the
last good reading and the tooltip/menu shows the error.

### Token self-refresh (survives Claude Code being closed)

The OAuth access token in `.credentials.json` lasts ~8 hours and is normally
refreshed by Claude Code whenever it runs. If Claude Code **isn't** running
(e.g. you closed the IDE), the token would otherwise expire and the tray would
start returning `401`.

To handle that, the app refreshes the token itself when needed:

- It only refreshes when the stored token is **expired or within 2 minutes of
  expiring** — never while it's healthy, so it does **not** compete with a
  running Claude Code (which keeps the token fresh on its own).
- Before refreshing it **re-reads the file** in case Claude Code already
  refreshed it.
- On success it writes the rotated `accessToken` / `refreshToken` / `expiresAt`
  back to `.credentials.json` (only those three fields; everything else is
  preserved) using an atomic temp-file replace, so Claude Code stays in sync.
- The write-back is **guarded**: it only happens for a complete, valid refresh
  response, so an unexpected reply can never corrupt your login.
- If the refresh endpoint returns `429` (rate limited), it backs off for 2
  minutes instead of hammering it.

Refresh activity is logged to
`%LOCALAPPDATA%\ClaudeUsageTray\refresh.log` for troubleshooting.

> You still need to log into Claude Code **once** to create the credentials
> file in the first place — the app refreshes an existing login, it can't create
> one. The Claude **Desktop** chat app uses a separate auth store and does *not*
> create or refresh this file.

### Errors & the occasional 401

A one-off `API 401` is normal and self-healing: Claude Code rotates its OAuth
access token periodically and revokes the old one server-side the instant it
does. If a poll happens to fire across that rotation boundary it uses a
just-revoked token and gets a single 401. This is **auth**, not rate limiting
(that would be `429`) — at one call every 5 minutes you're nowhere near any
throttle. After any error the app retries every **30 seconds** until it
succeeds, then drops back to the 5-minute cadence, so a transient 401 clears in
seconds. The app never crashes on errors; it just shows them in the tooltip/menu.

## For recipients (sharing)

No credentials are bundled with this app. It reads whatever OAuth token is in
**your own** `%USERPROFILE%\.claude\.credentials.json`, which is created when you
log into [Claude Code](https://docs.anthropic.com/en/docs/claude-code) with a
Claude **Pro/Max subscription**. So to use it you just need:

1. Claude Code installed and logged in (the usual `claude` CLI login).
2. Run `ClaudeUsageTray.exe` (or rebuild it — see Build below).

It will then show *your* account's usage. It is **not** an API key and costs
nothing to run.

## Build

```powershell
.\build.ps1
```

This invokes the in-box C# compiler
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) and produces
`ClaudeUsageTray.exe`. No SDK or NuGet packages required.

## Run

Double-click `ClaudeUsageTray.exe` (or it starts automatically at login —
see below). Only one instance runs at a time.

## Autostart

On first run it adds itself to
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` so it launches at login.
Toggle this any time from the tray menu → **Start with Windows**.

To remove autostart manually:

```powershell
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name ClaudeUsageTray
```

## Quit

Tray menu → **Quit**. (This does not disable autostart; untick **Start with
Windows** first if you want it gone for good.)

## Notes / tweaks

All behavior lives in `ClaudeUsageTray.cs`:

- Poll interval: `PollMs` (default 5 min).
- Bar colors/shading: `DrawBar` (hue 212 = session blue, 140 = week green) and
  the lightness curve `0.82 - frac * 0.37`.
- Tooltip/menu text: `UpdateTooltip` / `BuildMenu`.

After editing, re-run `.\build.ps1` and relaunch.
