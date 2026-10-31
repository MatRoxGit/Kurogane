using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Icones a l'echelle de la machine : dossier generique, fleche de raccourci,
/// lecteurs. HKLM, donc droits administrateur requis.
///
/// Les index de « Shell Icons » ne sont pas documentes par Microsoft. Ceux
/// employes ici — 3 et 29 — ont ete verifies par la sonde sur Windows 11, pas
/// repris d'une table trouvee en ligne.
/// </summary>
public sealed class ShellIconsModule : IThemeModule
{
    private const string ShellIcons =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    private const string DriveIcons =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    private const int IndexDossier  = 3;
    private const int IndexRaccourci = 29;

    public string Name => "icones-systeme";
    public int Level => 2;

    public bool AppliesTo(ThemeDefinition theme) =>
        theme.Icons is { } i && (i.Dossier ?? i.Lecteur ?? i.RaccourciFleche) is not null;

    public IEnumerable<RegistryChange> Plan(ThemeDefinition theme)
    {
        var i = theme.Icons!;

        if (i.Dossier is { } d)
            yield return Shell(theme, IndexDossier, d, "Dossier generique");

        if (i.RaccourciFleche is { } r)
            yield return Shell(theme, IndexRaccourci, r, "Fleche de raccourci");

        if (i.Lecteur is { } l)
        {
            string chemin = Verifie(theme, l);

            // Les icones de lecteur se posent lettre par lettre : il n'existe
            // pas de valeur globale. On ne touche qu'aux disques fixes, un
            // lecteur amovible pouvant disparaitre entre deux executions.
            foreach (var disque in DriveInfo.GetDrives())
            {
                if (disque.DriveType != DriveType.Fixed) continue;

                string lettre = disque.Name.TrimEnd('\\', ':');

                yield return new RegistryChange
                {
                    Module      = Name,
                    Hive        = RegistryHive.LocalMachine,
                    SubKey      = $@"{DriveIcons}\{lettre}\DefaultIcon",
                    Name        = "",
                    Kind        = RegistryValueKind.ExpandString,
                    NewValue    = $"{chemin},0",
                    Description = $"Lecteur {lettre}: — {Path.GetFileName(chemin)}",
                };
            }
        }
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.RefreshShellIcons();
        journal.Ok("Cache d'icones du shell invalide");
        journal.Info("Les icones deja dessinees ne se rafraichissent qu'au redemarrage");
        journal.Info("d'explorer.exe : Stop-Process -Name explorer");
    }

    private RegistryChange Shell(ThemeDefinition theme, int index, string fichier, string description)
    {
        string chemin = Verifie(theme, fichier);

        return new RegistryChange
        {
            Module      = Name,
            Hive        = RegistryHive.LocalMachine,
            SubKey      = ShellIcons,
            Name        = index.ToString(),
            Kind        = RegistryValueKind.ExpandString,
            NewValue    = $"{chemin},0",
            Description = $"{description} (index {index}) — {Path.GetFileName(chemin)}",
        };
    }

    private static string Verifie(ThemeDefinition theme, string fichier)
    {
        string chemin = theme.ResolveIcon(fichier);

        if (!File.Exists(chemin))
            throw new FileNotFoundException(
                $"Icone introuvable : {chemin}\n" +
                $"(declaree dans le theme « {theme.Name} » comme « {fichier} »)");

        return chemin;
    }
}
