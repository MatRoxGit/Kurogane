using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kurogane.Theme;

public sealed class ThemeDefinition
{
    public string Name { get; set; } = "sans nom";
    public string Version { get; set; } = "0.0.0";
    public string? Author { get; set; }

    public DarkModeSection? DarkMode { get; set; }
    public AccentSection?   Accent   { get; set; }
    public WallpaperSection? Wallpaper { get; set; }
    public IconsSection?    Icons     { get; set; }
    public TaskbarSection?  Taskbar   { get; set; }
    public StartMenuSection? StartMenu { get; set; }
    public TaskbarStyleSection? TaskbarStyle { get; set; }
    public BarreSection?    Barre     { get; set; }
    public FenetresSection? Fenetres  { get; set; }

    /// <summary>Dossier d'où le thème a été chargé — sert à résoudre les chemins relatifs.</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = "";

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ThemeDefinition Load(string path)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Thème introuvable : {full}");

        var theme = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(full), Opts)
            ?? throw new InvalidDataException($"Thème illisible : {full}");

        theme.BaseDirectory = Path.GetDirectoryName(full) ?? "";
        return theme;
    }

    /// <summary>Chemin absolu d'une icône, résolu depuis la base déclarée.</summary>
    public string ResolveIcon(string fileName) =>
        Resolve(Path.Combine(Icons?.Directory ?? "", fileName));

    public string Resolve(string relativeOrAbsolute) =>
        Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.GetFullPath(Path.Combine(BaseDirectory, relativeOrAbsolute));
}

public sealed class DarkModeSection
{
    /// <summary>Thème sombre pour les applications.</summary>
    public bool Apps { get; set; } = true;

    /// <summary>Thème sombre pour le shell (barre des tâches, menu Démarrer).</summary>
    public bool System { get; set; } = true;
}

public sealed class AccentSection
{
    /// <summary>Couleur d'accent, au format #RRGGBB.</summary>
    public string Color { get; set; } = "#C8102E";

    /// <summary>Colorer les barres de titre et les bordures de fenêtre.</summary>
    public bool TitleBars { get; set; } = true;

    /// <summary>Colorer la barre des tâches, le menu Démarrer et le centre de notifications.</summary>
    public bool TaskbarAndStart { get; set; } = true;

    /// <summary>Effets de transparence du shell.</summary>
    public bool Transparency { get; set; }
}

public sealed class WallpaperSection
{
    /// <summary>Chemin vers l'image, absolu ou relatif au fichier de thème.</summary>
    public string Path { get; set; } = "";

    /// <summary>fill | fit | stretch | center | tile | span</summary>
    public string Style { get; set; } = "fill";
}

public sealed class TaskbarSection
{
    /// <summary>« left » ou « center ». Absent : inchangé.</summary>
    public string? Alignment { get; set; }

    /// <summary>« small », « medium » ou « large ».</summary>
    public string? Size { get; set; }

    /// <summary>« hidden », « icon », « box » ou « boxLabel ».</summary>
    public string? Search { get; set; }

    public bool? Widgets { get; set; }
    public bool? Chat { get; set; }
    public bool? TaskView { get; set; }

    /// <summary>« always », « whenFull » ou « never ».</summary>
    public string? CombineButtons { get; set; }

    public bool? SecondsInClock { get; set; }
    public bool? Animations { get; set; }

    /// <summary>Masquage automatique : un seul bit dans un blob binaire.</summary>
    public bool? AutoHide { get; set; }
}

/// <summary>
/// Restylage à chaud de la barre : barre flottante et teinte. Ces réglages
/// ne sont pas appliqués par « apply » — ils n'écrivent rien et ne durent
/// que le temps où le compagnon résident tourne.
/// </summary>
/// <summary>Une application épinglée dans la barre Kurogane.</summary>
public sealed class EpingleBarre
{
    /// <summary>Texte de l'info-bulle, et repli si l'icône manque.</summary>
    public string? Libelle { get; set; }

    /// <summary>Exécutable ou document à ouvrir.</summary>
    public string? Commande { get; set; }

    public string? Arguments { get; set; }

    /// <summary>Nom de fichier .ico, résolu dans le dossier d'icônes du thème.</summary>
    public string? Icone { get; set; }
}

/// <summary>
/// La barre des tâches Kurogane, qui remplace celle de Windows pendant
/// qu'elle tourne. Rien n'est écrit : c'est une fenêtre, pas un réglage.
/// </summary>
public sealed class BarreSection
{
    /// <summary>Première ligne du bloc d'état.</summary>
    public string? Noeud { get; set; }

    /// <summary>Seconde ligne du bloc d'état.</summary>
    public string? Etat { get; set; }

    /// <summary>Suffixe affiché après la date, par exemple « KRG ».</summary>
    public string? SuffixeDate { get; set; }

    public List<EpingleBarre>? Epingles { get; set; }
}

/// <summary>
/// L'habillage des fenêtres de toutes les applications : barre de titre,
/// texte, bordure, et angles vifs plutôt qu'arrondis.
///
/// Posé par les attributs DWM, donc sans rien écrire sur le disque et sans
/// toucher au code des applications. Deux limites assumées : une
/// application qui dessine sa propre barre de titre — Chrome, Firefox,
/// VS Code — n'a pas de barre système à colorer et reste inchangée ; et DWM
/// n'offre aucun moyen de relire l'habillage courant d'une fenêtre, donc le
/// retour arrière rend le choix à Windows au lieu de rejouer une valeur
/// capturée.
/// </summary>
public sealed class FenetresSection
{
    /// <summary>Angles vifs. Absent ou faux : on laisse l'arrondi de Windows 11.</summary>
    public bool? CoinsCarres { get; set; }

    /// <summary>Couleur de la barre de titre, au format #RRGGBB.</summary>
    public string? BarreTitre { get; set; }

    /// <summary>Couleur du titre, au format #RRGGBB.</summary>
    public string? TexteTitre { get; set; }

    /// <summary>Couleur du liseré de fenêtre, au format #RRGGBB.</summary>
    public string? Bordure { get; set; }
}

public sealed class TaskbarStyleSection
{
    /// <summary>Marge latérale en pixels. Au-delà d'une douzaine, le contenu est rogné.</summary>
    public int? Marge { get; set; }

    /// <summary>Marge haute et basse en pixels.</summary>
    public int? MargeBasse { get; set; }

    /// <summary>Rayon des coins en pixels.</summary>
    public int? Rayon { get; set; }

    /// <summary>Couleur de teinte, au format #RRGGBB. Absente : aucune teinte.</summary>
    public string? Teinte { get; set; }

    /// <summary>Opacité de la teinte, de 0 à 255.</summary>
    public int? Opacite { get; set; }

    /// <summary>Flou acrylique plutôt qu'un dégradé net.</summary>
    public bool? Acrylique { get; set; }
}

public sealed class StartMenuSection
{
    /// <summary>« default », « morePins » ou « moreRecommendations ».</summary>
    public string? Layout { get; set; }

    public bool? RecentFiles { get; set; }
    public bool? RecentApps { get; set; }
    public bool? AccountNotifications { get; set; }
}

public sealed class IconsSection
{
    /// <summary>Dossier des .ico, absolu ou relatif au fichier de thème.</summary>
    public string Directory { get; set; } = "";

    // Niveau 1 — HKCU, sans élévation
    public string? CePc { get; set; }
    public string? Reseau { get; set; }
    public string? Utilisateur { get; set; }
    public string? PanneauConfiguration { get; set; }
    public string? CorbeilleVide { get; set; }
    public string? CorbeillePleine { get; set; }
    public string? Bureau { get; set; }

    // Niveau 1 — desktop.ini dans le dossier du profil
    public string? Documents { get; set; }
    public string? Telechargements { get; set; }
    public string? Images { get; set; }
    public string? Musique { get; set; }
    public string? Videos { get; set; }

    // Niveau 2 — HKLM, administrateur requis
    public string? Dossier { get; set; }
    public string? Lecteur { get; set; }
    public string? RaccourciFleche { get; set; }
}
