using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using Kurogane.Explorateur;

namespace Kurogane.Bureau;

/// <summary>
/// Ce qu'une icône du bureau affiche, et où elle se trouve.
///
/// Plus maigre que la carte de l'explorateur : le bureau ne montre ni
/// taille, ni date, ni type. Mais elle porte en plus sa place, que
/// l'explorateur n'a pas à connaître — sa grille se range seule.
///
/// La position vit en deux formes. La <b>cellule</b> est ce qu'on retient
/// et ce qu'on enregistre : elle survit à un changement de taille d'icônes
/// ou de résolution. Les <b>coordonnées</b> en découlent, et changent avec
/// la taille des cases.
/// </summary>
public sealed class IconeVue : INotifyPropertyChanged
{
    public required Entree Source { get; init; }
    public required string Nom { get; init; }
    public BitmapSource? Icone { get; init; }

    public string Chemin => Source.Chemin;

    public int Colonne { get; set; }
    public int Ligne { get; set; }

    private double _x;
    private double _y;

    public double X
    {
        get => _x;
        set { if (_x.Equals(value)) return; _x = value; Prevenir(); }
    }

    public double Y
    {
        get => _y;
        set { if (_y.Equals(value)) return; _y = value; Prevenir(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Prevenir([CallerMemberName] string? propriete = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propriete));
}
