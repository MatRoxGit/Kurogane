using System.IO;
using Kurogane.Core;
using Kurogane.Native;

namespace Kurogane.Probe;

/// <summary>
/// Étape A de la substitution de ressources : valider le graveur SUR UNE
/// COPIE, avant de toucher à un fichier système.
///
/// L'API documentée UpdateResource ne convient pas : elle refuse d'écrire
/// dans les fichiers du modèle MUI, dont les .mun, et répond
/// ERROR_NOT_SUPPORTED. On patche donc le PE sur place, sans déplacer un
/// octet, ce qui impose un budget de place par image mais rend l'opération
/// exactement réversible.
///
/// Le contrôle décisif est la relecture par LoadLibraryEx : c'est Windows
/// lui-même qui valide le PE. Un fichier malformé dans SystemResources,
/// c'est un shell qui ne démarre plus.
/// </summary>
internal static class MunWriteProbe
{
    internal static int Executer(Journal journal, int ressource)
    {
        string racine = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "probe-mun", "essai");

        journal.Step("Validation du graveur de ressources — sur copie");
        journal.Info("Patch sur place : aucune section reconstruite, aucun octet déplacé.");
        journal.Info($"ressource visée : {ressource}");
        journal.Blank();

        string? source = MunPatcher.Trouver("imageres");
        if (source is null) { journal.Error("imageres introuvable."); return 1; }

        Directory.CreateDirectory(racine);
        string copie = Path.Combine(racine, Path.GetFileName(source));

        var octets = File.ReadAllBytes(source);

        journal.Step("Lecture");
        journal.Info($"source : {source}");
        journal.Ok($"{octets.Length / 1024} Ko lus");

        List<PeResources.Entree> entrees;
        try { entrees = PeResources.Lire(octets); }
        catch (Exception ex) { journal.Error(ex.Message); return 1; }

        journal.Ok($"{entrees.Count} ressources localisées dans le PE");
        journal.Blank();

        journal.Step("Budget de place et fabrication du témoin");

        var plan = MunPatcher.Preparer(octets, entrees, ressource, IconProbe.DrawWitness, journal);
        if (plan is null) { journal.Error("Rien n'a été écrit."); return 1; }

        journal.Blank();
        journal.Step("Gravure sur la copie");

        try
        {
            MunPatcher.Appliquer(octets, plan);
            PeResources.CorrigerChecksum(octets);

            File.WriteAllBytes(copie, octets);
            File.SetAttributes(copie, FileAttributes.Normal);
        }
        catch (Exception ex)
        {
            journal.Error(ex.Message);
            journal.Warn("Rien n'a été tenté sur un fichier système.");
            return 1;
        }

        journal.Ok($"{plan.Images.Count} images + 1 annuaire écrits, somme de contrôle corrigée");
        journal.Ok($"{new FileInfo(copie).Length / 1024} Ko — taille inchangée : "
                 + $"{(new FileInfo(copie).Length == new FileInfo(source).Length ? "oui" : "NON")}");
        journal.Blank();

        journal.Step("Relecture par Windows et contrôle d'intégrité");

        int ecarts = Controler(journal, source, copie, new[] { plan });
        if (ecarts < 0) return 1;

        if (ecarts > 0)
        {
            journal.Blank();
            journal.Error("Le graveur n'est pas fiable. On s'arrête là.");
            return 1;
        }

        using (var patchee = ResourceNative.Module.Open(copie))
        {
            if (patchee is not null)
            {
                string apercu = Path.Combine(racine, $"verification-{ressource}.ico");
                File.WriteAllBytes(apercu, Reassembler(patchee, ressource, plan.Groupe.Langue));

                journal.Blank();
                journal.Step("Résultat");
                journal.Ok("Le graveur écrit exactement ce qui est demandé, et rien d'autre.");
                journal.Info($"témoin extrait de la copie : {apercu}");
                journal.Info($"copie modifiée             : {copie}");
                journal.Blank();
                journal.Info("Cette copie n'est pas installée : Windows continue d'utiliser");
                journal.Info("son propre fichier.");
            }
        }

        return 0;
    }

    /// <summary>
    /// Compare un binaire patché à son original. Renvoie le nombre d'écarts,
    /// ou -1 si la comparaison elle-même n'a pas pu avoir lieu.
    ///
    /// Partagé avec la substitution réelle, qui s'en sert pour valider le
    /// fichier AVANT de le déposer dans le système.
    /// </summary>
    internal static int Controler(Journal journal, string original, string patche,
                                  IReadOnlyList<MunPatcher.Plan> plans)
    {
        using var origine = ResourceNative.Module.Open(original);
        using var patchee = ResourceNative.Module.Open(patche);

        if (patchee is null)
        {
            journal.Error("Windows refuse de charger le fichier patché : le PE est malformé.");
            journal.Warn("C'est exactement ce que ce contrôle sert à détecter.");
            return -1;
        }

        if (origine is null) { journal.Error("Original illisible."); return -1; }

        journal.Ok("LoadLibraryEx accepte le fichier — l'en-tête PE reste valide");

        int ecarts = 0;

        var groupesAvant = origine.GroupIconIds();
        var groupesApres = patchee.GroupIconIds();
        var iconesAvant  = origine.Ids(ResourceNative.RT_ICON);
        var iconesApres  = patchee.Ids(ResourceNative.RT_ICON);

        if (!groupesAvant.SequenceEqual(groupesApres))
        { journal.Error($"Groupes : {groupesAvant.Count} avant, {groupesApres.Count} après"); ecarts++; }
        else journal.Ok($"{groupesApres.Count} groupes, inventaire inchangé");

        if (!iconesAvant.SequenceEqual(iconesApres))
        { journal.Error($"Images : {iconesAvant.Count} avant, {iconesApres.Count} après"); ecarts++; }
        else journal.Ok($"{iconesApres.Count} images, inventaire inchangé");

        var cibles = plans.Select(p => p.Ressource).ToHashSet();
        var modifiees = plans.SelectMany(p => p.Images).Select(i => i.Ou.Id).ToHashSet();

        foreach (var p in plans)
        {
            var relu = patchee.Read(ResourceNative.RT_GROUP_ICON, p.Ressource, p.Groupe.Langue);
            if (relu is null || !relu.SequenceEqual(MunPatcher.AnnuaireCorrige(p)))
            { journal.Error($"Groupe {p.Ressource} : annuaire inattendu"); ecarts++; }
            else journal.Ok($"Groupe {p.Ressource} : annuaire conforme");

            int ok = 0;
            foreach (var img in p.Images)
            {
                var lu = patchee.Read(ResourceNative.RT_ICON, img.Ou.Id, img.Ou.Langue);
                if (lu is not null && lu.SequenceEqual(img.Donnees)) ok++;
                else { journal.Error($"Image {img.Ou.Id} ({img.Taille}px) : contenu inattendu"); ecarts++; }
            }
            if (ok == p.Images.Count) journal.Ok($"Groupe {p.Ressource} : {ok} images conformes");
        }

        int intacts = 0, alteres = 0;

        foreach (int id in iconesAvant.Where(i => !modifiees.Contains(i)))
            if (Identique(origine, patchee, ResourceNative.RT_ICON, id)) intacts++;
            else { alteres++; if (alteres <= 5) journal.Error($"Image {id} altérée"); }

        foreach (int id in groupesAvant.Where(i => !cibles.Contains(i)))
            if (Identique(origine, patchee, ResourceNative.RT_GROUP_ICON, id)) intacts++;
            else { alteres++; if (alteres <= 5) journal.Error($"Groupe {id} altéré"); }

        if (alteres == 0) journal.Ok($"{intacts} ressources voisines intactes, octet pour octet");
        else { journal.Error($"{alteres} ressources altérées"); ecarts++; }

        return ecarts;
    }

    private static bool Identique(ResourceNative.Module a, ResourceNative.Module b, int type, int id)
    {
        var x = a.Read(type, id);
        var y = b.Read(type, id);
        return x is not null && y is not null && x.SequenceEqual(y);
    }

    /// <summary>Réassemble un .ico à partir de ce que le binaire contient réellement.</summary>
    internal static byte[] Reassembler(ResourceNative.Module m, int ressource, ushort langue)
    {
        var grp = m.Read(ResourceNative.RT_GROUP_ICON, ressource, langue)
                  ?? throw new InvalidDataException("Groupe illisible à la relecture.");

        int nb = BitConverter.ToUInt16(grp, 4);
        var morceaux = new byte[nb][];

        for (int i = 0; i < nb; i++)
        {
            int nId = BitConverter.ToUInt16(grp, 6 + i * 14 + 12);
            morceaux[i] = m.Read(ResourceNative.RT_ICON, nId, langue)
                          ?? throw new InvalidDataException($"Image {nId} illisible.");
        }

        int entete = 6 + nb * 16;
        var ico = new byte[entete + morceaux.Sum(b => b.Length)];

        ico[2] = 1;
        BitConverter.GetBytes((ushort)nb).CopyTo(ico, 4);

        int offset = entete;
        for (int i = 0; i < nb; i++)
        {
            Array.Copy(grp, 6 + i * 14, ico, 6 + i * 16, 12);
            BitConverter.GetBytes(offset).CopyTo(ico, 6 + i * 16 + 12);
            morceaux[i].CopyTo(ico, offset);
            offset += morceaux[i].Length;
        }

        return ico;
    }
}
