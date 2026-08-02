using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace RitschyMirror;

/// <summary>Ein enumeriertes Display (fuer GUI-Dropdowns + Agent /displays).</summary>
public sealed class DisplayInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";        // GDI-Name, z.B. \\.\DISPLAY2
    public string Friendly { get; set; } = "";     // EDID-Name, z.B. „LG ULTRAGEAR" (leer = unbekannt)
    public string Key { get; set; } = "";          // stabile Identität (monitorDevicePath, sonst Fallback)
    public string Resolution { get; set; } = "";
    public bool Hdr { get; set; }
    public string Adapter { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }

    /// <summary>Anzeigename: Friendly bevorzugt, sonst GDI-Name.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Friendly) ? Name : Friendly;
}

/// <summary>
/// Steuerbare Render-Engine: Start/Stop des Monitor-Mirrors auf einem eigenen Thread
/// (eigenes Win32-Fenster + Message-Pump + D3D-Loop). Frueher war das der Inhalt von
/// Program.Run(); jetzt on-demand startbar (Tray-Menue, lokale GUI, HTTP-Agent).
///
/// Die Konfiguration kommt aus mirror_config.json und wird im Loop live nachgeladen
/// (LIVE-Keys). Struktur-Aenderungen (Display/Aufloesung/output_mode) brauchen Restart().
/// </summary>
public sealed class MirrorEngine
{
    public string ConfigPath { get; }
    public string LogPath { get; }

    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _stop;
    public string LastError { get; private set; } = "";

    public MirrorEngine(string baseDir)
    {
        ConfigPath = Path.Combine(baseDir, "mirror_config.json");
        LogPath = Path.Combine(baseDir, "mirror.log");
    }

    public bool IsRunning => _thread is { IsAlive: true };

    public void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }

    // ── Steuerung ─────────────────────────────────────────────────────────
    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _stop = false;
            LastError = "";
            _thread = new Thread(RenderThreadMain) { IsBackground = true, Name = "MirrorRender" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { _stop = true; t = _thread; }
        if (t != null && t.IsAlive && Thread.CurrentThread != t)
            t.Join(4000);
        lock (_gate) { _thread = null; }
    }

    public void Restart() { Stop(); Start(); }

    // ── Stabile Monitor-Identität ─────────────────────────────────────────
    /// <summary>Stabiler Schlüssel für einen Monitor: bevorzugt der CCD-DevicePath, sonst
    /// EDID-Name, sonst GDI-Name. Wird in der Config als source_key/target_key abgelegt.</summary>
    public static string MakeKey(string devicePath, string friendly, string gdiName)
    {
        if (!string.IsNullOrWhiteSpace(devicePath)) return "path:" + devicePath;
        if (!string.IsNullOrWhiteSpace(friendly)) return "edid:" + friendly;
        return "gdi:" + gdiName;
    }

    /// <summary>
    /// Löst eine gespeicherte Monitor-Auswahl gegen die aktuell vorhandenen Displays auf.
    /// Reihenfolge: exakter key (DevicePath) → eindeutiger label (EDID-Name) → (nur wenn KEINE
    /// Identität gespeichert ist) der alte Index als Abwärtskompat-Fallback. Ist eine Identität
    /// gesetzt, passt aber kein Monitor ⇒ idx=-1 + Klartext-Fehler, damit NICHT still der falsche
    /// Bildschirm gespiegelt wird.
    /// </summary>
    public static (int idx, string error) ResolveSelection(
        string key, string label, int index,
        IReadOnlyList<(string Key, string Label)> displays, string role)
    {
        if (displays.Count == 0) return (-1, "Keine Displays gefunden");
        bool hasIdentity = !string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(label);
        if (hasIdentity)
        {
            if (!string.IsNullOrWhiteSpace(key))
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Key, key, StringComparison.OrdinalIgnoreCase)) return (i, "");
            if (!string.IsNullOrWhiteSpace(label))
            {
                int found = -1, count = 0;
                for (int i = 0; i < displays.Count; i++)
                    if (string.Equals(displays[i].Label, label, StringComparison.OrdinalIgnoreCase)) { found = i; count++; }
                if (count == 1) return (found, "");
            }
            string name = string.IsNullOrWhiteSpace(label) ? key : label;
            return (-1, $"{role}-Monitor '{name}' nicht verbunden");
        }
        return (Math.Clamp(index, 0, displays.Count - 1), ""); // alte Config ohne Identität
    }

    /// <summary>Vorab-Check (Tray/GUI): sind die konfigurierten Quelle+Ziel aktuell auflösbar?
    /// null = ok, sonst Klartext-Fehler. Startet nichts.</summary>
    public string? Preflight()
    {
        var displays = EnumerateDisplays();
        if (displays.Count == 0) return "Keine Displays gefunden";
        var cfg = MirrorConfig.Load(ConfigPath);
        var ids = displays.Select(d => (Key: d.Key, Label: d.Friendly)).ToList();
        var (_, se) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, "Quell");
        if (se.Length != 0) return se;
        var (_, de) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, "Ziel");
        if (de.Length != 0) return de;
        return null;
    }

    // ── Render-Thread ─────────────────────────────────────────────────────
    /// <summary>Ausgang einer Render-Session: sauber gestoppt, transient verloren (Reinit),
    /// oder fatal (Konfig-/Auswahl-Fehler — Reinit würde nichts bringen).</summary>
    private enum SessionResult { Stopped, Lost, Fatal }

    // Eine Session, die so viele Frames sauber gerendert hat, gilt als „lief gesund" → ein
    // danach folgender Verlust ist transient und setzt das Reinit-Budget zurück (nur enge
    // Fehlschlag-Ketten zählen Richtung Abbruch-Limit). ~5 s bei 60 fps.
    private const long HealthyFrameThreshold = 300;
    private const int MaxConsecutiveReinit = 8;

    private void RenderThreadMain()
    {
        int consecutive = 0;
        while (!_stop)
        {
            SessionResult result;
            long frames = 0;
            try
            {
                (result, frames) = RunSession();
            }
            catch (Exception ex)
            {
                // Harter Fehler beim Aufsetzen (z.B. Device/Capture-Erstellung) → wie ein
                // Verlust behandeln und mit Backoff erneut versuchen, statt den Thread zu killen.
                LastError = ex.Message;
                Log("FEHLER (Render-Session): " + ex);
                result = SessionResult.Lost;
            }

            if (_stop || result == SessionResult.Stopped || result == SessionResult.Fatal)
                break;

            // Lief die Session vorher lange genug gesund, war der Verlust transient → Budget zurück.
            if (frames >= HealthyFrameThreshold) consecutive = 0;
            consecutive++;
            if (consecutive > MaxConsecutiveReinit)
            {
                LastError = "Spiegelung ließ sich nach mehreren Versuchen nicht wiederherstellen — gestoppt.";
                Log("ABBRUCH: " + LastError);
                break;
            }

            // Exponentielles Backoff (250 ms → max 5 s), unterbrechbar durch Stop().
            int backoffMs = Math.Min(5000, 250 * (1 << Math.Min(consecutive - 1, 4)));
            Log($"Spiegelung verloren — Voll-Reinit in {backoffMs} ms (Versuch {consecutive}/{MaxConsecutiveReinit}).");
            InterruptibleSleep(backoffMs);
        }
        Log("Render-Thread beendet.");
    }

    /// <summary>Schlaf, der bei Stop() sofort abbricht (50-ms-Takt) — damit ein Backoff den
    /// Engine-Stop nicht um Sekunden verzögert.</summary>
    private void InterruptibleSleep(int ms)
    {
        for (int slept = 0; slept < ms && !_stop; slept += 50)
            Thread.Sleep(Math.Min(50, ms - slept));
    }

    /// <summary>Schneller Versuch, die Quelle nach einem Verlust (ACCESS_LOST / Frame-Pool-Fehler)
    /// OHNE Voll-Reinit wieder aufzusetzen — deckt den häufigen transienten Fall ab (Auflösungs-/
    /// Moduswechsel an der Quelle, Vollbild-App, UAC-Sicherheitsdesktop). false = nicht erholt
    /// → der Aufrufer bricht die Session ab und der Supervisor baut die ganze Kette neu.</summary>
    private bool TryRecoverCapture(ICaptureSource capture)
    {
        for (int attempt = 0; attempt < 5 && !_stop; attempt++)
        {
            if (capture.Recover()) { Log("Bildquelle wiederhergestellt."); return true; }
            InterruptibleSleep(120);
        }
        return false;
    }

    private (SessionResult result, long frames) RunSession()
    {
        LastError = "";  // optimistisch: ein geglückter Reinit soll keine Altmeldung stehen lassen
        var cfg = MirrorConfig.Load(ConfigPath);
        Log("RitschyMirror Render-Start.");

        var factory = CreateDXGIFactory2<IDXGIFactory2>(false);

        // ALLE Adapter + Outputs enumerieren (Quelle/Ziel koennen an versch. Adaptern haengen).
        var meta = MonitorNames.GetMonitorMeta();  // GDI-Name → {Friendly, DevicePath}
        var adapters = new List<IDXGIAdapter1>();
        var displays = new List<(IDXGIAdapter1 Adapter, IDXGIOutput6 Output,
                                 int L, int T, int R, int B, bool Hdr, string Name, string Friendly, string Key)>();
        for (uint a = 0; ; a++)
        {
            if (factory.EnumAdapters1(a, out IDXGIAdapter1? ad).Failure || ad is null) break;
            adapters.Add(ad);
            string adName = ad.Description1.Description;
            for (uint o = 0; ; o++)
            {
                if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                var o6 = outp.QueryInterface<IDXGIOutput6>();
                var d = o6.Description1;
                bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                        || d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                var dc = d.DesktopCoordinates;
                meta.TryGetValue(d.DeviceName, out var mm);
                string friendly = mm.Friendly ?? "";
                string key = MakeKey(mm.DevicePath ?? "", friendly, d.DeviceName);
                displays.Add((ad, o6, dc.Left, dc.Top, dc.Right, dc.Bottom, hdr, $"{d.DeviceName} [{adName}]", friendly, key));
                outp.Dispose();
                Log($"  Display {displays.Count - 1}: {d.DeviceName} ({(friendly.Length > 0 ? friendly : "?")})  {dc.Right - dc.Left}x{dc.Bottom - dc.Top}  HDR={hdr}  [{adName}]");
            }
        }
        if (displays.Count == 0) { Log("Keine Displays gefunden."); LastError = "Keine Displays gefunden"; factory.Dispose(); return (SessionResult.Fatal, 0); }

        // Ziel-Monitor immer über stabile Identität auflösen (beide Quellen-Modi spiegeln dorthin);
        // fehlt der konfigurierte Monitor → sauberer Abbruch mit Klartext.
        var ids = displays.Select(x => (Key: x.Key, Label: x.Friendly)).ToList();
        void CleanupEnum()
        {
            foreach (var disp in displays) disp.Output.Dispose();
            foreach (var ad in adapters) ad.Dispose();
            factory.Dispose();
        }
        var (dstIdx, dstErr) = ResolveSelection(cfg.TargetKey, cfg.TargetLabel, cfg.TargetDisplay, ids, "Ziel");
        if (dstErr.Length != 0)
        {
            Log("Start abgebrochen: " + dstErr);
            LastError = dstErr;
            CleanupEnum();
            return (SessionResult.Fatal, 0);
        }
        var dst = displays[dstIdx];
        string mode = cfg.ResolveOutputMode();
        string captureMode = cfg.ResolveCaptureMode();

        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        ID3D11Device device;
        ID3D11DeviceContext context;
        ICaptureSource capture;

        if (captureMode == "window")
        {
            // Fenster-Quelle (WGC): gespeicherte Identität → aktuelles HWND. Fehlt das Fenster
            // (App nicht offen) → sauberer Abbruch mit Klartext, statt blind etwas zu spiegeln.
            var (hwnd, winErr) = WindowEnum.Resolve(cfg.WindowExe, cfg.WindowTitle);
            if (winErr.Length != 0 || hwnd == IntPtr.Zero)
            {
                string e = winErr.Length != 0 ? winErr : "Kein Fenster ausgewählt";
                Log("Start abgebrochen: " + e);
                LastError = e;
                CleanupEnum();
                return (SessionResult.Fatal, 0);
            }
            // Device auf dem ZIEL-Adapter (Present same-adapter); WGC liefert das Fensterbild
            // adapter-übergreifend über den DWM — genau das macht es Cross-GPU-robust.
            D3D11CreateDevice(dst.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                out device, out context).CheckError();
            // HDR-Erkennung: hängt das Fenster (Mittelpunkt) auf einem HDR-Monitor? → Tonemap rollt Highlights.
            bool winHdr = false;
            if (GetWindowRect(hwnd, out RECT wr))
            {
                int cx = (wr.Left + wr.Right) / 2, cy = (wr.Top + wr.Bottom) / 2;
                foreach (var d in displays)
                    if (cx >= d.L && cx < d.R && cy >= d.T && cy < d.B) { winHdr = d.Hdr; break; }
            }
            Log($"Quelle = Fenster '{cfg.WindowTitle}' ({cfg.WindowExe}), Ziel = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}, HDR={winHdr}");
            capture = new WindowCapture(device, hwnd, winHdr, cfg.ShowCursor);
        }
        else
        {
            // Monitor-Quelle (Desktop Duplication, duplication-gebunden) → Device auf QUELL-Adapter.
            var (srcIdx, srcErr) = ResolveSelection(cfg.SourceKey, cfg.SourceLabel, cfg.SourceDisplay, ids, "Quell");
            if (srcErr.Length != 0)
            {
                Log("Start abgebrochen: " + srcErr);
                LastError = srcErr;
                CleanupEnum();
                return (SessionResult.Fatal, 0);
            }
            var src = displays[srcIdx];
            D3D11CreateDevice(src.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
                out device, out context).CheckError();
            Log($"Quelle = Display {srcIdx} ({src.Name}), Ziel = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}");
            capture = new DuplicationCapture(device, src.Output);
        }

        // Fenstergeometrie nach output_mode (immer vom ZIEL-Display)
        bool windowed = mode == "windowed";
        int x, y, outW, outH; bool borderless;
        if (windowed)
        {
            x = 100; y = 100; outW = cfg.WindowWidth; outH = cfg.WindowHeight; borderless = false;
        }
        else
        {
            x = dst.L; y = dst.T; outW = dst.R - dst.L; outH = dst.B - dst.T; borderless = true;
        }

        Log("Schritt: Fenster erstellen...");
        var window = new Win32Window("RitschyMirror", x, y, outW, outH, borderless);
        Log("Schritt: Renderer/Swapchain...");
        var renderer = new Renderer(factory, device, context, window.Hwnd, outW, outH, cfg.OutputBitDepth);
        Log($"Capture {capture.Width}x{capture.Height} HDR={capture.InputIsHdr} → Ausgabe {outW}x{outH} ({(cfg.OutputBitDepth >= 10 ? "10bit" : "8bit")})");
        Log(capture.InputIsHdr
            ? $"Quelle ist HDR → Tonemapping {(cfg.TonemapEnabled ? "aktiv" : "AUS")} (Spitze {cfg.SourcePeakNits:F0} nits auf Weisspunkt {cfg.TargetPaperwhite:F0} nits)."
            : "Quelle ist SDR → 1:1-Durchreichung, kein Tonemapping (source_peak_nits/target_paperwhite wirken hier nicht).");

        // Maus-Sperre / Exclusive je nach output_mode
        bool exclusive = false;
        if (!windowed && mode == "exclusive")
        {
            if (renderer.TrySetExclusiveFullscreen(dst.Output)) { exclusive = true; Log("Exclusive-Fullscreen aktiv."); }
            else { Log("Exclusive nicht moeglich (Cross-Adapter?) → Fallback fullscreen_block."); window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B); }
        }
        else if (!windowed && mode == "fullscreen_block")
        {
            window.EnableCursorBlock(dst.L, dst.T, dst.R, dst.B);
            Log("Maus-Sperre aktiv (fullscreen_block).");
        }

        // Ab hier wird wirklich gespiegelt → optional Bildschirm/Schlaf blockieren (Auto-Freigabe
        // beim Stop). Live umschaltbar: keep_awake wird im Hot-Reload unten nachgezogen.
        bool keepAwakeOn = false;
        if (cfg.KeepAwake) { KeepAwakeBegin(); keepAwakeOn = true; }

        DateTime lastCfgWrite = SafeWriteTime();
        bool lastSrcHdr = capture.InputIsHdr;
        int frame = 0;
        long framesRendered = 0;
        var sessionResult = SessionResult.Stopped;  // sauberer Default: Fenster zu / Stop()

        while (window.Running && !_stop)
        {
            window.PumpMessages();

            // Config-Hot-Reload (mtime-Polling). Nur LIVE-Parameter; Struktur braucht Restart.
            if ((frame++ & 31) == 0)
            {
                var t = SafeWriteTime();
                if (t != lastCfgWrite)
                {
                    lastCfgWrite = t;
                    var nc = MirrorConfig.Load(ConfigPath);
                    cfg.TonemapEnabled = nc.TonemapEnabled; cfg.Operator = nc.Operator;
                    cfg.SourcePeakNits = nc.SourcePeakNits; cfg.TargetPaperwhite = nc.TargetPaperwhite;
                    cfg.Exposure = nc.Exposure; cfg.Saturation = nc.Saturation;
                    cfg.Contrast = nc.Contrast; cfg.Gamma = nc.Gamma; cfg.ContentOffsetY = nc.ContentOffsetY;
                    cfg.Vsync = nc.Vsync;
                    cfg.LayoutMode = nc.LayoutMode;
                    cfg.CropX = nc.CropX; cfg.CropY = nc.CropY; cfg.CropW = nc.CropW; cfg.CropH = nc.CropH;
                    cfg.ShowCursor = nc.ShowCursor;
                    cfg.KeepAwake = nc.KeepAwake;
                    // Schlafmodus-Sperre live an/aus, ohne Render-Neustart.
                    if (cfg.KeepAwake && !keepAwakeOn) { KeepAwakeBegin(); keepAwakeOn = true; }
                    else if (!cfg.KeepAwake && keepAwakeOn) { KeepAwakeEnd(); keepAwakeOn = false; }
                    // Quellseitige Live-Parameter (z.B. WGC-Cursor-Aufnahme im Fenster-Modus).
                    capture.ApplyLiveConfig(cfg);
                    Log("Config neu geladen (Live-Parameter).");
                }
            }

            // HDR am Monitor an/aus im Betrieb nachziehen (ca. alle 256 Frames). Ohne das rechnet
            // der Shader nach einem Umschalten mit der falschen Quell-Kennlinie weiter: HDR-Quelle
            // als SDR = ausgefressen, SDR-Quelle als HDR = zu dunkel.
            if ((frame & 255) == 0)
            {
                capture.RefreshSourceState();
                if (capture.InputIsHdr != lastSrcHdr)
                {
                    lastSrcHdr = capture.InputIsHdr;
                    Log(lastSrcHdr
                        ? "Quelle wechselte auf HDR → Tonemapping-Pfad aktiv."
                        : "Quelle wechselte auf SDR → 1:1-Durchreichung, kein Tonemapping.");
                }
            }

            try { capture.TryAcquire(context); }
            catch (Exception ex)
            {
                // ACCESS_LOST o.ä. → erst schnelle Wiederherstellung der Quelle (transienter Fall).
                Log("Bildquelle verloren (" + ex.Message + ") → neu aufsetzen.");
                if (!TryRecoverCapture(capture))
                {
                    // Bleibt sie tot (z.B. Device verloren, Cross-Adapter) → KEIN Weiterspinnen,
                    // sondern Session abbrechen → Supervisor baut die ganze Kette neu.
                    Log("Duplication-Wiederherstellung fehlgeschlagen → Voll-Reinit der Render-Kette.");
                    sessionResult = SessionResult.Lost;
                    break;
                }
                continue;  // frische Duplication → nächste Runde sauber neu acquiren
            }

            if (capture.Srv != null)
            {
                renderer.Render(capture, cfg);
                renderer.Present(cfg.Vsync);
                framesRendered++;
            }
        }

        Log(sessionResult == SessionResult.Lost
            ? "Render-Session verloren, raeume fuer Reinit auf."
            : "Render beendet, raeume auf.");
        if (keepAwakeOn) KeepAwakeEnd();
        window.DisableCursorBlock();
        if (exclusive) renderer.ExitFullscreen(); // VOR Swapchain-Dispose (DXGI-Pflicht)
        capture.Dispose();
        renderer.Dispose();
        window.Destroy();                          // NACH Swapchain-Dispose: HWND nicht mehr referenziert
        context.Dispose();
        device.Dispose();
        foreach (var disp in displays) disp.Output.Dispose();
        foreach (var ad in adapters) ad.Dispose();
        factory.Dispose();
        return (sessionResult, framesRendered);
    }

    private DateTime SafeWriteTime()
    {
        try { return File.GetLastWriteTimeUtc(ConfigPath); } catch { return DateTime.MinValue; }
    }

    // ── Schlafmodus / Monitor-Abschaltung verhindern, solange gespiegelt wird ──
    // Wie ein Videoplayer: ES_DISPLAY_REQUIRED haelt den Bildschirm an, ES_SYSTEM_REQUIRED
    // verhindert das Einschlafen des Rechners. ES_CONTINUOUS macht den Zustand dauerhaft,
    // bis wir ihn (auf DEMSELBEN Thread) wieder zuruecknehmen. Beendet der Thread, faellt der
    // Zustand automatisch zurueck — ein gestopptes/abgestuerztes Mirroring blockiert also nie.
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    // Fensterrechteck (für die HDR-Erkennung im Fenster-Capture-Modus: auf welchem Monitor liegt es?).
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private void KeepAwakeBegin()
    {
        var r = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        Log(r == 0 ? "WARN: Schlafmodus-Sperre fehlgeschlagen (SetThreadExecutionState)."
                   : "Schlafmodus + Monitor-Abschaltung gesperrt (Mirroring laeuft).");
    }

    private void KeepAwakeEnd()
    {
        // Nur ES_CONTINUOUS = Anforderungen zuruecknehmen, normales Energieverhalten wieder erlauben.
        SetThreadExecutionState(ExecutionState.Continuous);
        Log("Schlafmodus-Sperre aufgehoben.");
    }

    // ── Display-Enumeration ohne laufendes Rendern (GUI-Dropdowns + Agent) ──
    public static List<DisplayInfo> EnumerateDisplays()
    {
        var result = new List<DisplayInfo>();
        var meta = MonitorNames.GetMonitorMeta();  // \\.\DISPLAYx → {Friendly, DevicePath}
        IDXGIFactory2? factory = null;
        try
        {
            factory = CreateDXGIFactory2<IDXGIFactory2>(false);
            for (uint a = 0; ; a++)
            {
                if (factory.EnumAdapters1(a, out IDXGIAdapter1? ad).Failure || ad is null) break;
                string adName = ad.Description1.Description;
                for (uint o = 0; ; o++)
                {
                    if (ad.EnumOutputs(o, out IDXGIOutput? outp).Failure || outp is null) break;
                    var o6 = outp.QueryInterface<IDXGIOutput6>();
                    var d = o6.Description1;
                    bool hdr = d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020
                            || d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709;
                    var dc = d.DesktopCoordinates;
                    meta.TryGetValue(d.DeviceName, out var mm);
                    string friendlyName = mm.Friendly ?? "";
                    result.Add(new DisplayInfo
                    {
                        Index = result.Count,
                        Name = d.DeviceName,
                        Friendly = friendlyName,
                        Key = MakeKey(mm.DevicePath ?? "", friendlyName, d.DeviceName),
                        Resolution = $"{dc.Right - dc.Left}x{dc.Bottom - dc.Top}",
                        Hdr = hdr,
                        Adapter = adName,
                        Left = dc.Left, Top = dc.Top, Right = dc.Right, Bottom = dc.Bottom,
                    });
                    o6.Dispose();
                    outp.Dispose();
                }
                ad.Dispose();
            }
        }
        catch { /* leere Liste = keine Displays ermittelbar */ }
        finally { factory?.Dispose(); }
        return result;
    }
}
