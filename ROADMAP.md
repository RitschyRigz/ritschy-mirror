# Roadmap

## Planned for v1.0.2

- **Single-instance guard.** If a second copy is launched — two autostart entries, or a manual
  double-click while it's already running — detect the existing instance via a named mutex and
  exit gracefully (optionally focus the existing tray / settings window) instead of starting a
  second tray icon + HTTP agent. A second agent collides on port 8788 and falls back to a
  localhost-only listener, which is confusing.

- **Unify autostart on one mechanism.** The installer's "start at login" task and the Settings
  checkbox currently use *different* mechanisms:
  - installer → Startup-folder shortcut (`shell:startup\RitschyMirror.lnk`)
  - Settings checkbox → `HKCU\…\Run` value

  Enabling both creates a **double autostart** (two instances at login). Make both manage the
  **same** `HKCU\…\Run` entry so the Settings checkbox is the single source of truth, and a
  reinstall/update can't reintroduce a duplicate. (Observed 2026-06-08: an installed copy had
  both a Startup shortcut and a Run value pointing at the same exe → two instances at next login.)

## Later / nice-to-have

- **Code signing** — sign the installer + exe (Authenticode) to remove the Windows SmartScreen
  "Windows protected your PC" warning on first run.
- **DisplayFusion profile hook** — optionally load a monitor profile when the mirror starts, so
  the capture-card display is activated automatically before mirroring.
- **Wake-from-off (advanced)** — document a Wake-on-LAN + auto-login path so a controller (e.g.
  the RitschyBot Cockpit) can bring the machine up and have the agent auto-start, enabling
  control even when the PC was fully off.
