# RitschyMirror — Architecture & Reference

Internal/technical documentation. For the user-facing overview see the main
[README](../README.md).

## Pipeline

**DXGI Desktop Duplication** (FP16, HDR/scRGB) → **HLSL tonemap shader**
(`bt2390` / `reinhard` / `hable` / `aces`) → layout/crop composite → **flip-model
swap chain** (8- or 10-bit). The render device is created on the **source adapter**
(Desktop Duplication is adapter-bound); when the target display hangs off a
different GPU, the DWM performs the cross-adapter transfer in borderless mode.

Per-Monitor-V2 DPI awareness is set programmatically (`SetProcessDpiAwarenessContext`),
so capture/display geometry is in real pixels and not distorted by Windows scaling.

## Source files

| File | Purpose |
|---|---|
| `Program.cs` | Entry point → tray app |
| `TrayContext.cs` | NotifyIcon + menu, owns the engine + HTTP agent |
| `SettingsForm.cs` | Local settings GUI (all parameters) |
| `MirrorEngine.cs` | Render loop (start/stop), display enumeration, live config reload |
| `Renderer.cs` | Swap chain + layout/crop geometry + shader pipeline |
| `Capture.cs` | DXGI Desktop Duplication (incl. optional cursor compositing) |
| `Shaders.cs` | HLSL (tonemap + crop UV + cursor blend) |
| `Win32Window.cs` | Window + mouse-lock hook (`WH_MOUSE_LL`) |
| `MonitorNames.cs` | Friendly EDID monitor names (CCD `QueryDisplayConfig`) |
| `ControlAgent.cs` | HTTP control agent (`HttpListener`) |
| `MirrorConfig.cs` | Render config (`mirror_config.json`) |
| `AppSettings.cs` | App config (`app_settings.json`: agent port/bind, autostart) |
| `AppIcon.cs` | Loads the embedded app icon for tray + window |

## Configuration — `mirror_config.json`

Read live (mtime polling — `FileSystemWatcher` misses atomic writes). The file is the
**single source of truth**: the local GUI, the HTTP agent and (in the RitschyBot setup) the
remote controller all write the same file; the render loop reads it live.

**Live keys** (apply immediately): `tonemap_enabled`, `operator`, `source_peak_nits`,
`target_paperwhite`, `exposure`, `saturation`, `contrast`, `gamma`, `content_offset_y`,
`vsync`, `layout_mode`, `crop_x`, `crop_y`, `crop_w`, `crop_h`, `show_cursor`, `keep_awake`.

`keep_awake` (default `true`) holds off display power-off + system sleep while the mirror is
running, via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
on the render thread. It's set once the render loop actually starts (after source/target
resolve), toggled live on config reload, and cleared on stop — and because the hold is bound to
the render thread, Windows releases it automatically if that thread ever exits.

**Structural keys** (need a render restart): `output_bit_depth`, `source_display`,
`target_display`, `output_mode`, `windowed`, `window_width`, `window_height`,
`output_width`, `output_height`.

`crop_*` are in 0..1 of the source and only used when `layout_mode = crop_region`.
`output_mode` empty falls back to the legacy `windowed` flag for backward compatibility.

## HTTP control agent

Listens on `http://<bind>:<port>/` — default port **8788**. `bind` = `"+"` makes it reachable
from the network (requires a URL-ACL / admin; otherwise it falls back to `localhost`).
Configured via `app_settings.json` (`agent_bind`, `agent_port`, `agent_enabled`).

| Route | Description |
|---|---|
| `GET /health` | Ping → `{ok, running}` |
| `GET /status` | `{running, state, config}` |
| `GET /config` · `POST /config` | Read config / merge a whitelisted patch |
| `POST /start` · `/stop` · `/restart` | Control rendering |
| `GET /displays` | Enumerated displays (index, name, friendly, resolution, hdr, adapter) |

### RitschyBot Cockpit integration (optional)

The [RitschyBot](https://github.com/RitschyRigz) Cockpit can drive this agent via
`cockpit/services/mirror_control.py`: connector `gaming_pc` → field **`agent_port`** set ⇒
control over HTTP; empty ⇒ WinRM fallback. Response shapes match the WinRM contract so the
two transports are interchangeable.

## Log

`mirror.log` next to the exe lists every output at render start (index, name, resolution,
color space, adapter) — the basis for display selection and HDR detection.

## Packaging

- `publish.ps1` — `dotnet publish` self-contained single-file (win-x64).
- `installer/RitschyMirror.iss` + `installer/build_installer.ps1` — Inno Setup, per-user
  install to `%LOCALAPPDATA%\Programs\RitschyMirror`, no admin required.
- `install.ps1` — minimal alternative: copy + shortcuts (and, as admin, URL-ACL + firewall for
  network reachability of the agent).
