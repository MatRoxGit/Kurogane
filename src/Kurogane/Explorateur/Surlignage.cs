using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Kurogane.Explorateur;

/// <summary>
/// Montre, dans un nom, la partie qui répond au motif.
///
/// Un filtre qui retient « aadWamExtension.dll » pour « me » a raison —
/// « aadWa<b>mE</b>xtension » — mais l'utilisateur ne peut pas le vérifier :
/// la correspondance est au milieu d'un nom que la carte tronque. Le
/// résultat a l'air arbitraire, et un outil qui a l'air arbitraire n'est
/// pas utilisable même quand il est juste.
///
/// Deux gestes, donc : souligner la correspondance, et décaler le nom pour
/// qu'elle entre dans la largeur disponible.
///
/// Propriété attachée plutôt que liaison directe : un TextBlock coloré par
/// morceaux se construit en Inlines, qui ne se lient pas. Le travail n'a
/// lieu que sur les cartes réellement affichées — le panneau virtualise.
/// </summary>
public static class Surlignage
{
    /// <summary>
    /// Au-delà de cette position, la correspondance risque de tomber hors
    /// du cadre : on coupe la tête du nom pour la ramener vers la gauche.
    /// Valeur en caractères, approximative par nature — la largeur réelle
    /// dépend de la police, mais le nom est en chasse fixe.
    /// </summary>
    private const int Confort = 16;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(Surlignage),
            new PropertyMetadata(null, Change));

    public static readonly DependencyProperty MotifProperty =
        DependencyProperty.RegisterAttached(
            "Motif", typeof(string), typeof(Surlignage),
            new PropertyMetadata(null, Change));

    public static string? GetSource(DependencyObject o) =>
        (string?)o.GetValue(SourceProperty);

    public static void SetSource(DependencyObject o, string? valeur) =>
        o.SetValue(SourceProperty, valeur);

    public static string? GetMotif(DependencyObject o) =>
        (string?)o.GetValue(MotifProperty);

    public static void SetMotif(DependencyObject o, string? valeur) =>
        o.SetValue(MotifProperty, valeur);

    private static void Change(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is TextBlock bloc) Rendre(bloc);
    }

    private static void Rendre(TextBlock bloc)
    {
        string texte = GetSource(bloc) ?? "";
        string motif = (GetMotif(bloc) ?? "").Trim();

        bloc.Inlines.Clear();

        var (debut, longueur) = Filtrage.Position(texte, motif);

        if (debut < 0 || longueur <= 0)
        {
            bloc.Inlines.Add(new Run(texte));
            return;
        }

        // Ramener la correspondance dans le champ de vision. La troncature
        // de WPF mange la fin ; c'est donc le début qu'on sacrifie.
        string visible = texte;
        int position = debut;

        if (debut > Confort)
        {
            int coupe = debut - 4;
            visible = "…" + texte[coupe..];
            position = debut - coupe + 1;
        }

        if (position > 0) bloc.Inlines.Add(new Run(visible[..position]));

        bloc.Inlines.Add(new Run(visible.Substring(position, longueur))
        {
            Foreground = Teinte(bloc),
            FontWeight = FontWeights.Bold,
        });

        int suite = position + longueur;

        if (suite < visible.Length) bloc.Inlines.Add(new Run(visible[suite..]));
    }

    /// <summary>
    /// La couleur de surlignage vient du thème chargé, pas d'une constante :
    /// une teinte écrite en dur ici survivrait à un changement de palette.
    /// </summary>
    private static Brush Teinte(FrameworkElement element) =>
        element.TryFindResource("Ambre") as Brush ?? Brushes.Orange;
}
