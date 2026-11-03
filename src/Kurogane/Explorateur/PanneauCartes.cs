using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Kurogane.Explorateur;

/// <summary>
/// Panneau en grille qui ne matérialise que les cartes visibles.
///
/// Mesuré avant d'être écrit : sur C:\Windows\System32 et ses 4686 entrées,
/// la lecture prenait 65 ms et le rendu 671 ms — 560 et 1806 ms à froid. Le
/// coût n'était pas le disque mais l'instanciation de 4686 contrôles WPF.
///
/// WPF fournit VirtualizingStackPanel, qui empile en une colonne. Pour une
/// grille il faut écrire le sien. Le cas est ici simple : les cartes ont
/// toutes la même taille, donc la position de l'élément n se calcule au lieu
/// de se mesurer, et IScrollInfo peut répondre sans rien matérialiser.
/// </summary>
public sealed class PanneauCartes : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty LargeurCarteProperty =
        DependencyProperty.Register(nameof(LargeurCarte), typeof(double), typeof(PanneauCartes),
            new FrameworkPropertyMetadata(246.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HauteurCarteProperty =
        DependencyProperty.Register(nameof(HauteurCarte), typeof(double), typeof(PanneauCartes),
            new FrameworkPropertyMetadata(72.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double LargeurCarte
    {
        get => (double)GetValue(LargeurCarteProperty);
        set => SetValue(LargeurCarteProperty, value);
    }

    public double HauteurCarte
    {
        get => (double)GetValue(HauteurCarteProperty);
        set => SetValue(HauteurCarteProperty, value);
    }

    private int _colonnes = 1;
    private Size _etendue;
    private Size _fenetre;
    private Point _decalage;

    // ------------------------------------------------------------- mesure

    protected override Size MeasureOverride(Size dispo)
    {
        // Touche le générateur : sans cet accès, InternalChildren reste vide
        // et rien ne se matérialise jamais.
        _ = InternalChildren;

        var proprietaire = ItemsControl.GetItemsOwner(this);
        int total = proprietaire?.Items.Count ?? 0;

        double largeur = double.IsInfinity(dispo.Width) ? LargeurCarte : dispo.Width;
        double hauteur = double.IsInfinity(dispo.Height) ? HauteurCarte : dispo.Height;

        _colonnes = Math.Max(1, (int)(largeur / LargeurCarte));

        int lignes = total == 0 ? 0 : (total + _colonnes - 1) / _colonnes;

        var etendue = new Size(largeur, lignes * HauteurCarte);
        var fenetre = new Size(largeur, hauteur);

        if (etendue != _etendue || fenetre != _fenetre)
        {
            _etendue = etendue;
            _fenetre = fenetre;
            ScrollOwner?.InvalidateScrollInfo();
        }

        // Un redimensionnement peut rendre le décalage courant hors bornes.
        Borner();

        if (total == 0)
        {
            Vider();
            return new Size(largeur, 0);
        }

        var (premier, dernier) = Visible(total);

        Materialiser(premier, dernier);
        Oublier(premier, dernier);

        return new Size(largeur, Math.Min(hauteur, etendue.Height));
    }

    /// <summary>
    /// Première et dernière carte à matérialiser. Une ligne de marge de part
    /// et d'autre : sans elle, une carte apparaîtrait pile au moment où elle
    /// entre à l'écran, et le défilement accrocherait.
    /// </summary>
    private (int Premier, int Dernier) Visible(int total)
    {
        int ligneHaut = Math.Max(0, (int)(_decalage.Y / HauteurCarte) - 1);
        int ligneBas  = (int)((_decalage.Y + _fenetre.Height) / HauteurCarte) + 1;

        int premier = ligneHaut * _colonnes;
        int dernier = Math.Min(total - 1, (ligneBas + 1) * _colonnes - 1);

        return (Math.Min(premier, Math.Max(0, total - 1)), dernier);
    }

    private void Materialiser(int premier, int dernier)
    {
        var generateur = ItemContainerGenerator;
        var depart = generateur.GeneratorPositionFromIndex(premier);

        int rang = depart.Offset == 0 ? depart.Index : depart.Index + 1;

        using var _ = generateur.StartAt(depart, GeneratorDirection.Forward, true);

        for (int i = premier; i <= dernier; i++, rang++)
        {
            if (generateur.GenerateNext(out bool neuf) is not UIElement enfant) break;

            if (neuf)
            {
                if (rang >= InternalChildren.Count) AddInternalChild(enfant);
                else InsertInternalChild(rang, enfant);

                generateur.PrepareItemContainer(enfant);
            }

            enfant.Measure(new Size(LargeurCarte, HauteurCarte));
        }
    }

    /// <summary>Rend au générateur les cartes sorties de l'écran.</summary>
    private void Oublier(int premier, int dernier)
    {
        var generateur = ItemContainerGenerator;

        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            int index = generateur.IndexFromGeneratorPosition(position);

            if (index >= premier && index <= dernier) continue;

            generateur.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void Vider()
    {
        if (InternalChildren.Count == 0) return;

        ItemContainerGenerator.RemoveAll();
        RemoveInternalChildRange(0, InternalChildren.Count);
    }

    // ----------------------------------------------------------- placement

    protected override Size ArrangeOverride(Size finale)
    {
        var generateur = ItemContainerGenerator;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            int index = generateur.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;

            int ligne   = index / _colonnes;
            int colonne = index % _colonnes;

            InternalChildren[i].Arrange(new Rect(
                colonne * LargeurCarte,
                ligne * HauteurCarte - _decalage.Y,
                LargeurCarte,
                HauteurCarte));
        }

        return finale;
    }

    // ---------------------------------------------------------- IScrollInfo

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; }

    public double ExtentWidth  => _etendue.Width;
    public double ExtentHeight => _etendue.Height;

    public double ViewportWidth  => _fenetre.Width;
    public double ViewportHeight => _fenetre.Height;

    public double HorizontalOffset => 0;
    public double VerticalOffset   => _decalage.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    private void Borner()
    {
        double max = Math.Max(0, _etendue.Height - _fenetre.Height);
        _decalage.Y = Math.Max(0, Math.Min(_decalage.Y, max));
    }

    public void SetVerticalOffset(double offset)
    {
        double max = Math.Max(0, _etendue.Height - _fenetre.Height);
        double vise = Math.Max(0, Math.Min(offset, max));

        if (Math.Abs(vise - _decalage.Y) < 0.5) return;

        _decalage.Y = vise;

        // Le décalage change la tranche visible : il faut re-mesurer, pas
        // seulement replacer, sinon on fait défiler du vide.
        InvalidateMeasure();
        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetHorizontalOffset(double offset) { }

    // Trois lignes de texte par cran de molette, comme partout ailleurs
    // dans Windows.
    private const double Pas = 48;

    public void LineUp()   => SetVerticalOffset(VerticalOffset - Pas);
    public void LineDown() => SetVerticalOffset(VerticalOffset + Pas);

    public void MouseWheelUp()   => SetVerticalOffset(VerticalOffset - Pas * 3);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Pas * 3);

    public void PageUp()   => SetVerticalOffset(VerticalOffset - _fenetre.Height);
    public void PageDown() => SetVerticalOffset(VerticalOffset + _fenetre.Height);

    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }

    public Rect MakeVisible(Visual visuel, Rect rectangle)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (!ReferenceEquals(InternalChildren[i], visuel)) continue;

            int index = ItemContainerGenerator.IndexFromGeneratorPosition(
                new GeneratorPosition(i, 0));

            if (index < 0) break;

            double haut = index / _colonnes * HauteurCarte;

            if (haut < _decalage.Y) SetVerticalOffset(haut);
            else if (haut + HauteurCarte > _decalage.Y + _fenetre.Height)
                SetVerticalOffset(haut + HauteurCarte - _fenetre.Height);

            break;
        }

        return rectangle;
    }
}
