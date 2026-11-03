using System.Windows.Media.Imaging;

namespace Kurogane.Explorateur;

/// <summary>
/// Ce qu'une carte affiche, prêt à être lié.
///
/// Les cartes étaient construites en C# et ajoutées une par une à
/// l'ItemsControl. Un panneau virtualisant n'aurait alors rien eu à
/// virtualiser : les 4686 contrôles existaient déjà avant qu'il entre en
/// jeu. En passant par des données et un gabarit, le contrôle n'est créé
/// que lorsque la carte entre à l'écran.
/// </summary>
public sealed class CarteVue
{
    public required Entree Source { get; init; }

    public required string Nom { get; init; }

    /// <summary>« NOEUD » pour un dossier, sinon extension et poids.</summary>
    public required string Meta { get; init; }

    public required string Date { get; init; }

    public BitmapSource? Icone { get; init; }

    public string Chemin => Source.Chemin;
}
