using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kurogane.Barre;

/// <summary>
/// Un cadre aux coins coupés : haut-droit et bas-gauche.
///
/// Pourquoi une classe plutôt qu'un Polygon en XAML : un polygone a des
/// coordonnées fixes. Posé dans un bouton dont la largeur dépend du texte,
/// il faudrait soit le redimensionner — et la coupe s'étirerait avec —,
/// soit recalculer les points à chaque mise en page. Ici le dessin est
/// refait à la taille réelle, et la coupe garde exactement la même valeur
/// quelle que soit la largeur.
///
/// Dérive de Border pour hériter de sa disposition : marge intérieure,
/// enfant, épaisseur de trait. Seul le rendu change.
/// </summary>
public sealed class Biseau : Border
{
    public static readonly DependencyProperty CoupeProperty =
        DependencyProperty.Register(nameof(Coupe), typeof(double), typeof(Biseau),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Longueur de la coupe, en unités de mise en page.</summary>
    public double Coupe
    {
        get => (double)GetValue(CoupeProperty);
        set => SetValue(CoupeProperty, value);
    }

    protected override void OnRender(DrawingContext contexte)
    {
        double l = ActualWidth, h = ActualHeight;
        if (l <= 0 || h <= 0) return;

        double trait = BorderThickness.Top;
        double demi  = trait / 2.0;

        // Le tracé passe au milieu du trait : sinon la moitié du contour
        // déborde et le biseau paraît plus épais que les côtés droits.
        double c = Math.Min(Coupe, Math.Min(l, h) / 2.0);

        var figure = new StreamGeometry();
        using (var dessin = figure.Open())
        {
            dessin.BeginFigure(new Point(demi, demi), isFilled: true, isClosed: true);
            dessin.LineTo(new Point(l - c - demi, demi),        true, false);
            dessin.LineTo(new Point(l - demi, c + demi),        true, false);
            dessin.LineTo(new Point(l - demi, h - demi),        true, false);
            dessin.LineTo(new Point(c + demi, h - demi),        true, false);
            dessin.LineTo(new Point(demi, h - c - demi),        true, false);
        }

        figure.Freeze();

        var stylo = trait > 0 && BorderBrush is not null
            ? new Pen(BorderBrush, trait)
            : null;

        contexte.DrawGeometry(Background, stylo, figure);
    }
}
