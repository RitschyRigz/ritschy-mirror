using System.Text.Json;
using System.Text.Json.Serialization;

namespace RitschyMirror;

/// <summary>
/// App-Ebene (app_settings.json) — bewusst GETRENNT von mirror_config.json (Render-Parameter),
/// damit der Cockpit-WinRM-Fallback (der nur Render-Keys liest/schreibt) unberuehrt bleibt.
/// Hier: HTTP-Steuer-Agent (Bind/Port) + Autostart.
/// </summary>
public sealed class AppSettings
{
    // Agent-Bind: "localhost" (nur lokal) oder "+"/"0.0.0.0" (vom Netzwerk erreichbar → Cockpit).
    [JsonPropertyName("agent_bind")]    public string AgentBind { get; set; } = "+";
    [JsonPropertyName("agent_port")]    public int AgentPort { get; set; } = 8788;
    [JsonPropertyName("agent_enabled")] public bool AgentEnabled { get; set; } = true;
    // Spiegelung beim App-Start automatisch beginnen (sonst nur Tray-idle).
    [JsonPropertyName("autostart_mirror")] public bool AutostartMirror { get; set; } = false;

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), ReadOpts) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOpts)); } catch { }
    }
}
