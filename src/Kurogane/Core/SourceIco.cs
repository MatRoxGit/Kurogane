using System.IO;
namespace Kurogane.Core;

/// <summary>
/// Fournit les images d'un fichier .ico à la taille demandée, pour le
/// remplacement de ressources.
///
/// Deux chemins, dans cet ordre :
///
///   1. <see cref="Charge"/> rend les octets du fichier TELS QUELS lorsque
///      la taille demandée s'y trouve. C'est le cas idéal : un bitmap
///      32 bits de mêmes dimensions que celui qu'il remplace fait
///      exactement la même taille, donc il tient toujours, et aucune
///      conversion ne peut dégrader l'image.
///
///   2. <see cref="Pixels"/> décode — et rééchantillonne si la taille
///      manque — pour les cas où la charge directe ne convient pas :
///      le 256 px, où l'original est un PNG bien plus compact qu'un
///      bitmap, et les tailles absentes du fichier.
/// </summary>
public sealed class SourceIco
{
    private readonly Dictionary<int, IcoReader.Entree> _parTaille = new();
    private readonly string _nom;

    public SourceIco(string chemin)
    {
        _nom = Path.GetFileName(chemin);

        foreach (var e in IcoReader.Lire(chemin))
            _parTaille[e.Taille] = e;          // en cas de doublon, la dernière gagne

        if (_parTaille.Count == 0)
            throw new InvalidDataException($"{_nom} ne contient aucune image.");
    }

    public IEnumerable<int> Tailles => _parTaille.Keys.OrderBy(t => t);

    /// <summary>Octets prêts à écrire, ou null si la taille n'est pas dans le fichier.</summary>
    public byte[]? Charge(int taille) =>
        _parTaille.TryGetValue(taille, out var e) ? e.Charge : null;

    /// <summary>Pixels BGRA, première ligne en haut.</summary>
    public byte[] Pixels(int taille)
    {
        // L'entrée exacte ne sert que si elle est en bitmap : le PNG n'est
        // pas décodable ici, et c'est justement le format du 256 px.
        if (_parTaille.TryGetValue(taille, out var exacte) && !exacte.EstPng)
            return IcoReader.VersBgra(exacte);

        // Taille absente ou en PNG : on part de la plus grande disponible en
        // bitmap, celle qui porte le plus de détail, et on rééchantillonne.
        var meilleure = _parTaille.Values
            .Where(e => !e.EstPng)
            .OrderByDescending(e => e.Taille)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                   $"{_nom} : aucune image en bitmap, impossible de produire du {taille}px.");

        return Reechantillonner(IcoReader.VersBgra(meilleure), meilleure.Taille, taille);
    }

    /// <summary>
    /// Rééchantillonnage par moyenne de surface. Rudimentaire mais correct
    /// pour réduire : chaque pixel de sortie est la moyenne des pixels
    /// d'entrée qu'il recouvre. Les canaux sont pondérés par l'alpha, sans
    /// quoi les bords transparents tireraient la couleur vers le noir.
    /// </summary>
    private static byte[] Reechantillonner(byte[] source, int de, int vers)
    {
        if (de == vers) return source;

        var sortie = new byte[vers * vers * 4];

        for (int y = 0; y < vers; y++)
        {
            int y0 = y * de / vers;
            int y1 = Math.Max(y0 + 1, (y + 1) * de / vers);

            for (int x = 0; x < vers; x++)
            {
                int x0 = x * de / vers;
                int x1 = Math.Max(x0 + 1, (x + 1) * de / vers);

                double b = 0, g = 0, r = 0, a = 0, poids = 0;

                for (int sy = y0; sy < y1; sy++)
                for (int sx = x0; sx < x1; sx++)
                {
                    int o = (sy * de + sx) * 4;
                    double alpha = source[o + 3] / 255.0;

                    b += source[o + 0] * alpha;
                    g += source[o + 1] * alpha;
                    r += source[o + 2] * alpha;
                    a += source[o + 3];
                    poids += alpha;
                }

                int n = (y1 - y0) * (x1 - x0);
                int d = (y * vers + x) * 4;

                if (poids > 0)
                {
                    sortie[d + 0] = Octet(b / poids);
                    sortie[d + 1] = Octet(g / poids);
                    sortie[d + 2] = Octet(r / poids);
                }

                sortie[d + 3] = Octet(a / n);
            }
        }

        return sortie;
    }

    private static byte Octet(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
