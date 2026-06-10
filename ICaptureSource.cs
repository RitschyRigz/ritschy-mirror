using Vortice.Direct3D11;

namespace RitschyMirror;

/// <summary>
/// Gemeinsame Schnittstelle für eine Bildquelle, die der <see cref="Renderer"/> spiegelt —
/// unabhängig davon, WIE das Bild zustande kommt. Zwei Implementierungen:
///   • <see cref="DuplicationCapture"/> — ein ganzer Monitor (DXGI Desktop Duplication).
///   • <see cref="WindowCapture"/>      — ein einzelnes Fenster / eine Vollbild-App
///                                         (Windows.Graphics.Capture, wie OBS' Fenster-Aufnahme).
///
/// Der Renderer konsumiert nur diese Naht (eine FP16-SRV + Maße + Cursor-Infos); die ganze
/// Pipeline danach (Tonemap/Layout/Crop/Present) bleibt für beide Quellen identisch. Neue
/// Quellen-Typen lassen sich so anstecken, ohne Renderer oder Engine-Loop anzufassen.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Breite/Höhe der aktuellen Quelle in echten Pixeln (Monitor- bzw. Fenster-Inhalt).</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>True, wenn die Quelle HDR liefert → der Tonemap-Shader rollt die Highlights.</summary>
    bool InputIsHdr { get; }

    /// <summary>Shader-lesbare Kopie des letzten Frames (R16G16B16A16_Float). null = noch kein Frame.</summary>
    ID3D11ShaderResourceView? Srv { get; }

    // ── Maus-Cursor (separat von DXGI geliefert; nur Monitor-Capture nutzt das, Fenster-Capture
    //    bäckt den Cursor bereits ins Frame → liefert hier dauerhaft „nicht sichtbar"). ─────────
    bool CursorVisible { get; }
    int CursorX { get; }
    int CursorY { get; }
    int CursorW { get; }
    int CursorH { get; }
    ID3D11ShaderResourceView? CursorSrv { get; }

    /// <summary>Neues Frame holen + in die SRV kopieren. true = neues Frame, false = Timeout/keins.
    /// Wirft bei einem echten Verlust (z.B. ACCESS_LOST / Quelle weg) → der Render-Loop entscheidet
    /// dann über Wiederherstellung (<see cref="Recover"/>) bzw. Voll-Reinit.</summary>
    bool TryAcquire(ID3D11DeviceContext ctx, int timeoutMs = 16);

    /// <summary>Die Quelle nach einem Verlust ohne Voll-Reinit wieder aufsetzen (z.B. Duplication
    /// neu erzeugen / Frame-Pool neu anlegen). true = erholt, false = unwiederbringlich (Aufrufer
    /// baut die ganze Render-Kette neu). Hinterlässt bei false einen sauberen Zustand (kein Spin
    /// auf einer halbtoten Ressource).</summary>
    bool Recover();

    /// <summary>Live-Config (Hot-Reload) auf die Quelle anwenden, soweit sie quellseitig wirkt.
    /// Beispiel: Fenster-Capture schaltet die Cursor-Aufnahme der WGC-Session live um. Quellen ohne
    /// solchen Zustand (Monitor-Capture: Cursor macht der Renderer) implementieren das als No-op.</summary>
    void ApplyLiveConfig(MirrorConfig cfg);
}
