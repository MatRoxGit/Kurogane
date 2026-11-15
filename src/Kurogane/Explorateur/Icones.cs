using System.IO;
using System.Windows.Media.Imaging;
using Kurogane.Theme;

namespace Kurogane.Explorateur;

/// <summary>
/// Les icônes du thème, chargées une fois et retenues.
///
/// Sorti de l'explorateur le 15 novembre, quand la couche bureau a eu
/// besoin du même travail. Le cache compte autant que le chargement : sans
/// lui, un dossier de mille entrées relirait le même .ico mille fois depuis
/// le disque.
///
/// Une instance par vue : deux vues n'ont pas à partager leur cache, et le
/// partager imposerait une synchronisation pour un gain nul.
/// </summary>
internal sealed class Icones
{
    private readonly ThemeDefinition? _theme;
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    internal Icones(ThemeDefinition? theme) => _theme = theme;

    /// <summary>
    /// L'icône d'une entrée, d'après sa famille de type. Repli sur
    /// l'icône générique si celle de la famille n'a pas encore été
    /// dessinée : une case vide ne dit rien à l'utilisateur. Le manque,
    /// lui, est signalé là où il doit l'être — par <c>probe-pack</c>, pas
    /// par un trou dans l'affichage.
    /// </summary>
    internal BitmapSource? Pour(Entree entree)
    {
        if (_theme?.Icons is null) return null;

        return Fichier(TypesFichiers.Pour(entree) + ".ico")
            ?? Fichier(TypesFichiers.Generique + ".ico");
    }

    /// <summary>Une icône du pack, par nom de fichier.</summary>
    internal BitmapSource? Fichier(string fichier)
    {
        if (_cache.TryGetValue(fichier, out var connue)) return connue;

        BitmapSource? image = null;

        try
        {
            string chemin = _theme!.ResolveIcon(fichier);

            if (File.Exists(chemin))
            {
                using var flux = File.OpenRead(chemin);

                var decodeur = new IconBitmapDecoder(flux,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                int ecart = int.MaxValue;

                // Un .ico porte plusieurs tailles : on prend la plus proche
                // de 32 plutôt que la première venue, qui pourrait être une
                // vignette de 16 étirée.
                foreach (var frame in decodeur.Frames)
                {
                    int d = Math.Abs(frame.PixelWidth - 32);
                    if (d >= ecart) continue;

                    ecart = d;
                    image = frame;
                }
            }
        }
        catch
        {
            // Une icône illisible laisse une vignette vide, jamais une
            // exception qui ferait tomber la fenêtre.
        }

        _cache[fichier] = image;
        return image;
    }
}
