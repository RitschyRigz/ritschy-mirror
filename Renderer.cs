using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;
using Vortice.Mathematics;

namespace RitschyMirror;

/// <summary>
/// Swapchain + Shader-Pipeline. Zeichnet die gespiegelte (ggf. tonemappte) Quelle
/// in den oberen Bereich des Ausgabeframes, Rest schwarz (21:9 → 16:9 oben).
/// </summary>
public sealed class Renderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderParams
    {
        public float Exposure, SourcePeakNits, TargetPaperwhite, Saturation, Contrast, Gamma;
        public int OperatorId, TonemapEnabled, InputIsHdr, OutputIsHdr;
        public float CropMinX, CropMinY, CropMaxX, CropMaxY;
        public float Pad0, Pad1;
    }

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11RenderTargetView _rtv = null!;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11PixelShader _psCursor;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11Buffer _cbuffer;
    private readonly ID3D11BlendState _blend;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Renderer(IDXGIFactory2 factory, ID3D11Device device, ID3D11DeviceContext ctx,
                    IntPtr hwnd, int width, int height, int bitDepth)
    {
        _device = device;
        _ctx = ctx;
        Width = width;
        Height = height;

        var format = bitDepth >= 10 ? Format.R10G10B10A2_UNorm : Format.B8G8R8A8_UNorm;
        var scDesc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = format,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, hwnd, scDesc);
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
        CreateRtv();

        // Shader kompilieren
        Compiler.Compile(Shaders.Hlsl, "VSMain", "tonemap.hlsl", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob is null) throw new Exception("VS-Compile: " + (vsErr?.AsString() ?? "unbekannt"));
        Compiler.Compile(Shaders.Hlsl, "PSMain", "tonemap.hlsl", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob is null) throw new Exception("PS-Compile: " + (psErr?.AsString() ?? "unbekannt"));
        Compiler.Compile(Shaders.Hlsl, "PSCursor", "tonemap.hlsl", "ps_5_0", out var pcBlob, out var pcErr);
        if (pcBlob is null) throw new Exception("PSCursor-Compile: " + (pcErr?.AsString() ?? "unbekannt"));
        _vs = _device.CreateVertexShader(vsBlob.AsBytes());
        _ps = _device.CreatePixelShader(psBlob.AsBytes());
        _psCursor = _device.CreatePixelShader(pcBlob.AsBytes());
        vsBlob.Dispose(); psBlob.Dispose(); pcBlob.Dispose();

        // Alpha-Blend fuer den Cursor (Straight-Alpha ueber das fertige Bild).
        _blend = _device.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha));

        _sampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        _cbuffer = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Default));
    }

    private void CreateRtv()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>
    /// Ein Frame zeichnen. Platzierung (Viewport) + Quell-Crop kommen aus
    /// <see cref="MirrorConfig.LayoutMode"/> — siehe <see cref="ComputeLayout"/>.
    /// Optional wird der Maus-Cursor (separat von DXGI geliefert) einkomponiert.
    /// </summary>
    public void Render(ICaptureSource cap, MirrorConfig cfg)
    {
        int srcW = cap.Width, srcH = cap.Height;
        var (vp, cropMinX, cropMinY, cropMaxX, cropMaxY) = ComputeLayout(srcW, srcH, cfg);

        var p = new ShaderParams
        {
            Exposure = cfg.Exposure,
            SourcePeakNits = cfg.SourcePeakNits,
            TargetPaperwhite = cfg.TargetPaperwhite,
            Saturation = cfg.Saturation,
            Contrast = cfg.Contrast,
            Gamma = cfg.Gamma,
            OperatorId = cfg.OperatorId,
            TonemapEnabled = cfg.TonemapEnabled ? 1 : 0,
            InputIsHdr = cap.InputIsHdr ? 1 : 0,
            OutputIsHdr = 0, // SDR-Ausgabe (Passthrough-HDR ist Phase 2)
            CropMinX = cropMinX, CropMinY = cropMinY,
            CropMaxX = cropMaxX, CropMaxY = cropMaxY,
        };
        _ctx.UpdateSubresource(p, _cbuffer);

        _ctx.ClearRenderTargetView(_rtv, new Color4(0f, 0f, 0f, 1f));

        // gemeinsame Pipeline-States
        _ctx.OMSetRenderTargets(_rtv);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetConstantBuffer(0, _cbuffer);

        // Haupt-Pass (opak)
        _ctx.OMSetBlendState(null);
        _ctx.RSSetViewport(vp);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResource(0, cap.Srv);
        _ctx.Draw(3, 0);

        // Cursor-Pass (alpha) — Quell-Cursorposition in den Ausgabe-Viewport mappen.
        if (cfg.ShowCursor && cap.CursorVisible && cap.CursorSrv != null && cap.CursorW > 0)
        {
            float crW = cropMaxX - cropMinX, crH = cropMaxY - cropMinY;
            float nx = ((float)cap.CursorX / srcW - cropMinX) / crW;
            float ny = ((float)cap.CursorY / srcH - cropMinY) / crH;
            float nw = ((float)cap.CursorW / srcW) / crW;
            float nh = ((float)cap.CursorH / srcH) / crH;
            if (nx < 1f && ny < 1f && nx + nw > 0f && ny + nh > 0f) // zumindest teils sichtbar
            {
                var cvp = new Viewport(vp.X + nx * vp.Width, vp.Y + ny * vp.Height,
                                       nw * vp.Width, nh * vp.Height, 0f, 1f);
                _ctx.OMSetBlendState(_blend);
                _ctx.RSSetViewport(cvp);
                _ctx.PSSetShader(_psCursor);
                _ctx.PSSetShaderResource(0, cap.CursorSrv);
                _ctx.Draw(3, 0);
                _ctx.OMSetBlendState(null);
            }
        }
    }

    /// <summary>
    /// Viewport (Platzierung im Ausgabeframe) + Quell-Crop (UV 0..1) aus dem layout_mode:
    ///   fit         — ganze Quelle seitenverhaeltnis-erhaltend zentriert (nichts abgeschnitten)
    ///   stretch     — Quelle fuellt den Frame (Seitenverhaeltnis ignoriert)
    ///   top_strip   — Quelle auf Breite, oben ausgerichtet, Rest schwarz (21:9→16:9)
    ///   crop_region — wie fit, aber nur das Quell-Rechteck crop_x/y/w/h
    /// content_offset_y verschiebt vertikal.
    /// </summary>
    private (Viewport vp, float cMinX, float cMinY, float cMaxX, float cMaxY)
        ComputeLayout(int srcW, int srcH, MirrorConfig cfg)
    {
        float W = Width, H = Height;
        string mode = (cfg.LayoutMode ?? "top_strip").Trim().ToLowerInvariant();

        // Crop-UVs — nur crop_region nutzt sie; sonst volle Quelle (0..1).
        float cMinX = 0f, cMinY = 0f, cMaxX = 1f, cMaxY = 1f;
        float effSrcW = srcW, effSrcH = srcH;
        if (mode == "crop_region")
        {
            cMinX = System.Math.Clamp(cfg.CropX, 0f, 1f);
            cMinY = System.Math.Clamp(cfg.CropY, 0f, 1f);
            cMaxX = System.Math.Clamp(cfg.CropX + cfg.CropW, 0f, 1f);
            cMaxY = System.Math.Clamp(cfg.CropY + cfg.CropH, 0f, 1f);
            if (cMaxX <= cMinX) cMaxX = System.Math.Min(1f, cMinX + 0.01f);
            if (cMaxY <= cMinY) cMaxY = System.Math.Min(1f, cMinY + 0.01f);
            effSrcW = srcW * (cMaxX - cMinX);
            effSrcH = srcH * (cMaxY - cMinY);
        }
        if (effSrcW < 1f) effSrcW = 1f;
        if (effSrcH < 1f) effSrcH = 1f;

        Viewport vp;
        switch (mode)
        {
            case "stretch":
                vp = new Viewport(0f, cfg.ContentOffsetY, W, H, 0f, 1f);
                break;
            case "top_strip":
            {
                float stripH = (float)System.Math.Round(W * (double)effSrcH / effSrcW);
                vp = new Viewport(0f, cfg.ContentOffsetY, W, stripH, 0f, 1f);
                break;
            }
            default: // "fit" und "crop_region": seitenverhaeltnis-erhaltend, zentriert
            {
                double srcAspect = (double)effSrcW / effSrcH;
                double outAspect = (double)W / H;
                float vpW, vpH;
                if (srcAspect > outAspect) { vpW = W; vpH = (float)(W / srcAspect); }
                else                       { vpH = H; vpW = (float)(H * srcAspect); }
                float ox = (W - vpW) / 2f;
                float oy = (H - vpH) / 2f + cfg.ContentOffsetY;
                vp = new Viewport(ox, oy, vpW, vpH, 0f, 1f);
                break;
            }
        }
        return (vp, cMinX, cMinY, cMaxX, cMaxY);
    }

    public void Present(bool vsync) => _swapChain.Present(vsync ? 1u : 0u, PresentFlags.None);

    /// <summary>
    /// Echtes DXGI-Exclusive-Fullscreen versuchen. Schlaegt fehl, wenn Ziel-Output an einem
    /// ANDEREN Adapter haengt als das Render-Device (Cross-Adapter) — dann false → Caller
    /// faellt auf borderless + Maus-Sperre zurueck.
    /// </summary>
    public bool TrySetExclusiveFullscreen(IDXGIOutput? output)
    {
        try { _swapChain.SetFullscreenState(true, output); return true; }
        catch { return false; }
    }

    /// <summary>Exclusive-Fullscreen verlassen — MUSS vor dem Swapchain-Dispose passieren (DXGI-Pflicht).</summary>
    public void ExitFullscreen()
    {
        try { _swapChain.SetFullscreenState(false, null); } catch { }
    }

    public void Dispose()
    {
        _rtv?.Dispose();
        _cbuffer?.Dispose();
        _blend?.Dispose();
        _sampler?.Dispose();
        _psCursor?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
        _swapChain?.Dispose();
    }
}
