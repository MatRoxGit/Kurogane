using Kurogane.Core;
using Kurogane.Theme;

namespace Kurogane.Modules;

/// <summary>
/// Un module traduit une section du thème en écritures de registre, puis
/// notifie le système une fois celles-ci appliquées.
///
/// Contrat : Plan() ne doit avoir AUCUN effet de bord. Tout ce qui écrit
/// passe par les RegistryChange retournés (donc par la capture et le point
/// de restauration) ou par PostApply(), qui ne fait que notifier.
/// </summary>
public interface IThemeModule
{
    string Name { get; }

    /// <summary>Niveau 1 = HKCU, sans élévation. Niveau 2 = HKLM, admin requis.</summary>
    int Level { get; }

    /// <summary>true si la section correspondante est présente dans le thème.</summary>
    bool AppliesTo(ThemeDefinition theme);

    /// <summary>Écritures prévues. Lecture seule, aucun effet de bord.</summary>
    IEnumerable<RegistryChange> Plan(ThemeDefinition theme);

    /// <summary>Notification du shell après écriture. N'est pas appelé en simulation.</summary>
    void PostApply(ThemeDefinition theme, Journal journal);
}
