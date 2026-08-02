namespace RitschyMirror;

/// <summary>
/// HLSL-Quelltext, zur Laufzeit via D3DCompiler kompiliert. Vertex-Shader zeichnet
/// ein bildschirmfuellendes Dreieck (ohne Vertex-Buffer); Pixel-Shader macht
/// HDR→SDR-Tonemapping + Bildkorrekturen. Die cbuffer-Felder MUESSEN feldweise zur
/// Renderer-Params-Struktur passen (gleiche Reihenfolge/Groesse, 16-Byte-aligned).
/// </summary>
public static class Shaders
{
    public const string Hlsl = @"
Texture2D    Src : register(t0);
SamplerState Smp : register(s0);

cbuffer Params : register(b0)
{
    float Exposure;          // Belichtung in Stops
    float SrcPeak;           // Quell-Spitzenluminanz in WEISSPUNKT-Einheiten (1.0 = kein Headroom)
    float TargetPaperwhite;  // SDR-Weisspunkt in nits (nur noch fuer den HDR-Ausgabe-Pfad)
    float Saturation;
    float Contrast;
    float Gamma;
    int   OperatorId;        // 0 bt2390, 1 reinhard, 2 hable, 3 aces
    int   TonemapEnabled;    // 0/1
    int   SrcIsLinear;       // 1 = Puffer ist bereits linear (FP16/scRGB), 0 = sRGB-kodiert
    int   OutputIsHdr;       // 1 = HDR-Passthrough (kein Tonemap, scRGB raus)
    float2 CropMin;          // Quell-Crop in UV (0..1), Default (0,0)
    float2 CropMax;          // Quell-Crop in UV (0..1), Default (1,1)
    float SrcScale;          // Puffer-Wert → Arbeitslicht (1.0 = Weisspunkt)
    float _pad;
};

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2); // (0,0) (2,0) (0,2)
    o.uv  = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// ── Farb-Helfer ───────────────────────────────────────────────────────────
float3 SrgbToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}
float3 LinearToSrgb(float3 c)
{
    c = saturate(c);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}
float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// ── Tonemap-Operatoren (Eingabe linear, 1.0 = Weisspunkt) ──────────────────
float3 TmReinhard(float3 x, float wp)
{
    // Extended Reinhard mit Weisspunkt
    float3 num = x * (1.0 + x / (wp * wp));
    return num / (1.0 + x);
}
float3 TmHable(float3 x)
{
    const float A=0.15,B=0.50,C=0.10,D=0.20,E=0.02,F=0.30;
    return ((x*(A*x+C*B)+D*E)/(x*(A*x+B)+D*F))-E/F;
}
float3 Hable(float3 x, float wp)
{
    float3 w = TmHable(float3(wp,wp,wp));
    return TmHable(x) / w;
}
float3 TmAces(float3 x)
{
    const float a=2.51,b=0.03,c=2.43,d=0.59,e=0.14;
    return saturate((x*(a*x+b))/(x*(c*x+d)+e));
}
// Vereinfachtes BT.2390-artiges Roll-off auf der Luminanz
float3 TmBt2390(float3 x, float wp)
{
    float L = max(Luminance(x), 1e-5);
    float Lt = (L * (1.0 + L / (wp * wp))) / (1.0 + L); // weicher Knie-Verlauf
    return x * (Lt / L);
}

float4 PSMain(VSOut i) : SV_Target
{
    // Quell-Crop: sichtbare UV (0..1 ueber den Viewport) auf das Crop-Rechteck der
    // Quelle abbilden. Ohne Crop ist CropMin=(0,0)/CropMax=(1,1) → Identitaet.
    float2 suv = CropMin + i.uv * (CropMax - CropMin);
    float3 col = Src.Sample(Smp, suv).rgb;

    // Eingangsfarbraum -> lineares Arbeitslicht (1.0 = Weisspunkt).
    // WICHTIG: DXGI Desktop Duplication und WGC liefern mit R16G16B16A16_FLOAT IMMER
    // lineares scRGB — auch wenn der Monitor im SDR-Modus laeuft (nachgemessen). Ein
    // sRGB-Dekodieren waere dort ein ZWEITES Gamma und macht das Bild dramatisch zu
    // dunkel. Dekodiert wird also nur ein wirklich sRGB-kodierter Puffer (8-bit-Quellen).
    if (SrcIsLinear)
        col = max(col, 0);
    else
        col = SrgbToLinear(saturate(col));
    col *= SrcScale;   // scRGB: 80/Weisspunkt · SDR-Quelle: 1.0 (Weiss ist schon Weiss)

    col *= exp2(Exposure);

    // Tonemapping nur, wenn die Quelle ueberhaupt Headroom UEBER dem Weisspunkt hat.
    // Eine SDR-Quelle (SrcPeak == 1) hat keinen — sie zu rollen wuerde ein bereits
    // fertiges 1:1-Bild bloss abdunkeln. So merkt der Tonemapper selbst, ob HDR anliegt.
    if (TonemapEnabled && !OutputIsHdr && SrcPeak > 1.0001)
    {
        float wp = SrcPeak;
        if      (OperatorId == 1) col = TmReinhard(col, wp);
        else if (OperatorId == 2) col = Hable(col, wp);
        else if (OperatorId == 3) col = TmAces(col);
        else                      col = TmBt2390(col, wp);
    }

    // Saettigung
    float l = Luminance(col);
    col = lerp(float3(l, l, l), col, Saturation);
    // Kontrast um Mittelgrau
    col = (col - 0.18) * Contrast + 0.18;
    col = max(col, 0);
    // Gamma-Trim
    col = pow(max(col, 1e-6), 1.0 / max(Gamma, 1e-3));

    if (OutputIsHdr)
        return float4(col * TargetPaperwhite / 80.0, 1); // zurueck nach scRGB
    return float4(LinearToSrgb(col), 1);
}

// Maus-Cursor: bereits dekodierte BGRA-Textur (display-referred) direkt mit Alpha ausgeben.
float4 PSCursor(VSOut i) : SV_Target
{
    return Src.Sample(Smp, i.uv);
}
";
}
