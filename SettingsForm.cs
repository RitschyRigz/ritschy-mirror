using Microsoft.Win32;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Lokale Einstellungs-GUI — dieselben Parameter, die sonst remote ueber das Cockpit
/// gestellt werden. Schreibt live in mirror_config.json (Engine laedt LIVE-Keys sofort
/// nach); Struktur-Aenderungen (Display/Modus/Bit-Tiefe) brauchen einen Render-Neustart.
/// App-Ebene (Agent/Autostart) geht in app_settings.json.
/// </summary>
public sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RitschyMirror";

    private readonly MirrorEngine _engine;
    private readonly string _appSettingsPath;
    private readonly AppSettings _app;
    private MirrorConfig _cfg;
    private bool _loading;

    private ComboBox _srcCombo = null!, _dstCombo = null!, _layoutCombo = null!, _outputCombo = null!,
                     _operatorCombo = null!, _bitDepthCombo = null!;
    private List<DisplayInfo> _displays = new();

    public SettingsForm(MirrorEngine engine, string appSettingsPath, AppSettings app)
    {
        _engine = engine;
        _appSettingsPath = appSettingsPath;
        _app = app;
        _cfg = MirrorConfig.Load(engine.ConfigPath);

        Text = "RitschyMirror — Einstellungen";
        Width = 560; Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        Controls.Add(root);

        _loading = true;
        root.Controls.Add(BuildSourceTargetGroup());
        root.Controls.Add(BuildImageGroup());
        root.Controls.Add(BuildCropGroup());
        root.Controls.Add(BuildConnectionGroup());
        root.Controls.Add(BuildButtonsRow());
        _loading = false;
    }

    // ── Persistenz ────────────────────────────────────────────────────────
    private void SaveCfg() { if (_loading) return; try { _cfg.Save(_engine.ConfigPath); } catch { } }
    private void SaveApp() { if (_loading) return; _app.Save(_appSettingsPath); }

    // ── Helfer ────────────────────────────────────────────────────────────
    private static GroupBox Group(string title, out TableLayoutPanel table)
    {
        var g = new GroupBox { Text = title, AutoSize = true, Width = 520, Margin = new Padding(3, 3, 3, 8) };
        table = new TableLayoutPanel { ColumnCount = 3, RowCount = 16, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(6) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        g.Controls.Add(table);
        return g;
    }

    private ComboBox AddCombo(TableLayoutPanel t, int row, string label, string[] items, string current, Action<string> set)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, row);
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, Anchor = AnchorStyles.Left };
        c.Items.AddRange(items);
        c.SelectedItem = items.FirstOrDefault(i => string.Equals(i, current, StringComparison.OrdinalIgnoreCase)) ?? (items.Length > 0 ? items[0] : null);
        c.SelectedIndexChanged += (_, _) => { if (!_loading && c.SelectedItem is string s) { set(s); SaveCfg(); } };
        t.Controls.Add(c, 1, row);
        return c;
    }

    private void AddFloat(TableLayoutPanel t, int row, string label, float min, float max, float val, string fmt, Action<float> set)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, row);
        var tb = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = 270, Anchor = AnchorStyles.Left };
        var valLbl = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0), Text = val.ToString(fmt) };
        int ToTick(float v) => (int)Math.Round((v - min) / (max - min) * 1000);
        float ToVal(int tick) => min + (max - min) * tick / 1000f;
        tb.Value = Math.Clamp(ToTick(val), 0, 1000);
        tb.Scroll += (_, _) => { float v = ToVal(tb.Value); valLbl.Text = v.ToString(fmt); if (!_loading) { set(v); SaveCfg(); } };
        t.Controls.Add(tb, 1, row);
        t.Controls.Add(valLbl, 2, row);
    }

    private NumericUpDown AddNumeric(TableLayoutPanel t, int row, string label, decimal min, decimal max, decimal val, decimal step, int decimals, Action<decimal> set)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, row);
        var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Increment = step, DecimalPlaces = decimals, Width = 100, Anchor = AnchorStyles.Left };
        n.ValueChanged += (_, _) => { if (!_loading) { set(n.Value); SaveCfg(); } };
        t.Controls.Add(n, 1, row);
        return n;
    }

    private CheckBox AddCheck(TableLayoutPanel t, int row, string label, bool val, Action<bool> set)
    {
        var c = new CheckBox { Text = label, Checked = val, AutoSize = true, Anchor = AnchorStyles.Left };
        c.CheckedChanged += (_, _) => { if (!_loading) { set(c.Checked); SaveCfg(); } };
        t.Controls.Add(c, 0, row);
        t.SetColumnSpan(c, 2);
        return c;
    }

    // ── Gruppen ───────────────────────────────────────────────────────────
    private GroupBox BuildSourceTargetGroup()
    {
        var g = Group("Quelle / Ziel & Modus", out var t);
        _displays = MirrorEngine.EnumerateDisplays();
        var names = DisplayNames();

        t.Controls.Add(new Label { Text = "Quell-Monitor", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, 0);
        _srcCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, Anchor = AnchorStyles.Left };
        _srcCombo.Items.AddRange(names);
        if (_cfg.SourceDisplay < names.Length) _srcCombo.SelectedIndex = _cfg.SourceDisplay;
        _srcCombo.SelectedIndexChanged += (_, _) => { if (!_loading) { _cfg.SourceDisplay = _srcCombo.SelectedIndex; SaveCfg(); } };
        t.Controls.Add(_srcCombo, 1, 0);

        t.Controls.Add(new Label { Text = "Ziel-Monitor", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, 1);
        _dstCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, Anchor = AnchorStyles.Left };
        _dstCombo.Items.AddRange(names);
        if (_cfg.TargetDisplay < names.Length) _dstCombo.SelectedIndex = _cfg.TargetDisplay;
        _dstCombo.SelectedIndexChanged += (_, _) => { if (!_loading) { _cfg.TargetDisplay = _dstCombo.SelectedIndex; SaveCfg(); } };
        t.Controls.Add(_dstCombo, 1, 1);

        var refreshBtn = new Button { Text = "↻ Displays", AutoSize = true, Anchor = AnchorStyles.Left };
        refreshBtn.Click += (_, _) => RefreshDisplays();
        t.Controls.Add(refreshBtn, 2, 1);

        _layoutCombo = AddCombo(t, 2, "Layout-Modus", new[] { "fit", "stretch", "top_strip", "crop_region" }, _cfg.LayoutMode, s => _cfg.LayoutMode = s);
        _outputCombo = AddCombo(t, 3, "Ausgabe-Modus", new[] { "windowed", "borderless", "fullscreen_block", "exclusive" }, _cfg.ResolveOutputMode(), s => _cfg.OutputMode = s);
        _bitDepthCombo = AddCombo(t, 4, "Bit-Tiefe", new[] { "8", "10" }, _cfg.OutputBitDepth.ToString(), s => _cfg.OutputBitDepth = int.TryParse(s, out var v) ? v : 10);

        AddNumeric(t, 5, "Test-Fenster Breite", 320, 7680, _cfg.WindowWidth, 16, 0, v => _cfg.WindowWidth = (int)v);
        AddNumeric(t, 6, "Test-Fenster Höhe", 240, 4320, _cfg.WindowHeight, 16, 0, v => _cfg.WindowHeight = (int)v);

        t.Controls.Add(new Label { Text = "Struktur-Änderungen (Monitor/Modus/Bit-Tiefe) wirken erst nach Neustart.", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(3, 6, 3, 0) }, 0, 7);
        t.SetColumnSpan(t.GetControlFromPosition(0, 7)!, 3);
        return g;
    }

    private GroupBox BuildImageGroup()
    {
        var g = Group("Bild / Tonemap", out var t);
        AddCheck(t, 0, "Tonemap aktiv (HDR→SDR)", _cfg.TonemapEnabled, v => _cfg.TonemapEnabled = v);
        _operatorCombo = AddCombo(t, 1, "Operator", new[] { "bt2390", "reinhard", "hable", "aces" }, _cfg.Operator, s => _cfg.Operator = s);
        AddFloat(t, 2, "Belichtung (Stops)", -3f, 3f, _cfg.Exposure, "0.00", v => _cfg.Exposure = v);
        AddFloat(t, 3, "Sättigung", 0f, 2f, _cfg.Saturation, "0.00", v => _cfg.Saturation = v);
        AddFloat(t, 4, "Kontrast", 0f, 2f, _cfg.Contrast, "0.00", v => _cfg.Contrast = v);
        AddFloat(t, 5, "Gamma", 0.3f, 3f, _cfg.Gamma, "0.00", v => _cfg.Gamma = v);
        AddFloat(t, 6, "Weißpunkt (nits)", 80f, 400f, _cfg.TargetPaperwhite, "0", v => _cfg.TargetPaperwhite = v);
        AddFloat(t, 7, "Quell-Spitze (nits)", 200f, 4000f, _cfg.SourcePeakNits, "0", v => _cfg.SourcePeakNits = v);
        AddNumeric(t, 8, "Vertikal-Offset (px)", -2160, 2160, _cfg.ContentOffsetY, 2, 0, v => _cfg.ContentOffsetY = (int)v);
        AddCheck(t, 9, "VSync", _cfg.Vsync, v => _cfg.Vsync = v);
        return g;
    }

    private GroupBox BuildCropGroup()
    {
        var g = Group("Crop (nur Layout-Modus crop_region)", out var t);
        AddNumeric(t, 0, "Crop X (0..1)", 0m, 1m, (decimal)_cfg.CropX, 0.01m, 2, v => _cfg.CropX = (float)v);
        AddNumeric(t, 1, "Crop Y (0..1)", 0m, 1m, (decimal)_cfg.CropY, 0.01m, 2, v => _cfg.CropY = (float)v);
        AddNumeric(t, 2, "Crop Breite (0..1)", 0.01m, 1m, (decimal)_cfg.CropW, 0.01m, 2, v => _cfg.CropW = (float)v);
        AddNumeric(t, 3, "Crop Höhe (0..1)", 0.01m, 1m, (decimal)_cfg.CropH, 0.01m, 2, v => _cfg.CropH = (float)v);
        return g;
    }

    private GroupBox BuildConnectionGroup()
    {
        var g = Group("Verbindung / Autostart", out var t);
        t.Controls.Add(new Label { Text = "Agent-Bind", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0) }, 0, 0);
        var bind = new TextBox { Text = _app.AgentBind, Width = 270, Anchor = AnchorStyles.Left };
        bind.TextChanged += (_, _) => { if (!_loading) { _app.AgentBind = bind.Text.Trim(); SaveApp(); } };
        t.Controls.Add(bind, 1, 0);
        t.Controls.Add(new Label { Text = "(„+\" = vom Netz erreichbar, „localhost\" = nur lokal)", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(3, 6, 3, 0) }, 2, 0);

        AddNumeric(t, 1, "Agent-Port", 1, 65535, _app.AgentPort, 1, 0, v => { _app.AgentPort = (int)v; SaveApp(); });

        var agentOn = new CheckBox { Text = "HTTP-Agent aktiv (Cockpit-Fernsteuerung)", Checked = _app.AgentEnabled, AutoSize = true, Anchor = AnchorStyles.Left };
        agentOn.CheckedChanged += (_, _) => { if (!_loading) { _app.AgentEnabled = agentOn.Checked; SaveApp(); } };
        t.Controls.Add(agentOn, 0, 2); t.SetColumnSpan(agentOn, 3);

        var autoMirror = new CheckBox { Text = "Spiegelung beim App-Start automatisch beginnen", Checked = _app.AutostartMirror, AutoSize = true, Anchor = AnchorStyles.Left };
        autoMirror.CheckedChanged += (_, _) => { if (!_loading) { _app.AutostartMirror = autoMirror.Checked; SaveApp(); } };
        t.Controls.Add(autoMirror, 0, 3); t.SetColumnSpan(autoMirror, 3);

        var autoLogin = new CheckBox { Text = "App beim Windows-Login automatisch starten", Checked = IsLoginAutostart(), AutoSize = true, Anchor = AnchorStyles.Left };
        autoLogin.CheckedChanged += (_, _) => { if (!_loading) SetLoginAutostart(autoLogin.Checked); };
        t.Controls.Add(autoLogin, 0, 4); t.SetColumnSpan(autoLogin, 3);

        t.Controls.Add(new Label { Text = "Agent-/Bind-Änderungen wirken nach App-Neustart.", AutoSize = true, ForeColor = System.Drawing.Color.DimGray, Margin = new Padding(3, 6, 3, 0) }, 0, 5);
        t.SetColumnSpan(t.GetControlFromPosition(0, 5)!, 3);
        return g;
    }

    private FlowLayoutPanel BuildButtonsRow()
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        var startBtn = new Button { Text = "Mirror starten/stoppen", AutoSize = true };
        startBtn.Click += (_, _) => { if (_engine.IsRunning) _engine.Stop(); else _engine.Start(); };
        var restartBtn = new Button { Text = "Neustart (Struktur übernehmen)", AutoSize = true };
        restartBtn.Click += (_, _) => { if (_engine.IsRunning) _engine.Restart(); else _engine.Start(); };
        var closeBtn = new Button { Text = "Schließen", AutoSize = true };
        closeBtn.Click += (_, _) => Close();
        row.Controls.Add(startBtn);
        row.Controls.Add(restartBtn);
        row.Controls.Add(closeBtn);
        return row;
    }

    // ── Displays ──────────────────────────────────────────────────────────
    private string[] DisplayNames() =>
        _displays.Select(d => $"{d.Index}: {d.Name} {d.Resolution}{(d.Hdr ? " HDR" : "")} [{d.Adapter}]").ToArray();

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
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k!.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            else k!.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { _engine.Log("Login-Autostart setzen fehlgeschlagen: " + ex.Message); }
    }
}
