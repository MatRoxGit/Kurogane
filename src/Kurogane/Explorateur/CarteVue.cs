using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Kurogane.Explorateur;

/// <summary>
/// Ce qu'une carte affiche, prêt à être lié.
///
/// Les cartes étaient construites en C# et ajoutées une par une. Un panneau
/// virtualisant n'aurait alors rien eu à virtualiser : les contrôles
/// existaient déjà avant qu'il entre en jeu. En passant par des données et
/// un gabarit, le contrôle n'est créé qu'à l'entrée de la carte à l'écran.
///
/// La notification de changement sert à l'édition en place : le gabarit
/// bascule entre l'étiquette et le champ de saisie sans qu'on reconstruise
/// la liste.
/// </summary>
public sealed class CarteVue : INotifyPropertyChanged
{
    public required Entree Source { get; init; }

    private string _nom = "";

    public required string Nom
    {
        get => _nom;
        init => _nom = value;
    }

    /// <summary>« NOEUD » pour un dossier, sinon extension et poids.</summary>
    public required string Meta { get; init; }

    public required string Date { get; init; }

    public BitmapSource? Icone { get; init; }

    /// <summary>
    /// Entrée cachée ou système. Elle s'affiche en retrait plutôt que de
    /// se confondre avec le reste : la montrer sans la distinguer ferait
    /// croire à un dossier ordinaire.
    /// </summary>
    public bool Attenue { get; init; }

    public string Chemin => Source.Chemin;

    private bool _enEdition;

    /// <summary>
    /// Vrai pendant que l'utilisateur saisit un nouveau nom. Une seule carte
    /// à la fois : l'explorateur remet la précédente à faux avant d'en
    /// ouvrir une autre.
    /// </summary>
    public bool EnEdition
    {
        get => _enEdition;
        set
        {
            if (_enEdition == value) return;

            _enEdition = value;
            Prevenir();

            // Rouvrir une édition doit repartir du nom réel, pas d'une
            // saisie abandonnée au tour précédent.
            if (value) Saisie = _nom;
        }
    }

    private string _saisie = "";

    /// <summary>Le texte en cours de frappe, distinct du nom affiché.</summary>
    public string Saisie
    {
        get => _saisie;
        set { if (_saisie == value) return; _saisie = value; Prevenir(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Prevenir([CallerMemberName] string? propriete = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propriete));
}
