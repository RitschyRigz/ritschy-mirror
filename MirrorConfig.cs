using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RitschyMirror;

/// <summary>
/// Laufzeit-Konfiguration, gelesen aus mirror_config.json.
///
/// Zwei Wirk-Klassen (siehe <see cref="LiveKeys"/> / <see cref="StructKeys"/>):
///   • LIVE   — Bild/Geometrie, wirkt sofort per Hot-Reload (FileWatcher in MirrorEngine).
///   • STRUCT — Aufloesung/Display/Fenster-Modus, wirkt erst bei Render-Neustart.
///
/// Die Datei ist die EINZIGE Wahrheit: lokale GUI, lokaler HTTP-Agent und (Fallback)
/// das Cockpit ueber WinRM schreiben alle dieselbe Datei; der Render-Loop liest sie live.
/// </summary>
public sealed class MirrorConfig
{
    // ── Tonemapping / Bild (live) ────────────────────────────────────────
    [JsonPropertyName("tonemap_enabled")]  public bool TonemapEnabled { get; set; } = true;
    [JsonPropertyName("operator")]         public string Operator { get; set; } = "bt2390";
    [JsonPropertyName("source_peak_nits")] public float SourcePeakNits { get; set; } = 1000f;
    [JsonPropertyName("target_paperwhite")] public float TargetPaperwhite { get; set; } = 200f;
    [JsonPropertyName("exposure")]         public float Exposure { get; set; } = 0f;
    [JsonPropertyName("saturation")]       public float Saturation { get; set; } = 1f;
    [JsonPropertyName("contrast")]         public float Contrast { get; set; } = 1f;
    [JsonPropertyName("gamma")]            public float Gamma { get; set; } = 1f;
    [JsonPropertyName("content_offset_y")] public int ContentOffsetY { get; set; } = 0;

    // ── Layout / Geometrie (live) ────────────────────────────────────────
    // layout_mode: "fit" (1:1, nichts abgeschnitten) | "stretch" | "top_strip" (21:9→16:9 oben)
    //              | "crop_region" (frei definiertes Quell-Rechteck)
    [JsonPropertyName("layout_mode")]      public string LayoutMode { get; set; } = "top_strip";
    // Quell-Crop in 0..1 (nur fuer layout_mode="crop_region"): Ausschnitt der Quelle.
    [JsonPropertyName("crop_x")]           public float CropX { get; set; } = 0f;
    [JsonPropertyName("crop_y")]           public float CropY { get; set; } = 0f;
    [JsonPropertyName("crop_w")]           public float CropW { get; set; } = 1f;
    [JsonPropertyName("crop_h")]           public float CropH { get; set; } = 1f;
    // Maus-Cursor in die Ausgabe komponieren (Desktop Duplication liefert ihn separat).
    [JsonPropertyName("show_cursor")]      public bool ShowCursor { get; set; } = false;
    [JsonPropertyName("vsync")]            public bool Vsync { get; set; } = true;

    // ── Struktur (Neustart noetig) ───────────────────────────────────────
    [JsonPropertyName("output_bit_depth")] public int OutputBitDepth { get; set; } = 10;
    [JsonPropertyName("source_display")]   public int SourceDisplay { get; set; } = 0;
    [JsonPropertyName("target_display")]   public int TargetDisplay { get; set; } = 0;
    // output_mode: "windowed" | "borderless" | "fullscreen_block" (Maus-Sperre) | "exclusive".
    // Leer = aus dem alten `windowed`-Flag abgeleitet (Abwaerts-Kompatibilitaet).
    [JsonPropertyName("output_mode")]      public string OutputMode { get; set; } = "";
    [JsonPropertyName("windowed")]         public bool Windowed { get; set; } = true;
    [JsonPropertyName("window_width")]     public int WindowWidth { get; set; } = 1280;
    [JsonPropertyName("window_height")]    public int WindowHeight { get; set; } = 720;
    // 0 = aus dem Ziel-Display ableiten; >0 = Override.
    [JsonPropertyName("output_width")]     public int OutputWidth { get; set; } = 0;
    [JsonPropertyName("output_height")]    public int OutputHeight { get; set; } = 0;

    /// <summary>Operator-String → Shader-Konstante (muss zu Tonemap.hlsl passen).</summary>
    public int OperatorId => Operator?.ToLowerInvariant() switch
    {
        "reinhard" => 1,
        "hable"    => 2,
        "aces"     => 3,
        _          => 0, // bt2390 (Default)
    };

    /// <summary>Effektiver Fenster-Modus — leeres output_mode faellt auf das alte windowed-Flag zurueck.</summary>
    public string ResolveOutputMode()
    {
        var m = (OutputMode ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return Windowed ? "windowed" : "borderless";
        return m;
    }

    // ── Schluessel-Whitelist (gegen Injection; LIVE wirkt sofort) ─────────
    public static readonly HashSet<string> LiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tonemap_enabled", "operator", "source_peak_nits", "target_paperwhite",
        "exposure", "saturation", "contrast", "gamma", "content_offset_y", "vsync",
        "layout_mode", "crop_x", "crop_y", "crop_w", "crop_h", "show_cursor",
    };
    public static readonly HashSet<string> StructKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "output_bit_depth", "source_display", "target_display", "output_mode", "windowed",
        "window_width", "window_height", "output_width", "output_height",
    };
    public static IEnumerable<string> AllKeys => LiveKeys.Concat(StructKeys);

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static MirrorConfig Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MirrorConfig>(json, ReadOpts) ?? new MirrorConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] Lesefehler ({path}): {ex.Message} — nutze Defaults/letzten Stand.");
            return new MirrorConfig();
        }
    }

    /// <summary>Ganzes Objekt schreiben (von der GUI genutzt).</summary>
    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts));

    /// <summary>
    /// Read-modify-write: nur whitelisted Schluessel aus <paramref name="patch"/> in die
    /// bestehende Datei mergen (unbekannte Felder bleiben erhalten). Spiegelt die Semantik
    /// von cockpit/services/mirror_control.set_config — daher bleibt der WinRM-Pfad kompatibel.
    /// Gibt das frische JSON-Objekt zurueck.
    /// </summary>
    public static JsonObject MergePatch(string path, JsonObject patch)
    {
        JsonObject current;
        try { current = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
        catch { current = new JsonObject(); }

        foreach (var kv in patch)
        {
            if (!LiveKeys.Contains(kv.Key) && !StructKeys.Contains(kv.Key)) continue;
            current[kv.Key] = kv.Value?.DeepClone();
        }
        File.WriteAllText(path, current.ToJsonString(WriteOpts));
        return current;
    }
}
