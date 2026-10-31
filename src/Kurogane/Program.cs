using Kurogane.Core;
using Kurogane.Modules;
using Kurogane.Probe;
using Kurogane.Theme;

namespace Kurogane;

internal static class Program
{
    private const string Version = "0.3.0";

    private static readonly IThemeModule[] Modules =
    {
        new DarkModeModule(),
        new AccentModule(),
        new WallpaperModule(),
        new DesktopIconsModule(),
        new SpecialFoldersModule(),
        new ShellIconsModule(),
    };

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

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
            journal.Error($"Échec à la modification {applied + 1} : {ex.Message}");
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
