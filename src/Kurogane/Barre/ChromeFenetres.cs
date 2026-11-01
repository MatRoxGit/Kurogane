using System.Runtime.InteropServices;
using System.Text;
using Kurogane.Core;

namespace Kurogane.Barre;

/// <summary>
/// Applique l'habillage Kurogane aux fenêtres de toutes les applications,
/// et le retire proprement à l'arrêt.
///
/// Rien n'est écrit : les attributs DWM vivent avec la fenêtre. Une fenêtre
/// fermée emporte son habillage, une fenêtre nouvelle n'en a pas tant qu'on
/// ne le lui a pas posé — d'où le passage régulier.
///
/// On note les fenêtres traitées pour savoir lesquelles rendre à leur état
/// d'origine. « D'origine » signifie ici la valeur « défaut », celle qui
/// rend à Windows le choix de la couleur : DWM n'offre aucun moyen de lire
/// l'habillage courant d'une fenêtre, donc on ne peut pas capturer avant
/// d'écrire comme on le fait pour le registre. C'est une limite de l'API,
/// pas un relâchement de la discipline.
/// </summary>
internal sealed class ChromeFenetres
{
    internal sealed record Reglage(
        bool CoinsCarres,
        Rgb? BarreTitre,
        Rgb? TexteTitre,
        Rgb? Bordure);

    private readonly Reglage _reglage;
    private readonly HashSet<IntPtr> _traitees = new();

    internal ChromeFenetres(Reglage reglage) => _reglage = reglage;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumProc rappel, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder tampon, int taille);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder tampon, int taille);

    private const int GWL_STYLE        = -16;
    private const int GWL_EXSTYLE      = -20;
    private const long WS_CAPTION      = 0x00C00000;
    private const long WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// Les fenêtres du shell : les habiller ferait apparaître une barre de
    /// titre rouge sur le bureau ou sur notre propre barre.
    ///
    /// « ApplicationFrameWindow » n'y figure pas volontairement : c'est
    /// l'hôte des applications UWP — Paramètres, Calculatrice, Photos — et
    /// sa barre de titre est bien dessinée par le système. Si l'une d'elles
    /// peint la sienne par-dessus et que le résultat jure, c'est une ligne
    /// à ajouter ici.
    /// </summary>
    private static readonly string[] Exclues =
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow",
    };

    /// <summary>
    /// Passe sur toutes les fenêtres visibles et habille les nouvelles.
    /// Avec un journal, détaille ce que DWM répond fenêtre par fenêtre :
    /// c'est la seule façon de distinguer « appliqué » de « tenté ».
    /// </summary>
    internal int Appliquer(Journal? journal = null)
    {
        int posees = 0;

        bool Visiter(IntPtr h, IntPtr _)
        {
            if (!Eligible(h) || !_traitees.Add(h)) return true;

            var verdicts = Habiller(h);
            posees++;

            if (journal is not null)
            {
                journal.Info($"  {Classe(h),-28} « {Titre(h)} »");
                foreach (var (attribut, hr) in verdicts)
                    journal.Info($"      {attribut,-14} {ChromeNative.Verdict(hr)}");
            }

            return true;
        }

        EnumProc rappel = Visiter;
        EnumWindows(rappel, IntPtr.Zero);
        GC.KeepAlive(rappel);

        // Une fenêtre fermée emporte son habillage : son handle n'a plus
        // rien à restaurer, et le garder ferait enfler la liste au fil des
        // heures. On l'oublie.
        _traitees.RemoveWhere(h => !IsWindow(h));

        return posees;
    }

    /// <summary>
    /// Les fenêtres candidates, sans rien leur poser. Sert à la sonde, qui
    /// doit mesurer leur état avant d'y toucher.
    /// </summary>
    internal static List<(IntPtr Hwnd, string Classe, string Titre)> Candidates()
    {
        var liste = new List<(IntPtr, string, string)>();

        bool Visiter(IntPtr h, IntPtr _)
        {
            if (Eligible(h)) liste.Add((h, Classe(h), Titre(h)));
            return true;
        }

        EnumProc rappel = Visiter;
        EnumWindows(rappel, IntPtr.Zero);
        GC.KeepAlive(rappel);

        return liste;
    }

    /// <summary>Rend toutes les fenêtres traitées à l'habillage par défaut.</summary>
    internal void Restaurer()
    {
        foreach (var h in _traitees)
        {
            ChromeNative.PoserCoins(h, ChromeNative.Coins.Defaut);
            ChromeNative.PoserBarreTitre(h, ChromeNative.COULEUR_DEFAUT);
            ChromeNative.PoserTexteTitre(h, ChromeNative.COULEUR_DEFAUT);
            ChromeNative.PoserBordure(h, ChromeNative.COULEUR_DEFAUT);
        }

        _traitees.Clear();
    }

    private List<(string Attribut, int Hr)> Habiller(IntPtr h)
    {
        var verdicts = new List<(string, int)>(4);

        if (_reglage.CoinsCarres)
            verdicts.Add(("coins", ChromeNative.PoserCoins(h, ChromeNative.Coins.Carres)));

        if (_reglage.BarreTitre is { } barre)
            verdicts.Add(("barre-titre", ChromeNative.PoserBarreTitre(h, ChromeNative.Colorref(barre))));

        if (_reglage.TexteTitre is { } texte)
            verdicts.Add(("texte-titre", ChromeNative.PoserTexteTitre(h, ChromeNative.Colorref(texte))));

        if (_reglage.Bordure is { } bordure)
            verdicts.Add(("bordure", ChromeNative.PoserBordure(h, ChromeNative.Colorref(bordure))));

        return verdicts;
    }

    private static string Classe(IntPtr h)
    {
        var nom = new StringBuilder(256);
        GetClassName(h, nom, nom.Capacity);
        return nom.ToString();
    }

    private static string Titre(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n == 0) return "";

        var t = new StringBuilder(n + 1);
        GetWindowText(h, t, t.Capacity);

        var s = t.ToString();
        return s.Length > 44 ? s[..44] + "…" : s;
    }

    private static bool Eligible(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindowTextLength(h) == 0) return false;

        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION) return false;

        long etendu = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((etendu & WS_EX_TOOLWINDOW) != 0) return false;

        return !Exclues.Contains(Classe(h));
    }
}
