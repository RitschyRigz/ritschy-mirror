using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace RitschyMirror;

/// <summary>
/// DXGI Desktop Duplication eines Outputs. Liefert pro Frame eine Shader-lesbare
/// Kopie (R16G16B16A16_Float) als SRV. HDR-faehig via DuplicateOutput1 mit FP16.
///
/// Der Maus-Cursor ist im Desktop-Bild NICHT enthalten (DXGI liefert ihn separat:
/// Position + Shape). Wird hier optional eingelesen und als BGRA-Textur bereitgestellt,
/// damit der Renderer ihn auf Wunsch einkomponiert (output.show_cursor).
/// </summary>
public sealed class DuplicationCapture : ICaptureSource
{
    private readonly ID3D11Device _device;
    private readonly IDXGIOutput6 _output;   // für Recover() (Duplication ist output-gebunden)
    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _copyTex;
    private ID3D11ShaderResourceView? _srv;

    // Cursor
    private ID3D11Texture2D? _cursorTex;
    private ID3D11ShaderResourceView? _cursorSrv;
    private byte[]? _shapeBuf;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool InputIsHdr { get; private set; }
    public ID3D11ShaderResourceView? Srv => _srv;

    public bool CursorVisible { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public int CursorW { get; private set; }
    public int CursorH { get; private set; }
    public ID3D11ShaderResourceView? CursorSrv => _cursorSrv;

    public DuplicationCapture(ID3D11Device device, IDXGIOutput6 output)
    {
        _device = device;
        _output = output;
        var desc = output.Description1;
        Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        InputIsHdr = desc.ColorSpace == ColorSpaceType.RgbFullG10NoneP709
                  || desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        _dup = output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
    }

    /// <summary>True wenn ein neues Frame geholt+kopiert wurde; false bei Timeout.
    /// Ist die Duplication (nach fehlgeschlagenem Recreate) tot, liefert die Methode false,
    /// statt auf einer Null-Referenz zu werfen — der Render-Loop entscheidet dann über Reinit.</summary>
    public bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16)
    {
        var dup = _dup;
        if (dup is null) return false;
        Result r = dup.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo frameInfo, out IDXGIResource? resource);
        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        r.CheckError();

        try
        {
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureTarget(tex.Description);
            ctx.CopyResource(_copyTex!, tex);
            UpdateCursor(frameInfo);
        }
        finally
        {
            resource?.Dispose();
            dup.ReleaseFrame();
        }
        return true;
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
        Width = (int)src.Width;
        Height = (int)src.Height;
    }

    // ── Cursor: Position + Shape einlesen ─────────────────────────────────
    private void UpdateCursor(OutduplFrameInfo fi)
    {
        var dup = _dup;
        if (dup is null) return;
        try
        {
            if (fi.LastMouseUpdateTime != 0)
            {
                CursorVisible = fi.PointerPosition.Visible;
                CursorX = fi.PointerPosition.Position.X;
                CursorY = fi.PointerPosition.Position.Y;
            }
            if (fi.PointerShapeBufferSize == 0)
                return;

            int size = (int)fi.PointerShapeBufferSize;
            if (_shapeBuf == null || _shapeBuf.Length < size) _shapeBuf = new byte[size];
            var handle = GCHandle.Alloc(_shapeBuf, GCHandleType.Pinned);
            try
            {
                dup.GetFramePointerShape((uint)size, handle.AddrOfPinnedObject(), out uint _, out OutduplPointerShapeInfo info);
                BuildCursorTexture(_shapeBuf, info);
            }
            finally { handle.Free(); }
        }
        catch { /* Cursor optional — bei Fehlern einfach nicht zeichnen */ }
    }

    private void BuildCursorTexture(byte[] buf, OutduplPointerShapeInfo info)
    {
        int type = (int)info.Type;   // 1=MONOCHROME, 2=COLOR, 4=MASKED_COLOR
        int w = (int)info.Width;
        int h = (type == 1) ? (int)info.Height / 2 : (int)info.Height;
        int pitch = (int)info.Pitch;
        if (w <= 0 || h <= 0) return;

        var bgra = new byte[w * h * 4];
        if (type == 2 || type == 4) // COLOR / MASKED_COLOR
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * pitch + x * 4;
                    int d = (y * w + x) * 4;
                    bgra[d] = buf[s]; bgra[d + 1] = buf[s + 1]; bgra[d + 2] = buf[s + 2];
                    bgra[d + 3] = (type == 2) ? buf[s + 3] : (byte)255; // MASKED_COLOR → opak
                }
        }
        else // MONOCHROME (AND-Maske + XOR-Maske, 1bpp)
        {
            int xorOff = h * pitch;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int bytePos = x / 8, bit = 7 - (x % 8);
                    int a = (buf[y * pitch + bytePos] >> bit) & 1;
                    int xo = (buf[xorOff + y * pitch + bytePos] >> bit) & 1;
                    int d = (y * w + x) * 4;
                    if (a == 0 && xo == 0) { bgra[d + 3] = 255; }                                   // schwarz opak
                    else if (a == 0 && xo == 1) { bgra[d] = bgra[d + 1] = bgra[d + 2] = bgra[d + 3] = 255; } // weiss opak
                    else if (a == 1 && xo == 0) { bgra[d + 3] = 0; }                                 // transparent
                    else { bgra[d + 3] = 255; }                                                       // invert → schwarz opak (Naeherung)
                }
        }

        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.None,
        };
        var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(pin.AddrOfPinnedObject(), (uint)(w * 4));
            _cursorTex = _device.CreateTexture2D(desc, new[] { data });
            _cursorSrv = _device.CreateShaderResourceView(_cursorTex);
            CursorW = w; CursorH = h;
        }
        finally { pin.Free(); }
    }

    /// <summary>Duplication nach AccessLost neu aufsetzen (am gespeicherten Output).
    /// true = erfolgreich neu aufgesetzt; false = fehlgeschlagen (z.B. Device verloren / weiterhin
    /// kein Zugriff) — die Capture bleibt dann in einem SAUBEREN toten Zustand (`_dup == null`),
    /// statt als Halbleiche im Render-Loop eine Null-Ref-Endlosschleife auszulösen. Der Aufrufer
    /// soll bei false die ganze Render-Kette (Device + Capture) neu bauen.</summary>
    public bool Recover()
    {
        try { _dup?.Dispose(); } catch { /* schon hin — egal */ }
        _dup = null;
        try
        {
            _dup = _output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
            return true;
        }
        catch
        {
            _dup = null;
            return false;
        }
    }

    /// <summary>No-op: bei Monitor-Capture komponiert der Renderer den Cursor (separat von DXGI),
    /// es gibt keinen quellseitigen Live-Zustand nachzuziehen.</summary>
    public void ApplyLiveConfig(MirrorConfig cfg) { }

    public void Dispose()
    {
        _cursorSrv?.Dispose();
        _cursorTex?.Dispose();
        _srv?.Dispose();
        _copyTex?.Dispose();
        _dup?.Dispose();
    }
}
