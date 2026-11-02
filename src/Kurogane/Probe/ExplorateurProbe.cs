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

    /// <summary>
    /// Chemin de la surcharge utilisateur du gestionnaire de dossiers, par
    /// niveaux : il faut savoir lesquels on crée pour ne défaire que ceux-là.
    /// </summary>
    private static readonly string[] Niveaux =
    {
        @"Software\Classes",
        @"Software\Classes\Directory",
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Directory\shell\open",
        @"Software\Classes\Directory\shell\open\command",
    };

    /// <summary>
    /// Ce que l'essai écrit. Ce n'est pas un marqueur fantaisiste : c'est le
    /// comportement actuel de Windows, écrit explicitement. Si le nettoyage
    /// échouait, ouvrir un dossier continuerait de fonctionner exactement
    /// comme avant. L'essai ne mesure que si l'écriture tient, pas si elle
    /// change quelque chose.
    /// </summary>
    private const string Temoin = "\"C:\\Windows\\Explorer.exe\" \"%1\"";

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
        journal.Info("Essai non destructif : kurogane probe-explorateur --essai");

        return 0;
    }

    /// <summary>
    /// Écrit une surcharge utilisateur du gestionnaire de dossiers, la relit,
    /// puis la défait. C'est le seul moyen de savoir si UCPD intercepte cette
    /// clé-là : l'état du pilote ne le dit pas, et la documentation non plus.
    ///
    /// Trois précautions. La valeur écrite reproduit le comportement actuel
    /// de Windows, donc un nettoyage manqué ne casserait rien. Seuls les
    /// niveaux de clé réellement créés sont supprimés, du plus profond au
    /// plus haut. Et la simulation reste le défaut.
    /// </summary>
    internal static int Essai(Journal journal, bool commit)
    {
        string chemin = Niveaux[^1];

        journal.Step("Essai d'écriture");
        journal.Info($"cible  : HKCU\\{chemin}");
        journal.Info($"valeur : {Temoin}");
        journal.Blank();
        journal.Info("Cette valeur reproduit le comportement actuel de Windows.");
        journal.Info("Même si le nettoyage échouait, ouvrir un dossier marcherait.");
        journal.Blank();

        if (!commit)
        {
            journal.Step("Simulation");
            journal.Info("Rien n'a été écrit. Ajouter --commit pour mener l'essai.");
            return 0;
        }

        // Quels niveaux existent deja : on ne defera que ce qu'on aura cree.
        var aCreer = new List<string>();
        foreach (var n in Niveaux)
        {
            using var k = Registry.CurrentUser.OpenSubKey(n);
            if (k is null) aCreer.Add(n);
        }

        journal.Info($"{aCreer.Count} niveau(x) de clé à créer, "
                   + $"{Niveaux.Length - aCreer.Count} déjà présent(s)");
        journal.Blank();

        string? relu;

        try
        {
            using (var cle = Registry.CurrentUser.CreateSubKey(chemin, writable: true))
            {
                if (cle is null)
                {
                    journal.Error("Création de la clé refusée.");
                    return 1;
                }

                cle.SetValue(null, Temoin, RegistryValueKind.String);
            }

            // Relecture par une ouverture neuve : lire le handle qu'on vient
            // d'ecrire renverrait notre propre valeur en cache, pas celle que
            // le systeme a reellement conservee.
            using var verif = Registry.CurrentUser.OpenSubKey(chemin);
            relu = verif?.GetValue(null) as string;
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
            journal.Blank();
            journal.Warn("Écriture refusée — signature d'une interception UCPD.");
            Nettoyer(journal, aCreer);
            return 1;
        }

        journal.Step("Relecture");

        bool tenu = string.Equals(relu, Temoin, StringComparison.Ordinal);

        journal.Info($"attendu : {Temoin}");
        journal.Info($"relu    : {relu ?? "(rien)"}");
        journal.Blank();

        if (tenu) journal.Ok("L'écriture tient. UCPD n'intercepte pas cette clé.");
        else      journal.Warn("L'écriture n'a pas tenu — interception probable.");

        Nettoyer(journal, aCreer);

        journal.Blank();

        if (tenu)
        {
            journal.Info("Voie du remplacement : ouverte sur la surcharge utilisateur.");
            journal.Info("Reste à vérifier que le shell la respecte réellement à");
            journal.Info("l'ouverture d'un dossier — une clé acceptée n'est pas une");
            journal.Info("clé honorée. C'est la même leçon que les attributs DWM.");
        }
        else
        {
            journal.Info("L'explorateur Kurogane restera une application lancée à la");
            journal.Info("main. C'est une limite de Windows, pas du projet.");
        }

        return 0;
    }

    internal static string CheminPreuve => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kurogane", "preuve-ouverture.txt");

    /// <summary>
    /// Relais appelé par le shell quand la surcharge est en place. Il note le
    /// chemin reçu, puis ouvre le dossier avec l'Explorateur.
    ///
    /// Ce second geste n'est pas de la politesse : il garantit qu'une
    /// surcharge oubliée en place ne casse rien. Les dossiers continuent de
    /// s'ouvrir, simplement en passant par nous. Même principe que la valeur
    /// témoin de l'essai d'écriture.
    /// </summary>
    internal static int Preuve(string? chemin)
    {
        try
        {
            string f = CheminPreuve;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
            System.IO.File.AppendAllText(f,
                $"{DateTime.Now:O}\t{chemin ?? "(aucun chemin)"}{Environment.NewLine}");
        }
        catch
        {
            // Une preuve qu'on n'arrive pas a ecrire ne doit pas empecher le
            // dossier de s'ouvrir.
        }

        if (!string.IsNullOrWhiteSpace(chemin))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{chemin}\"")
                    { UseShellExecute = true });
            }
            catch { }
        }

        return 0;
    }

    /// <summary>
    /// Essai fonctionnel : la surcharge est-elle réellement honorée par le
    /// shell à l'ouverture d'un dossier ?
    ///
    /// L'essai d'écriture précédent prouve que la clé tient. Il ne prouve pas
    /// que quelqu'un la lit. La différence a déjà coûté une conclusion fausse
    /// sur les attributs DWM, où douze codes « ok » ne garantissaient rien à
    /// l'écran.
    /// </summary>
    internal static int Ouverture(Journal journal, bool commit)
    {
        string exe = Environment.ProcessPath ?? "kurogane.exe";
        string commande = $"\"{exe}\" preuve-ouverture \"%1\"";

        journal.Step("Essai d'ouverture");
        journal.Info($"cible  : HKCU\\{Niveaux[^1]}");
        journal.Info($"valeur : {commande}");
        journal.Blank();
        journal.Info("Le relais note le chemin reçu, puis ouvre le dossier avec");
        journal.Info("l'Explorateur. Une surcharge oubliée en place ne casse donc");
        journal.Info("rien : les dossiers s'ouvrent, en passant par nous.");
        journal.Blank();

        if (!commit)
        {
            journal.Step("Simulation");
            journal.Info("Rien n'a été écrit. Ajouter --commit pour mener l'essai.");
            return 0;
        }

        try { System.IO.File.Delete(CheminPreuve); } catch { }

        var aCreer = new List<string>();
        foreach (var n in Niveaux)
        {
            using var k = Registry.CurrentUser.OpenSubKey(n);
            if (k is null) aCreer.Add(n);
        }

        try
        {
            using var cle = Registry.CurrentUser.CreateSubKey(Niveaux[^1], writable: true);
            if (cle is null)
            {
                journal.Error("Création de la clé refusée.");
                return 1;
            }

            cle.SetValue(null, commande, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
            Nettoyer(journal, aCreer);
            return 1;
        }

        journal.Ok("surcharge posée");
        journal.Blank();
        journal.Step("À toi de jouer");
        journal.Info("Ouvre un dossier par double-clic — depuis le bureau, pas");
        journal.Info("depuis une fenêtre d'Explorateur déjà ouverte.");
        journal.Info("Puis reviens ici et appuie sur Entrée.");

        if (!Console.IsInputRedirected) Console.ReadLine();

        journal.Blank();
        journal.Step("Preuve");

        string? trace = null;
        try
        {
            if (System.IO.File.Exists(CheminPreuve))
                trace = System.IO.File.ReadAllText(CheminPreuve).Trim();
        }
        catch (Exception ex)
        {
            journal.Error($"lecture de la preuve : {ex.Message}");
        }

        bool honoree = !string.IsNullOrWhiteSpace(trace);

        if (honoree)
        {
            journal.Ok("Le shell a appelé notre commande.");
            foreach (var l in trace!.Split('\n')) journal.Info("  " + l.TrimEnd('\r'));
        }
        else
        {
            journal.Warn("Aucune trace : le shell a ignoré la surcharge.");
            journal.Info($"attendu dans : {CheminPreuve}");
        }

        Nettoyer(journal, aCreer);

        journal.Blank();

        if (honoree)
        {
            journal.Info("Voie du remplacement : confirmée de bout en bout.");
            journal.Info("L'explorateur Kurogane pourra devenir le gestionnaire");
            journal.Info("de dossiers par défaut, de façon réversible.");
        }
        else
        {
            journal.Info("La clé est acceptée mais pas lue pour ce verbe. Il faudra");
            journal.Info("chercher ailleurs — ou accepter une application lancée à");
            journal.Info("la main. Ne pas conclure sur un seul essai : refaire le");
            journal.Info("test depuis le bureau et depuis Win+E.");
        }

        return 0;
    }

    /// <summary>
    /// Supprime les niveaux créés, du plus profond au plus haut. Un niveau
    /// préexistant n'est jamais touché : il peut contenir autre chose.
    /// </summary>
    private static void Nettoyer(Journal journal, List<string> crees)
    {
        journal.Step("Nettoyage");

        if (crees.Count == 0)
        {
            journal.Warn("Aucun niveau créé par l'essai — la clé préexistait.");
            journal.Warn("La valeur écrite n'est PAS défaite automatiquement :");
            journal.Warn("supprimer à la main la valeur par défaut de");
            journal.Warn($"HKCU\\{Niveaux[^1]} si elle n'y était pas.");
            return;
        }

        for (int i = crees.Count - 1; i >= 0; i--)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(crees[i], throwOnMissingSubKey: false);
                journal.Ok($"supprimé : HKCU\\{crees[i]}");
            }
            catch (Exception ex)
            {
                journal.Error($"HKCU\\{crees[i]} — {ex.GetType().Name} : {ex.Message}");
            }
        }

        using var reste = Registry.CurrentUser.OpenSubKey(Niveaux[^1]);
        if (reste is null) journal.Ok("état d'origine rétabli");
        else               journal.Error("la clé subsiste — à vérifier à la main");
    }
}
