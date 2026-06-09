# Changelog

## v1.2.0 — keep the PC awake while mirroring

- **Prevent sleep / monitor power-off while mirroring.** As long as the mirror is running, the
  PC no longer dims, blanks, or goes to sleep — just like a video player does during playback.
  No more capture-card display dropping out because Windows turned the screen off mid-stream.
- **On by default**, and you can turn it off: a *„Schlafmodus / Monitor-Abschaltung verhindern"*
  checkbox in **Settings** (Bild / Tonemap) and in the **OBS browser dock**. It's a **live**
  setting — toggling it takes effect immediately, without restarting the mirror.
- The keep-awake hold is released automatically the moment mirroring stops (or if the render
  thread ever exits), so it can never leave your PC stuck awake.

## v1.1.0 — OBS browser dock (remote control for everyone)

- **Built-in OBS / browser control dock.** The app's HTTP agent now serves a small control page at
  `http://<host>:8788/` (and `/dock`). Add it to OBS as a **Custom Browser Dock** (or open it in any
  browser) to control the mirror remotely — no extra app needed:
  - status light (green = mirroring, red = stopped) + one-click **Start / Stop**
  - source / target monitor pickers (identity-stable, like the app)
  - layout & output mode
  - **live image sliders** (saturation / contrast / gamma / exposure / paper-white)
  - **live crop sliders** (on-the-fly crop region)
- Same-origin (served by the agent itself) → no CORS, no extra hosting. Works single-PC
  (`localhost:8788`) or two-PC (`<mirror-pc-ip>:8788`).

## v1.0.2 — status icon, single-instance, About, update check

- **Tray icon now shows status** — green when the mirror is running, red when it's stopped.
- **Single-instance guard** — launching a second copy (or a stray double autostart) no longer
  creates a second tray icon; it just brings the running instance's settings to the front.
- **Version in the settings title bar** (`… — Einstellungen vX.Y.Z`).
- **About section** at the bottom of Settings — version, copyright, MIT license, GitHub link.
- **Update check** — the app checks GitHub for a newer release on start (best-effort) and shows a
  clickable tray notification; you can also check on demand from the About section. (Check only —
  it never downloads or installs anything by itself.)

## v1.0.1 — stable monitor selection (replug-proof)

Monitor selection now survives unplugging/replugging and display-reordering — important for
setups where the capture card isn't always connected.

- Source/target monitors are now remembered by a **stable identity** (EDID device path + name),
  not just the enumeration index. When your monitors come back, the right ones are picked again
  automatically, even if Windows reordered them.
- If a configured monitor is **not connected**, the app now **refuses to start with a clear
  message** (tray balloon / dialog) instead of silently mirroring the wrong screen.
- Settings window flags a saved source/target that's currently disconnected (the selection is
  kept, not lost).
- Fully backward compatible with v1.0.0 configs (falls back to the index until you re-pick once).

## v1.0.0 — first public release

First standalone release of RitschyMirror — a Windows tray app that mirrors any monitor to a
capture card / target display with proper HDR → SDR tonemapping. Built for dual-PC streaming
setups.

**Highlights**
- Source/target monitor pickers with friendly EDID names; multi-GPU & HDR aware.
- GPU HDR → SDR tonemapping (`bt2390` / `reinhard` / `hable` / `aces`) with live image controls.
- Layout modes: `fit`, `stretch`, `top_strip`, `crop_region`.
- Output modes: `windowed`, `borderless`, `fullscreen_block` (mouse-lock), `exclusive`
  (with automatic fallback when source & target are on different GPUs).
- Lives in the system tray; optional auto-start at login; live config reload.
- Optional HTTP control agent (start/stop, config, displays) for remote/dashboard control.
- **Per-user installer** (`RitschyMirror-Setup-1.0.0.exe`) — no admin/UAC, clean uninstall.
