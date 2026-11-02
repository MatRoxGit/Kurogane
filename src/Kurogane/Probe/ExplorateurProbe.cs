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
    private static string[] Niveaux(string classe) => new[]
    {
        @"Software\Classes",
        $@"Software\Classes\{classe}",
        $@"Software\Classes\{classe}\shell",
        $@"Software\Classes\{classe}\shell\open",
        $@"Software\Classes\{classe}\shell\open\command",
    };

    /// <summary>
    /// Les deux classes candidates. « Directory » est la classe d'un dossier
    /// du système de fichiers, « Folder » celle de tout objet-dossier, y
    /// compris ceux de l'espace de noms. Elles ne sont pas lues dans les
    /// mêmes situations, et confondre les deux fait conclure à tort.
    /// </summary>
    private static readonly string[] Classes = { "Directory", "Folder" };

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
        string chemin = Niveaux("Directory")[^1];

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
        foreach (var n in Niveaux("Directory"))
        {
            using var k = Registry.CurrentUser.OpenSubKey(n);
            if (k is null) aCreer.Add(n);
        }

        journal.Info($"{aCreer.Count} niveau(x) de clé à créer, "
                   + $"{5 - aCreer.Count} déjà présent(s)");
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
    /// Essai fonctionnel : la surcharge est-elle réellement lue ?
    ///
    /// L'essai d'écriture prouve que la clé tient. Il ne prouve pas que
    /// quelqu'un la lise — la différence a déjà coûté une conclusion fausse
    /// sur les attributs DWM, où douze codes « ok » ne garantissaient rien à
    /// l'écran.
    ///
    /// Trois situations sont distinguées, parce qu'elles n'empruntent pas le
    /// même chemin dans Windows :
    ///
    ///   1. ShellExecute sur un dossier — testé automatiquement, sans
    ///      intervention humaine, donc sans erreur de manipulation possible.
    ///   2. Le double-clic depuis le bureau — testé à la main.
    ///   3. Win+E — testé à la main.
    ///
    /// Les deux classes « Directory » et « Folder » sont surchargées
    /// ensemble : elles ne sont pas lues dans les mêmes situations, et n'en
    /// surcharger qu'une ferait conclure à tort.
    /// </summary>
    internal static int Ouverture(Journal journal, bool commit)
    {
        string exe = Environment.ProcessPath ?? "kurogane.exe";
        string commande = $"\"{exe}\" preuve-ouverture \"%1\"";

        journal.Step("Essai d'ouverture");
        journal.Info($"valeur : {commande}");
        journal.Info($"classes : {string.Join(", ", Classes)}");
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

        Vider();

        var aCreer = new List<string>();

        foreach (var classe in Classes)
        {
            foreach (var n in Niveaux(classe))
            {
                using var k = Registry.CurrentUser.OpenSubKey(n);
                if (k is null && !aCreer.Contains(n)) aCreer.Add(n);
            }

            try
            {
                using var cle = Registry.CurrentUser.CreateSubKey(
                    Niveaux(classe)[^1], writable: true);

                if (cle is null)
                {
                    journal.Error($"{classe} : création refusée.");
                    continue;
                }

                cle.SetValue(null, commande, RegistryValueKind.String);
                journal.Ok($"{classe} : surcharge posée");
            }
            catch (Exception ex)
            {
                journal.Error($"{classe} : {ex.GetType().Name} — {ex.Message}");
            }
        }

        journal.Blank();

        // ---- 1. ShellExecute, sans intervention humaine
        journal.Step("1. ShellExecute sur un dossier");

        string dossier = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        journal.Info($"cible : {dossier}");

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(dossier) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
        }

        bool viaShellExecute = Attendre(TimeSpan.FromSeconds(4));

        if (viaShellExecute) journal.Ok("le relais a été appelé");
        else                 journal.Warn("aucune trace — la surcharge n'a pas été lue");

        Vider();
        journal.Blank();

        // ---- 2 et 3. Les chemins qui demandent une main humaine
        journal.Step("2. Double-clic depuis le bureau");
        journal.Info("Ouvre un dossier par double-clic depuis le BUREAU — pas");
        journal.Info("depuis une fenêtre d'Explorateur déjà ouverte, qui navigue");
        journal.Info("en interne sans repasser par le verbe « open ».");
        journal.Info("Puis Entrée.");

        if (!Console.IsInputRedirected) Console.ReadLine();

        bool viaDoubleClic = Lire() is not null;
        if (viaDoubleClic) journal.Ok("le relais a été appelé");
        else               journal.Warn("aucune trace");

        Vider();
        journal.Blank();

        journal.Step("3. Win+E");
        journal.Info("Appuie sur Win+E, puis reviens ici et Entrée.");

        if (!Console.IsInputRedirected) Console.ReadLine();

        bool viaWinE = Lire() is not null;
        if (viaWinE) journal.Ok("le relais a été appelé");
        else         journal.Warn("aucune trace");

        Nettoyer(journal, aCreer);

        // ---- Verdict
        journal.Blank();
        journal.Step("Verdict");
        journal.Info($"  ShellExecute        : {(viaShellExecute ? "lue" : "ignorée")}");
        journal.Info($"  double-clic bureau  : {(viaDoubleClic ? "lue" : "ignorée")}");
        journal.Info($"  Win+E               : {(viaWinE ? "lue" : "ignorée")}");
        journal.Blank();

        if (viaDoubleClic)
        {
            journal.Info("Voie du remplacement : confirmée pour l'usage courant.");
        }
        else if (viaShellExecute)
        {
            journal.Info("La surcharge est lue par ShellExecute mais pas par le");
            journal.Info("double-clic : celui-ci est traité en interne par l'espace");
            journal.Info("de noms du shell, ce que laissait déjà penser le verbe");
            journal.Info("par défaut « none » relevé sur Directory\\shell.");
            journal.Info("Remplacer l'Explorateur demanderait alors d'intervenir");
            journal.Info("dans cet espace de noms, pas dans les associations.");
        }
        else
        {
            journal.Info("Aucun des trois chemins ne lit la surcharge. Les");
            journal.Info("associations ne gouvernent pas l'ouverture d'un dossier");
            journal.Info("sur cette version de Windows.");
        }

        return 0;
    }

    private static void Vider()
    {
        try { System.IO.File.Delete(CheminPreuve); } catch { }
    }

    private static string? Lire()
    {
        try
        {
            if (!System.IO.File.Exists(CheminPreuve)) return null;
            var t = System.IO.File.ReadAllText(CheminPreuve).Trim();
            return string.IsNullOrWhiteSpace(t) ? null : t;
        }
        catch { return null; }
    }

    /// <summary>
    /// Attend qu'une trace apparaisse. Le relais est un processus séparé :
    /// lire tout de suite reviendrait à conclure avant qu'il ait démarré.
    /// </summary>
    private static bool Attendre(TimeSpan limite)
    {
        var fin = DateTime.UtcNow + limite;

        while (DateTime.UtcNow < fin)
        {
            if (Lire() is not null) return true;
            System.Threading.Thread.Sleep(150);
        }

        return false;
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
            journal.Warn($"HKCU\\{Niveaux("Directory")[^1]} si elle n'y était pas.");
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

        // Verifier les deux classes : l'essai d'ouverture en surcharge deux,
        // et ne controler que la premiere laisserait l'autre passer.
        bool propre = true;

        foreach (var classe in Classes)
        {
            using var reste = Registry.CurrentUser.OpenSubKey(Niveaux(classe)[^1]);
            if (reste is null) continue;

            propre = false;
            journal.Error($"subsiste : HKCU\\{Niveaux(classe)[^1]} — à vérifier à la main");
        }

        if (propre) journal.Ok("état d'origine rétabli");
    }
}
