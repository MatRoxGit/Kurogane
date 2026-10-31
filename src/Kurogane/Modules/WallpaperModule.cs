using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Fond d'écran. SystemParametersInfo écrit déjà la valeur lui-même, mais on
/// la pose explicitement d'abord : sans cela elle échappe à la capture, donc
/// au point de restauration, et l'ancien fond serait perdu.
/// </summary>
public sealed class WallpaperModule : IThemeModule
{
    private const string DesktopKey = @"Control Panel\Desktop";

    public string Name => "wallpaper";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) =>
        theme.Wallpaper is not null && !string.IsNullOrWhiteSpace(theme.Wallpaper.Path);

    public IEnumerable<RegistryChange> Plan(ThemeDefinition theme)
    {
        var w = theme.Wallpaper!;
        string path = theme.Resolve(w.Path);

        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Image de fond introuvable : {path}\n" +
                $"(déclarée dans le thème « {theme.Name} » comme « {w.Path} »)");

        var (style, tile) = StyleValues(w.Style);

        yield return Desktop("Wallpaper", RegistryValueKind.String, path,
            $"Fond d'écran : {Path.GetFileName(path)}");

        yield return Desktop("WallpaperStyle", RegistryValueKind.String, style,
            $"Ajustement : {w.Style}");

        yield return Desktop("TileWallpaper", RegistryValueKind.String, tile,
            "Mosaïque");
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        string path = theme.Resolve(theme.Wallpaper!.Path);

        if (NativeMethods.SetWallpaper(path))
            journal.Ok("Fond d'écran appliqué");
        else
            journal.Warn("SystemParametersInfo a refusé le fond d'écran — "
                       + "valeurs de registre posées, effet à la prochaine ouverture de session");
    }

    private static (string Style, string Tile) StyleValues(string style) =>
        style.ToLowerInvariant() switch
        {
            "fill"    => ("10", "0"),
            "fit"     => ("6",  "0"),
            "stretch" => ("2",  "0"),
            "center"  => ("0",  "0"),
            "tile"    => ("0",  "1"),
            "span"    => ("22", "0"),
            _ => throw new FormatException(
                     $"Ajustement inconnu : « {style} ». "
                   + "Valeurs acceptées : fill, fit, stretch, center, tile, span."),
        };

    private RegistryChange Desktop(string name, RegistryValueKind kind, object value, string desc) =>
        new() { Module = Name, Hive = RegistryHive.CurrentUser, SubKey = DesktopKey,
                Name = name, Kind = kind, NewValue = value, Description = desc };
}
