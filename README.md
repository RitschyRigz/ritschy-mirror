# RitschyMirror

Leichtgewichtiger Monitor-Mirror als OBS-Ersatz: greift einen Monitor ab, tonemappt
**HDR→SDR** und gibt das Bild auf ein Ziel-Display / eine Capture-Card aus. Eigenständiges,
**auf jedem Windows-PC** lokal installierbares Programm mit **Tray + Einstellungs-GUI** und
einem **lokalen HTTP-Steuer-Agenten** (Fernsteuerung z.B. aus dem RitschyBot-Cockpit).

## Was es kann
- **Render-Modi (`layout_mode`)** — frei wählbar, nichts hardware-hardcodiert:
  - `fit` — ganze Quelle seitenverhältnis-erhaltend, nichts abgeschnitten (1:1-Spiegel)
  - `stretch` — füllt den Frame (Seitenverhältnis ignoriert)
  - `top_strip` — Quelle oben, schwarzer Balken unten (21:9-Inhalt in 16:9-Frame)
  - `crop_region` — frei definierter Quell-Ausschnitt (`crop_x/y/w/h`)
- **Ausgabe-Modi (`output_mode`)**:
  - `windowed` — Testfenster · `borderless` — randloses Vollbild
  - `fullscreen_block` — randloses Vollbild **+ Maus-Sperre** (Cursor wird vom Ziel-Display
    ferngehalten; funktioniert auf jedem Setup, auch Cross-Adapter)
  - `exclusive` — echtes DXGI-Exclusive-Fullscreen; nur sauber wenn Quelle+Ziel am **selben
    Adapter** hängen, sonst **automatischer Fallback** auf `fullscreen_block`
- **Beliebige Monitore** per Dropdown (Quelle/Ziel), HDR-fähig, 8/10-bit-Ausgabe.
- **Tonemap** (`bt2390`/`reinhard`/`hable`/`aces`) + Bildkorrekturen (Belichtung/Sättigung/
  Kontrast/Gamma/Weißpunkt) — **live** verstellbar.

## Technik
- **C# / .NET 9** + **Vortice.Windows** (Direct3D11 / DXGI).
- **DXGI Desktop Duplication** (FP16, HDR-fähig) → HLSL-Tonemap-Shader → Flip-Model-Swapchain.
- Quelle+Ziel dürfen an **verschiedenen Adaptern** hängen (Device am Quell-Adapter; DWM macht
  den Cross-Adapter-Transfer im randlosen Modus).
- **Tray-App** (`TrayContext`): startet idle, hostet den Agenten; „Start" beginnt das Rendern
  auf eigenem Thread, „Stop" beendet es — die App bleibt im Tray (kein Scheduled-Task nötig).

## Architektur (Dateien)
| Datei | Zweck |
|---|---|
| `Program.cs` | Entry → Tray-App |
| `TrayContext.cs` | NotifyIcon + Menü, hält Engine + Agent |
| `SettingsForm.cs` | lokale Einstellungs-GUI (alle Parameter) |
| `MirrorEngine.cs` | Render-Loop (Start/Stop), Display-Enumeration |
| `Renderer.cs` | Swapchain + Layout/Crop-Geometrie + Shader-Pipeline |
| `Capture.cs` | DXGI Desktop Duplication |
| `Shaders.cs` | HLSL (Tonemap + Crop-UV) |
| `Win32Window.cs` | Fenster + Maus-Sperr-Hook (`WH_MOUSE_LL`) |
| `ControlAgent.cs` | HTTP-Steuer-Agent (HttpListener) |
| `MirrorConfig.cs` | Render-Config (`mirror_config.json`) |
| `AppSettings.cs` | App-Config (`app_settings.json`: Agent-Port/Bind, Autostart) |

## HTTP-Steuer-Agent
Lauscht auf `http://<bind>:<port>/` (Default Port `8788`, `bind`="+" = vom Netz erreichbar;
ohne URL-ACL Fallback auf `localhost`). Routen:

| Route | |
|---|---|
| `GET /health` | Ping |
| `GET /status` | `{running, state, config}` |
| `GET /config` · `POST /config` | Config lesen / Patch mergen (Whitelist) |
| `POST /start` · `/stop` · `/restart` | Rendern steuern |
| `GET /displays` | enumerierte Displays |

Das RitschyBot-Cockpit nutzt genau diese Routen (`cockpit/services/mirror_control.py`):
Connector `gaming_pc` → Feld **`agent_port`** gesetzt ⇒ Steuerung über HTTP; leer ⇒ WinRM-Fallback.

## Bauen / Installieren
```powershell
# 1) self-contained .exe bauen (ohne .NET am Ziel-PC):
powershell -ExecutionPolicy Bypass -File publish.ps1
# 2) lokal installieren (Kopie + Verknüpfungen; als Admin für Netz-Erreichbarkeit):
powershell -ExecutionPolicy Bypass -File install.ps1 -AgentPort 8788
```
Danach: Tray-Icon → **Einstellungen** (Quelle/Ziel/Modi/Bild) bzw. Fernsteuerung im Cockpit
(Connector `gaming_pc`: `host` = IP dieses PCs, `agent_port` = 8788).

## Konfiguration — `mirror_config.json` (live nachgeladen)
Bild/Layout (`tonemap_enabled`, `operator`, `saturation`, `contrast`, `gamma`, `exposure`,
`target_paperwhite`, `source_peak_nits`, `content_offset_y`, `layout_mode`, `crop_*`, `vsync`)
wirken **sofort**; Struktur (`source_display`, `target_display`, `output_mode`, `output_bit_depth`,
`window_*`) erst nach **Neustart** des Renderns. `app_settings.json` hält Agent-Port/-Bind + Autostart.

## Log
`mirror.log` neben der Exe listet beim Render-Start alle Outputs (Index, Name, Auflösung,
ColorSpace, Adapter) — Basis für die Display-Auswahl.
