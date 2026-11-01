using System.Runtime.InteropServices;

namespace Kurogane.Barre;

/// <summary>
/// Habillage des fenêtres par les attributs DWM.
///
/// Microsoft documente ces attributs depuis Windows 11 : couleur de la
/// barre de titre, du texte, de la bordure, et préférence d'arrondi des
/// coins. Ils s'appliquent par fenêtre, depuis l'extérieur, et se défont
/// par une valeur « défaut ».
///
/// Deux limites à connaître, et qui ne se contournent pas.
///
/// Une application qui dessine sa propre barre de titre — Chrome, Firefox,
/// VS Code, la plupart des applications Electron — ignore ces attributs :
/// il n'y a pas de barre de titre système à colorer. Les fenêtres Win32
/// classiques, l'Explorateur, PowerShell, le Bloc-notes, les Paramètres,
/// obéissent.
///
/// Et ces attributs n'existent qu'à partir de Windows 11 build 22000 : sur
/// une version antérieure l'appel échoue sans rien casser.
/// </summary>
internal static class ChromeNative
{
    private const int CORNER_PREFERENCE = 33;
    private const int BORDER_COLOR      = 34;
    private const int CAPTION_COLOR     = 35;
    private const int TEXT_COLOR        = 36;

    /// <summary>Valeur rendant à Windows le choix de la couleur.</summary>
    internal const int COULEUR_DEFAUT = unchecked((int)0xFFFFFFFF);

    internal enum Coins { Defaut = 0, Carres = 1, Arrondis = 2, PeuArrondis = 3 }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribut,
        ref int valeur, int taille);

    /// <summary>
    /// Renvoie le HRESULT, pas un booleen : savoir *que* ca a echoue ne sert
    /// a rien, savoir *pourquoi* decide de la suite. 0 vaut succes,
    /// 0x80070057 (E_INVALIDARG) signale un attribut que cette version de
    /// Windows ne connait pas.
    /// </summary>
    private static int Poser(IntPtr hwnd, int attribut, int valeur) =>
        DwmSetWindowAttribute(hwnd, attribut, ref valeur, sizeof(int));

    internal static int PoserCoins(IntPtr hwnd, Coins coins) =>
        Poser(hwnd, CORNER_PREFERENCE, (int)coins);

    internal static int PoserBarreTitre(IntPtr hwnd, int couleur) =>
        Poser(hwnd, CAPTION_COLOR, couleur);

    internal static int PoserTexteTitre(IntPtr hwnd, int couleur) =>
        Poser(hwnd, TEXT_COLOR, couleur);

    internal static int PoserBordure(IntPtr hwnd, int couleur) =>
        Poser(hwnd, BORDER_COLOR, couleur);

    /// <summary>Un HRESULT en clair, pour le journal.</summary>
    internal static string Verdict(int hr) => hr switch
    {
        0                               => "ok",
        unchecked((int)0x80070057)      => "E_INVALIDARG (attribut inconnu ici)",
        unchecked((int)0x80070006)      => "E_HANDLE (fenetre disparue)",
        unchecked((int)0x80004005)      => "E_FAIL",
        _                               => $"0x{hr:X8}",
    };

    /// <summary>
    /// 0x00BBGGRR — l'ordre attendu par COLORREF, l'inverse de l'écriture
    /// hexadécimale habituelle d'une couleur.
    /// </summary>
    internal static int Colorref(Kurogane.Core.Rgb c) =>
        (c.B << 16) | (c.G << 8) | c.R;
}
