using System.Runtime.InteropServices;
using System.Text;

namespace RitschyMirror;

/// <summary>Ein aufnehmbares Top-Level-Fenster (für GUI-/Dock-Picker + Agent /windows).</summary>
public sealed class WindowInfo
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = "";
    public string Exe { get; set; } = "";   // Prozess-Exe (z.B. „eldenring.exe") = stabile Identität
    public int Pid { get; set; }

    /// <summary>Anzeigename für Dropdowns: „Titel  (exe)".</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Exe) ? Title : $"{Title}  ({Exe})";
}

/// <summary>
/// Top-Level-Fenster auflisten (für die Fenster-Auswahl) und eine gespeicherte Auswahl
/// identitäts-stabil gegen die AKTUELL offenen Fenster auflösen.
///
/// Identität = Prozess-Exe (primär, überlebt Titeländerungen wie „Spiel — Level 2") + Fenstertitel
/// (sekundär, zum Unterscheiden mehrerer Fenster derselben Exe). HWNDs sind NICHT stabil über
/// Programmstarts → deshalb merken wir exe+title, nicht das Handle (gleiche Philosophie wie die
/// umsteck-feste Monitor-Auswahl in <see cref="MirrorEngine.ResolveSelection"/>).
/// </summary>
public static class WindowEnum
{
    /// <summary>Sichtbare, „echte" Top-Level-Fenster (mit Titel, keine Tool-/Geister-Fenster,
    /// nicht unser eigener Prozess). Sortiert nach Titel für eine ruhige Dropdown-Liste.</summary>
    public static List<WindowInfo> List()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsCapturable(hWnd, ownPid, out string title, out int pid, out string exe))
                return true; // weiter

            result.Add(new WindowInfo { Hwnd = hWnd, Title = title, Pid = pid, Exe = exe });
            return true;
        }, IntPtr.Zero);

        result.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Gespeicherte (exe, title) gegen die aktuell offenen Fenster auflösen.
    /// Reihenfolge: gleiche Exe + exakter Titel → gleiche Exe + Titel-Prefix → einzige Exe-Übereinstimmung
    /// → exakter Titel (ohne Exe-Info) → nichts. Liefert (hwnd, "") bei Treffer, sonst (Zero, Klartext-Fehler).
    /// </summary>
    public static (IntPtr hwnd, string error) Resolve(string exe, string title)
    {
        var wins = List();
        if (wins.Count == 0) return (IntPtr.Zero, "Keine aufnehmbaren Fenster gefunden");

        bool hasExe = !string.IsNullOrWhiteSpace(exe);
        bool hasTitle = !string.IsNullOrWhiteSpace(title);
        if (!hasExe && !hasTitle)
            return (IntPtr.Zero, "Kein Fenster ausgewählt");

        if (hasExe)
        {
            var sameExe = wins.Where(w => string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameExe.Count == 1) return (sameExe[0].Hwnd, "");
            if (sameExe.Count > 1)
            {
                var exact = sameExe.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return (exact.Hwnd, "");
                var prefix = sameExe.FirstOrDefault(w =>
                    hasTitle && w.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase));
                if (prefix != null) return (prefix.Hwnd, "");
                return (sameExe[0].Hwnd, ""); // mehrere gleicher Exe, kein Titel-Match → erstes
            }
        }
        if (hasTitle)
        {
            var byTitle = wins.FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase));
            if (byTitle != null) return (byTitle.Hwnd, "");
        }

        string what = hasTitle ? title : exe;
        return (IntPtr.Zero, $"Fenster '{what}' nicht gefunden (läuft die Anwendung?)");
    }

    // ── Filter: ist das ein „echtes", aufnehmbares App-Fenster? ───────────────────
    private static bool IsCapturable(IntPtr hWnd, uint ownPid, out string title, out int pid, out string exe)
    {
        title = ""; pid = 0; exe = "";

        if (!IsWindowVisible(hWnd)) return false;

        // Nur Wurzel-Fenster (kein Kind/Popup-Eigentümer-Wirrwarr).
        if (GetAncestor(hWnd, GA_ROOT) != hWnd) return false;

        // Tool-Fenster (Tray-Helfer etc.) raus.
        long exStyle = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0) return false;

        // DWM-„cloaked" Fenster (UWP-Geister, andere Desktops) raus.
        if (IsCloaked(hWnd)) return false;

        // Titel muss vorhanden sein.
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return false;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        title = sb.ToString().Trim();
        if (title.Length == 0) return false;

        // Prozess ermitteln; eigenen Prozess (RitschyMirror) ausschließen.
        GetWindowThreadProcessId(hWnd, out uint wpid);
        if (wpid == 0 || wpid == ownPid) return false;
        pid = (int)wpid;
        exe = ProcessExeName(wpid);

        return true;
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0)
                return cloaked != 0;
        }
        catch { /* DWM-Attribut nicht verfügbar → als nicht-cloaked behandeln */ }
        return false;
    }

    /// <summary>Exe-Dateiname eines Prozesses (robust auch bei eingeschränktem Zugriff:
    /// PROCESS_QUERY_LIMITED_INFORMATION + QueryFullProcessImageName, kein MainModule).</summary>
    private static string ProcessExeName(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
            {
                string full = sb.ToString(0, size);
                int slash = full.LastIndexOfAny(new[] { '\\', '/' });
                return slash >= 0 ? full[(slash + 1)..] : full;
            }
        }
        catch { /* ignorieren → leerer Exe-Name */ }
        finally { CloseHandle(h); }
        return "";
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────────
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint GA_ROOT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // GetWindowLongPtr ist auf x64 der richtige Einstieg (32-bit-Variante fällt weg, wir bauen x64-only).
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);
}
