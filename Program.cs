using System.Windows.Forms;

namespace RitschyMirror;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static void Main()
    {
        // Per-Monitor-V2 programmatisch (zuverlaessiger als Manifest bei .NET) —
        // sonst werden Vollbild-Koordinaten/Groessen vom Windows-Scaling verfaelscht.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}
