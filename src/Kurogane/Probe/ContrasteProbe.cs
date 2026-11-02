using System.IO;
using System.Text;
using System.Text.Json;
using Kurogane.Core;
using Microsoft.Win32;

namespace Kurogane.Probe;

/// <summary>
/// Relevé de tout ce qu'un thème de contraste peut modifier — LECTURE SEULE
/// du registre.
///
/// Un thème de contraste est la seule voie documentée qui atteigne
/// l'intérieur de l'Explorateur de Windows 11. Mais il agit large, et on ne
/// sait pas d'avance jusqu'où. Écrire un module avant de l'avoir mesuré
/// reviendrait à promettre une restauration qu'on ne saurait pas tenir.
///
/// D'où la méthode : on relève des clés **entières**, pas une liste de
/// valeurs choisies d'avance. Une valeur qu'on n'aurait pas pensé à
/// surveiller est exactement celle qui manquerait au retour arrière.
///
/// Usage : un relevé avant d'activer un thème de contraste, un relevé après,
/// puis la comparaison des deux. Le diff est la spécification du module.
/// </summary>
internal static class ContrasteProbe
{
    private sealed record Cle(string Chemin, bool Recursif = false);

    /// <summary>
    /// Clés relevées en entier. HKCU seulement : un thème de contraste
    /// s'applique à l'utilisateur courant.
    ///
    /// `Explorer\CLSID` est parcourue récursivement, et ce n'est pas un
    /// détail : un fichier .theme y réécrit les icônes du bureau — Ce PC, le
    /// Réseau, les fichiers de l'utilisateur, la Corbeille — exactement là où
    /// notre module d'icônes écrit. Une première version de cette sonde ne la
    /// relevait pas, et le diff concluait donc à tort qu'un thème de
    /// contraste laissait nos icônes tranquilles.
    /// </summary>
    private static readonly Cle[] Cles =
    {
        new(@"Control Panel\Colors"),
        new(@"Control Panel\Desktop"),
        new(@"Control Panel\Cursors"),
        new(@"Control Panel\Accessibility\HighContrast"),
        new(@"Software\Microsoft\Windows\CurrentVersion\Themes"),
        new(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"),
        new(@"Software\Microsoft\Windows\DWM"),
        new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"),
        new(@"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID", Recursif: true),
    };

    /// <summary>
    /// Garde-fou de profondeur : une récursion sans limite sur une clé mal
    /// choisie relèverait des milliers de valeurs et rendrait le diff
    /// illisible.
    /// </summary>
    private const int ProfondeurMax = 4;

    /// <summary>Dossiers où Windows range ses fichiers .theme.</summary>
    private static readonly string[] DossiersThemes =
    {
        @"C:\Windows\Resources\Ease of Access Themes",
        @"C:\Windows\Resources\Themes",
    };

    private sealed record Valeur(string Type, string Donnee);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static int Executer(Journal journal, string? sortie, string? comparer)
    {
        journal.Step("Relevé");

        var etat = Relever(journal);
        journal.Ok($"{etat.Count} valeur(s) relevée(s) sur {Cles.Length} clé(s)");

        journal.Blank();
        Themes(journal);

        if (comparer is not null)
        {
            journal.Blank();
            return Comparer(journal, comparer, etat);
        }

        string chemin = sortie ?? Path.Combine(Directory.GetCurrentDirectory(),
            $"contraste-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        File.WriteAllText(chemin, JsonSerializer.Serialize(etat, Json), new UTF8Encoding(false));

        journal.Blank();
        journal.Ok($"relevé écrit : {chemin}");
        journal.Blank();
        journal.Info("Prochaine étape : active le thème de contraste, puis relance");
        journal.Info($"  kurogane probe-contraste --comparer \"{chemin}\"");
        journal.Info("Le diff dira exactement ce qu'il y a à capturer et à défaire.");

        return 0;
    }

    private static SortedDictionary<string, Valeur> Relever(Journal journal)
    {
        var etat = new SortedDictionary<string, Valeur>(StringComparer.OrdinalIgnoreCase);

        foreach (var cible in Cles)
        {
            using var cle = Registry.CurrentUser.OpenSubKey(cible.Chemin);

            if (cle is null)
            {
                // Une clé absente est une information, pas une panne : elle
                // se restaure par une suppression, pas par une recréation.
                journal.Warn($"absente : HKCU\\{cible.Chemin}");
                continue;
            }

            Parcourir(cle, cible.Chemin, cible.Recursif, 0, etat);
        }

        return etat;
    }

    private static void Parcourir(RegistryKey cle, string chemin, bool recursif,
        int profondeur, SortedDictionary<string, Valeur> etat)
    {
        foreach (var nom in cle.GetValueNames())
        {
            object? v = cle.GetValue(nom, null,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is null) continue;

            // Le nom vide est la valeur « par défaut » de la clé — c'est
            // justement elle que portent les DefaultIcon.
            string etiquette = nom.Length == 0 ? "(défaut)" : nom;

            etat[$@"HKCU\{chemin}\{etiquette}"] =
                new Valeur(cle.GetValueKind(nom).ToString(), Formater(v));
        }

        if (!recursif || profondeur >= ProfondeurMax) return;

        foreach (var sous in cle.GetSubKeyNames())
        {
            using var enfant = cle.OpenSubKey(sous);
            if (enfant is null) continue;

            Parcourir(enfant, $@"{chemin}\{sous}", true, profondeur + 1, etat);
        }
    }

    private static string Formater(object v) => v switch
    {
        byte[] o   => Convert.ToHexString(o),
        string[] t => string.Join(" | ", t),
        _          => v.ToString() ?? "",
    };

    private static void Themes(Journal journal)
    {
        journal.Step("Fichiers .theme disponibles");

        foreach (var dossier in DossiersThemes)
        {
            if (!Directory.Exists(dossier))
            {
                journal.Warn($"absent : {dossier}");
                continue;
            }

            var fichiers = Directory.GetFiles(dossier, "*.theme");
            journal.Info($"{dossier} — {fichiers.Length} fichier(s)");

            foreach (var f in fichiers)
                journal.Info($"    {Path.GetFileName(f)}");
        }
    }

    private static int Comparer(Journal journal,
        string chemin, SortedDictionary<string, Valeur> apres)
    {
        if (!File.Exists(chemin))
        {
            journal.Error($"relevé introuvable : {chemin}");
            return 1;
        }

        var avant = JsonSerializer.Deserialize<SortedDictionary<string, Valeur>>(
            File.ReadAllText(chemin), Json);

        if (avant is null)
        {
            journal.Error("relevé illisible.");
            return 1;
        }

        journal.Step("Comparaison");
        journal.Info($"avant : {chemin}");
        journal.Blank();

        var tous = new SortedSet<string>(avant.Keys, StringComparer.OrdinalIgnoreCase);
        tous.UnionWith(apres.Keys);

        int modifiees = 0, ajoutees = 0, supprimees = 0;

        foreach (var k in tous)
        {
            bool a = avant.TryGetValue(k, out var va);
            bool b = apres.TryGetValue(k, out var vb);

            if (a && b)
            {
                if (va!.Donnee == vb!.Donnee && va.Type == vb.Type) continue;

                modifiees++;
                journal.Info($"~ {k}");
                journal.Info($"    {va.Donnee}");
                journal.Info($" -> {vb.Donnee}");
            }
            else if (b)
            {
                // Créée par le thème : se défait par une suppression.
                ajoutees++;
                journal.Info($"+ {k}");
                journal.Info($"    {vb!.Donnee}   ({vb.Type})");
            }
            else
            {
                // Supprimée par le thème : se défait par une réécriture.
                supprimees++;
                journal.Info($"- {k}");
                journal.Info($"    {va!.Donnee}   ({va.Type})");
            }
        }

        journal.Blank();
        journal.Ok($"{modifiees} modifiée(s), {ajoutees} créée(s), {supprimees} supprimée(s)");

        if (modifiees + ajoutees + supprimees == 0)
            journal.Warn("aucun écart : le thème n'a pas été appliqué, "
                       + "ou il n'écrit pas dans ces clés.");

        journal.Blank();
        journal.Info("« + » se défait par une suppression, « - » par une réécriture.");
        journal.Info("C'est la distinction que le socle de restauration doit respecter.");

        return 0;
    }
}
