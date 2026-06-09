# Roadmap

## Done in v1.2.1 ✓

- **Smooth cursor in `fullscreen_block`** — the mouse-lock's low-level hook moved off the
  vsync-bound render thread onto its own dedicated, fast-pumping thread. The cursor stays
  smooth at full polling rate (no more render-cadence throttling) while the mouse is still
  locked out of the target display.

## Done in v1.2.0 ✓

- **Keep the PC awake while mirroring** — the mirror now holds off display power-off and system
  sleep while it's running (`SetThreadExecutionState`, like a video player), so the capture-card
  display can't drop out because Windows blanked the screen. On by default, live-toggleable from
  Settings and the OBS dock, and released automatically when mirroring stops.

## Done in v1.0.2 ✓

- **Single-instance guard** — a second launch (or a stray double autostart) no longer creates a
  second tray icon + HTTP agent; it brings the running instance's settings to the front and exits.
- **Autostart unified on one mechanism** — installer and the Settings checkbox now both manage the
  same `HKCU\…\Run` value, so they can't create a double autostart and the checkbox always reflects
  reality. (Previously: installer = Startup-folder shortcut, Settings = Run value → could double up.)
- Tray icon shows status (green = mirroring, red = stopped); version in the settings title bar;
  About section (version / copyright / MIT / GitHub link); GitHub update check (notify only).

## Done in v1.1.0 ✓

- **OBS browser dock** — the HTTP agent serves a small control page (status, start/stop, monitor
  pick, live crop & image sliders) at `/` and `/dock` that you can add as an **OBS custom browser
  dock** or open in any browser. Same-origin → no CORS, no extra hosting. Reaches everyone who runs
  OBS, no separate app needed.

## Later / nice-to-have

- **Stream Deck integration** — the HTTP API already lets you bind buttons to `start` / `stop` /
  `config` (on-the-fly crops!) today via a generic web-request plugin; a dedicated Stream Deck
  plugin with status feedback + dial controls (live crop / saturation) would be the premium step.
- **Code signing** — sign the installer + exe (Authenticode) to remove the Windows SmartScreen
  "Windows protected your PC" warning on first run.
- **DisplayFusion profile hook** — optionally load a monitor profile when the mirror starts, so
  the capture-card display is activated automatically before mirroring.
- **Wake-from-off (advanced)** — document a Wake-on-LAN + auto-login path so a controller (e.g.
  the RitschyBot Cockpit) can bring the machine up and have the agent auto-start, enabling
  control even when the PC was fully off.
- **In-app auto-update (optional)** — beyond the current notify-only check, optionally download &
  launch the new installer from within the app.
