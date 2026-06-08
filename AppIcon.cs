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
    public static Icon Load(bool small = true)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            if (s != null)
                return small ? new Icon(s, SystemInformation.SmallIconSize) : new Icon(s, 256, 256);
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }
}
