using System.Runtime.InteropServices;

namespace RitschyMirror;

/// <summary>
/// Minimales Win32-Fenster (kein WinForms/WPF) — fuer randloses Vollbild auf einem
/// bestimmten Display oder ein kleines Test-Fenster. Liefert das HWND fuer die
/// DXGI-Swapchain und pumpt die Nachrichtenschleife.
/// </summary>
public sealed class Win32Window
{
    public IntPtr Hwnd { get; private set; }
    public bool Running { get; private set; } = true;

    // Fensterklasse-WndProc: EINMAL pro Prozess registriert, Funktionszeiger auf einen STATISCHEN
    // Delegate, der als static-Feld die ganze Prozess-Lebensdauer gewurzelt ist → GC kann ihn nie
    // einsammeln. Zwei zuvor latente Crash-Ursachen sind damit weg (Fix v1.3.1, Stream-Crash 01:17):
    //  (1) frueher war der Delegate ein INSTANZ-Feld → Fenster<->Delegate bildeten einen
    //      selbstreferenziellen, GC-einsammelbaren Zyklus; wurde er mitten in DispatchMessage
    //      eingesammelt (JIT haelt "this" nicht laenger lebendig als noetig), rief Windows einen
    //      toten Delegate auf → CLR-FailFast "callback on a garbage collected delegate" (harter
    //      Prozess-Tod OHNE Log-Zeile — exakt das 01:17-Muster).
    //  (2) RegisterClassEx wurde bei jedem Fenster erneut versucht, scheiterte aber ab dem 2. still
    //      (Klasse existiert schon) → ALLE Fenster teilten den Delegate des ERSTEN; endete dessen
    //      Instanz nach einem Reinit, dangelte der Klassen-Zeiger fuer alle spaeteren Fenster.
    // Loesung: ein prozess-weiter statischer WndProc, der per HWND→Instanz an das richtige Fenster
    // dispatcht (unbekanntes HWND → DefWindowProc, nie ein Aufruf auf etwas Totes).
    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static readonly object s_lock = new();
    private static readonly Dictionary<IntPtr, Win32Window> s_windows = new();
    private static bool s_classRegistered;
    private const string ClassName = "RitschyMirrorWindowClass";

    // ── Maus-Sperre (output_mode "fullscreen_block") ─────────────────────
    // Low-Level-Maus-Hook haelt den Cursor aus dem Ziel-Display-Rechteck. Funktioniert
    // auf JEDEM Setup (auch Cross-Adapter), ohne echtes Exclusive-Fullscreen.
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private RECT _blockRect;
    private bool _inReposition;
    // Eigener Thread NUR für den Maus-Hook (siehe EnableCursorBlock).
    private Thread? _hookThread;
    private volatile uint _hookThreadId;

    public Win32Window(string title, int x, int y, int width, int height, bool borderless)
    {
        // Klasse nur EINMAL pro Prozess registrieren (statischer WndProc, s. Feld-Kommentar oben).
        lock (s_lock)
        {
            if (!s_classRegistered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                    hInstance = GetModuleHandle(null),
                    hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = ClassName,
                };
                RegisterClassEx(ref wc);
                s_classRegistered = true;
            }
        }

        // Borderless (WS_POPUP) fuer Vollbild, sonst overlapped Fenster.
        uint style = borderless ? 0x80000000 /*WS_POPUP*/ : 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/;
        uint exStyle = borderless ? 0x00000008u /*WS_EX_TOPMOST*/ : 0u;

        Hwnd = CreateWindowEx(
            exStyle, ClassName, title, style,
            x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd != IntPtr.Zero) { lock (s_lock) s_windows[Hwnd] = this; }

        ShowWindow(Hwnd, 5 /*SW_SHOW*/);
        UpdateWindow(Hwnd);
    }

    /// <summary>Nicht-blockierend alle anstehenden Nachrichten abarbeiten.</summary>
    public void PumpMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) { Running = false; break; }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// Fenster explizit zerstoeren. Noetig fuer den Reinit ZWISCHEN Render-Sessions: dort
    /// endet der Render-Thread NICHT (er baut die Kette neu), also wuerde das alte HWND sonst
    /// als randloses Topmost-Geisterfenster auf dem Ziel-Display stehen bleiben, waehrend das
    /// neue dahinter aufmacht. Muss auf dem Erzeuger-Thread (= Render-Thread) laufen.
    /// </summary>
    public void Destroy()
    {
        DisableCursorBlock();
        var h = Hwnd;
        Hwnd = IntPtr.Zero;
        if (h != IntPtr.Zero)
        {
            lock (s_lock) s_windows.Remove(h);
            DestroyWindow(h); // synchron → WM_DESTROY → PostQuitMessage(0)
        }
        // Das von WM_DESTROY gepostete WM_QUIT (und Restnachrichten) hier am Thread abraeumen,
        // sonst beendet es beim naechsten PumpMessages sofort die frische Session.
        while (PeekMessage(out _, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/)) { }
        Running = false;
    }

    /// <summary>
    /// Cursor aus dem Rechteck (l,t,r,b in Bildschirm-Koordinaten) heraushalten.
    ///
    /// Der WH_MOUSE_LL-Hook läuft auf einem EIGENEN, schlanken Thread (nur GetMessage-Loop) —
    /// NICHT auf dem Render-Thread. Grund: ein Low-Level-Maus-Hook wird auf dem installierenden
    /// Thread dispatcht und braucht eine flotte Message-Pump. Liefe er auf dem Render-Thread,
    /// würde er nur ~1×/Frame bedient (Present(vsync) blockiert dazwischen) → die Maus würde
    /// systemweit auf Render-Kadenz gedrosselt + ruckeln. Auf dem dedizierten Thread kehrt der
    /// Callback sofort zurück → Cursor bleibt smooth (volle Polling-Rate), Sperre bleibt.
    /// </summary>
    public void EnableCursorBlock(int l, int t, int r, int b)
    {
        _blockRect = new RECT { Left = l, Top = t, Right = r, Bottom = b };
        if (_hookThread != null) return;
        _mouseProc = MouseHookProc; // als Feld halten (GC)

        using var ready = new ManualResetEventSlim(false);
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            _mouseHook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, _mouseProc!, GetModuleHandle(null), 0);
            ready.Set();
            // Schlanke Loop — tut nichts außer den Hook-Callback prompt zu bedienen.
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        }) { IsBackground = true, Name = "MirrorMouseHook" };
        _hookThread.Start();
        ready.Wait(2000); // bis Hook installiert ist
    }

    public void DisableCursorBlock()
    {
        var t = _hookThread;
        if (t != null)
        {
            // WM_QUIT an den Hook-Thread → GetMessage-Loop endet → Unhook dort.
            if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != t) t.Join(2000);
            _hookThread = null;
            _hookThreadId = 0;
        }
        _mouseProc = null;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_inReposition && wParam.ToInt32() == 0x0200 /*WM_MOUSEMOVE*/)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int x = data.pt_x, y = data.pt_y;
            if (x >= _blockRect.Left && x < _blockRect.Right && y >= _blockRect.Top && y < _blockRect.Bottom)
            {
                // Auf die naechstgelegene Aussenkante schieben (kleinste Eindringtiefe).
                int dl = x - _blockRect.Left, dr = _blockRect.Right - x;
                int dt = y - _blockRect.Top,  db = _blockRect.Bottom - y;
                int min = System.Math.Min(System.Math.Min(dl, dr), System.Math.Min(dt, db));
                int nx = x, ny = y;
                if (min == dl)      nx = _blockRect.Left - 1;
                else if (min == dr) nx = _blockRect.Right;
                else if (min == dt) ny = _blockRect.Top - 1;
                else                ny = _blockRect.Bottom;
                _inReposition = true;
                SetCursorPos(nx, ny);
                _inReposition = false;
                return new IntPtr(1); // Bewegung schlucken
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // Prozess-weiter Dispatcher: findet die Instanz zum HWND und reicht weiter. Unbekanntes HWND
    // (z. B. Restnachricht nach Destroy) → DefWindowProc, nie ein Aufruf auf etwas Totes.
    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Win32Window? self;
        lock (s_lock) s_windows.TryGetValue(hWnd, out self);
        return self != null
            ? self.InstanceWndProc(hWnd, msg, wParam, lParam)
            : DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0010: // WM_CLOSE
                Running = false;
                DestroyWindow(hWnd);
                return IntPtr.Zero;
            case 0x0002: // WM_DESTROY
                Running = false;
                lock (s_lock) s_windows.Remove(hWnd);
                DisableCursorBlock();
                PostQuitMessage(0);
                return IntPtr.Zero;
            case 0x0100: // WM_KEYDOWN
                if (wParam.ToInt32() == 0x1B /*VK_ESCAPE*/) { Running = false; DestroyWindow(hWnd); }
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── P/Invoke ─────────────────────────────────────────────────────────
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int pt_x;
        public int pt_y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int X, int Y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
