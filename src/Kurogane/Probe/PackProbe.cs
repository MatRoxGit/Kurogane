using System.IO;
using System.Reflection;
using Kurogane.Core;
using Kurogane.Explorateur;
using Kurogane.Theme;

namespace Kurogane.Probe;

/// <summary>
/// Confronte le pack d'icônes présent sur le disque à ce que le projet
/// référence réellement — LECTURE SEULE.
///
/// Elle existe à cause d'une panne silencieuse. Les corrections du 01/11 ont
/// été exportées sous des noms anglais (`this-pc`, `folder`…) alors que le
/// thème référence les noms français (`ce-pc`, `dossier`…). Pendant deux
/// jours, les icônes affichées étaient les versions non corrigées, et rien
/// ne le signalait : aucun fichier ne manquait, aucune erreur n'était levée.
///
/// Un nom référencé sans fichier se voit tout de suite. Un fichier sans
/// référence ne se voit jamais — c'est pourtant le symptôme du même défaut.
/// </summary>
internal static class PackProbe
{
    internal static int Executer(Journal journal, ThemeDefinition theme)
    {
        string dossier = theme.ResolveIcon("");

        journal.Info($"thème « {theme.Name} » v{theme.Version}");
        journal.Info($"dossier : {dossier}");
        journal.Blank();

        if (!Directory.Exists(dossier))
        {
            journal.Error("dossier d'icônes introuvable.");
            return 1;
        }

        var references = Referencees(theme);

        var presentes = Directory.GetFiles(dossier, "*.ico")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        journal.Step("Références");
        journal.Info($"{references.Count} nom(s) référencé(s), {presentes.Count} fichier(s) présent(s)");
        journal.Blank();

        var manquantes = references.Keys
            .Where(n => !presentes.Contains(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orphelines = presentes
            .Where(n => !references.ContainsKey(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (manquantes.Count > 0)
        {
            journal.Step("Référencées mais absentes");
            journal.Info("Ce que le projet demande et qui n'existe pas.");
            journal.Blank();

            foreach (var n in manquantes)
                journal.Error($"{n}  ← {references[n]}");

            journal.Blank();
        }

        if (orphelines.Count > 0)
        {
            journal.Step("Présentes mais jamais lues");
            journal.Info("Un export sous un nom que rien ne référence n'est pas");
            journal.Info("livré, il est seulement présent. C'est le symptôme");
            journal.Info("qui serait passé inaperçu le 01/11.");
            journal.Blank();

            foreach (var n in orphelines)
                journal.Warn(n);

            journal.Blank();
        }

        journal.Step("Bilan");

        if (manquantes.Count == 0 && orphelines.Count == 0)
        {
            journal.Ok("pack et références alignés");
            return 0;
        }

        journal.Warn($"{manquantes.Count} absente(s), {orphelines.Count} orpheline(s)");
        journal.Info("Une orpheline n'est pas forcément un défaut : une icône");
        journal.Info("peut être produite pour un usage futur. Mais une orpheline");
        journal.Info("dont le nom ressemble à une référencée en est un.");

        return 0;
    }

    /// <summary>
    /// Tous les noms de fichiers que le projet attend, avec leur provenance.
    /// Les icônes du thème sont lues par réflexion : ajouter une propriété à
    /// IconsSection sans penser à cette sonde la laisserait hors contrôle.
    /// </summary>
    private static Dictionary<string, string> Referencees(ThemeDefinition theme)
    {
        var noms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Ajouter(string? fichier, string origine)
        {
            if (string.IsNullOrWhiteSpace(fichier)) return;

            if (noms.TryGetValue(fichier, out var deja)) noms[fichier] = $"{deja}, {origine}";
            else noms[fichier] = origine;
        }

        if (theme.Icons is { } icones)
            foreach (var p in typeof(IconsSection).GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.PropertyType != typeof(string)) continue;
                if (p.Name == nameof(IconsSection.Directory)) continue;

                Ajouter(p.GetValue(icones) as string, $"thème/{p.Name}");
            }

        foreach (var epingle in theme.Barre?.Epingles ?? new List<EpingleBarre>())
            Ajouter(epingle.Icone, "barre/épingle");

        foreach (var nom in TypesFichiers.Noms())
            Ajouter(nom + ".ico", "types de fichiers");

        return noms;
    }
}
