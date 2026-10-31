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
