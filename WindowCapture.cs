using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace RitschyMirror;

/// <summary>
/// Aufnahme EINES Fensters / einer Vollbild-Anwendung über <b>Windows.Graphics.Capture</b> (WGC) —
/// dieselbe OS-API, die OBS' „Fenster-Aufnahme (Windows 10 1903+)" nutzt. Anders als OBS' „Game
/// Capture" wird NICHTS in den Spielprozess injiziert (kein Anti-Cheat-Risiko, keine Elevation-
/// Probleme): WGC zieht das Bild aus dem DWM-Kompositor. Das macht es zudem GPU-übergreifend
/// robust (genau der Cross-Adapter-Fall, an dem OBS' Game-Capture oft schwarz blieb).
///
/// Liefert die Frames als R16G16B16A16_Float-Textur → identisch zum Duplication-Pfad, daher steckt
/// es als <see cref="ICaptureSource"/> ohne Pipeline-Änderung in Renderer/Engine.
///
/// Sicherheits-Eigenschaft: Es wird NUR der Fensterinhalt gespiegelt — tabst du raus, läuft die
/// App im Hintergrund weiter und der Stream zeigt weiter sie, NICHT deinen Desktop.
/// </summary>
public sealed class WindowCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _d3dDevice;   // WinRT-Sicht auf dasselbe D3D11-Device
    private readonly GraphicsCaptureItem _item;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _copyTex;
    private ID3D11ShaderResourceView? _srv;

    private int _poolW, _poolH;
    private volatile bool _sourceLost;   // Fenster geschlossen (Closed-Event)
    private bool _showCursor;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool InputIsHdr { get; }
    public ID3D11ShaderResourceView? Srv => _srv;

    // Fenster-Capture bäckt den Cursor (falls aktiv) bereits ins Frame → der separate Cursor-Pass
    // des Renderers entfällt hier (immer „nicht sichtbar"). Steuerung läuft über die WGC-Session.
    public bool CursorVisible => false;
    public int CursorX => 0;
    public int CursorY => 0;
    public int CursorW => 0;
    public int CursorH => 0;
    public ID3D11ShaderResourceView? CursorSrv => null;

    public WindowCapture(ID3D11Device device, IntPtr hwnd, bool inputIsHdr, bool showCursor)
    {
        _device = device;
        InputIsHdr = inputIsHdr;
        _showCursor = showCursor;
        _d3dDevice = CreateDirect3DDevice(device);
        _item = CreateItemForWindow(hwnd);
        _item.Closed += (_, _) => _sourceLost = true;
        StartSession();
    }

    private void StartSession()
    {
        var size = _item.Size;
        _poolW = size.Width > 0 ? size.Width : 1;
        _poolH = size.Height > 0 ? size.Height : 1;
        if (Width == 0) { Width = _poolW; Height = _poolH; }   // vorläufige Maße bis zum ersten Frame
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3dDevice, DirectXPixelFormat.R16G16B16A16Float, 2, new SizeInt32 { Width = _poolW, Height = _poolH });
        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _showCursor;
        TryDisableBorder(_session);
        _session.StartCapture();
    }

    private void StopSession()
    {
        try { _session?.Dispose(); } catch { /* schon zu */ }
        try { _framePool?.Dispose(); } catch { }
        _session = null;
        _framePool = null;
    }

    /// <summary>Den gelben WGC-Aufnahmerand abschalten, wo die API es kennt (Win11, Build 22000+).
    /// Auf Windows 10 existiert die Property nicht → Rand bleibt (rein kosmetisch, kein Fehler).</summary>
    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
                && ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;
        }
        catch { /* nicht kritisch — Rand bleibt halt sichtbar */ }
    }

    public bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16)
    {
        if (_sourceLost)
            throw new InvalidOperationException("Aufnahme-Fenster wurde geschlossen");

        var pool = _framePool;
        if (pool is null) return false;

        using var frame = pool.TryGetNextFrame();
        if (frame is null) return false; // noch kein neues Frame (kein Fehler)

        using var surface = frame.Surface;
        using var tex = GetTextureFromSurface(surface);
        var desc = tex.Description;

        EnsureTarget(desc);
        ctx.CopyResource(_copyTex!, tex);
        Width = (int)desc.Width;
        Height = (int)desc.Height;

        // Fenstergröße geändert → Frame-Pool für die FOLGE-Frames nachziehen (dieses Frame ist schon kopiert).
        var content = frame.ContentSize;
        if (content.Width > 0 && content.Height > 0 && (content.Width != _poolW || content.Height != _poolH))
        {
            _poolW = content.Width; _poolH = content.Height;
            try { pool.Recreate(_d3dDevice, DirectXPixelFormat.R16G16B16A16Float, 2, content); } catch { /* nächster Versuch */ }
        }
        return true;
    }

    /// <summary>Session/Frame-Pool am selben Fenster neu aufsetzen (transiente Fehler).
    /// Ist das Fenster geschlossen → false: der Supervisor macht Voll-Reinit, der die Auswahl
    /// neu auflöst und dann sauber mit „Fenster nicht gefunden" stoppt (statt blind weiterzudrehen).</summary>
    public bool Recover()
    {
        if (_sourceLost) return false;
        try
        {
            StopSession();
            StartSession();
            return true;
        }
        catch
        {
            StopSession();
            return false;
        }
    }

    /// <summary>No-op: der HDR-Zustand kommt hier vom Monitor, auf dem das Fenster BEIM START lag
    /// (die Fensterwahl ist ohnehin ein Struktur-Parameter). Fenster auf einen Monitor mit anderem
    /// HDR-Zustand geschoben → Render-Neustart. Beim Monitor-Capture wird live nachgezogen.</summary>
    public void RefreshSourceState() { }

    /// <summary>Live-Config anwenden: Cursor-Aufnahme der WGC-Session ohne Neustart umschalten.</summary>
    public void ApplyLiveConfig(MirrorConfig cfg)
    {
        if (cfg.ShowCursor == _showCursor) return;
        _showCursor = cfg.ShowCursor;
        try { if (_session != null) _session.IsCursorCaptureEnabled = _showCursor; } catch { }
    }

    private void EnsureTarget(Texture2DDescription src)
    {
        if (_copyTex != null && _copyTex.Description.Width == src.Width && _copyTex.Description.Height == src.Height)
            return;

        _srv?.Dispose();
        _copyTex?.Dispose();

        var desc = src;
        desc.BindFlags = BindFlags.ShaderResource;
        desc.Usage = ResourceUsage.Default;
        desc.CPUAccessFlags = CpuAccessFlags.None;
        desc.MiscFlags = ResourceOptionFlags.None;
        _copyTex = _device.CreateTexture2D(desc);
        _srv = _device.CreateShaderResourceView(_copyTex);
    }

    public void Dispose()
    {
        StopSession();
        _srv?.Dispose();
        _copyTex?.Dispose();
        // _d3dDevice (WinRT-Wrapper) + _item werden vom GC/CsWinRT freigegeben; das eigentliche
        // ID3D11Device besitzt die Engine und entsorgt es selbst.
        (_d3dDevice as IDisposable)?.Dispose();
    }

    // ── WGC-/WinRT-Interop ────────────────────────────────────────────────────────
    // IID von GraphicsCaptureItem (für den Window-Interop-Factory-Aufruf).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    // IID von ID3D11Texture2D (um die D3D11-Textur aus der WGC-Surface zu ziehen).
    private static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);
        var item = GraphicsCaptureItem.FromAbi(abi);
        Marshal.Release(abi);
        return item;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgi = d3dDevice.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr ptr);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        Marshal.Release(ptr);
        return device;
    }

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = ID3D11Texture2DIid;
        IntPtr p = access.GetInterface(ref iid);
        return new ID3D11Texture2D(p);
    }
}
