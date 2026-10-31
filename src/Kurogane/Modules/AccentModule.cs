using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Couleur d'accent : barres de titre, barre des tâches, menu Démarrer.
///
/// Trois emplacements concourent au résultat, et ils ne se recouvrent pas :
///   DWM\AccentColor            — bordures et barres de titre        (ABGR)
///   DWM\ColorizationColor      — couleur de composition             (ARGB)
///   Explorer\Accent\AccentPalette — nuancier 8 teintes du shell     (binaire)
/// Modifier l'un sans les autres donne un rendu incohérent entre le bureau
/// et le menu Démarrer.
/// </summary>
public sealed class AccentModule : IThemeModule
{
    private const string DwmKey         = @"Software\Microsoft\Windows\DWM";
    private const string AccentKey      = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public string Name => "accent";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) => theme.Accent is not null;

    public IEnumerable<RegistryChange> Plan(ThemeDefinition theme)
    {
        var a   = theme.Accent!;
        var rgb = ColorUtil.Parse(a.Color);

        int abgr = ColorUtil.ToAbgr(rgb);
        int argb = ColorUtil.ToArgb(rgb);

        // --- DWM : bordures et barres de titre ---

        yield return Dwm("AccentColor", RegistryValueKind.DWord, abgr,
            $"Accent DWM {rgb} (ABGR 0x{abgr:X8})");

        yield return Dwm("ColorizationColor", RegistryValueKind.DWord, argb,
            $"Couleur de composition {rgb} (ARGB 0x{argb:X8})");

        yield return Dwm("ColorizationAfterglow", RegistryValueKind.DWord, argb,
            "Halo de composition aligné sur l'accent");

        yield return Dwm("ColorPrevalence", RegistryValueKind.DWord, a.TitleBars ? 1 : 0,
            a.TitleBars ? "Barres de titre colorées" : "Barres de titre neutres");

        // --- Explorer : nuancier du shell ---

        // La valeur en place est relue pour préserver son index 7, dont
        // l'usage nous échappe (voir ColorUtil). Lecture seule : Plan() reste
        // sans effet de bord.
        byte[] palette = ColorUtil.BuildAccentPalette(rgb, ReadCurrentPalette());

        yield return Accent("AccentPalette", RegistryValueKind.Binary, palette,
            $"Nuancier d'accent (accent au rang {ColorUtil.AccentIndex}, index 7 préservé)");

        yield return Accent("AccentColorMenu", RegistryValueKind.DWord, abgr,
            "Accent des menus");

        // Le menu Démarrer n'utilise pas l'accent brut mais la nuance du rang
        // juste en dessous — constaté sur deux installations de Windows 11.
        var startShade = ColorUtil.PaletteShade(rgb, ColorUtil.StartMenuIndex);

        yield return Accent("StartColorMenu", RegistryValueKind.DWord, ColorUtil.ToAbgr(startShade),
            $"Accent du menu Démarrer {startShade} (nuance {ColorUtil.StartMenuIndex})");

        // --- Personalize : barre des tâches et Démarrer ---

        yield return Personalize("ColorPrevalence", RegistryValueKind.DWord,
            a.TaskbarAndStart ? 1 : 0,
            a.TaskbarAndStart ? "Barre des tâches et Démarrer colorés" : "Barre des tâches neutre");

        yield return Personalize("EnableTransparency", RegistryValueKind.DWord,
            a.Transparency ? 1 : 0,
            a.Transparency ? "Transparence activée" : "Transparence désactivée");
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.BroadcastThemeChange();
        journal.Ok("Couleurs rediffusées au shell");
        journal.Info("Si la barre des tâches ne se recolore pas, un redémarrage "
                   + "d'explorer.exe suffit — il n'y a rien d'autre à refaire.");
    }

    /// <summary>
    /// Lit le nuancier actuellement en place. Lecture seule, aucun effet de
    /// bord. Retourne null si la valeur est absente ou d'un autre type.
    /// </summary>
    private static byte[]? ReadCurrentPalette()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var key = baseKey.OpenSubKey(AccentKey, writable: false);
        return key?.GetValue("AccentPalette") as byte[];
    }

    // --- fabriques ---

    private RegistryChange Dwm(string name, RegistryValueKind kind, object value, string desc) =>
        new() { Module = Name, Hive = RegistryHive.CurrentUser, SubKey = DwmKey,
                Name = name, Kind = kind, NewValue = value, Description = desc };

    private RegistryChange Accent(string name, RegistryValueKind kind, object value, string desc) =>
        new() { Module = Name, Hive = RegistryHive.CurrentUser, SubKey = AccentKey,
                Name = name, Kind = kind, NewValue = value, Description = desc };

    private RegistryChange Personalize(string name, RegistryValueKind kind, object value, string desc) =>
        new() { Module = Name, Hive = RegistryHive.CurrentUser, SubKey = PersonalizeKey,
                Name = name, Kind = kind, NewValue = value, Description = desc };
}
