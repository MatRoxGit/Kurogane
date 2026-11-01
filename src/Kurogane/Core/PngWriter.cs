using System.IO;
using System.IO.Compression;
using System.Text;

namespace Kurogane.Core;

/// <summary>
/// Encodeur PNG minimal, sans dépendance.
///
/// Il existe pour une raison précise : le graveur de ressources patche sur
/// place, donc chaque image doit tenir dans la place de celle qu'elle
/// remplace. Un bitmap non compressé de 256x256 pèse 270 Ko ; le même dessin
/// en PNG, sur des aplats, tombe à quelques kilo-octets. Sans compression,
/// la contrainte de place serait intenable aux grandes tailles.
///
/// Windows accepte une charge PNG dans une ressource d'icône depuis Vista.
///
/// La compression passe par ZLibStream, présent dans .NET depuis la 6 :
/// aucun paquet à installer, et c'est exactement le format que PNG attend.
/// </summary>
public static class PngWriter
{
    /// <param name="bgraHautEnBas">
    /// Pixels BGRA 32 bits, première ligne en haut — même convention que
    /// <see cref="IcoWriter"/>.
    /// </param>
    public static byte[] Encode(int taille, byte[] bgraHautEnBas)
    {
        if (bgraHautEnBas.Length != taille * taille * 4)
            throw new ArgumentException(
                $"Image {taille}x{taille} : {bgraHautEnBas.Length} octets fournis, "
              + $"{taille * taille * 4} attendus.", nameof(bgraHautEnBas));

        // Chaque ligne est précédée d'un octet de filtre. 0 = aucun filtre :
        // les aplats se compressent déjà très bien, et un filtre mal choisi
        // dégraderait le résultat.
        var brut = new byte[taille * (1 + taille * 4)];
        int o = 0;

        for (int y = 0; y < taille; y++)
        {
            brut[o++] = 0;
            int src = y * taille * 4;

            for (int x = 0; x < taille; x++)
            {
                brut[o++] = bgraHautEnBas[src + 2];   // R
                brut[o++] = bgraHautEnBas[src + 1];   // G
                brut[o++] = bgraHautEnBas[src + 0];   // B
                brut[o++] = bgraHautEnBas[src + 3];   // A
                src += 4;
            }
        }

        byte[] comprime;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(brut, 0, brut.Length);
            comprime = ms.ToArray();
        }

        var ihdr = new byte[13];
        GrandBoutiste(ihdr, 0, taille);
        GrandBoutiste(ihdr, 4, taille);
        ihdr[8]  = 8;    // 8 bits par canal
        ihdr[9]  = 6;    // couleur vraie avec alpha
        ihdr[10] = 0;    // compression deflate
        ihdr[11] = 0;    // filtrage standard
        ihdr[12] = 0;    // non entrelacé

        var sortie = new MemoryStream();
        sortie.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Bloc(sortie, "IHDR", ihdr);
        Bloc(sortie, "IDAT", comprime);
        Bloc(sortie, "IEND", Array.Empty<byte>());

        return sortie.ToArray();
    }

    private static void Bloc(Stream s, string type, byte[] donnees)
    {
        var entete = new byte[4];
        GrandBoutiste(entete, 0, donnees.Length);
        s.Write(entete);

        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(donnees);

        var crc = new byte[4];
        GrandBoutiste(crc, 0, unchecked((int)Crc32(t, donnees)));
        s.Write(crc);
    }

    private static void GrandBoutiste(byte[] cible, int offset, int valeur)
    {
        cible[offset + 0] = (byte)(valeur >> 24);
        cible[offset + 1] = (byte)(valeur >> 16);
        cible[offset + 2] = (byte)(valeur >> 8);
        cible[offset + 3] = (byte)valeur;
    }

    private static readonly uint[] Table = Construire();

    private static uint[] Construire()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(params byte[][] morceaux)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var m in morceaux)
            foreach (byte b in m)
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
