# Roadmap

## Done in v1.0.2 ✓

- **Single-instance guard** — a second launch (or a stray double autostart) no longer creates a
  second tray icon + HTTP agent; it brings the running instance's settings to the front and exits.
- **Autostart unified on one mechanism** — installer and the Settings checkbox now both manage the
  same `HKCU\…\Run` value, so they can't create a double autostart and the checkbox always reflects
  reality. (Previously: installer = Startup-folder shortcut, Settings = Run value → could double up.)
- Tray icon shows status (green = mirroring, red = stopped); version in the settings title bar;
  About section (version / copyright / MIT / GitHub link); GitHub update check (notify only).

## Later / nice-to-have

- **Code signing** — sign the installer + exe (Authenticode) to remove the Windows SmartScreen
  "Windows protected your PC" warning on first run.
- **DisplayFusion profile hook** — optionally load a monitor profile when the mirror starts, so
  the capture-card display is activated automatically before mirroring.
- **Wake-from-off (advanced)** — document a Wake-on-LAN + auto-login path so a controller (e.g.
  the RitschyBot Cockpit) can bring the machine up and have the agent auto-start, enabling
  control even when the PC was fully off.
- **In-app auto-update (optional)** — beyond the current notify-only check, optionally download &
  launch the new installer from within the app.
