using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace RitschyMirror;

/// <summary>
/// Laedt das eingebettete App-Icon (assets\app.ico, LogicalName "app.ico") fuer Tray +
/// Fenster. Faellt auf das System-Icon zurueck, falls die Ressource fehlt.
/// </summary>
internal static class AppIcon
{
    public static Icon Load(bool small = true) => LoadNamed("app.ico", small);

    /// <summary>Tray-Status-Icon: grün (Mirror läuft) oder rot (gestoppt).</summary>
    public static Icon LoadStatus(bool running) => LoadNamed(running ? "app_on.ico" : "app_off.ico", small: true);

    private static Icon LoadNamed(string resource, bool small)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (s != null)
                return small ? new Icon(s, SystemInformation.SmallIconSize) : new Icon(s, 256, 256);
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }
}
