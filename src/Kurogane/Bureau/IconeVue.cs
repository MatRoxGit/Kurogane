using System.Windows.Media.Imaging;
using Kurogane.Explorateur;

namespace Kurogane.Bureau;

/// <summary>
/// Ce qu'une icône du bureau affiche.
///
/// Plus maigre que la carte de l'explorateur : le bureau ne montre ni
/// taille, ni date, ni type. Une classe à part plutôt qu'un champ ignoré
/// sur l'autre — un gabarit qui lie des propriétés vides ment sur ce que
/// la vue sait.
/// </summary>
public sealed class IconeVue
{
    public required Entree Source { get; init; }
    public required string Nom { get; init; }
    public BitmapSource? Icone { get; init; }

    public string Chemin => Source.Chemin;
}
