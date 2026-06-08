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
    public string Name { get; set; } = "";
    public string Resolution { get; set; } = "";
    public bool Hdr { get; set; }
    public string Adapter { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
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

    // ── Render-Thread ─────────────────────────────────────────────────────
    private void RenderThreadMain()
    {
        try { RenderLoop(); }
        catch (Exception ex) { LastError = ex.Message; Log("FATAL (Render): " + ex); }
    }

    private void RenderLoop()
    {
        var cfg = MirrorConfig.Load(ConfigPath);
        Log("RitschyMirror Render-Start.");

        var factory = CreateDXGIFactory2<IDXGIFactory2>(false);

        // ALLE Adapter + Outputs enumerieren (Quelle/Ziel koennen an versch. Adaptern haengen).
        var adapters = new List<IDXGIAdapter1>();
        var displays = new List<(IDXGIAdapter1 Adapter, IDXGIOutput6 Output,
                                 int L, int T, int R, int B, bool Hdr, string Name)>();
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
                displays.Add((ad, o6, dc.Left, dc.Top, dc.Right, dc.Bottom, hdr, $"{d.DeviceName} [{adName}]"));
                outp.Dispose();
                Log($"  Display {displays.Count - 1}: {d.DeviceName}  {dc.Right - dc.Left}x{dc.Bottom - dc.Top}  HDR={hdr}  [{adName}]");
            }
        }
        if (displays.Count == 0) { Log("Keine Displays gefunden."); LastError = "Keine Displays gefunden"; factory.Dispose(); return; }

        int srcIdx = Math.Clamp(cfg.SourceDisplay, 0, displays.Count - 1);
        int dstIdx = Math.Clamp(cfg.TargetDisplay, 0, displays.Count - 1);
        var src = displays[srcIdx];
        var dst = displays[dstIdx];
        string mode = cfg.ResolveOutputMode();
        Log($"Quelle = Display {srcIdx} ({src.Name}), Ziel = Display {dstIdx} ({dst.Name}), output_mode={mode}, layout={cfg.LayoutMode}");

        // Device auf dem QUELL-Adapter (Desktop Duplication ist adapter-gebunden).
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11CreateDevice(src.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        // Fenstergeometrie nach output_mode
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
        Log("Schritt: Capture/Duplication...");
        var capture = new DuplicationCapture(device, src.Output);
        Log($"Capture {capture.Width}x{capture.Height} HDR={capture.InputIsHdr} → Ausgabe {outW}x{outH} ({(cfg.OutputBitDepth >= 10 ? "10bit" : "8bit")})");

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

        DateTime lastCfgWrite = SafeWriteTime();
        int frame = 0;

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
                    Log("Config neu geladen (Live-Parameter).");
                }
            }

            try { capture.TryAcquire(context); }
            catch (Exception ex)
            {
                Log("Capture verloren, neu aufsetzen: " + ex.Message);
                try { capture.Recreate(src.Output); } catch { }
            }

            if (capture.Srv != null)
            {
                renderer.Render(capture.Srv, capture.Width, capture.Height, cfg, capture.InputIsHdr);
                renderer.Present(cfg.Vsync);
            }
        }

        Log("Render beendet, raeume auf.");
        window.DisableCursorBlock();
        if (exclusive) renderer.ExitFullscreen(); // VOR Swapchain-Dispose (DXGI-Pflicht)
        capture.Dispose();
        renderer.Dispose();
        context.Dispose();
        device.Dispose();
        foreach (var disp in displays) disp.Output.Dispose();
        foreach (var ad in adapters) ad.Dispose();
        factory.Dispose();
    }

    private DateTime SafeWriteTime()
    {
        try { return File.GetLastWriteTimeUtc(ConfigPath); } catch { return DateTime.MinValue; }
    }

    // ── Display-Enumeration ohne laufendes Rendern (GUI-Dropdowns + Agent) ──
    public static List<DisplayInfo> EnumerateDisplays()
    {
        var result = new List<DisplayInfo>();
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
                    result.Add(new DisplayInfo
                    {
                        Index = result.Count,
                        Name = d.DeviceName,
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
