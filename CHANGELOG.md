# Changelog

## v1.3.2 — fix a badly darkened picture when the source monitor is in SDR mode

- **Fixed: with HDR turned off on the source monitor, the mirrored picture came out far too dark.**
  Highlights survived, but midtones and shadows were crushed — a mid-grey left the mirror at roughly
  40 % of its real brightness, a dark grey at 20 %. Games looked murky and washed out. Turning the
  tone mapper off did **not** help, because the darkening happened before tone mapping.
- **Root cause: one gamma too many.** Screen capture (both the monitor and the window source) hands
  us 16-bit float frames, and those are **always in a linear colour space — including when the
  display is in SDR mode**. The renderer assumed an SDR display meant gamma-encoded pixels and
  decoded them a second time, so every value was effectively raised to the power of 2.2.
- **Fixed: the tone mapper no longer squashes an SDR source.** It used to roll highlights against
  the configured HDR source peak even when the source had no HDR range at all, dimming pure white
  to about 75 %. It now derives its range from the source that is actually connected: HDR source →
  roll off as before, SDR source → pass the picture straight through, untouched.
- **Fixed: switching HDR on or off is now noticed while the mirror is running.** Previously the
  source's HDR state was only read when the mirror started, so toggling HDR mid-session kept the
  wrong colour maths running until you restarted the mirror. It is now re-checked continuously and
  after every capture recovery, and the change is written to the log.
- **Nothing changes for an HDR source** — that path is mathematically identical to v1.3.1. The one
  exception: if you set the source peak at or below the white point, tone mapping is now skipped
  instead of applied at a neutral setting (with the `hable` and `aces` operators that was never
  truly neutral).
- **Note:** `source peak` and `white point` only affect an HDR source. With an SDR source the
  picture is passed through 1:1 and those two sliders do nothing — use exposure, contrast, gamma
  and saturation to grade it.

## v1.3.1 — fix a rare mid-stream crash (window message pump)

- **Fixed a rare hard crash that could kill the mirror mid-stream.** On long sessions the app could
  vanish instantly (no error dialog, you had to relaunch it), most often after the render pipeline
  had been rebuilt one or more times. This is *not* the capture-loss crash fixed in v1.2.2 — it was
  a separate issue in the window message loop.
- **Root cause.** The window procedure is handed to Windows as a native callback, but the delegate
  backing it could be garbage-collected while Windows was still dispatching messages to the window
  (window and delegate referenced only each other — a cycle the GC is free to collect). When that
  happened, the next window message invoked a freed callback and .NET terminated the process
  immediately (`FailFast: callback on a garbage collected delegate`). A second, related flaw meant
  every window after the first silently reused the first window's callback, so once that first
  window's session ended the pointer could dangle for all later windows.
- **The fix.** The window class is now registered **once per process** against a **static** window
  procedure that lives for the entire process and dispatches to the right window by its handle. The
  collectible cycle is gone, so this class of crash can no longer occur — no matter how many times
  the pipeline rebuilds during a stream.

## v1.3.0 — mirror a single window or fullscreen app (not just the whole screen)

- **New "window" source mode.** Instead of mirroring a whole monitor, you can now mirror **one
  specific window or fullscreen app** — e.g. just your game, not your whole desktop. Pick it from
  a dropdown in **Settings**, the **OBS dock**, or the RitschyBot Cockpit. The old whole-monitor
  mode is unchanged and stays the default.
- **Safer for streaming.** A window source shows *only that window* — when you alt-tab away, the
  mirror keeps showing the app, **never your desktop**. No more accidentally flashing something
  private into the capture card.
- **Built on Windows.Graphics.Capture** — the same modern API OBS uses for its "Windows 10 (1903+)"
  window capture, *not* injection. So: no anti-cheat risk, no admin/elevation mismatch, and it's
  robust across two GPUs (the classic reason OBS' Game Capture goes black on a multi-GPU rig).
- The chosen window is remembered by **app + title**, so it's re-selected automatically next time
  even though Windows hands out fresh window handles on each launch — the same replug-proof
  approach as the monitor selection. If the app isn't running, the mirror refuses to start with a
  clear message instead of mirroring the wrong thing.

## v1.2.2 — self-healing capture (no more crash when the capture drops)

- **The mirror now recovers on its own when the screen capture is lost.** Certain events on the
  source PC (a resolution/refresh change, a game switching to fullscreen, a UAC prompt, or a
  cross-GPU hiccup) make Windows drop the capture session. This is rare — it doesn't happen every
  frame, only when one of those events fires. Previously, when it did, a failed re-grab left the
  app holding a dead capture; the next call into the graphics driver touched freed memory and the
  **whole app crashed and disappeared** — you had to relaunch it mid-stream. Now it retries the
  capture a few times and, if that's not enough, **fully rebuilds the whole render pipeline**
  automatically (with a short, growing back-off), so the picture comes back on its own.
- **No more crash, no silent hang.** If recovery genuinely can't succeed after several attempts,
  the mirror stops cleanly with a clear status instead of crashing — and it never blocks your PC
  from sleeping in that state.

## v1.2.1 — smooth cursor in `fullscreen_block` (mouse-lock)

- **Fixed the laggy/stuttery cursor in `fullscreen_block` mode.** The mouse-lock used a global
  low-level mouse hook that ran on the render thread — which spends most of its time waiting on
  vsync, so every mouse movement system-wide was throttled to the render cadence and felt heavy
  (even though the display stayed at full refresh / G-Sync). The hook now runs on its own
  dedicated, fast-pumping thread, so the cursor stays smooth at full polling rate while the
  display is still mirrored. The mouse is still locked out of the target display — just fluffy
  again.
- No effect on controller/gamepad input (it never went through the hook). Mouse-driven games
  (e.g. shooters) no longer inherit the added input latency while the mirror is running.

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
