using Vortice.Direct3D11;
using Vortice.DXGI;
using SharpGen.Runtime;

namespace RitschyMirror;

/// <summary>
/// DXGI Desktop Duplication eines Outputs. Liefert pro Frame eine Shader-lesbare
/// Kopie (R16G16B16A16_Float) als SRV. HDR-faehig via DuplicateOutput1 mit FP16.
/// </summary>
public sealed class DuplicationCapture : IDisposable
{
    private readonly ID3D11Device _device;
    private IDXGIOutputDuplication _dup;
    private ID3D11Texture2D? _copyTex;
    private ID3D11ShaderResourceView? _srv;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool InputIsHdr { get; private set; }
    public ID3D11ShaderResourceView? Srv => _srv;

    public DuplicationCapture(ID3D11Device device, IDXGIOutput6 output)
    {
        _device = device;
        var desc = output.Description1;
        Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
        Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        // HDR aktiv? scRGB (G10) oder HDR10 (G2084) Colorspace am Output.
        InputIsHdr = desc.ColorSpace == ColorSpaceType.RgbFullG10NoneP709
                  || desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
        _dup = output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
    }

    /// <summary>True wenn ein neues Frame geholt+kopiert wurde; false bei Timeout.</summary>
    public bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16)
    {
        Result r = _dup.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out IDXGIResource? resource);
        if (r == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;
        r.CheckError();

        try
        {
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            EnsureTarget(tex.Description);
            ctx.CopyResource(_copyTex!, tex);
        }
        finally
        {
            resource?.Dispose();
            _dup.ReleaseFrame();
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

    /// <summary>Duplication nach AccessLost neu aufsetzen.</summary>
    public void Recreate(IDXGIOutput6 output)
    {
        _dup.Dispose();
        _dup = output.DuplicateOutput1(_device, 1, new[] { Format.R16G16B16A16_Float });
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _copyTex?.Dispose();
        _dup.Dispose();
    }
}
