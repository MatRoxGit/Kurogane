namespace Kurogane.Core;

/// <summary>
/// Lecture de l'arbre de ressources d'un fichier PE, et remplacement SUR
/// PLACE du contenu d'une ressource.
///
/// Pourquoi ne pas utiliser UpdateResource : l'API documentée refuse d'écrire
/// dans les fichiers du modèle MUI — les .mun en font partie — et répond
/// ERROR_NOT_SUPPORTED. Constaté sur Windows 11 le 31/10/2026, et conforme à
/// la table de restrictions de la documentation Microsoft.
///
/// Le parti pris : on ne déplace jamais un octet. Une image de remplacement
/// doit tenir dans la place de celle qu'elle remplace ; on écrase alors les
/// octets en question et on corrige le champ de taille de l'entrée. Aucune
/// section n'est reconstruite, aucune adresse virtuelle n'est recalculée,
/// et les centaines d'autres ressources gardent leur position exacte.
///
/// C'est plus contraignant qu'un graveur généraliste — il faut un budget
/// d'octets par image — mais c'est aussi ce qui rend l'opération vérifiable
/// et exactement réversible : il suffit de conserver les octets d'origine de
/// la zone et la valeur d'origine du champ de taille.
/// </summary>
public static class PeResources
{
    /// <summary>Type de ressource : une image d'icône.</summary>
    public const int RtIcon = 3;

    /// <summary>Type de ressource : l'annuaire qui regroupe les images d'une icône.</summary>
    public const int RtGroupIcon = 14;

    /// <summary>
    /// Une ressource localisée dans le fichier.
    /// <paramref name="OffsetEntree"/> pointe sur l'IMAGE_RESOURCE_DATA_ENTRY
    /// dont le second champ porte la taille ; <paramref name="OffsetDonnees"/>
    /// pointe sur les octets eux-mêmes.
    /// </summary>
    public sealed record Entree(
        int Type, int Id, ushort Langue,
        int OffsetEntree, int OffsetDonnees, int Taille);

    /// <summary>Énumère les ressources à identifiant entier du fichier.</summary>
    public static List<Entree> Lire(byte[] f)
    {
        var (sections, offsetOptionnel) = Enveloppe(f);

        ushort magie = BitConverter.ToUInt16(f, offsetOptionnel);
        int repertoires = offsetOptionnel + (magie == 0x20B ? 112 : 96);

        int rvaRessources = BitConverter.ToInt32(f, repertoires + 2 * 8);
        if (rvaRessources == 0)
            throw new InvalidDataException("Ce fichier ne contient pas de section de ressources.");

        int baseRessources = VersOffset(sections, rvaRessources);
        var entrees = new List<Entree>();

        Parcourir(f, sections, baseRessources, baseRessources, 0, 0, 0, entrees);
        return entrees;
    }

    /// <summary>
    /// Remplace le contenu d'une ressource sans déplacer quoi que ce soit.
    /// Échoue si les nouvelles données ne tiennent pas dans la place
    /// existante — c'est volontaire : la seule alternative serait de
    /// reconstruire le fichier.
    /// </summary>
    public static void Remplacer(byte[] f, Entree e, byte[] donnees)
    {
        if (donnees.Length > e.Taille)
            throw new InvalidOperationException(
                $"Ressource {e.Type}/{e.Id} : {donnees.Length} octets à écrire pour "
              + $"{e.Taille} disponibles. Il manque {donnees.Length - e.Taille} octets.");

        donnees.CopyTo(f, e.OffsetDonnees);
        BitConverter.GetBytes(donnees.Length).CopyTo(f, e.OffsetEntree + 4);
    }

    /// <summary>Octets d'origine d'une ressource, à conserver pour la restauration.</summary>
    public static byte[] Extraire(byte[] f, Entree e) =>
        f.AsSpan(e.OffsetDonnees, e.Taille).ToArray();

    // ------------------------------------------------------------------
    //  Somme de contrôle de l'en-tête
    // ------------------------------------------------------------------

    /// <summary>Position du champ CheckSum dans l'en-tête optionnel.</summary>
    public static int OffsetChecksum(byte[] f) => Enveloppe(f).OffsetOptionnel + 64;

    /// <summary>
    /// Recalcule la somme de contrôle du PE. Windows ne la vérifie pas pour
    /// un module utilisateur, mais la laisser fausse signerait une
    /// modification maladroite et gênerait tout outil d'inspection.
    /// </summary>
    public static void CorrigerChecksum(byte[] f)
    {
        int champ = OffsetChecksum(f);

        ulong somme = 0;
        for (int i = 0; i + 1 < f.Length; i += 2)
        {
            if (i == champ || i == champ + 2) continue;   // le champ lui-même compte pour zéro
            somme += BitConverter.ToUInt16(f, i);
        }

        if ((f.Length & 1) != 0) somme += f[^1];

        somme = (somme >> 16) + (somme & 0xFFFF);
        somme = (somme >> 16) + somme;
        somme &= 0xFFFF;

        BitConverter.GetBytes((uint)somme + (uint)f.Length).CopyTo(f, champ);
    }

    // ------------------------------------------------------------------
    //  En-têtes
    // ------------------------------------------------------------------

    private readonly record struct Section(int Rva, int TailleVirtuelle, int Taille, int Offset);

    private static (List<Section> Sections, int OffsetOptionnel) Enveloppe(byte[] f)
    {
        if (f.Length < 0x40 || f[0] != (byte)'M' || f[1] != (byte)'Z')
            throw new InvalidDataException("En-tête MZ absent : ce n'est pas un fichier PE.");

        int pe = BitConverter.ToInt32(f, 0x3C);
        if (pe <= 0 || pe + 24 > f.Length || BitConverter.ToUInt32(f, pe) != 0x00004550)
            throw new InvalidDataException("Signature PE absente.");

        int coff            = pe + 4;
        int nbSections      = BitConverter.ToUInt16(f, coff + 2);
        int tailleOptionnel = BitConverter.ToUInt16(f, coff + 16);
        int offsetOptionnel = coff + 20;
        int table           = offsetOptionnel + tailleOptionnel;

        var sections = new List<Section>(nbSections);
        for (int i = 0; i < nbSections; i++)
        {
            int s = table + i * 40;
            sections.Add(new Section(
                Rva:             BitConverter.ToInt32(f, s + 12),
                TailleVirtuelle: BitConverter.ToInt32(f, s + 8),
                Taille:          BitConverter.ToInt32(f, s + 16),
                Offset:          BitConverter.ToInt32(f, s + 20)));
        }

        return (sections, offsetOptionnel);
    }

    private static int VersOffset(List<Section> sections, int rva)
    {
        foreach (var s in sections)
        {
            int etendue = Math.Max(s.TailleVirtuelle, s.Taille);
            if (rva >= s.Rva && rva < s.Rva + etendue)
                return s.Offset + (rva - s.Rva);
        }

        throw new InvalidDataException($"Adresse virtuelle hors sections : 0x{rva:X8}.");
    }

    // ------------------------------------------------------------------
    //  Arbre de ressources : type -> identifiant -> langue
    // ------------------------------------------------------------------

    private static void Parcourir(byte[] f, List<Section> sections, int baseRessources,
        int repertoire, int profondeur, int type, int id, List<Entree> sortie)
    {
        int nbNommees = BitConverter.ToUInt16(f, repertoire + 12);
        int nbEntieres = BitConverter.ToUInt16(f, repertoire + 14);

        for (int i = 0; i < nbNommees + nbEntieres; i++)
        {
            int e = repertoire + 16 + i * 8;
            uint nom  = BitConverter.ToUInt32(f, e);
            uint vers = BitConverter.ToUInt32(f, e + 4);

            // Les ressources à nom textuel sont écartées : les binaires du
            // shell n'utilisent que des identifiants entiers.
            if ((nom & 0x80000000u) != 0) continue;

            int valeur = (int)(nom & 0x7FFFFFFF);

            if ((vers & 0x80000000u) != 0)
            {
                Parcourir(f, sections, baseRessources,
                    baseRessources + (int)(vers & 0x7FFFFFFF),
                    profondeur + 1,
                    profondeur == 0 ? valeur : type,
                    profondeur == 1 ? valeur : id,
                    sortie);
                continue;
            }

            int entree = baseRessources + (int)vers;
            int rva    = BitConverter.ToInt32(f, entree);
            int taille = BitConverter.ToInt32(f, entree + 4);

            sortie.Add(new Entree(type, id, (ushort)valeur,
                entree, VersOffset(sections, rva), taille));
        }
    }
}
