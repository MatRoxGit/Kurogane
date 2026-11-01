using System.Windows;
using System.Windows.Media;

namespace Kurogane.Barre;

/// <summary>
/// Une jauge segmentée, à la manière d'un indicateur d'instrument.
///
/// Pas de dégradé, pas d'animation de remplissage : des barreaux allumés ou
/// éteints. Une valeur continue affichée en pas discrets se lit mieux du
/// coin de l'œil, et correspond au langage de l'ensemble.
/// </summary>
public sealed class Jauge : FrameworkElement
{
    private static FrameworkPropertyMetadata Rendu(object defaut) =>
        new(defaut, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty ValeurProperty =
        DependencyProperty.Register(nameof(Valeur), typeof(double), typeof(Jauge), Rendu(0.0));

    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.Register(nameof(Segments), typeof(int), typeof(Jauge), Rendu(10));

    public static readonly DependencyProperty AllumeProperty =
        DependencyProperty.Register(nameof(Allume), typeof(Brush), typeof(Jauge), Rendu(null!));

    public static readonly DependencyProperty EteintProperty =
        DependencyProperty.Register(nameof(Eteint), typeof(Brush), typeof(Jauge), Rendu(null!));

    public static readonly DependencyProperty AlerteProperty =
        DependencyProperty.Register(nameof(Alerte), typeof(Brush), typeof(Jauge), Rendu(null!));

    /// <summary>De 0 à 1. Les valeurs hors bornes sont ramenées.</summary>
    public double Valeur
    {
        get => (double)GetValue(ValeurProperty);
        set => SetValue(ValeurProperty, value);
    }

    public int Segments
    {
        get => (int)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public Brush? Allume
    {
        get => (Brush?)GetValue(AllumeProperty);
        set => SetValue(AllumeProperty, value);
    }

    public Brush? Eteint
    {
        get => (Brush?)GetValue(EteintProperty);
        set => SetValue(EteintProperty, value);
    }

    /// <summary>Teinte des deux derniers barreaux, quand ils s'allument.</summary>
    public Brush? Alerte
    {
        get => (Brush?)GetValue(AlerteProperty);
        set => SetValue(AlerteProperty, value);
    }

    protected override void OnRender(DrawingContext contexte)
    {
        int nombre = Math.Max(1, Segments);
        double l = ActualWidth, h = ActualHeight;
        if (l <= 0 || h <= 0) return;

        double espace = 1.0;
        double largeur = (l - espace * (nombre - 1)) / nombre;
        if (largeur <= 0) return;

        double valeur = Math.Clamp(Valeur, 0.0, 1.0);
        int allumes = (int)Math.Round(valeur * nombre);

        for (int i = 0; i < nombre; i++)
        {
            bool actif = i < allumes;
            bool critique = i >= nombre - 2;

            var brosse = actif
                ? (critique && Alerte is not null ? Alerte : Allume)
                : Eteint;

            if (brosse is null) continue;

            contexte.DrawRectangle(brosse, null,
                new Rect(i * (largeur + espace), 0, largeur, h));
        }
    }
}
