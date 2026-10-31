using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Bascule clair/sombre.
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
///   AppsUseLightTheme   : 0 = applications en sombre
///   SystemUsesLightTheme: 0 = shell en sombre
/// </summary>
public sealed class DarkModeModule : IThemeModule
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public string Name => "dark-mode";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) => theme.DarkMode is not null;

    public IEnumerable<RegistryChange> Plan(ThemeDefinition theme)
    {
        var d = theme.DarkMode!;

        yield return new RegistryChange
        {
            Module = Name, Hive = RegistryHive.CurrentUser, SubKey = Key,
            Name = "AppsUseLightTheme", Kind = RegistryValueKind.DWord,
            NewValue = d.Apps ? 0 : 1,
            Description = d.Apps ? "Applications en thème sombre" : "Applications en thème clair",
        };

        yield return new RegistryChange
        {
            Module = Name, Hive = RegistryHive.CurrentUser, SubKey = Key,
            Name = "SystemUsesLightTheme", Kind = RegistryValueKind.DWord,
            NewValue = d.System ? 0 : 1,
            Description = d.System ? "Shell en thème sombre" : "Shell en thème clair",
        };
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.BroadcastThemeChange();
        journal.Ok("Notification du shell envoyée (ImmersiveColorSet)");
    }
}
