using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;

namespace Kurogane.Probe;

/// <summary>
/// Étape B — substitution réelle dans un binaire du shell.
///
/// C'est la seule opération du projet qui modifie un fichier de C:\Windows.
/// Elle exige les droits administrateur ET une autorisation explicite en
/// ligne de commande, et elle n'est appelée par aucun module de thème.
///
/// Déroulé :
///   1. le fichier patché est construit EN MÉMOIRE, puis écrit dans un
///      fichier d'essai local ;
///   2. ce fichier d'essai subit le contrôle d'intégrité complet de
///      l'étape A — y compris la relecture par LoadLibraryEx ;
///   3. seulement si tout passe, on touche au système.
///
/// Le fichier système est mappé par le shell : on ne peut pas l'écraser. On
/// renomme donc l'original sur place et on dépose le nouveau à côté. Le
/// renommage fonctionne sur un fichier mappé, l'écrasement non. D'où le
/// redémarrage obligatoire.
///
/// Heureuse conséquence : l'original renommé reste dans SystemResources,
/// c'est la sauvegarde la plus sûre qui soit. Une copie est tout de même
/// conservée sous %LOCALAPPDATA%.
///
/// Garde-fou de restauration. On enregistre la version du fichier d'origine
/// et son empreinte SHA-256. Si, au moment de restaurer, le fichier en place
/// n'est plus celui qu'on a déposé — mise à jour cumulative, SFC —, la
/// restauration est REFUSÉE : réinstaller un binaire de six mois par-dessus
/// un fichier à jour reviendrait à annuler un correctif de sécurité sans
/// s'en apercevoir.
///
/// Limites assumées, non contournables : la signature de catalogue du
/// fichier ne correspond plus, donc SFC peut vouloir le restaurer ; et une
/// mise à jour cumulative le remplacera, faisant disparaître la
/// personnalisation.
/// </summary>
internal static class MunSubstitution
{
    internal sealed record Enregistrement(
        string Horodatage,
        string Cible,
        string Sauvegarde,
        string CopieLocale,
        string VersionOrigine,
        string EmpreinteOrigine,
        string EmpreintePatchee,
        string Sddl,
        string SddlDossier,
        int[] Ressources);

    private static string CheminEnregistrement => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kurogane", "substitution.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // ==================================================================
    //  Application
    // ==================================================================

    /// <summary>
    /// Associe chaque dossier du profil à la ressource qui porte son icône.
    /// Le Bureau n'y figure pas : il passe par le registre, qui l'emporte
    /// sur la ressource.
    /// </summary>
    private static IEnumerable<(int Ressource, string Fichier)> Correspondance(ThemeDefinition t)
    {
        var i = t.Icons;
        if (i is null) yield break;

        if (i.Documents       is { } a) yield return (112, a);
        if (i.Images          is { } b) yield return (113, b);
        if (i.Musique         is { } c) yield return (108, c);
        if (i.Telechargements is { } d) yield return (184, d);
        if (i.Videos          is { } e) yield return (189, e);
    }

    internal static int Appliquer(Journal journal, int[] ressources, string? theme,
                                  bool commit, bool autorise)
    {
        journal.Step("Substitution de ressources dans un fichier système");
        journal.Blank();

        // ---- Source des images : un thème, ou le témoin de diagnostic

        var sources = new Dictionary<int, SourceIco>();

        if (theme is not null)
        {
            ThemeDefinition definition;
            try { definition = ThemeDefinition.Load(theme); }
            catch (Exception ex) { journal.Error(ex.Message); return 1; }

            journal.Step($"Thème « {definition.Name} » v{definition.Version}");

            foreach (var (r, fichier) in Correspondance(definition))
            {
                string chemin = definition.ResolveIcon(fichier);

                if (!File.Exists(chemin))
                {
                    journal.Error($"ressource {r} : {chemin} est introuvable.");
                    return 1;
                }

                try
                {
                    var ico = new SourceIco(chemin);
                    sources[r] = ico;
                    journal.Ok($"ressource {r} ← {fichier} "
                             + $"({string.Join(", ", ico.Tailles)} px)");
                }
                catch (Exception ex)
                {
                    journal.Error($"ressource {r} : {ex.Message}");
                    return 1;
                }
            }

            if (sources.Count == 0)
            {
                journal.Error("Ce thème ne déclare aucune des cinq icônes concernées.");
                return 1;
            }

            var absentes = ressources.Where(x => !sources.ContainsKey(x)).ToArray();
            if (absentes.Length > 0)
                journal.Warn($"non déclarées dans le thème, ignorées : {string.Join(", ", absentes)}");

            ressources = ressources.Where(sources.ContainsKey).ToArray();
            journal.Blank();
        }
        else
        {
            journal.Warn("Aucun thème indiqué : un témoin magenta sera gravé.");
            journal.Info("C'est le mode diagnostic. Pour tes icônes : --theme <fichier.json>");
            journal.Blank();
        }

        journal.Info($"ressources visées : {string.Join(", ", ressources)}");
        journal.Blank();

        // Les trois verrous ne gardent que l'écriture. La simulation lit le
        // fichier système, construit le candidat et le vérifie sans rien
        // modifier : la bloquer n'apporterait aucune sécurité et priverait
        // d'un contrôle gratuit.

        bool dejaEnPlace = File.Exists(CheminEnregistrement);
        bool administrateur = Elevation.CanWriteHklm();

        if (commit)
        {
            if (!autorise)
            {
                journal.Error("Autorisation manquante.");
                journal.Info("Cette commande modifie un fichier de C:\\Windows. Elle exige");
                journal.Info("l'option --autoriser-fichier-systeme, en plus de --commit.");
                return 2;
            }

            if (dejaEnPlace)
            {
                journal.Error("Une substitution est déjà en place.");
                journal.Info("Restaure-la d'abord : kurogane restaurer-mun --commit");
                return 2;
            }

            if (!administrateur)
            {
                journal.Error("Droits administrateur requis.");
                return 2;
            }
        }
        else
        {
            if (dejaEnPlace)
                journal.Warn("Une substitution est déjà en place ; cette simulation "
                           + "part du fichier actuellement installé.");

            if (!administrateur)
                journal.Warn("Terminal non élevé : la simulation fonctionne, "
                           + "l'installation exigera un terminal administrateur.");

            if (!autorise)
                journal.Info("Mode simulation : aucune autorisation n'est requise.");

            journal.Blank();
        }

        string? source = MunPatcher.Trouver("imageres");
        if (source is null) { journal.Error("imageres introuvable."); return 1; }

        var octets = File.ReadAllBytes(source);
        var empreinteOrigine = Empreinte(octets);
        var versionOrigine = FileVersionInfo.GetVersionInfo(source).FileVersion ?? "inconnue";

        journal.Step("Fichier visé");
        journal.Info($"{source}");
        journal.Info($"version {versionOrigine}");
        journal.Info($"empreinte {empreinteOrigine[..16]}…");
        journal.Blank();

        List<PeResources.Entree> entrees;
        try { entrees = PeResources.Lire(octets); }
        catch (Exception ex) { journal.Error(ex.Message); return 1; }

        journal.Step("Construction en mémoire");

        var plans = new List<MunPatcher.Plan>();
        foreach (int r in ressources)
        {
            sources.TryGetValue(r, out var ico);

            var plan = MunPatcher.Preparer(octets, entrees, r,
                           ico is null ? IconProbe.DrawWitness : ico.Pixels,
                           journal, detaille: ico is not null,
                           charge: ico is null ? null : ico.Charge);

            if (plan is null) { journal.Error("Rien n'a été touché."); return 1; }

            journal.Ok($"groupe {r} : {plan.Images.Count} images prêtes "
                     + $"({string.Join(", ", plan.Images.GroupBy(i => i.Forme)
                                                        .Select(g => $"{g.Count()} en {g.Key}"))})");
            if (ico is not null) journal.Blank();
            plans.Add(plan);
        }

        foreach (var p in plans) MunPatcher.Appliquer(octets, p);
        PeResources.CorrigerChecksum(octets);

        string travail = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "travail");
        Directory.CreateDirectory(travail);

        string essai = Path.Combine(travail, Path.GetFileName(source));
        File.WriteAllBytes(essai, octets);

        journal.Ok($"fichier candidat écrit : {essai}");
        journal.Blank();

        // ------------------------------------------------------------
        //  Le contrôle complet AVANT de toucher au système
        // ------------------------------------------------------------

        journal.Step("Contrôle du candidat");

        int ecarts = MunWriteProbe.Controler(journal, source, essai, plans);
        if (ecarts != 0)
        {
            journal.Blank();
            journal.Error("Le candidat ne passe pas le contrôle. Le système n'a pas été touché.");
            return 1;
        }

        journal.Blank();

        if (!commit)
        {
            journal.Step("Simulation");
            journal.Info("Le candidat est valide. Rien n'a été installé.");
            journal.Blank();
            journal.Info("Pour l'installer réellement, dans un terminal administrateur :");
            journal.Info("  kurogane substituer-mun --commit --autoriser-fichier-systeme");
            journal.Info("Un redémarrage sera nécessaire ensuite.");
            return 0;
        }

        // ------------------------------------------------------------
        //  Installation
        // ------------------------------------------------------------

        string horodatage = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string sauvegarde = $"{source}.kurogane-{horodatage}";

        string copieLocale = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "sauvegarde-systeme", Path.GetFileName(source));

        journal.Step("Installation");

        string dossier = Path.GetDirectoryName(source)!;
        string sddl, sddlDossier;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copieLocale)!);
            File.Copy(source, copieLocale, overwrite: true);
            journal.Ok($"copie de sécurité : {copieLocale}");

            sddl        = FileOwnership.CapturerSddl(source);
            sddlDossier = FileOwnership.CapturerSddlDossier(dossier);
            journal.Ok("descripteurs de sécurité capturés : fichier et dossier");

            // Le dossier d'abord : sans droit d'écriture dessus, le
            // renommage échouerait même en étant propriétaire du fichier.
            FileOwnership.PrendreDossier(dossier);
            FileOwnership.Prendre(source);
            journal.Ok("propriété prise sur le dossier et sur le fichier");
        }
        catch (Exception ex)
        {
            journal.Error(ex.Message);
            journal.Warn("Le fichier système n'a pas été modifié.");
            return 1;
        }

        try
        {
            File.Move(source, sauvegarde);
            journal.Ok($"original renommé : {Path.GetFileName(sauvegarde)}");
        }
        catch (Exception ex)
        {
            journal.Error($"Renommage impossible : {ex.Message}");
            TenterRestituer(journal, source, sddl);
            TenterRestituerDossier(journal, dossier, sddlDossier);
            return 1;
        }

        try
        {
            File.WriteAllBytes(source, octets);
            FileOwnership.Restituer(source, sddl);
            FileOwnership.Restituer(sauvegarde, sddl);
            FileOwnership.RestituerDossier(dossier, sddlDossier);
            journal.Ok("fichier patché déposé, descripteurs restitués");
        }
        catch (Exception ex)
        {
            journal.Error($"Dépôt impossible : {ex.Message}");
            journal.Warn("Retour arrière : on remet l'original en place.");

            try
            {
                if (File.Exists(source)) File.Delete(source);
                File.Move(sauvegarde, source);
                TenterRestituer(journal, source, sddl);
                TenterRestituerDossier(journal, dossier, sddlDossier);
                journal.Ok("original remis en place.");
            }
            catch (Exception ex2)
            {
                journal.Error($"ÉCHEC DU RETOUR ARRIÈRE : {ex2.Message}");
                journal.Error($"L'original est ici : {sauvegarde}");
                journal.Error($"Il doit être renommé en {Path.GetFileName(source)}.");
            }

            return 1;
        }

        var enregistrement = new Enregistrement(
            DateTime.Now.ToString("o"), source, sauvegarde, copieLocale,
            versionOrigine, empreinteOrigine, Empreinte(octets),
            sddl, sddlDossier, ressources);

        Directory.CreateDirectory(Path.GetDirectoryName(CheminEnregistrement)!);
        File.WriteAllText(CheminEnregistrement,
            JsonSerializer.Serialize(enregistrement, Json));

        journal.Ok($"enregistrement : {CheminEnregistrement}");
        journal.Blank();

        journal.Step("Redémarrage nécessaire");
        journal.Info("Le shell a l'ancien fichier en mémoire. Les icônes ne changeront");
        journal.Info("qu'après un redémarrage complet de la machine.");
        journal.Blank();
        journal.Info("Pour revenir en arrière :");
        journal.Info("  kurogane restaurer-mun --commit");

        return 0;
    }

    // ==================================================================
    //  Restauration
    // ==================================================================

    internal static int Restaurer(Journal journal, bool commit)
    {
        journal.Step("Restauration du fichier système");

        if (!File.Exists(CheminEnregistrement))
        {
            journal.Warn("Aucune substitution enregistrée.");
            return 0;
        }

        var e = JsonSerializer.Deserialize<Enregistrement>(
                    File.ReadAllText(CheminEnregistrement), Json)
                ?? throw new InvalidDataException("Enregistrement illisible.");

        journal.Info($"posée le {e.Horodatage}");
        journal.Info($"cible : {e.Cible}");
        journal.Info($"ressources : {string.Join(", ", e.Ressources)}");
        journal.Blank();

        if (!Elevation.CanWriteHklm())
        {
            journal.Error("Droits administrateur requis.");
            return 2;
        }

        if (!File.Exists(e.Cible)) { journal.Error("Le fichier cible a disparu."); return 1; }

        // ---- Garde-fou : le fichier en place est-il bien le nôtre ?

        var actuel = File.ReadAllBytes(e.Cible);
        string empreinteActuelle = Empreinte(actuel);
        string versionActuelle = FileVersionInfo.GetVersionInfo(e.Cible).FileVersion ?? "inconnue";

        journal.Step("Contrôle avant restauration");

        if (empreinteActuelle != e.EmpreintePatchee)
        {
            journal.Error("Le fichier en place n'est pas celui que Kurogane a déposé.");
            journal.Info($"version actuelle : {versionActuelle}");
            journal.Info($"version d'origine : {e.VersionOrigine}");
            journal.Blank();
            journal.Info("Windows l'a probablement remplacé — mise à jour cumulative ou SFC.");
            journal.Info("Restaurer par-dessus réinstallerait un binaire périmé et pourrait");
            journal.Info("annuler un correctif de sécurité. La restauration est REFUSÉE.");
            journal.Blank();
            journal.Info("Le système est déjà revenu à un fichier d'origine : il n'y a rien");
            journal.Info("à réparer. Tu peux supprimer la sauvegarde et l'enregistrement :");
            journal.Info($"  {e.Sauvegarde}");
            journal.Info($"  {CheminEnregistrement}");
            return 1;
        }

        journal.Ok("le fichier en place est bien celui déposé par Kurogane");

        if (!File.Exists(e.Sauvegarde))
        {
            journal.Warn($"Sauvegarde absente de SystemResources : {e.Sauvegarde}");

            if (!File.Exists(e.CopieLocale))
            {
                journal.Error("La copie locale est absente elle aussi. Restauration impossible.");
                return 1;
            }

            journal.Info($"la copie locale sera utilisée : {e.CopieLocale}");
        }

        string origine = File.Exists(e.Sauvegarde) ? e.Sauvegarde : e.CopieLocale;

        if (Empreinte(File.ReadAllBytes(origine)) != e.EmpreinteOrigine)
        {
            journal.Error("L'empreinte de la sauvegarde ne correspond pas à l'original.");
            journal.Info("Restauration refusée : on ne réinstalle pas un fichier douteux.");
            return 1;
        }

        journal.Ok("la sauvegarde correspond à l'original, octet pour octet");
        journal.Blank();

        if (!commit)
        {
            journal.Step("Simulation");
            journal.Info("Tous les contrôles passent. Rien n'a été modifié.");
            journal.Info("Pour restaurer réellement : kurogane restaurer-mun --commit");
            return 0;
        }

        // ---- Échange

        journal.Step("Échange");

        string aJeter = $"{e.Cible}.kurogane-aremplacer-{DateTime.Now:yyyyMMdd-HHmmss}";

        string dossier = Path.GetDirectoryName(e.Cible)!;

        try
        {
            FileOwnership.PrendreDossier(dossier);
            FileOwnership.Prendre(e.Cible);

            File.Move(e.Cible, aJeter);

            if (origine == e.Sauvegarde) File.Move(origine, e.Cible);
            else                          File.Copy(origine, e.Cible);

            FileOwnership.Restituer(e.Cible, e.Sddl);
            FileOwnership.RestituerDossier(dossier, e.SddlDossier);
            journal.Ok("original remis en place, descripteurs restitués");
        }
        catch (Exception ex)
        {
            journal.Error($"Échange impossible : {ex.Message}");
            journal.Warn($"Si le fichier cible manque, l'original est ici : {aJeter}");
            return 1;
        }

        if (NativeMethods.SupprimerAuRedemarrage(aJeter))
            journal.Ok("le fichier patché sera supprimé au redémarrage");
        else
            journal.Warn($"à supprimer manuellement après redémarrage : {aJeter}");

        File.Delete(CheminEnregistrement);
        journal.Ok("enregistrement effacé");
        journal.Blank();

        journal.Step("Redémarrage nécessaire");
        journal.Info("Le shell a le fichier patché en mémoire jusqu'au redémarrage.");

        return 0;
    }

    // ==================================================================

    internal static int Etat(Journal journal)
    {
        if (!File.Exists(CheminEnregistrement))
        {
            journal.Info("Aucune substitution de fichier système en place.");
            return 0;
        }

        var e = JsonSerializer.Deserialize<Enregistrement>(
                    File.ReadAllText(CheminEnregistrement), Json);

        if (e is null) { journal.Error("Enregistrement illisible."); return 1; }

        journal.Step("Substitution en place");
        journal.Info($"posée le   : {e.Horodatage}");
        journal.Info($"cible      : {e.Cible}");
        journal.Info($"sauvegarde : {e.Sauvegarde}");
        journal.Info($"version    : {e.VersionOrigine}");
        journal.Info($"ressources : {string.Join(", ", e.Ressources)}");

        if (File.Exists(e.Cible))
        {
            string actuelle = Empreinte(File.ReadAllBytes(e.Cible));
            if (actuelle == e.EmpreintePatchee) journal.Ok("le fichier en place est celui de Kurogane");
            else journal.Warn("le fichier en place a été remplacé par Windows");
        }

        return 0;
    }

    private static void TenterRestituer(Journal journal, string chemin, string sddl)
    {
        try { FileOwnership.Restituer(chemin, sddl); journal.Ok("descripteur du fichier restitué"); }
        catch (Exception ex) { journal.Warn($"descripteur du fichier non restitué : {ex.Message}"); }
    }

    private static void TenterRestituerDossier(Journal journal, string chemin, string sddl)
    {
        try { FileOwnership.RestituerDossier(chemin, sddl); journal.Ok("descripteur du dossier restitué"); }
        catch (Exception ex) { journal.Warn($"descripteur du dossier non restitué : {ex.Message}"); }
    }

    private static string Empreinte(byte[] octets) =>
        Convert.ToHexString(SHA256.HashData(octets));
}
