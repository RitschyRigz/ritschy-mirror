using System.Threading;
using System.Windows.Forms;

namespace RitschyMirror;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // Named-Objekte für Single-Instance (Mutex) + „Einstellungen zeigen"-Signal an die laufende Instanz.
    internal const string ShowSettingsEvent = "RitschyMirror.ShowSettings.Event";
    private const string MutexName = "RitschyMirror.SingleInstance.Mutex";

    [STAThread]
    private static void Main()
    {
        // Single-Instance: läuft schon eine Kopie, bitten wir sie nur, die Einstellungen
        // zu öffnen, und beenden uns. Verhindert doppelte Tray-Icons + Agent-Port-Konflikt.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowSettingsEvent).Set(); } catch { }
            return;
        }

        // Per-Monitor-V2 programmatisch (zuverlaessiger als Manifest bei .NET) —
        // sonst werden Vollbild-Koordinaten/Groessen vom Windows-Scaling verfaelscht.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(Environment.GetCommandLineArgs()));
        GC.KeepAlive(mutex);  // Mutex bis App-Ende am Leben halten
    }
}
