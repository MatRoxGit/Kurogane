using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Borne une fenêtre maximisée à la zone de travail.
///
/// Une fenêtre WPF en <c>WindowStyle="None"</c> ne connaît plus les limites
/// que le système pose d'ordinaire : maximisée, elle couvre le moniteur
/// entier, barre des tâches comprise, et son bas disparaît dessous.
///
/// La correction consiste à répondre à WM_GETMINMAXINFO avec la zone de
/// travail du moniteur — celle que Windows calcule en retirant les barres
/// réservées, la nôtre incluse puisqu'elle occupe la bande que la barre
/// Windows réservait déjà.
///
/// Les coordonnées sont en pixels physiques. Le processus étant déclaré
/// sensible au DPI, il n'y a pas de virtualisation à défaire.
/// </summary>
internal static class MaximisationNative
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint drapeaux);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr moniteur, ref MONITORINFO info);

    /// <summary>
    /// À brancher sur le HwndSource de la fenêtre. Renvoie sans rien faire
    /// pour tous les autres messages.
    /// </summary>
    internal static IntPtr Filtrer(IntPtr hwnd, int message, IntPtr wParam,
        IntPtr lParam, ref bool traite)
    {
        if (message != WM_GETMINMAXINFO) return IntPtr.Zero;

        IntPtr moniteur = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (moniteur == IntPtr.Zero) return IntPtr.Zero;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(moniteur, ref info)) return IntPtr.Zero;

        var limites = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Les positions sont relatives au coin du moniteur, pas à l'origine
        // du bureau : sur un écran secondaire, utiliser les coordonnées
        // absolues placerait la fenêtre hors champ.
        limites.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        limites.ptMaxPosition.Y = info.rcWork.Top  - info.rcMonitor.Top;

        limites.ptMaxSize.X = info.rcWork.Right  - info.rcWork.Left;
        limites.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

        Marshal.StructureToPtr(limites, lParam, true);

        traite = true;
        return IntPtr.Zero;
    }
}
