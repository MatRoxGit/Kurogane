using System.IO;
using Kurogane.Core;
using Kurogane.Modules;
using Kurogane.Probe;
using Kurogane.Theme;

namespace Kurogane;

internal static class Program
{
    private const string Version = "0.11.4";

    private static readonly IThemeModule[] Modules =
    {
        new DarkModeModule(),
        new AccentModule(),
        new WallpaperModule(),
        new DesktopIconsModule(),
        new SpecialFoldersModule(),
        new ShellIconsModule(),
        new TaskbarModule(),
    };

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Avant toute lecture de géométrie : sans cette déclaration, Windows
        // virtualise les coordonnées sur un écran mis à l'échelle et les
        // régions de fenêtre sont posées de travers.
        Kurogane.Native.NativeMethods.DeclarerSensibleAuDpi();

        if (args.Length == 0 || IsHelp(args[0])) { PrintUsage(); return 0; }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "apply"    => CmdApply(args),
                "restore"  => CmdRestore(args),
                "list"     => CmdList(),
                "refresh-icons" => CmdRefreshIcons(args),
                "probe-icons" => CmdProbe(args),
                "probe-ownership" => CmdProbeOwnership(args),
                "probe-mun" => CmdProbeMun(args),
                "probe-taskbar" => CmdProbeTaskbar(),
                "probe-taskbar-fenetres" => CmdProbeTaskbarFenetres(),
                "taskbar-style" => CmdTaskbarStyle(args),
                "resident" => CmdResident(args),
                "barre" => CmdBarre(args),
                "probe-mun-ecriture" => CmdProbeMunEcriture(args),
                "substituer-mun" => CmdSubstituerMun(args),
                "restaurer-mun" => CmdRestaurerMun(args),
                "etat-mun" => CmdEtatMun(),
                _          => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"ERREUR : {ex.Message}");
            return 1;
        }
    }

    // ---------------------------------------------------------------- apply

    private static int CmdApply(string[] args)
    {
        string? themePath = Flag(args, "--theme");
        bool commit = Has(args, "--commit");

        if (themePath is null)
        {
            Console.Error.WriteLine("Usage : kurogane apply --theme <fichier.json> [--commit]");
            return 2;
        }

        using var journal = new Journal(commit ? "apply" : "simulation");

        var theme = ThemeDefinition.Load(themePath);
        journal.Step($"Thème « {theme.Name} » v{theme.Version}");
        journal.Info($"source : {Path.GetFullPath(themePath)}");
        journal.Blank();

        // 1. Planification — aucune écriture.
        var planned = new List<PlanItem>();
        foreach (var m in Modules)
        {
            if (!m.AppliesTo(theme))
            {
                journal.Info($"module {m.Name} : non concerné par ce thème");
                continue;
            }

            // Un module de niveau 2 écrit dans HKLM. Sans élévation il
            // échouerait au milieu de l'application ; mieux vaut l'écarter
            // dès la planification et le dire.
            if (m.Level >= 2 && !Elevation.CanWriteHklm())
            {
                journal.Warn($"module {m.Name} : ignoré, droits administrateur requis");
                continue;
            }

            foreach (var c in m.Plan(theme))
                planned.Add(new PlanItem(m.Name, m.Level, c));
        }

        if (planned.Count == 0)
        {
            journal.Warn("Aucune modification à appliquer.");
            return 0;
        }

        return Execute(theme.Name, planned, commit, journal, j =>
        {
            foreach (var m in Modules.Where(m => m.AppliesTo(theme)))
            {
                try { m.PostApply(theme, j); }
                catch (Exception ex) { j.Warn($"{m.Name} : {ex.Message}"); }
            }
        });
    }

    // -------------------------------------------------------- refresh-icons

    private static int CmdRefreshIcons(string[] args)
    {
        bool complet = Has(args, "--purge");

        using var journal = new Journal("refresh-icons");

        journal.Step("Rafraîchissement du cache d'icônes");
        journal.Info("Les icônes sont référencées par chemin : remplacer un .ico sans");
        journal.Info("changer son nom ne modifie aucune valeur, et Windows continue");
        journal.Info("d'afficher l'ancienne image depuis son cache.");
        journal.Blank();

        if (complet)
        {
            journal.Warn("Purge complète : toutes les fenêtres de l'Explorateur vont se fermer.");
            journal.Blank();
            IconCache.PurgerEtRedemarrer(journal);
        }
        else
        {
            IconCache.Rafraichir(journal);
            journal.Blank();
            journal.Info("Si l'ancienne icône persiste, la purge complète est plus sûre :");
            journal.Info("  kurogane refresh-icons --purge");
        }

        journal.Blank();
        journal.Info($"Journal : {journal.Path}");
        return 0;
    }

    // ---------------------------------------------------------- probe-icons

    private static int CmdProbe(string[] args)
    {
        bool commit = Has(args, "--commit");

        using var journal = new Journal(commit ? "probe-icons" : "probe-simulation");

        journal.Step("Sonde de substitution d'icônes");

        string icon = IconProbe.EnsureWitnessIcon();
        journal.Info($"icône témoin : {icon}");
        journal.Info("Magenta barré de blanc : aucune icône de Windows ne lui ressemble,");
        journal.Info("donc aucun doute possible sur ce qui a été substitué.");

        bool hklm = IconProbe.CanWriteHklm();
        if (!hklm)
        {
            journal.Blank();
            journal.Warn("Pas de droits administrateur : les cibles de niveau 2 sont ignorées");
            journal.Warn("(dossiers, lecteurs, flèche de raccourci).");
            journal.Warn("Relancer dans un terminal élevé pour les inclure.");
        }

        var planned = IconProbe.Targets(icon, hklm)
                               .Select(t => new PlanItem(t.Group, t.Level, t.Change))
                               .ToList();

        if (planned.Count == 0)
        {
            journal.Warn("Aucune cible candidate.");
            return 0;
        }

        return Execute("Sonde d'icônes", planned, commit, journal, j =>
        {
            Native.NativeMethods.RefreshShellIcons();
            Native.NativeMethods.BroadcastThemeChange();
            j.Ok("Cache d'icônes du shell invalidé");
            j.Blank();
            j.Info("Redémarre explorer.exe, puis regarde : Bureau, volet de navigation");
            j.Info("de l'Explorateur, Ce PC, Corbeille (vide et pleine), un fichier .txt,");
            j.Info("et la flèche sur un raccourci.");
            j.Info("Tout ce qui N'EST PAS devenu magenta n'est pas substituable ici.");
        });
    }

    // ------------------------------------------------- probe-ownership (N3)

    private const string DrapeauPossession = "--autoriser-prise-de-possession";

    private static int CmdProbeOwnership(string[] args)
    {
        bool commit   = Has(args, "--commit");
        bool autorise = Has(args, DrapeauPossession);

        using var journal = new Journal(commit ? "probe-ownership" : "probe-ownership-simulation");

        journal.Step("SONDE DE NIVEAU 3 — PRISE DE POSSESSION DE CLÉS SYSTÈME");
        journal.Blank();
        journal.Warn("Cette opération modifie la SÉCURITÉ du système, pas son apparence.");
        journal.Warn("Elle attribue au groupe Administrateurs la propriété de clés");
        journal.Warn("détenues par SYSTEM et protégées en écriture.");
        journal.Blank();
        journal.Warn("OUTIL DE DIAGNOSTIC. L'enquête du 31/10/2026 a conclu que cette");
        journal.Warn("voie ne change RIEN aux icônes de l'Accès rapide. Aucun module de");
        journal.Warn("thème ne l'utilise, et elle n'a pas vocation à être livrée.");
        journal.Blank();
        journal.Info("Le descripteur de sécurité d'origine est capturé en entier et");
        journal.Info("restituable par restore. Mais une mise à jour cumulative de Windows");
        journal.Info("peut réécrire ces clés et réinitialiser leurs permissions.");
        journal.Blank();
        journal.Info("À n'exécuter que sur une machine de test disposant d'un point de");
        journal.Info("contrôle.");
        journal.Blank();

        if (!autorise)
        {
            journal.Error($"Autorisation absente. Ajouter {DrapeauPossession} pour continuer.");
            journal.Info($"Journal : {journal.Path}");
            return 2;
        }

        if (!Elevation.CanWriteHklm())
        {
            journal.Error("Session non élevée : terminal administrateur requis.");
            journal.Info($"Journal : {journal.Path}");
            return 2;
        }

        // Les privilèges sont vérifiés AVANT toute écriture : sans de quoi
        // rendre la propriété, on ne la prend pas.
        try { Privileges.EnablePourPriseDePossession(); }
        catch (Exception ex)
        {
            journal.Error(ex.Message);
            journal.Info($"Journal : {journal.Path}");
            return 1;
        }
        journal.Ok("Privilèges de prise et de restitution actifs");

        string icon = IconProbe.EnsureWitnessIcon();
        journal.Info($"icône témoin : {icon}");

        var planned = OwnershipProbe.Targets(icon)
                                    .Select(t => new PlanItem(t.Group, t.Level, t.Change))
                                    .ToList();

        if (planned.Count == 0)
        {
            journal.Warn("Aucune clé candidate présente sur ce système.");
            return 0;
        }

        return Execute("Sonde niveau 3", planned, commit, journal, j =>
        {
            Native.NativeMethods.RefreshShellIcons();
            Native.NativeMethods.BroadcastThemeChange();
            j.Ok("Cache d'icônes du shell invalidé");
            j.Blank();
            j.Info("Redémarre explorer.exe et regarde l'Accueil et le volet épinglé.");
            j.Info("Le groupe qui aura agi — classes ou folderdesc — désigne la");
            j.Info("famille de clés à retenir. L'autre sera abandonnée.");
            j.Blank();
            j.Warn("Rendre la propriété : kurogane restore --last --commit");
        });
    }

    // ------------------------------------------------------------ probe-mun

    /// <summary>
    /// Catalogue les icônes des binaires du shell. Lecture seule de bout en
    /// bout : pas de --commit, pas de point de restauration, rien à défaire.
    /// </summary>
    private static int CmdProbeMun(string[] args)
    {
        using var journal = new Journal("probe-mun");
        return MunProbe.Executer(journal, Flag(args, "--binaire"));
    }

    // --------------------------------------------------------- probe-taskbar

    private static int CmdProbeTaskbar()
    {
        using var journal = new Journal("probe-taskbar");
        return TaskbarProbe.Executer(journal);
    }

    private static int CmdProbeTaskbarFenetres()
    {
        using var journal = new Journal("probe-taskbar-fenetres");
        return TaskbarWindowProbe.Executer(journal);
    }

    /// <summary>
    /// Restylage à chaud. Pas de --commit : rien n'est écrit, et l'effet
    /// disparaît de lui-même au redémarrage d'explorer.
    /// </summary>
    private static int CmdTaskbarStyle(string[] args)
    {
        using var journal = new Journal("taskbar-style");

        if (Has(args, "--reinitialiser")) return TaskbarStyle.Reinitialiser(journal);

        return TaskbarStyle.Appliquer(journal, Reglage(args, journal));
    }

    /// <summary>
    /// Compagnon résident. Occupe le terminal jusqu'à Ctrl+C, et retire le
    /// style en partant.
    /// </summary>
    private static int CmdResident(string[] args)
    {
        using var journal = new Journal("resident");
        return TaskbarResident.Executer(journal, Reglage(args, journal));
    }

    /// <summary>
    /// Barre des tâches Kurogane. Masque celle de Windows le temps qu'elle
    /// tourne, et la rend en partant. Rien n'est écrit.
    /// </summary>
    private static int CmdBarre(string[] args)
    {
        string? chemin = Flag(args, "--theme");

        if (chemin is null)
        {
            Console.Error.WriteLine("Usage : kurogane barre --theme <fichier.json>");
            return 2;
        }

        using var journal = new Journal("barre");
        return Kurogane.Barre.BarreApp.Executer(journal, ThemeDefinition.Load(chemin));
    }

    /// <summary>
    /// Réglage de restylage, lu dans le thème puis surchargé par les options
    /// de la ligne de commande. Partagé par taskbar-style et resident, pour
    /// que les deux se comportent exactement pareil.
    /// </summary>
    private static TaskbarStyle.Reglage Reglage(string[] args, Journal journal)
    {
        TaskbarStyleSection? section = null;

        if (Flag(args, "--theme") is { } chemin)
        {
            var theme = ThemeDefinition.Load(chemin);
            section = theme.TaskbarStyle;

            // Annoncer le thème chargé, et surtout son absence de section :
            // un binaire à jour lisant un thème périmé est l'erreur la plus
            // coûteuse du projet, parce qu'elle ne ressemble pas à une erreur.
            journal.Info($"thème « {theme.Name} » v{theme.Version}");

            if (section is null)
                journal.Warn("ce thème ne déclare pas de section « taskbarStyle » : "
                           + "les valeurs par défaut s'appliquent");

            journal.Blank();
        }

        int Nombre(string nom, int? duTheme, int defaut)
        {
            string? brut = Flag(args, nom);
            if (brut is not null && int.TryParse(brut, out int n)) return n;
            return duTheme ?? defaut;
        }

        string? teinte = Flag(args, "--teinte") ?? section?.Teinte;

        return new TaskbarStyle.Reglage(
            MargeLaterale: Nombre("--marge",       section?.Marge,      12),
            MargeBasse:    Nombre("--marge-basse", section?.MargeBasse,  6),
            Rayon:         Nombre("--rayon",       section?.Rayon,      10),
            Teinte:        teinte is null ? null : ColorUtil.Parse(teinte),
            Opacite:       (byte)Math.Clamp(Nombre("--opacite", section?.Opacite, 200), 0, 255),
            Acrylique:     !Has(args, "--sans-acrylique") && (section?.Acrylique ?? true));
    }

    // --------------------------------------------- probe-mun-ecriture (A)

    /// <summary>
    /// Valide le graveur de ressources sur une COPIE du binaire système.
    /// Pas de --commit : cette commande n'écrit que dans notre dossier, et
    /// Windows continue d'utiliser son propre fichier.
    /// </summary>
    private static int CmdProbeMunEcriture(string[] args)
    {
        string? brut = Flag(args, "--ressource");

        if (brut is not null && !int.TryParse(brut, out _))
        {
            Console.Error.WriteLine($"--ressource attend un nombre, reçu « {brut} ».");
            return 2;
        }

        int ressource = brut is null ? 112 : int.Parse(brut);

        using var journal = new Journal("probe-mun-ecriture");
        return MunWriteProbe.Executer(journal, ressource);
    }

    // ------------------------------------------------- substitution (B)

    /// <summary>
    /// Les cinq dossiers du profil que le registre ne sait pas atteindre.
    /// Le Bureau (183) n'y figure pas : il passe déjà par HKCU.
    /// </summary>
    private static readonly int[] AccesRapide = { 112, 113, 108, 184, 189 };

    private static int CmdSubstituerMun(string[] args)
    {
        if (!TryRessources(args, out var ressources)) return 2;

        using var journal = new Journal("substituer-mun");
        return MunSubstitution.Appliquer(journal, ressources,
            theme:    Flag(args, "--theme"),
            commit:   Has(args, "--commit"),
            autorise: Has(args, "--autoriser-fichier-systeme"));
    }

    private static int CmdRestaurerMun(string[] args)
    {
        using var journal = new Journal("restaurer-mun");
        return MunSubstitution.Restaurer(journal, Has(args, "--commit"));
    }

    private static int CmdEtatMun()
    {
        using var journal = new Journal("etat-mun");
        return MunSubstitution.Etat(journal);
    }

    private static bool TryRessources(string[] args, out int[] ressources)
    {
        ressources = AccesRapide;

        string? brut = Flag(args, "--ressources");
        if (brut is null) return true;

        var morceaux = brut.Split(',', StringSplitOptions.RemoveEmptyEntries
                                     | StringSplitOptions.TrimEntries);
        var liste = new List<int>(morceaux.Length);

        foreach (var m in morceaux)
        {
            if (!int.TryParse(m, out int n))
            {
                Console.Error.WriteLine($"--ressources : « {m} » n'est pas un nombre.");
                return false;
            }
            liste.Add(n);
        }

        if (liste.Count == 0)
        {
            Console.Error.WriteLine("--ressources : liste vide.");
            return false;
        }

        ressources = liste.ToArray();
        return true;
    }

    // ------------------------------------------------------------- exécution

    private sealed record PlanItem(string Group, int Level, IChange Change);

    /// <summary>
    /// Chemin commun à toute commande qui écrit : capture de l'état d'origine,
    /// écartement des valeurs déjà conformes, point de restauration posé AVANT
    /// la première écriture, application avec retour arrière automatique.
    ///
    /// Aucune commande ne doit réimplémenter cette séquence — c'est elle qui
    /// porte les garanties du projet, et une deuxième copie finirait par
    /// diverger de celle-ci.
    /// </summary>
    private static int Execute(string planName, List<PlanItem> planned, bool commit,
                               Journal journal, Action<Journal>? notify)
    {
        var pending = new List<(PlanItem Item, ChangeRecord Record)>();
        int conformes = 0;

        foreach (var item in planned)
        {
            var rec = item.Change.Capture();
            if (rec.IsNoOp) { conformes++; continue; }
            pending.Add((item, rec));
        }

        journal.Blank();
        journal.Step($"Plan : {pending.Count} modification(s)"
                   + (conformes > 0 ? $", {conformes} valeur(s) déjà conforme(s)" : ""));
        journal.Blank();

        if (pending.Count == 0)
        {
            journal.Ok("Le système est déjà dans l'état visé.");
            journal.Blank();
            journal.Info("Si tu viens de remplacer des fichiers d'icônes, c'est normal :");
            journal.Info("le registre stocke leur chemin, pas leur contenu. Rien n'a changé");
            journal.Info("pour l'outil, mais Windows affiche encore son cache.");
            journal.Info("  kurogane refresh-icons --purge");
            journal.Info($"Journal : {journal.Path}");
            return 0;
        }

        string? currentGroup = null;
        foreach (var (item, rec) in pending)
        {
            if (item.Group != currentGroup)
            {
                currentGroup = item.Group;
                journal.Info($"[{item.Group}] niveau {item.Level}");
            }
            journal.Info($"  - {rec.Description}");
            journal.Info($"    {rec.Render()}");
        }

        var records = pending.Select(p => p.Record).ToList();
        journal.Blank();

        // Contrôle d'écriture AVANT toute modification. L'élévation ne dit pas
        // tout : la permission se joue clé par clé, et certaines appartiennent
        // à TrustedInstaller. Échouer ici coûte zéro écriture ; échouer au
        // milieu du plan coûte un retour arrière.
        var refusees = pending.Where(p => !p.Item.Change.DeferWriteCheck
                                       && !p.Item.Change.CanWrite()).ToList();
        if (refusees.Count > 0)
        {
            journal.Error($"{refusees.Count} cible(s) refusent l'écriture :");
            foreach (var (_, rec) in refusees)
                journal.Error($"  {rec.Target}");
            journal.Blank();
            journal.Info("Ces clés appartiennent généralement à TrustedInstaller :");
            journal.Info("y écrire imposerait d'en prendre possession, ce que ce projet");
            journal.Info("s'interdit. Rien n'a été écrit.");
            journal.Info($"Journal : {journal.Path}");
            return 1;
        }

        if (!commit)
        {
            journal.Step("SIMULATION — rien n'a été écrit.");
            journal.Info("Relancer avec --commit pour appliquer.");
            journal.Blank();
            journal.Info($"Journal : {journal.Path}");
            return 0;
        }

        // Point de restauration AVANT toute écriture.
        var snapshot = Snapshot.Create(planName, records);
        string snapPath = snapshot.Save();
        journal.Step($"Point de restauration : {snapPath}");
        journal.Blank();

        int applied = 0;
        try
        {
            foreach (var (item, _) in pending)
            {
                item.Change.Apply();
                applied++;
            }
            journal.Ok($"{applied} modification(s) appliquée(s)");
        }
        catch (Exception ex)
        {
            // Nommer la cible, pas seulement son rang : un numéro oblige à
            // recompter le plan à la main pour savoir ce qui a échoué.
            var fautive = applied < pending.Count ? pending[applied].Item : null;

            journal.Error($"Échec à la modification {applied + 1} sur {pending.Count}");

            if (fautive is not null)
            {
                journal.Error($"  module      : {fautive.Group} (niveau {fautive.Level})");
                journal.Error($"  cible       : {fautive.Change.Target}");
                journal.Error($"  description : {fautive.Change.Description}");
            }

            journal.Error($"  {ex.GetType().Name} : {ex.Message}");

            if (ex is UnauthorizedAccessException && fautive is not null
                && fautive.Change.Target.StartsWith("HKCU", StringComparison.Ordinal))
            {
                journal.Blank();
                journal.Info("Un refus de droits sur HKCU est inhabituel : la ruche");
                journal.Info("appartient à l'utilisateur. Certaines valeurs sont");
                journal.Info("interceptées au niveau du noyau par le User Choice");
                journal.Info("Protection Driver (ucpd.sys), que Microsoft a introduit");
                journal.Info("en 2024. Aucune élévation ne contourne ce blocage.");
                journal.Info("Retire le réglage concerné du fichier de thème.");
            }
            journal.Step("Retour arrière automatique");

            for (int i = applied - 1; i >= 0; i--)
            {
                try { Changes.Revert(records[i]); }
                catch (Exception rex) { journal.Error($"  {records[i].Target} : {rex.Message}"); }
            }

            journal.Info($"État d'origine rétabli. Point de restauration conservé : {snapPath}");
            journal.Info($"Journal : {journal.Path}");
            return 1;
        }

        if (notify is not null)
        {
            journal.Blank();
            journal.Step("Notification du système");
            notify(journal);
        }

        journal.Blank();
        journal.Step("Terminé.");
        journal.Info("Annuler : kurogane restore --last --commit");
        journal.Info($"Journal : {journal.Path}");
        return 0;
    }

    // -------------------------------------------------------------- restore

    private static int CmdRestore(string[] args)
    {
        bool commit = Has(args, "--commit");
        string? explicitPath = Flag(args, "--snapshot");

        (string Path, Snapshot Snap)? target = explicitPath is not null
            ? (explicitPath, Snapshot.Load(explicitPath))
            : Snapshot.Latest();

        if (target is null)
        {
            Console.Error.WriteLine("Aucun point de restauration disponible.");
            return 2;
        }

        using var journal = new Journal(commit ? "restore" : "restore-simulation");

        var (path, snap) = target.Value;
        journal.Step($"Point de restauration {snap.Id}");
        journal.Info($"thème appliqué   : {snap.ThemeName}");
        journal.Info($"créé le          : {snap.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        journal.Info($"modifications    : {snap.Changes.Count}");
        journal.Info($"fichier          : {path}");
        journal.Blank();

        foreach (var r in snap.Changes)
            journal.Info($"  - {r.Target} -> {(r.Existed ? "valeur d'origine" : "suppression (n'existait pas)")}");

        journal.Blank();

        if (!commit)
        {
            journal.Step("SIMULATION — rien n'a été restauré.");
            journal.Info("Relancer avec --commit pour restaurer.");
            journal.Info($"Journal : {journal.Path}");
            return 0;
        }

        int ok = 0, failed = 0;
        foreach (var r in Enumerable.Reverse(snap.Changes))
        {
            try { Changes.Revert(r); ok++; }
            catch (Exception ex) { failed++; journal.Error($"{r.Target} : {ex.Message}"); }
        }

        Native.NativeMethods.BroadcastThemeChange();
        Native.NativeMethods.RefreshShellIcons();

        journal.Blank();
        journal.Step($"Restauré : {ok} / {snap.Changes.Count}" + (failed > 0 ? $"  ({failed} en échec)" : ""));
        journal.Info($"Journal : {journal.Path}");
        return failed > 0 ? 1 : 0;
    }

    // ----------------------------------------------------------------- list

    private static int CmdList()
    {
        var all = Snapshot.ListAll();
        if (all.Count == 0)
        {
            Console.WriteLine("Aucun point de restauration.");
            Console.WriteLine($"(dossier : {Snapshot.SnapshotDirectory})");
            return 0;
        }

        Console.WriteLine($"{"ID",-18} {"DATE",-20} {"MODIFS",-7} THÈME");
        foreach (var (_, s) in all)
            Console.WriteLine($"{s.Id,-18} {s.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  "
                            + $"{s.Changes.Count,-7} {s.ThemeName}");

        Console.WriteLine();
        Console.WriteLine($"Dossier : {Snapshot.SnapshotDirectory}");
        return 0;
    }

    // ---------------------------------------------------------------- utils

    private static bool Has(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string? Flag(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "help";

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"Commande inconnue : {cmd}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine($"""
            kurogane {Version} — personnalisation réversible de Windows 11

            COMMANDES
              apply --theme <fichier.json> [--commit]
                  Simule l'application du thème. Sans --commit, rien n'est écrit.

              restore [--snapshot <fichier.json> | --last] [--commit]
                  Simule la restauration. Sans --commit, rien n'est écrit.
                  Par défaut, reprend le point de restauration le plus récent.

              list
                  Liste les points de restauration.

              refresh-icons [--purge]
                  Force Windows à relire les fichiers d'icônes. Utile après
                  avoir remplacé un .ico sans changer son nom : l'outil n'a
                  alors rien à modifier, mais le cache du shell est périmé.
                  --purge ferme l'Explorateur et supprime ses bases de cache.

              resident [--theme <fichier.json>] [options de taskbar-style]
                  Maintient le restylage de la barre : le repose à chaque
                  fois qu'elle réapparaît — redémarrage d'explorer,
                  changement de résolution ou de mise à l'échelle.
                  Occupe le terminal jusqu'à Ctrl+C, et retire le style en
                  partant. Rien n'est écrit sur le disque.

              taskbar-style [--theme <fichier.json>] [--marge N]
                            [--marge-basse N] [--rayon N] [--teinte "#RRGGBB"]
                            [--opacite 0-255] [--sans-acrylique]
                            [--reinitialiser]
                  Restyle la barre des tâches à chaud : barre flottante à
                  coins arrondis, teinte acrylique. L'effet ne survit pas à
                  un redémarrage d'explorer — voir « resident ».
                  Sous PowerShell, les guillemets autour de la couleur sont
                  indispensables : le dièse y ouvre un commentaire.

              probe-taskbar
                  Inventaire en lecture seule de la barre des tâches et du
                  menu Démarrer : ce qui existe sur cette version de Windows,
                  ce que ça vaut, et ce qui est absent. Rien n'est écrit.

              probe-mun [--binaire <nom ou chemin>]
                  Catalogue les icônes stockées dans imageres.dll.mun,
                  shell32.dll.mun et ddores.dll.mun. Extrait un .ico par
                  groupe et produit une planche de contact HTML indexée par
                  identifiant de ressource.
                  Lecture seule : aucun fichier système n'est ouvert en
                  écriture, aucune élévation n'est requise, il n'y a rien à
                  annuler.

              probe-mun-ecriture [--ressource <n>]
                  Étape A de la substitution de ressources. Copie
                  imageres.dll.mun dans notre dossier, y remplace un groupe
                  d'icônes par un témoin, puis relit la copie pour vérifier
                  que le groupe visé porte le témoin et que toutes les autres
                  ressources sont intactes, octet pour octet.
                  Par défaut, la ressource 112 (Documents).
                  Le fichier système n'est ouvert qu'en lecture, pour être
                  copié. La copie n'est pas installée.

              substituer-mun [--theme <fichier.json>] [--ressources 112,113,…]
                             [--commit] [--autoriser-fichier-systeme]
                  Remplace des groupes d'icônes dans imageres.dll.mun, le
                  fichier système où le shell puise ses icônes. Par défaut,
                  les cinq dossiers du profil que le registre n'atteint pas :
                  Documents, Images, Musique, Téléchargements, Vidéos.
                  Avec --theme, les images viennent des .ico du thème ; sans,
                  un témoin magenta est gravé — c'est le mode diagnostic.
                  Le candidat est construit en mémoire et subit le contrôle
                  d'intégrité complet AVANT que le système soit touché.
                  Droits administrateur, double autorisation, redémarrage
                  nécessaire. Aucun module de thème ne l'appelle.

              restaurer-mun [--commit]
                  Remet le fichier d'origine. Refuse si Windows a remplacé le
                  fichier depuis : réinstaller un binaire périmé annulerait
                  un correctif de sécurité.

              etat-mun
                  Indique si une substitution est en place, et si le fichier
                  installé est toujours celui de Kurogane.

              probe-icons [--commit]
                  Écrit une icône témoin dans toutes les cibles de
                  substitution candidates, pour découvrir lesquelles cette
                  version de Windows accepte réellement. Un terminal
                  administrateur ajoute les cibles de niveau 2.
                  S'annule comme le reste : restore --last --commit.

            PRINCIPE
              La simulation est le mode par défaut : --commit est toujours
              nécessaire pour écrire. Chaque application enregistre l'état
              d'origine sur disque AVANT la première écriture, et un échec en
              cours de route déclenche un retour arrière automatique.

            EXEMPLE
              kurogane apply --theme themes\kurogane-obsidian.json
              kurogane apply --theme themes\kurogane-obsidian.json --commit
              kurogane probe-icons --commit
              kurogane restore --last --commit
            """);
    }
}
