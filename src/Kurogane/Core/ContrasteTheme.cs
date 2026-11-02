using System.Diagnostics;
using System.IO;
using System.Text;
using Kurogane.Theme;

namespace Kurogane.Core;

/// <summary>
/// Produit et applique le fichier .theme de contraste.
///
/// Pourquoi hors du socle de registre : un thème possède une annulation
/// native, documentée et accessible à l'utilisateur — Paramètres,
/// Accessibilité, Thèmes contrastés, Aucun. Le socle existe parce qu'une
/// écriture de registre n'a pas d'annulation ; reconstruire par-dessus celle
/// que Windows garantit déjà serait de la cérémonie. Le principe du projet
/// est « rien d'irréversible », pas « tout passe par le socle ».
///
/// Les .theme livrés par Windows ne sont pas touchés : le nôtre est écrit
/// dans %LOCALAPPDATA%\Microsoft\Windows\Themes, et Windows garde lui-même
/// la trace du thème précédent.
///
/// La simulation reste le défaut : sans --commit, le fichier est affiché,
/// pas écrit.
/// </summary>
public static class ContrasteTheme
{
    /// <summary>
    /// Style visuel livré par Windows sur lequel le thème se raccroche. On
    /// s'y rattache, on ne le remplace pas : aucune signature contournée.
    /// </summary>
    private const string StyleVisuel = @"%SystemRoot%\resources\themes\Aero\AeroLite.msstyles";

    private const string ThemeSombreWindows = @"%SystemRoot%\resources\Themes\dark.theme";

    /// <summary>
    /// Identifiants de dossier du shell portés par un .theme. Les mêmes que
    /// ceux du module d'icônes, et c'est voulu : sans eux, l'application du
    /// thème ferait retomber les icônes du bureau sur imageres.dll.
    /// </summary>
    private const string ClsidCePc        = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string ClsidUtilisateur = "{59031A47-3F72-44A7-89C5-5595FE6B30EE}";
    private const string ClsidReseau      = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    private const string ClsidCorbeille   = "{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <summary>
    /// Traduction des rôles du thème vers les trente-et-un noms de couleurs
    /// attendus par Windows. L'ordre suit celui des thèmes livrés par
    /// Microsoft, pour qu'une comparaison reste lisible.
    /// </summary>
    private static IEnumerable<(string Nom, string Role)> Correspondances(ContrasteSection c)
    {
        yield return ("Window",                c.Listes);
        yield return ("ButtonFace",            c.Surface);
        yield return ("Menu",                  c.Surface);
        yield return ("MenuBar",               c.Surface);
        yield return ("Scrollbar",             c.Surface);
        yield return ("InfoWindow",            c.Surface);
        yield return ("AppWorkspace",          c.Fond);
        yield return ("Background",            c.Fond);

        yield return ("WindowText",            c.Texte);
        yield return ("ButtonText",            c.Texte);
        yield return ("MenuText",              c.Texte);
        yield return ("InfoText",              c.Texte);
        yield return ("GrayText",              c.TexteEteint);

        yield return ("Hilight",               c.Selection);
        yield return ("HilightText",           c.TexteSelection);
        yield return ("MenuHilight",           c.Selection);
        yield return ("HotTrackingColor",      c.Survol);

        yield return ("WindowFrame",           c.Cadre);
        yield return ("ActiveBorder",          c.Cadre);
        yield return ("InactiveBorder",        c.Relief);

        yield return ("ActiveTitle",           c.Surface);
        yield return ("TitleText",             c.Texte);
        yield return ("InactiveTitle",         c.Listes);
        yield return ("InactiveTitleText",     c.TexteEteint);
        yield return ("GradientActiveTitle",   c.Surface);
        yield return ("GradientInactiveTitle", c.Listes);

        yield return ("ButtonShadow",          c.Relief);
        yield return ("ButtonHilight",         c.Relief);
        yield return ("ButtonLight",           c.Relief);
        yield return ("ButtonAlternateFace",   c.Relief);
        yield return ("ButtonDkShadow",        c.Fond);
    }

    /// <summary>
    /// Section curseurs. Son absence ne laisse pas les curseurs en place :
    /// Windows efface les dix-sept entrées existantes. Mesuré, pas supposé.
    /// </summary>
    private static readonly (string, string)[] Curseurs =
    {
        ("Arrow",       @"%SystemRoot%\cursors\aero_arrow.cur"),
        ("Help",        @"%SystemRoot%\cursors\aero_helpsel.cur"),
        ("AppStarting", @"%SystemRoot%\cursors\aero_working.ani"),
        ("Wait",        @"%SystemRoot%\cursors\aero_busy.ani"),
        ("NWPen",       @"%SystemRoot%\cursors\aero_pen.cur"),
        ("No",          @"%SystemRoot%\cursors\aero_unavail.cur"),
        ("SizeNS",      @"%SystemRoot%\cursors\aero_ns.cur"),
        ("SizeWE",      @"%SystemRoot%\cursors\aero_ew.cur"),
        ("Crosshair",   ""),
        ("IBeam",       ""),
        ("SizeNWSE",    @"%SystemRoot%\cursors\aero_nwse.cur"),
        ("SizeNESW",    @"%SystemRoot%\cursors\aero_nesw.cur"),
        ("SizeAll",     @"%SystemRoot%\cursors\aero_move.cur"),
        ("UpArrow",     @"%SystemRoot%\cursors\aero_up.cur"),
        ("Hand",        @"%SystemRoot%\cursors\aero_link.cur"),
        ("Link",        ""),
        ("DefaultValue","Windows Default"),
    };

    public static string Generer(ThemeDefinition theme)
    {
        var c = theme.Contraste ?? new ContrasteSection();
        var b = new StringBuilder();

        b.AppendLine("; Genere par Kurogane - ne pas editer a la main.");
        b.AppendLine("; La palette vit dans la section \"contraste\" du fichier de theme JSON.");
        b.AppendLine("; Retrait : Parametres > Accessibilite > Themes contrastes > Aucun.");
        b.AppendLine();

        b.AppendLine("[Theme]");
        b.AppendLine($"DisplayName={Ascii(theme.Name)}");
        b.AppendLine("SetLogonBackground=0");
        b.AppendLine();

        Icone(b, ClsidCePc,        theme, theme.Icons?.CePc,        "Ce PC");
        Icone(b, ClsidUtilisateur, theme, theme.Icons?.Utilisateur, "Fichiers de l'utilisateur");
        Icone(b, ClsidReseau,      theme, theme.Icons?.Reseau,      "Reseau");
        Corbeille(b, theme);

        b.AppendLine(@"[Control Panel\Colors]");
        foreach (var (nom, role) in Correspondances(c))
            b.AppendLine($"{nom}={Triplet(role)}");
        b.AppendLine();

        b.AppendLine(@"[Control Panel\Cursors]");
        foreach (var (nom, valeur) in Curseurs)
            b.AppendLine($"{nom}={valeur}");
        b.AppendLine();

        b.AppendLine(@"[Control Panel\Desktop]");
        if (theme.Wallpaper is { Path.Length: > 0 } fond)
        {
            // Redeclare explicitement : sans cette ligne, l'application du
            // theme vide la valeur et le fond d'ecran disparait.
            b.AppendLine($"Wallpaper={theme.Resolve(fond.Path)}");
            b.AppendLine("TileWallpaper=0");
            b.AppendLine("WallpaperStyle=10");
        }
        b.AppendLine("Pattern=");
        b.AppendLine();

        b.AppendLine("[VisualStyles]");
        b.AppendLine($"Path={StyleVisuel}");
        b.AppendLine("ColorStyle=NormalColor");
        b.AppendLine("Size=NormalSize");

        // Les quatre entrees qui suivent sont reprises d'aero.theme, livre par
        // Windows. Sans elles, le moteur de themes ecrase le mode sombre et
        // l'accent, qu'il fallait alors reposer par un apply apres coup.
        //
        // AutoColorization=0 empeche en plus Windows de rederiver l'accent
        // depuis le fond d'ecran.
        b.AppendLine("AutoColorization=0");

        if (theme.Accent is { Color.Length: > 0 } accent)
        {
            // 0XAARRGGBB, alpha 0xC4 : la forme qu'ecrit Windows lui-meme, et
            // celle que pose deja le module d'accent.
            int argb = ColorUtil.ToArgb(ColorUtil.Parse(accent.Color));
            b.AppendLine($"ColorizationColor=0X{argb:X8}");
        }

        bool sombre = theme.DarkMode?.Apps ?? true;
        b.AppendLine($"SystemMode={(sombre ? "Dark" : "Light")}");
        b.AppendLine($"AppMode={(sombre ? "Dark" : "Light")}");

        b.AppendLine("HighContrast=1");
        b.AppendLine();

        b.AppendLine("[MasterThemeSelector]");
        b.AppendLine("MTSM=RJSPBS");

        return b.ToString();
    }

    private static void Icone(StringBuilder b, string clsid,
        ThemeDefinition theme, string? fichier, string libelle)
    {
        if (fichier is null) return;

        b.AppendLine($"; {Ascii(libelle)}");
        b.AppendLine($@"[CLSID\{clsid}\DefaultIcon]");
        // Le suffixe ,0 reproduit la forme qu'ecrit le module d'icones. Il ne
        // suffit pas a eviter la reecriture : le moteur de themes normalise
        // la valeur et supprime l'index en l'appliquant. C'est ChangeRecord
        // qui traite les deux formes comme equivalentes.
        b.AppendLine($"DefaultValue={theme.ResolveIcon(fichier)},0");
        b.AppendLine();
    }

    private static void Corbeille(StringBuilder b, ThemeDefinition theme)
    {
        string? pleine = theme.Icons?.CorbeillePleine;
        string? vide   = theme.Icons?.CorbeilleVide;
        if (pleine is null && vide is null) return;

        b.AppendLine("; Corbeille");
        b.AppendLine($@"[CLSID\{ClsidCorbeille}\DefaultIcon]");
        if (pleine is not null) b.AppendLine($"Full={theme.ResolveIcon(pleine)},0");
        if (vide   is not null) b.AppendLine($"Empty={theme.ResolveIcon(vide)},0");
        b.AppendLine();
    }

    /// <summary>« #RRGGBB » vers « R G B », la forme attendue dans un .theme.</summary>
    private static string Triplet(string hex)
    {
        var c = ColorUtil.Parse(hex);
        return $"{c.R} {c.G} {c.B}";
    }

    /// <summary>
    /// Un .theme est lu comme un fichier INI. On reste en ASCII, seule forme
    /// vérifiée sur la VM, et on retire les accents plutôt que de risquer une
    /// lecture en page de codes locale.
    /// </summary>
    private static string Ascii(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(d.Length);

        foreach (var ch in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;

            b.Append(ch < 128 ? ch : '?');
        }

        return b.ToString();
    }

    public static string Destination(ThemeDefinition theme)
    {
        string dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Themes");

        string nom = Ascii(theme.Name).Replace(' ', '-');
        foreach (var c in Path.GetInvalidFileNameChars()) nom = nom.Replace(c, '-');

        return Path.Combine(dossier, nom + ".theme");
    }

    public static void Ecrire(string chemin, string contenu)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, contenu, Encoding.ASCII);
    }

    /// <summary>
    /// Applique le thème en le lançant. Il n'existe pas d'API documentée pour
    /// appliquer un thème sans ouvrir les Paramètres : la fenêtre s'ouvre,
    /// c'est le prix de la voie supportée.
    /// </summary>
    public static void Lancer(string chemin) =>
        Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });

    /// <summary>Repasse au thème sombre livré par Windows.</summary>
    public static void Retirer() =>
        Lancer(Environment.ExpandEnvironmentVariables(ThemeSombreWindows));
}
