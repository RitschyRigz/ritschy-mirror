using System.Diagnostics;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Tray-Host: die App startet beim Login, wartet idle im Tray und hostet den HTTP-Agenten.
/// "Mirror starten" beginnt das Rendern, "Mirror stoppen" beendet es — die App bleibt im
/// Tray und der Agent erreichbar. Loest die fruehere Session-0-Scheduled-Task-Kruecke
/// (laeuft in der interaktiven Session → immer Display-Zugriff).
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly string _baseDir;
    private readonly string _appSettingsPath;
    private readonly MirrorEngine _engine;
    private readonly AppSettings _app;
    private ControlAgent? _agent;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private SettingsForm? _settings;

    public TrayContext(string[]? args = null)
    {
        _baseDir = AppContext.BaseDirectory;
        _appSettingsPath = Path.Combine(_baseDir, "app_settings.json");
        _app = AppSettings.Load(_appSettingsPath);
        _engine = new MirrorEngine(_baseDir);

        _toggleItem = new ToolStripMenuItem("Mirror starten", null, (_, _) => ToggleMirror());
        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Einstellungen…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Log öffnen", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Beenden", null, (_, _) => Quit()));

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = "RitschyMirror",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        // Agent starten (falls aktiviert)
        if (_app.AgentEnabled)
        {
            try
            {
                _agent = new ControlAgent(_engine, _app.AgentBind, _app.AgentPort);
                _agent.Start();
            }
            catch (Exception ex) { _engine.Log("Agent-Start fehlgeschlagen: " + ex.Message); }
        }

        if (_app.AutostartMirror)
        {
            var err = _engine.Preflight();
            if (err == null) _engine.Start();
            else _engine.Log("Autostart-Mirror übersprungen: " + err);
        }

        // Menue-/Tooltip-Status aktuell halten
        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();
        RefreshUi();

        // Direkt die Einstellungen oeffnen (z.B. eigene „Einstellungen"-Verknuepfung / Test).
        if (args != null && Array.Exists(args, a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)))
            OpenSettings();
    }

    private void ToggleMirror()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
        }
        else
        {
            var err = _engine.Preflight();
            if (err != null)
            {
                _tray.ShowBalloonTip(4000, "RitschyMirror", err + ".", ToolTipIcon.Warning);
                RefreshUi();
                return;
            }
            _engine.Start();
        }
        RefreshUi();
    }

    private void OpenSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_engine, _appSettingsPath, _app);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(_engine.LogPath)) File.WriteAllText(_engine.LogPath, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_engine.LogPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _engine.Log("Log oeffnen fehlgeschlagen: " + ex.Message); }
    }

    private void RefreshUi()
    {
        bool running = _engine.IsRunning;
        _toggleItem.Text = running ? "Mirror stoppen" : "Mirror starten";
        string bind = _agent?.BoundPrefix ?? "Agent aus";
        _tray.Text = $"RitschyMirror — {(running ? "läuft" : "idle")}\n{bind}";
    }

    private void Quit()
    {
        _uiTimer.Stop();
        try { _engine.Stop(); } catch { }
        try { _agent?.Stop(); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
