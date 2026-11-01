using System.IO;
namespace Kurogane.Core;

/// <summary>
/// Lecture d'un fichier .ico : inventaire des images qu'il contient, et
/// accès à leur charge brute.
///
/// Pensé pour la substitution de ressources. Une image d'icône dans un
/// binaire PE et une image dans un fichier .ico sont le même objet — même
/// bitmap, mêmes octets. Quand le format et les dimensions correspondent,
/// on peut donc recopier la charge telle quelle, sans décoder ni
/// ré-encoder : c'est à la fois plus fidèle et garanti de tenir dans la
/// place disponible, puisqu'un bitmap 32 bits de mêmes dimensions fait
/// exactement la même taille que celui qu'il remplace.
/// </summary>
public static class IcoReader
{
    /// <param name="Taille">Côté en pixels ; 256 quand l'octet vaut 0.</param>
    /// <param name="Bits">Bits par pixel déclarés dans l'annuaire.</param>
    /// <param name="Charge">Octets de l'image, tels qu'ils sont dans le fichier.</param>
    /// <param name="EstPng">Vrai si la charge est un PNG plutôt qu'un bitmap.</param>
    public sealed record Entree(int Taille, int Bits, byte[] Charge, bool EstPng);

    private static readonly byte[] SignaturePng = { 0x89, 0x50, 0x4E, 0x47 };

    public static List<Entree> Lire(string chemin)
    {
        var f = File.ReadAllBytes(chemin);

        if (f.Length < 6 || BitConverter.ToUInt16(f, 0) != 0 || BitConverter.ToUInt16(f, 2) != 1)
            throw new InvalidDataException($"{Path.GetFileName(chemin)} n'est pas un fichier .ico.");

        int nb = BitConverter.ToUInt16(f, 4);
        var liste = new List<Entree>(nb);

        for (int i = 0; i < nb; i++)
        {
            int o = 6 + i * 16;
            if (o + 16 > f.Length)
                throw new InvalidDataException($"{Path.GetFileName(chemin)} : annuaire tronqué.");

            int taille = f[o] == 0 ? 256 : f[o];
            int bits   = BitConverter.ToUInt16(f, o + 6);
            int octets = BitConverter.ToInt32(f, o + 8);
            int offset = BitConverter.ToInt32(f, o + 12);

            if (offset < 0 || octets < 0 || offset + octets > f.Length)
                throw new InvalidDataException(
                    $"{Path.GetFileName(chemin)} : image {taille}px hors du fichier.");

            var charge = f.AsSpan(offset, octets).ToArray();
            bool png = charge.Length >= 4 && charge.AsSpan(0, 4).SequenceEqual(SignaturePng);

            liste.Add(new Entree(taille, bits, charge, png));
        }

        return liste;
    }

    /// <summary>
    /// Décode une image en pixels BGRA, première ligne en haut — la
    /// convention de <see cref="IcoWriter"/> et de <see cref="PngWriter"/>.
    ///
    /// Ne gère que le bitmap 32 bits non compressé, seul format produit par
    /// ce projet et seul format utilisé par Windows en dessous de 256 px.
    /// Un PNG ou une palette réduite lève une exception plutôt que de
    /// renvoyer une image approximative.
    /// </summary>
    public static byte[] VersBgra(Entree e)
    {
        if (e.EstPng)
            throw new NotSupportedException(
                $"Image {e.Taille}px en PNG : ce projet ne sait pas décoder le PNG. "
              + "Régénère le .ico en bitmap 32 bits.");

        var c = e.Charge;

        if (c.Length < 40)
            throw new InvalidDataException($"Image {e.Taille}px : en-tête bitmap tronqué.");

        int largeur = BitConverter.ToInt32(c, 4);
        int hauteur = BitConverter.ToInt32(c, 8) / 2;   // XOR + masque AND empilés
        int bits    = BitConverter.ToUInt16(c, 14);
        int compression = BitConverter.ToInt32(c, 16);

        if (bits != 32 || compression != 0)
            throw new NotSupportedException(
                $"Image {e.Taille}px : {bits} bits, compression {compression}. "
              + "Seul le bitmap 32 bits non compressé est géré.");

        if (largeur != e.Taille || hauteur != e.Taille)
            throw new InvalidDataException(
                $"Image annoncée {e.Taille}px, bitmap {largeur}x{hauteur}.");

        int attendu = 40 + largeur * hauteur * 4;
        if (c.Length < attendu)
            throw new InvalidDataException($"Image {e.Taille}px : données tronquées.");

        // Le bitmap est stocké de bas en haut : on remet à l'endroit.
        var bgra = new byte[largeur * hauteur * 4];
        int ligne = largeur * 4;

        for (int y = 0; y < hauteur; y++)
            Array.Copy(c, 40 + (hauteur - 1 - y) * ligne, bgra, y * ligne, ligne);

        return bgra;
    }
}
