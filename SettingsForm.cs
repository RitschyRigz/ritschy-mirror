using Microsoft.Win32;
using System.Drawing;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Lokale Einstellungs-GUI — dieselben Parameter, die sonst remote ueber das Cockpit
/// gestellt werden. Schreibt live in mirror_config.json (Engine laedt LIVE-Keys sofort
/// nach); Struktur-Aenderungen (Display/Modus/Bit-Tiefe) brauchen einen Render-Neustart.
/// App-Ebene (Agent/Autostart) geht in app_settings.json.
///
/// Layout bewusst MANUELL (Location/Size, scrollbares Panel) — verschachtelte
/// AutoSize-Container (FlowLayout→GroupBox→TableLayout) kollabierten auf Hoehe 0.
/// </summary>
public sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RitschyMirror";
    private const int LX = 16, CX = 230, VX = 500, ROW = 32;

    private readonly MirrorEngine _engine;
    private readonly string _appSettingsPath;
    private readonly AppSettings _app;
    private MirrorConfig _cfg;
    private bool _loading;

    private readonly Panel _panel;
    private int _y = 12;
    private ComboBox _srcCombo = null!, _dstCombo = null!;
    private List<DisplayInfo> _displays = new();

    public SettingsForm(MirrorEngine engine, string appSettingsPath, AppSettings app)
    {
        _engine = engine; _appSettingsPath = appSettingsPath; _app = app;
        _cfg = MirrorConfig.Load(engine.ConfigPath);

        Text = "RitschyMirror — Einstellungen";
        Icon = AppIcon.Load(small: false);
        ClientSize = new Size(620, 780);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(520, 420);

        _panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = SystemColors.ControlLight };
        Controls.Add(_panel);
        Controls.Add(bottom);

        _loading = true;
        BuildSourceTarget();
        BuildImage();
        BuildCrop();
        BuildConnection();
        _loading = false;

        var start = new Button { Text = "Start / Stop", Location = new Point(12, 9), Width = 120, Height = 30 };
        start.Click += (_, _) => { if (_engine.IsRunning) _engine.Stop(); else _engine.Start(); };
        var restart = new Button { Text = "Neustart (Struktur)", Location = new Point(140, 9), Width = 160, Height = 30 };
        restart.Click += (_, _) => { if (_engine.IsRunning) _engine.Restart(); else _engine.Start(); };
        var close = new Button { Text = "Schließen", Location = new Point(308, 9), Width = 110, Height = 30 };
        close.Click += (_, _) => Close();
        bottom.Controls.AddRange(new Control[] { start, restart, close });
    }

    // ── Persistenz ────────────────────────────────────────────────────────
    private void SaveCfg() { if (_loading) return; try { _cfg.Save(_engine.ConfigPath); } catch { } }
    private void SaveApp() { if (_loading) return; _app.Save(_appSettingsPath); }

    // ── Layout-Helfer (manuell, y-Cursor) ────────────────────────────────
    private void Header(string text)
    {
        _y += 8;
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX - 4, _y), AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _y += 24;
    }

    private void Lbl(string text) =>
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX, _y + 4), AutoSize = true });

    private void Note(string text)
    {
        _panel.Controls.Add(new Label { Text = text, Location = new Point(LX, _y), AutoSize = true, ForeColor = Color.DimGray });
        _y += 22;
    }

    private ComboBox ComboRow(string label, string[] items, string current, Action<string> set)
    {
        Lbl(label);
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 250 };
        c.Items.AddRange(items);
        c.SelectedItem = items.FirstOrDefault(i => string.Equals(i, current, StringComparison.OrdinalIgnoreCase)) ?? (items.Length > 0 ? items[0] : null);
        c.SelectedIndexChanged += (_, _) => { if (!_loading && c.SelectedItem is string s) { set(s); SaveCfg(); } };
        _panel.Controls.Add(c);
        _y += ROW;
        return c;
    }

    private void FloatRow(string label, float min, float max, float val, string fmt, Action<float> set)
    {
        Lbl(label);
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Location = new Point(CX, _y - 4), Width = 260 };
        var vl = new Label { Location = new Point(VX, _y + 4), AutoSize = true, Text = val.ToString(fmt) };
        int ToTick(float v) => (int)Math.Round((v - min) / (max - min) * 1000);
        float ToVal(int t) => min + (max - min) * t / 1000f;
        tb.Value = Math.Clamp(ToTick(val), 0, 1000);
        tb.Scroll += (_, _) => { float v = ToVal(tb.Value); vl.Text = v.ToString(fmt); if (!_loading) { set(v); SaveCfg(); } };
        _panel.Controls.Add(tb); _panel.Controls.Add(vl);
        _y += 42;
    }

    private NumericUpDown NumRow(string label, decimal min, decimal max, decimal val, decimal step, int dec, Action<decimal> set, bool app = false)
    {
        Lbl(label);
        var n = new NumericUpDown { Location = new Point(CX, _y), Width = 110, Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Increment = step, DecimalPlaces = dec };
        n.ValueChanged += (_, _) => { if (!_loading) { set(n.Value); if (app) SaveApp(); else SaveCfg(); } };
        _panel.Controls.Add(n);
        _y += ROW;
        return n;
    }

    private CheckBox CheckRow(string label, bool val, Action<bool> set, bool app = false)
    {
        var c = new CheckBox { Text = label, Location = new Point(LX, _y), AutoSize = true, Checked = val };
        c.CheckedChanged += (_, _) => { if (!_loading) { set(c.Checked); if (app) SaveApp(); else SaveCfg(); } };
        _panel.Controls.Add(c);
        _y += 28;
        return c;
    }

    // ── Gruppen ───────────────────────────────────────────────────────────
    private void BuildSourceTarget()
    {
        Header("Quelle / Ziel & Modus");
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();

        Lbl("Quell-Monitor");
        _srcCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _srcCombo.Items.AddRange(names);
        if (_cfg.SourceDisplay < names.Length) _srcCombo.SelectedIndex = _cfg.SourceDisplay;
        _srcCombo.SelectedIndexChanged += (_, _) => { if (!_loading) { _cfg.SourceDisplay = _srcCombo.SelectedIndex; SaveCfg(); } };
        _panel.Controls.Add(_srcCombo);
        _y += ROW;

        Lbl("Ziel-Monitor");
        _dstCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(CX, _y), Width = 330 };
        _dstCombo.Items.AddRange(names);
        if (_cfg.TargetDisplay < names.Length) _dstCombo.SelectedIndex = _cfg.TargetDisplay;
        _dstCombo.SelectedIndexChanged += (_, _) => { if (!_loading) { _cfg.TargetDisplay = _dstCombo.SelectedIndex; SaveCfg(); } };
        _panel.Controls.Add(_dstCombo);
        var refresh = new Button { Text = "↻", Location = new Point(CX + 338, _y - 1), Width = 32, Height = 26 };
        refresh.Click += (_, _) => RefreshDisplays();
        _panel.Controls.Add(refresh);
        _y += ROW;

        ComboRow("Layout-Modus", new[] { "fit", "stretch", "top_strip", "crop_region" }, _cfg.LayoutMode, s => _cfg.LayoutMode = s);
        ComboRow("Ausgabe-Modus", new[] { "windowed", "borderless", "fullscreen_block", "exclusive" }, _cfg.ResolveOutputMode(), s => _cfg.OutputMode = s);
        ComboRow("Bit-Tiefe", new[] { "8", "10" }, _cfg.OutputBitDepth.ToString(), s => _cfg.OutputBitDepth = int.TryParse(s, out var v) ? v : 10);
        NumRow("Test-Fenster Breite", 320, 7680, _cfg.WindowWidth, 16, 0, v => _cfg.WindowWidth = (int)v);
        NumRow("Test-Fenster Höhe", 240, 4320, _cfg.WindowHeight, 16, 0, v => _cfg.WindowHeight = (int)v);
        Note("Struktur (Monitor/Modus/Bit-Tiefe/Fenster) wirkt erst nach Neustart.");
    }

    private void BuildImage()
    {
        Header("Bild / Tonemap");
        CheckRow("Tonemap aktiv (HDR→SDR)", _cfg.TonemapEnabled, v => _cfg.TonemapEnabled = v);
        ComboRow("Operator", new[] { "bt2390", "reinhard", "hable", "aces" }, _cfg.Operator, s => _cfg.Operator = s);
        FloatRow("Belichtung (Stops)", -3f, 3f, _cfg.Exposure, "0.00", v => _cfg.Exposure = v);
        FloatRow("Sättigung", 0f, 2f, _cfg.Saturation, "0.00", v => _cfg.Saturation = v);
        FloatRow("Kontrast", 0f, 2f, _cfg.Contrast, "0.00", v => _cfg.Contrast = v);
        FloatRow("Gamma", 0.3f, 3f, _cfg.Gamma, "0.00", v => _cfg.Gamma = v);
        FloatRow("Weißpunkt (nits)", 80f, 400f, _cfg.TargetPaperwhite, "0", v => _cfg.TargetPaperwhite = v);
        FloatRow("Quell-Spitze (nits)", 200f, 4000f, _cfg.SourcePeakNits, "0", v => _cfg.SourcePeakNits = v);
        NumRow("Vertikal-Offset (px)", -2160, 2160, _cfg.ContentOffsetY, 2, 0, v => _cfg.ContentOffsetY = (int)v);
        CheckRow("Mauszeiger anzeigen", _cfg.ShowCursor, v => _cfg.ShowCursor = v);
        CheckRow("VSync", _cfg.Vsync, v => _cfg.Vsync = v);
    }

    private void BuildCrop()
    {
        Header("Crop (nur Layout-Modus crop_region)");
        NumRow("Crop X (0..1)", 0m, 1m, (decimal)_cfg.CropX, 0.01m, 2, v => _cfg.CropX = (float)v);
        NumRow("Crop Y (0..1)", 0m, 1m, (decimal)_cfg.CropY, 0.01m, 2, v => _cfg.CropY = (float)v);
        NumRow("Crop Breite (0..1)", 0.01m, 1m, (decimal)_cfg.CropW, 0.01m, 2, v => _cfg.CropW = (float)v);
        NumRow("Crop Höhe (0..1)", 0.01m, 1m, (decimal)_cfg.CropH, 0.01m, 2, v => _cfg.CropH = (float)v);
    }

    private void BuildConnection()
    {
        Header("Verbindung / Autostart");
        Lbl("Agent-Bind");
        var bind = new TextBox { Location = new Point(CX, _y), Width = 250, Text = _app.AgentBind };
        bind.TextChanged += (_, _) => { if (!_loading) { _app.AgentBind = bind.Text.Trim(); SaveApp(); } };
        _panel.Controls.Add(bind);
        _y += ROW;
        Note("\"+\" = vom Netz erreichbar  ·  \"localhost\" = nur lokal");
        NumRow("Agent-Port", 1, 65535, _app.AgentPort, 1, 0, v => _app.AgentPort = (int)v, app: true);
        CheckRow("HTTP-Agent aktiv (Cockpit-Fernsteuerung)", _app.AgentEnabled, v => _app.AgentEnabled = v, app: true);
        CheckRow("Spiegelung beim App-Start automatisch beginnen", _app.AutostartMirror, v => _app.AutostartMirror = v, app: true);
        var al = new CheckBox { Text = "App beim Windows-Login automatisch starten", Location = new Point(LX, _y), AutoSize = true, Checked = IsLoginAutostart() };
        al.CheckedChanged += (_, _) => { if (!_loading) SetLoginAutostart(al.Checked); };
        _panel.Controls.Add(al);
        _y += 28;
        Note("Agent-/Bind-/Port-Änderungen wirken nach App-Neustart.");
    }

    // ── Displays ──────────────────────────────────────────────────────────
    private string[] DisplayNames() =>
        _displays.Select(d => $"{d.Index}: {d.DisplayName}  {d.Resolution}{(d.Hdr ? " HDR" : "")}  [{d.Adapter}]").ToArray();

    private void RefreshDisplays()
    {
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();
        _loading = true;
        int s = _srcCombo.SelectedIndex, d = _dstCombo.SelectedIndex;
        _srcCombo.Items.Clear(); _srcCombo.Items.AddRange(names);
        _dstCombo.Items.Clear(); _dstCombo.Items.AddRange(names);
        if (s >= 0 && s < names.Length) _srcCombo.SelectedIndex = s;
        if (d >= 0 && d < names.Length) _dstCombo.SelectedIndex = d;
        _loading = false;
    }

    // ── Login-Autostart (HKCU\...\Run) ────────────────────────────────────
    private static bool IsLoginAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunValue) != null; }
        catch { return false; }
    }

    private void SetLoginAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k!.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            else k!.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { _engine.Log("Login-Autostart setzen fehlgeschlagen: " + ex.Message); }
    }
}
