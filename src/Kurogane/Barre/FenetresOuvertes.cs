using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kurogane.Barre;

/// <summary>
/// Les fenêtres que la barre des tâches doit lister, et le moyen d'y
/// revenir.
///
/// Le critère n'est pas « une fenêtre de premier niveau » : il y en a des
/// centaines, la plupart invisibles. Windows n'expose pas directement sa
/// propre règle, mais elle est stable et bien connue :
///
///   - visible, et sans propriétaire ;
///   - pas WS_EX_TOOLWINDOW, sauf si WS_EX_APPWINDOW est posé ;
///   - un titre non vide ;
///   - pas « cloaked » — les applications UWP suspendues restent des
///     fenêtres existantes et visibles au sens de l'API, mais ne sont plus
///     composées. Sans ce test, la barre se remplit de fantômes.
/// </summary>
internal static class FenetresOuvertes
{
    internal sealed record Entree(IntPtr Hwnd, string Titre, ImageSource? Icone);

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_APPWINDOW   = 0x00040000;
    private const uint GW_OWNER         = 4;
    private const int DWMWA_CLOAKED     = 14;

    private const int SW_RESTORE = 9;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc rappel, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint commande);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder tampon, int taille);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int commande);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam,
        IntPtr lParam, uint drapeaux, uint delaiMs, out IntPtr resultat);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribut,
        out int valeur, int taille);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    // ------------------------------------------------------------------

    internal static List<Entree> Lister(IntPtr aExclure)
    {
        var liste = new List<Entree>();

        bool Visiter(IntPtr h, IntPtr _)
        {
            if (h == aExclure || !Eligible(h)) return true;

            string titre = Titre(h);
            if (titre.Length == 0) return true;

            liste.Add(new Entree(h, titre, Icone(h)));
            return true;
        }

        EnumProc rappel = Visiter;
        EnumWindows(rappel, IntPtr.Zero);
        GC.KeepAlive(rappel);

        return liste;
    }

    private static bool Eligible(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;

        long styles = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();

        bool outil = (styles & WS_EX_TOOLWINDOW) != 0;
        bool appli = (styles & WS_EX_APPWINDOW) != 0;
        if (outil && !appli) return false;

        // Une application UWP suspendue reste visible au sens de l'API mais
        // n'est plus composée. Sans ce test, la barre se peuple de fantômes.
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int masquee, sizeof(int)) == 0
            && masquee != 0)
            return false;

        return true;
    }

    private static string Titre(IntPtr h)
    {
        int longueur = GetWindowTextLength(h);
        if (longueur <= 0) return "";

        var b = new StringBuilder(longueur + 1);
        GetWindowText(h, b, b.Capacity);
        return b.ToString();
    }

    /// <summary>
    /// Icône de la fenêtre. On interroge d'abord la fenêtre elle-même, puis
    /// on se rabat sur l'exécutable du processus. L'appel à la fenêtre passe
    /// par SendMessageTimeout : une application figée ne doit pas figer la
    /// barre avec elle.
    /// </summary>
    private static ImageSource? Icone(IntPtr h)
    {
        const uint WM_GETICON = 0x007F;
        const uint SMTO_ABORTIFHUNG = 0x0002;

        foreach (int type in new[] { 1 /* grande */, 0 /* petite */, 2 /* petite 2 */ })
        {
            SendMessageTimeout(h, WM_GETICON, new IntPtr(type), IntPtr.Zero,
                               SMTO_ABORTIFHUNG, 200, out var resultat);

            if (resultat != IntPtr.Zero) return Convertir(resultat);
        }

        // Repli : l'icône de la classe de fenêtre. Du Win32 pur — passer par
        // System.Drawing exigerait une référence que ce projet n'a pas, et
        // l'ajouter reviendrait à introduire un paquet NuGet.
        const int GCLP_HICON   = -14;
        const int GCLP_HICONSM = -34;

        foreach (int index in new[] { GCLP_HICON, GCLP_HICONSM })
        {
            var icone = GetClassLongPtr(h, index);
            if (icone != IntPtr.Zero) return Convertir(icone);
        }

        return null;
    }

    /// <summary>
    /// Les icônes obtenues ici appartiennent à la fenêtre ou à sa classe :
    /// on les lit, on ne les détruit jamais.
    /// </summary>
    private static ImageSource? Convertir(IntPtr icone)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icone, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ramène une fenêtre au premier plan. Windows restreint les changements
    /// de premier plan pour empêcher le vol de focus ; un clic de
    /// l'utilisateur sur la barre nous donne le droit de le faire.
    /// </summary>
    internal static void Activer(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }
}
