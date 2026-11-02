using Kurogane.Core;
using Microsoft.Win32;

namespace Kurogane.Probe;

/// <summary>
/// Sonde le remplacement de l'Explorateur comme gestionnaire de dossiers —
/// LECTURE SEULE.
///
/// Elle existe avant l'explorateur lui-même, et c'est volontaire. Devenir le
/// gestionnaire par défaut des dossiers passe par des associations que
/// Microsoft protège au niveau du noyau depuis 2024 : le User Choice
/// Protection Driver, `ucpd.sys`, qui nous a déjà bloqués sur les widgets de
/// la barre des tâches. Si la voie est fermée, mieux vaut le savoir avant
/// d'écrire l'application que de le découvrir une fois celle-ci terminée.
///
/// Aucune écriture, aucune élévation requise.
/// </summary>
internal static class ExplorateurProbe
{
    private sealed record Cible(string Libelle, RegistryKey Ruche, string Chemin, string? Valeur);

    /// <summary>
    /// CLSID du dossier « Ce PC ». C'est lui que Win+E ouvre, et c'est par sa
    /// sous-clé `opennewwindow` que des outils tiers détournent le raccourci.
    /// </summary>
    private const string ClsidCePc = "{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

    internal static int Executer(Journal journal)
    {
        journal.Step("Gestionnaire de dossiers");
        journal.Info("Qui ouvre un dossier aujourd'hui, et où est-ce écrit.");
        journal.Blank();

        var cibles = new[]
        {
            new Cible("Dossier — surcharge utilisateur", Registry.CurrentUser,
                @"Software\Classes\Directory\shell\open\command", null),
            new Cible("Dossier — machine", Registry.LocalMachine,
                @"Software\Classes\Directory\shell\open\command", null),
            new Cible("Folder — surcharge utilisateur", Registry.CurrentUser,
                @"Software\Classes\Folder\shell\open\command", null),
            new Cible("Folder — machine", Registry.LocalMachine,
                @"Software\Classes\Folder\shell\open\command", null),
            new Cible("Verbe par défaut d'un dossier", Registry.LocalMachine,
                @"Software\Classes\Directory\shell", null),
            new Cible("Win+E — surcharge utilisateur", Registry.CurrentUser,
                $@"Software\Classes\CLSID\{ClsidCePc}\shell\opennewwindow\command", null),
            new Cible("Win+E — machine", Registry.LocalMachine,
                $@"Software\Classes\CLSID\{ClsidCePc}\shell\opennewwindow\command", null),
        };

        foreach (var c in cibles) Lire(journal, c);

        journal.Blank();
        return Ucpd(journal);
    }

    private static void Lire(Journal journal, Cible c)
    {
        using var cle = c.Ruche.OpenSubKey(c.Chemin);

        string ruche = ReferenceEquals(c.Ruche, Registry.CurrentUser) ? "HKCU" : "HKLM";

        if (cle is null)
        {
            // Absente n'est pas anormal : une surcharge utilisateur n'existe
            // que si quelqu'un l'a posée. C'est justement l'endroit où l'on
            // écrirait, et son absence se restaure par une suppression.
            journal.Info($"  {c.Libelle}");
            journal.Info($"      {ruche}\\{c.Chemin}");
            journal.Info("      (absente)");
            return;
        }

        object? v = cle.GetValue(c.Valeur);

        journal.Info($"  {c.Libelle}");
        journal.Info($"      {ruche}\\{c.Chemin}");
        journal.Info($"      {v?.ToString() ?? "(pas de valeur par défaut)"}");

        var sous = cle.GetSubKeyNames();
        if (sous.Length > 0)
            journal.Info($"      sous-clés : {string.Join(", ", sous)}");
    }

    /// <summary>
    /// État du User Choice Protection Driver. C'est lui qui décide si
    /// l'ambition « remplacer l'Explorateur » est tenable.
    /// </summary>
    private static int Ucpd(Journal journal)
    {
        journal.Step("User Choice Protection Driver");

        using var service = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\UCPD");

        if (service is null)
        {
            journal.Ok("ucpd.sys absent de cette installation.");
            journal.Info("Les associations ne sont pas protégées au niveau du noyau.");
            journal.Blank();
            journal.Info("Voie du remplacement : ouverte, sous réserve d'un essai réel.");
            return 0;
        }

        int demarrage = Convert.ToInt32(service.GetValue("Start", 4));

        string etat = demarrage switch
        {
            0 => "démarrage noyau",
            1 => "démarrage système",
            2 => "automatique",
            3 => "manuel",
            4 => "désactivé",
            _ => $"inconnu ({demarrage})",
        };

        journal.Info($"  Start = {demarrage} — {etat}");

        if (demarrage >= 4)
        {
            journal.Ok("Pilote désactivé : les associations ne sont pas interceptées.");
            journal.Blank();
            journal.Info("Voie du remplacement : ouverte, sous réserve d'un essai réel.");
            return 0;
        }

        journal.Warn("Pilote actif : les écritures d'association peuvent être");
        journal.Warn("interceptées au niveau du noyau, y compris dans HKCU et");
        journal.Warn("y compris en tant qu'administrateur.");
        journal.Blank();
        journal.Info("C'est le même mécanisme qui a bloqué TaskbarDa. Il ne refuse");
        journal.Info("pas toujours : il protège les associations que l'utilisateur");
        journal.Info("est censé choisir lui-même. Seul un essai réel tranche.");
        journal.Blank();
        journal.Info("Essai non destructif possible : poser une surcharge HKCU sur");
        journal.Info("Directory\\shell\\open\\command, relire, puis la supprimer.");
        journal.Info("Si la relecture ne renvoie pas ce qu'on a écrit, c'est UCPD.");

        return 0;
    }
}
