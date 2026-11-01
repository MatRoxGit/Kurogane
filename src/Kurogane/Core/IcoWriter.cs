using System.IO;

namespace Kurogane.Core;

/// <summary>
/// Assemble un fichier .ico multi-resolutions a partir de bitmaps BGRA.
/// Ecrit le format a la main : pas de dependance, et surtout un controle
/// exact de ce qui entre dans le fichier, taille par taille.
/// </summary>
public static class IcoWriter
{
    /// <param name="images">
    /// Couples (taille, pixels BGRA 32 bits, premiere ligne en haut).
    /// </param>
    public static void Write(string path, IReadOnlyList<(int Size, byte[] Bgra)> images)
    {
        if (images.Count == 0)
            throw new ArgumentException("Aucune image fournie.", nameof(images));
        if (images.Count > 255)
            throw new ArgumentException("255 images au maximum dans un .ico.", nameof(images));

        var payloads = new List<byte[]>(images.Count);
        foreach (var (size, bgra) in images)
        {
            if (size < 1 || size > 256)
                throw new ArgumentOutOfRangeException(nameof(images),
                    $"Taille hors bornes : {size} (attendu 1 a 256).");
            if (bgra.Length != size * size * 4)
                throw new ArgumentException(
                    $"Image {size}x{size} : {bgra.Length} octets fournis, "
                  + $"{size * size * 4} attendus.", nameof(images));

            payloads.Add(BuildDib(size, bgra));
        }

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write((ushort)0);                 // reserve
        w.Write((ushort)1);                 // type : icone
        w.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            int size = images[i].Size;
            w.Write((byte)(size == 256 ? 0 : size));   // largeur, 0 signifie 256
            w.Write((byte)(size == 256 ? 0 : size));   // hauteur
            w.Write((byte)0);                          // couleurs de palette
            w.Write((byte)0);                          // reserve
            w.Write((ushort)1);                        // plans
            w.Write((ushort)32);                       // bits par pixel
            w.Write((uint)payloads[i].Length);
            w.Write((uint)offset);
            offset += payloads[i].Length;
        }

        foreach (var p in payloads)
            w.Write(p);
    }

    /// <summary>
    /// BITMAPINFOHEADER + image XOR (BGRA, de bas en haut) + masque AND.
    /// La hauteur declaree vaut le double de la vraie : le format empile les
    /// deux bitmaps dans une seule image. Oublier ce doublement produit un
    /// fichier que Windows accepte mais affiche coupe en deux.
    /// </summary>
    internal static byte[] BuildDib(int size, byte[] bgraTopDown)
    {
        int maskStride = (size + 31) / 32 * 4;   // 1 bit par pixel, lignes alignees sur 4 octets
        int xorBytes = size * size * 4;
        int andBytes = maskStride * size;

        var ms = new MemoryStream(40 + xorBytes + andBytes);
        var w = new BinaryWriter(ms);

        w.Write(40);                      // biSize
        w.Write(size);                    // biWidth
        w.Write(size * 2);                // biHeight : XOR + AND
        w.Write((ushort)1);               // biPlanes
        w.Write((ushort)32);              // biBitCount
        w.Write(0);                       // biCompression : BI_RGB
        w.Write(xorBytes + andBytes);     // biSizeImage
        w.Write(0); w.Write(0);           // resolutions
        w.Write(0); w.Write(0);           // palette

        for (int y = size - 1; y >= 0; y--)
            w.Write(bgraTopDown, y * size * 4, size * 4);

        w.Write(new byte[andBytes]);      // masque opaque : le canal alpha fait le travail

        w.Flush();
        return ms.ToArray();
    }
}
