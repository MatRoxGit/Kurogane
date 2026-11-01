using System.IO;
namespace Kurogane.Core;

/// <summary>
/// Prépare et applique le remplacement d'un groupe d'icônes dans un binaire
/// du shell, sur place.
///
/// Partagé entre la validation sur copie et la substitution réelle : c'est
/// exactement le même code qui calcule les octets dans les deux cas, donc ce
/// qui a été vérifié sur la copie est ce qui sera écrit sur le système.
/// </summary>
public static class MunPatcher
{
    public sealed record Image(PeResources.Entree Ou, int Taille, byte[] Donnees, string Forme);

    public sealed record Plan(
        int Ressource,
        PeResources.Entree Groupe,
        byte[] Annuaire,
        List<Image> Images);

    /// <summary>
    /// Calcule ce qu'il faudrait écrire, sans rien écrire.
    ///
    /// <paramref name="charge"/>, facultatif, peut fournir des octets prêts à
    /// écrire pour une taille donnée : quand ils tiennent, ils sont recopiés
    /// tels quels, sans décodage ni ré-encodage. <paramref name="dessin"/>
    /// fournit les pixels BGRA pour tous les autres cas.
    ///
    /// Renvoie null si le groupe est absent ou si une image ne tient pas dans
    /// la place disponible — auquel cas le journal dit laquelle et de combien.
    /// </summary>
    public static Plan? Preparer(byte[] octets, List<PeResources.Entree> entrees,
                                 int ressource, Func<int, byte[]> dessin,
                                 Journal journal, bool detaille = true,
                                 Func<int, byte[]?>? charge = null)
    {
        var groupe = entrees.FirstOrDefault(
            e => e.Type == PeResources.RtGroupIcon && e.Id == ressource);

        if (groupe is null)
        {
            journal.Error($"Le groupe {ressource} n'existe pas dans ce binaire.");
            return null;
        }

        var annuaire = PeResources.Extraire(octets, groupe);
        int nb = BitConverter.ToUInt16(annuaire, 4);

        var images = new List<Image>(nb);
        var manques = new List<string>();

        for (int i = 0; i < nb; i++)
        {
            int o = 6 + i * 14;
            int taille = annuaire[o] == 0 ? 256 : annuaire[o];
            int nId = BitConverter.ToUInt16(annuaire, o + 12);

            var cible = entrees.FirstOrDefault(
                            e => e.Type == PeResources.RtIcon && e.Id == nId
                              && e.Langue == groupe.Langue)
                     ?? entrees.FirstOrDefault(
                            e => e.Type == PeResources.RtIcon && e.Id == nId);

            if (cible is null)
            {
                journal.Error($"{taille}px : image {nId} introuvable dans le PE");
                return null;
            }

            byte[] donnees; string forme;

            // 1. Octets déjà prêts, repris tels quels quand ils tiennent.
            var prete = charge?.Invoke(taille);

            if (prete is not null && prete.Length <= cible.Taille)
            {
                donnees = prete;
                forme   = "copie";
            }
            else
            {
                // 2. Le bitmap non compressé est le format natif des icônes.
                //    Microsoft stocke ces tailles de la même façon, donc un
                //    bitmap de mêmes dimensions fait exactement la même
                //    taille et tient toujours. Le PNG ne sert qu'en 256 px,
                //    où l'original est lui-même compressé.
                var pixels = dessin(taille);
                var dib = IcoWriter.BuildDib(taille, pixels);

                if (dib.Length <= cible.Taille) { donnees = dib; forme = "bitmap"; }
                else
                {
                    var png = PngWriter.Encode(taille, pixels);
                    if (png.Length <= cible.Taille) { donnees = png; forme = "png"; }
                    else
                    {
                        manques.Add($"{taille}px : {png.Length} octets pour {cible.Taille} disponibles");
                        continue;
                    }
                }
            }

            if (detaille)
                journal.Ok($"{taille,3}px — budget {cible.Taille,7} o — écrit {donnees.Length,7} o "
                         + $"en {forme,-6} — marge {cible.Taille - donnees.Length,7} o");

            images.Add(new Image(cible, taille, donnees, forme));
        }

        if (manques.Count > 0)
        {
            foreach (var m in manques) journal.Error(m);
            journal.Error($"Groupe {ressource} : le dessin ne tient pas dans la place disponible.");
            return null;
        }

        return new Plan(ressource, groupe, annuaire, images);
    }

    /// <summary>Écrit un plan dans le tampon. Ne touche pas au fichier.</summary>
    public static void Appliquer(byte[] octets, Plan plan)
    {
        foreach (var img in plan.Images)
            PeResources.Remplacer(octets, img.Ou, img.Donnees);

        PeResources.Remplacer(octets, plan.Groupe, AnnuaireCorrige(plan));
    }

    /// <summary>
    /// Reprend l'annuaire d'origine et n'y corrige que les tailles. Le nombre
    /// d'entrées, les identifiants et les dimensions ne bougent pas : c'est ce
    /// qui garantit que l'annuaire conserve exactement sa longueur, donc que
    /// le patch reste sur place.
    /// </summary>
    public static byte[] AnnuaireCorrige(Plan plan)
    {
        var a = (byte[])plan.Annuaire.Clone();
        int nb = BitConverter.ToUInt16(a, 4);

        for (int i = 0; i < nb; i++)
        {
            int o = 6 + i * 14;
            int nId = BitConverter.ToUInt16(a, o + 12);

            var img = plan.Images.FirstOrDefault(x => x.Ou.Id == nId);
            if (img is null) continue;

            BitConverter.GetBytes(img.Donnees.Length).CopyTo(a, o + 8);
        }

        return a;
    }

    /// <summary>Emplacement du binaire : le .mun depuis 1903, le .dll avant.</summary>
    public static string? Trouver(string nom)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (string c in new[]
                 {
                     Path.Combine(windows, "SystemResources", $"{nom}.dll.mun"),
                     Path.Combine(windows, "System32", $"{nom}.dll"),
                 })
            if (File.Exists(c)) return c;

        return null;
    }
}
