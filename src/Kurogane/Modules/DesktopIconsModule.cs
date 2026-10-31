using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Icones du Bureau : Ce PC, Reseau, dossier utilisateur, Panneau de
/// configuration, Corbeille.
///
/// Mecanisme employe par le panneau « Parametres des icones du Bureau » de
/// Windows lui-meme, et confirme par la sonde sur Windows 11. HKCU, donc
/// aucun droit administrateur.
/// </summary>
public sealed class DesktopIconsModule : IThemeModule
{
    private const string Clsid =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{0}\DefaultIcon";

    private const string CePc        = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string Reseau      = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    private const string Utilisateur = "{59031a47-3f72-44a7-89c5-5595fe6b30ee}";
    private const string Panneau     = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";
    private const string Corbeille   = "{645FF040-5081-101B-9F08-00AA002F954E}";

    public string Name => "icones-bureau";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) =>
        theme.Icons is { } i &&
        (i.CePc ?? i.Reseau ?? i.Utilisateur ?? i.PanneauConfiguration
              ?? i.CorbeilleVide ?? i.CorbeillePleine) is not null;

    public IEnumerable<RegistryChange> Plan(ThemeDefinition theme)
    {
        var i = theme.Icons!;

        if (i.CePc is { } a)
            yield return Entree(theme, CePc, "", a, "Icone Ce PC");

        if (i.Reseau is { } b)
            yield return Entree(theme, Reseau, "", b, "Icone Reseau");

        if (i.Utilisateur is { } c)
            yield return Entree(theme, Utilisateur, "", c, "Icone du dossier utilisateur");

        if (i.PanneauConfiguration is { } d)
            yield return Entree(theme, Panneau, "", d, "Icone Panneau de configuration");

        // La Corbeille porte ses deux etats sous la meme cle. La valeur par
        // defaut recoit l'etat vide : c'est elle que lisent les composants qui
        // ignorent la distinction.
        if (i.CorbeilleVide is { } e)
        {
            yield return Entree(theme, Corbeille, "",      e, "Corbeille (valeur par defaut)");
            yield return Entree(theme, Corbeille, "empty", e, "Corbeille vide");
        }

        if (i.CorbeillePleine is { } f)
            yield return Entree(theme, Corbeille, "full", f, "Corbeille pleine");
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.RefreshShellIcons();
        journal.Ok("Cache d'icones du shell invalide");
    }

    private RegistryChange Entree(ThemeDefinition theme, string guid, string valeur,
                                  string fichier, string description)
    {
        string chemin = theme.ResolveIcon(fichier);

        if (!File.Exists(chemin))
            throw new FileNotFoundException(
                $"Icone introuvable : {chemin}\n" +
                $"(declaree dans le theme « {theme.Name} » comme « {fichier} »)");

        return new RegistryChange
        {
            Module      = Name,
            Hive        = RegistryHive.CurrentUser,
            SubKey      = string.Format(Clsid, guid),
            Name        = valeur,
            Kind        = RegistryValueKind.ExpandString,
            // Le suffixe « ,0 » designe la premiere ressource du fichier. Sans
            // lui, la Corbeille ne bascule pas entre vide et pleine.
            NewValue    = $"{chemin},0",
            Description = $"{description} — {Path.GetFileName(chemin)}",
        };
    }
}
