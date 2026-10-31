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

    // Niveau 2 — HKLM, administrateur requis
    public string? Dossier { get; set; }
    public string? Lecteur { get; set; }
    public string? RaccourciFleche { get; set; }
}
