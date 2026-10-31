using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Appels Win32 nécessaires pour que les modifications de registre soient
/// prises en compte à chaud, sans fermeture de session.
/// Tous documentés par Microsoft. Aucun ne modifie d'état persistant par
/// lui-même : ils ne font que notifier le shell.
/// </summary>
internal static class NativeMethods
{
    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPI_SETCURSORS       = 0x0057;
    private const uint SPIF_UPDATEINIFILE   = 0x01;
    private const uint SPIF_SENDCHANGE      = 0x02;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_THEMECHANGED  = 0x031A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    private const int  SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST       = 0x0000;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiString(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiPtr(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
        string lParam, uint flags, uint timeoutMs, out UIntPtr result);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>Applique le fond d'écran. Le chemin doit être absolu.</summary>
    internal static bool SetWallpaper(string absolutePath) =>
        SpiString(SPI_SETDESKWALLPAPER, 0, absolutePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    /// <summary>Recharge le jeu de curseurs depuis HKCU\Control Panel\Cursors.</summary>
    internal static bool ReloadCursors() =>
        SpiPtr(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);

    /// <summary>
    /// Force le shell et les applications à relire les couleurs et le thème.
    /// « ImmersiveColorSet » est le sujet que guettent Explorer et les apps
    /// WinUI pour basculer clair/sombre.
    /// </summary>
    internal static void BroadcastThemeChange()
    {
        foreach (var topic in new[] { "ImmersiveColorSet", "WindowsThemeElement" })
            SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                               topic, SMTO_ABORTIFHUNG, 1000, out _);

        SendMessageTimeout(HWND_BROADCAST, WM_THEMECHANGED, IntPtr.Zero,
                           string.Empty, SMTO_ABORTIFHUNG, 1000, out _);
    }

    /// <summary>Invalide le cache d'icônes du shell.</summary>
    internal static void RefreshShellIcons() =>
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
}
