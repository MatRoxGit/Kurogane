using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace Kurogane.Explorateur;

/// <summary>
/// Un nœud du volet gauche.
///
/// Les enfants sont chargés à l'ouverture et non d'avance : déplier le
/// disque entier au démarrage lirait des dizaines de milliers de dossiers
/// pour en afficher dix. Un enfant fictif est posé à la création, sans quoi
/// l'arbre n'afficherait pas de poignée et le nœud paraîtrait vide.
/// </summary>
public sealed class NoeudArbre
{
    public required string Libelle { get; init; }
    public required string Detail  { get; init; }
    public required string Chemin  { get; init; }

    public BitmapSource? Icone { get; set; }

    public ObservableCollection<NoeudArbre> Enfants { get; } = new();

    /// <summary>Vrai une fois les vrais enfants lus.</summary>
    public bool Charge { get; set; }

    /// <summary>
    /// Marqueur posé tant que le contenu n'est pas lu. Il porte un libellé
    /// visible : si un chargement échouait sans rien remplacer, on verrait
    /// « LECTURE… » rester, au lieu d'un nœud muet.
    /// </summary>
    public static NoeudArbre Fictif(string chemin) => new()
    {
        Libelle = "LECTURE",
        Detail  = "…",
        Chemin  = chemin,
    };

    public bool EstFictif => Charge == false && Libelle == "LECTURE" && Detail == "…";
}
