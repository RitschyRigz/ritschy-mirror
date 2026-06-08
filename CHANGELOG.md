# Changelog

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
