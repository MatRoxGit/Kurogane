using System.Runtime.InteropServices;
using System.Text;

namespace Kurogane.Native;

/// <summary>
/// Inspection des fenêtres du shell.
///
/// Tout est en lecture : énumération, classe, titre, position, visibilité.
/// Rien ici ne modifie une fenêtre — le restylage viendra dans son propre
/// fichier, comme pour le graveur de ressources.
/// </summary>
internal static class WindowNative
{
    internal delegate bool EnumWindowProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "FindWindowW")]
    internal static extern IntPtr FindWindow(string? classe, string? titre);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "FindWindowExW")]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr apres,
        string? classe, string? titre);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc rappel, IntPtr param);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder tampon, int taille);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder tampon, int taille);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    internal static string Classe(IntPtr hwnd)
    {
        var b = new StringBuilder(256);
        return GetClassName(hwnd, b, b.Capacity) > 0 ? b.ToString() : "";
    }

    internal static string Titre(IntPtr hwnd)
    {
        var b = new StringBuilder(512);
        return GetWindowText(hwnd, b, b.Capacity) > 0 ? b.ToString() : "";
    }

    /// <summary>Fenêtres filles directes et indirectes, dans l'ordre d'énumération.</summary>
    internal static List<IntPtr> Enfants(IntPtr parent)
    {
        var liste = new List<IntPtr>();

        bool Collecte(IntPtr h, IntPtr _) { liste.Add(h); return true; }

        EnumWindowProc rappel = Collecte;
        EnumChildWindows(parent, rappel, IntPtr.Zero);
        GC.KeepAlive(rappel);

        return liste;
    }
}
