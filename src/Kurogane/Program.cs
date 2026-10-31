using Kurogane.Core;
using Kurogane.Modules;
using Kurogane.Theme;

namespace Kurogane;

internal static class Program
{
    private const string Version = "0.1.0";

    private static readonly IThemeModule[] Modules =
    {
        new DarkModeModule(),
        new AccentModule(),
        new WallpaperModule(),
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
        var planned = new List<(IThemeModule Module, RegistryChange Change)>();
        foreach (var m in Modules)
        {
            if (!m.AppliesTo(theme))
            {
                journal.Info($"module {m.Name} : non concerné par ce thème");
                continue;
            }
            foreach (var c in m.Plan(theme))
                planned.Add((m, c));
        }

        if (planned.Count == 0)
        {
            journal.Warn("Aucune modification à appliquer.");
            return 0;
        }

        // 2. Capture de l'état d'origine — toujours, même en simulation.
        journal.Blank();
        journal.Step($"Plan : {planned.Count} modification(s)");
        journal.Blank();

        var records = new List<ChangeRecord>(planned.Count);
        string? currentModule = null;

        foreach (var (module, change) in planned)
        {
            if (module.Name != currentModule)
            {
                currentModule = module.Name;
                journal.Info($"[{module.Name}] niveau {module.Level}");
            }

            var rec = change.Capture();
            records.Add(rec);
            journal.Info($"  - {rec.Description}");
            journal.Info($"    {rec.Render()}");
        }

        journal.Blank();

        if (!commit)
        {
            journal.Step("SIMULATION — rien n'a été écrit.");
            journal.Info("Relancer avec --commit pour appliquer.");
            journal.Blank();
            journal.Info($"Journal : {journal.Path}");
            return 0;
        }

        // 3. Point de restauration AVANT toute écriture.
        var snapshot = Snapshot.Create(theme.Name, records);
        string snapPath = snapshot.Save();
        journal.Step($"Point de restauration : {snapPath}");
        journal.Blank();

        // 4. Application, avec retour arrière automatique en cas d'échec.
        int applied = 0;
        try
        {
            foreach (var (_, change) in planned)
            {
                change.Apply();
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
                try { RegistryChange.Revert(records[i]); }
                catch (Exception rex) { journal.Error($"  {records[i].Target} : {rex.Message}"); }
            }

            journal.Info($"État d'origine rétabli. Point de restauration conservé : {snapPath}");
            journal.Info($"Journal : {journal.Path}");
            return 1;
        }

        // 5. Notification du shell.
        journal.Blank();
        journal.Step("Notification du système");
        foreach (var m in Modules.Where(m => m.AppliesTo(theme)))
        {
            try { m.PostApply(theme, journal); }
            catch (Exception ex) { journal.Warn($"{m.Name} : {ex.Message}"); }
        }

        journal.Blank();
        journal.Step("Terminé.");
        journal.Info($"Annuler : kurogane restore --last");
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
            try { RegistryChange.Revert(r); ok++; }
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

            PRINCIPE
              La simulation est le mode par défaut : --commit est toujours
              nécessaire pour écrire. Chaque application enregistre l'état
              d'origine sur disque AVANT la première écriture, et un échec en
              cours de route déclenche un retour arrière automatique.

            EXEMPLE
              kurogane apply --theme themes\kurogane-obsidian.json
              kurogane apply --theme themes\kurogane-obsidian.json --commit
              kurogane restore --last --commit
            """);
    }
}
