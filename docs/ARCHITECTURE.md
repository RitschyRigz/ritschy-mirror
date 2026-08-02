# RitschyMirror — Architecture & Reference

Internal/technical documentation. For the user-facing overview see the main
[README](../README.md).

## Pipeline

**DXGI Desktop Duplication** (FP16, always linear scRGB) → **HLSL tonemap shader**
(`bt2390` / `reinhard` / `hable` / `aces`) → layout/crop composite → **flip-model
swap chain** (8- or 10-bit). The render device is created on the **source adapter**
(Desktop Duplication is adapter-bound); when the target display hangs off a
different GPU, the DWM performs the cross-adapter transfer in borderless mode.

Per-Monitor-V2 DPI awareness is set programmatically (`SetProcessDpiAwarenessContext`),
so capture/display geometry is in real pixels and not distorted by Windows scaling.

## Colour pipeline — HDR vs SDR source

Two properties of the source are easy to conflate, and conflating them is exactly what made
an SDR source come out far too dark before v1.3.2:

1. **Encoding of the captured buffer.** Both capture sources request
   `R16G16B16A16_FLOAT`, and that buffer is **always linear scRGB — also when the display is
   in SDR mode**. This is measured, not assumed: painting sRGB greys 255/192/128/64 on an SDR
   display and reading the duplicated FP16 buffer back yields `1.0000 / 0.5273 / 0.2158 /
   0.0513`, i.e. exactly `srgb_to_linear(v)`. Never sRGB-decode this buffer — that applies a
   second gamma and the output ends up at the *linear* value (mid-grey 128 → 55).
2. **Value range.** Whether anything reaches above the white point. That depends on the
   display's HDR state, which is what `ICaptureSource.InputIsHdr` reports.

The shader therefore takes three explicit inputs instead of one overloaded `InputIsHdr` flag,
computed in `Renderer.ResolveSourceLight`:

| cbuffer field | HDR source | SDR source |
|---|---|---|
| `SrcIsLinear` | `1` — no sRGB decode (both sources deliver FP16) | `1` |
| `SrcScale` — buffer value → working light, `1.0` = white point | `80 / target_paperwhite` (scRGB: `1.0` = 80 nits) | `1.0` (white already *is* the white point) |
| `SrcPeak` — source peak in white-point units | `max(source_peak_nits / target_paperwhite, 1)` | `1.0` |

Tone mapping runs only when `SrcPeak > 1`, so an SDR source is passed through 1:1 instead of
being squashed a second time. Consequence: **`source_peak_nits` and `target_paperwhite` only
affect an HDR source**; with an SDR source only exposure/saturation/contrast/gamma do anything.

`InputIsHdr` is re-read at runtime via `ICaptureSource.RefreshSourceState()` — periodically from
the render loop and after every `Recover()`, since a display mode change (HDR on/off is one) is
the most common cause of capture loss. Toggling HDR therefore no longer needs a render restart.
`WindowCapture` implements it as a no-op: its HDR state comes from the monitor the window sat on
at start, and the window selection is a structural parameter anyway.

## Capture sources

The thing being mirrored sits behind a small seam, `ICaptureSource` (a FP16 SRV + size +
HDR flag + cursor info + `TryAcquire`/`Recover`/`ApplyLiveConfig`/`RefreshSourceState`).
Everything downstream —
tonemap, layout, crop, present — is identical regardless of source, so adding a source type
doesn't touch the renderer or the engine loop. `capture_mode` (structural) selects which:

- **`monitor`** (default) — `DuplicationCapture`: whole-monitor **DXGI Desktop Duplication**, as
  above. The render device is created on the *source* adapter (Duplication is adapter-bound).
- **`window`** — `WindowCapture`: a single window / fullscreen app via **Windows.Graphics.Capture
  (WGC)** — the same OS API behind OBS' "Windows 10 (1903+)" window capture, *not* injection.
  Frames arrive as `R16G16B16A16_Float` D3D11 textures (free-threaded frame pool, polled with
  `TryGetNextFrame` from the render thread) → copied into the same shader-readable SRV the
  Duplication path produces. The render device is created on the *target* adapter; WGC delivers
  the window content across adapters via the DWM, which is why it's robust on multi-GPU rigs where
  injection-based capture goes black. The cursor (if enabled) is composited by WGC into the frame
  (`IsCursorCaptureEnabled`), so the renderer's separate cursor pass is unused in this mode; the
  yellow capture border is disabled where the API exists (Win11 22000+). The window is selected by
  **stable identity** (process exe + title, see `WindowEnum`), so it survives the non-stable HWNDs
  across app launches — and only that window is ever shown, so alt-tabbing never reveals the
  desktop. HDR is inferred from the monitor the window currently sits on.

## Resilience — capture-loss recovery

DXGI Desktop Duplication can lose access at runtime (`DXGI_ERROR_ACCESS_LOST`): a source-side
mode/refresh change, a game going fullscreen-exclusive, the UAC secure desktop, or a cross-GPU
device hiccup. Recovery is two-tiered so a transient loss never freezes the picture:

1. **Fast path (`MirrorEngine.TryRecoverCapture`)** — re-create just the duplication a few times
   with a short delay (`DuplicationCapture.Recreate`). Handles the common transient case without
   tearing the window down. On failure, `Recreate` leaves the capture in a clean *dead* state
   (`_dup == null`) — so `TryAcquire` returns `false` instead of touching a disposed COM object.
   The old code's silent `catch {}` left `_dup` dangling; subsequent calls first threw managed
   `NullReferenceException`s and then hit a **native access violation** in the D3D layer (freed
   memory) — a corrupted-state exception that managed `try/catch` can't intercept, so the whole
   process fast-failed and disappeared. That was the rare mid-stream crash this release fixes.
2. **Full reinit (supervisor loop in `RenderThreadMain`)** — if the fast path can't recover (e.g.
   the device itself was lost), the session is abandoned and the **whole render chain is rebuilt**
   (DXGI factory → adapter/output enumeration → device → window → renderer → capture) on a fresh
   `RunSession()`. Reinit uses **exponential back-off** (250 ms → 5 s, interruptible by `Stop()`)
   and a **retry cap** (`MaxConsecutiveReinit`); a session that ran healthy for a while
   (`HealthyFrameThreshold` frames) resets the budget, so only tight failure chains count toward
   the cap. If recovery genuinely fails, the engine stops cleanly with `LastError` set rather than
   hanging. Config-/selection errors (no displays, configured monitor unplugged) are terminal
   (`SessionResult.Fatal`) and are *not* retried — they surface their exact message instead.
3. **Window-procedure lifetime (`Win32Window`)** — the window class is registered **once per
   process** against a **static** window procedure held in a `static` field (rooted for the whole
   process), dispatching to the live instance by `HWND`. This closes a separate rare crash: an
   *instance* delegate handed to Win32 as a native callback formed a self-referential window↔delegate
   cycle the GC could collect mid-`DispatchMessage`, so Windows would invoke a freed callback and the
   CLR `FailFast`ed (`callback on a garbage collected delegate`) — a hard, log-less process death,
   made worse by every window after the first silently reusing the first window's delegate (a failed
   re-`RegisterClassEx`). Unrelated to the capture-loss reinit above.

## Source files

| File | Purpose |
|---|---|
| `Program.cs` | Entry point → tray app |
| `TrayContext.cs` | NotifyIcon + menu, owns the engine + HTTP agent |
| `SettingsForm.cs` | Local settings GUI (all parameters) |
| `MirrorEngine.cs` | Render loop (start/stop), display enumeration, live config reload |
| `Renderer.cs` | Swap chain + layout/crop geometry + shader pipeline (consumes `ICaptureSource`) |
| `ICaptureSource.cs` | Capture-source seam (FP16 SRV + size/HDR/cursor + acquire/recover/refresh) |
| `Capture.cs` | `DuplicationCapture` — DXGI Desktop Duplication (incl. optional cursor compositing) |
| `WindowCapture.cs` | `WindowCapture` — single window / fullscreen app via Windows.Graphics.Capture |
| `WindowEnum.cs` | Enumerate capturable windows + resolve a saved window by stable identity (exe+title) |
| `Shaders.cs` | HLSL (tonemap + crop UV + cursor blend) |
| `Win32Window.cs` | Window + mouse-lock hook (`WH_MOUSE_LL`, on its own dedicated thread so the cursor stays smooth — the hook is never serviced on the vsync-bound render thread) |
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

`tonemap_enabled`, `operator`, `source_peak_nits` and `target_paperwhite` are only in effect for
an **HDR** source — see [Colour pipeline](#colour-pipeline--hdr-vs-sdr-source).

`keep_awake` (default `true`) holds off display power-off + system sleep while the mirror is
running, via `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)`
on the render thread. It's set once the render loop actually starts (after source/target
resolve), toggled live on config reload, and cleared on stop — and because the hold is bound to
the render thread, Windows releases it automatically if that thread ever exits.

**Structural keys** (need a render restart): `output_bit_depth`, `capture_mode`, `window_exe`,
`window_title`, `source_display`, `target_display`, `output_mode`, `windowed`, `window_width`,
`window_height`, `output_width`, `output_height`.

`capture_mode` = `monitor` (default) or `window`; in window mode the source is the window matched
by `window_exe` + `window_title` (stable identity) instead of `source_display`.

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
| `GET /windows` | Capturable windows for the window-source picker (title, exe, pid) |

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
