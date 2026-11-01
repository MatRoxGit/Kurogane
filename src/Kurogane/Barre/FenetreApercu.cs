using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kurogane.Barre;

/// <summary>
/// La vignette d'aperçu affichée au survol d'une fenêtre.
///
/// Construite en code plutôt qu'en XAML : elle n'a qu'un cadre et un titre,
/// et son contenu réel est dessiné par le compositeur, pas par WPF.
///
/// Elle n'utilise PAS AllowsTransparency : une fenêtre en transparence par
/// couche est composée hors du chemin DWM, et la miniature n'y apparaîtrait
/// jamais. D'où un fond obsidienne opaque et un cadre rouge, ce qui va de
/// toute façon mieux au thème qu'un fond translucide.
/// </summary>
internal sealed class FenetreApercu : Window
{
    private const int Largeur = 260;
    private const int Marge   = 6;
    private const int Entete  = 22;

    private IntPtr _miniature;
    private readonly TextBlock _titre;

    internal FenetreApercu()
    {
        WindowStyle         = WindowStyle.None;
        ResizeMode          = ResizeMode.NoResize;
        ShowInTaskbar       = false;
        Topmost             = true;
        ShowActivated       = false;
        Background          = new SolidColorBrush(Color.FromRgb(0x0B, 0x07, 0x08));
        BorderThickness     = new Thickness(0);
        SnapsToDevicePixels = true;

        _titre = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x10, 0x2E)),
            FontFamily = new FontFamily("JetBrains Mono, Consolas, Courier New"),
            FontSize   = 10,
            Margin     = new Thickness(Marge, 4, Marge, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Content = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0xC8, 0x10, 0x2E)),
            BorderThickness = new Thickness(1),
            Child           = new StackPanel { Children = { _titre } },
        };
    }

    private IntPtr Descripteur => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// Affiche l'aperçu d'une fenêtre, centré au-dessus du point donné.
    /// <paramref name="ancreX"/> et <paramref name="plafond"/> sont en
    /// pixels réels.
    /// </summary>
    internal void Montrer(IntPtr source, string titre, int ancreX, int plafond)
    {
        _titre.Text = titre;

        Retirer();
        Show();

        _miniature = ApercuNative.Inscrire(Descripteur, source);
        if (_miniature == IntPtr.Zero) { Hide(); return; }

        var taille = ApercuNative.TailleSource(_miniature);
        if (taille.Largeur <= 0 || taille.Hauteur <= 0) { Retirer(); Hide(); return; }

        int interne = Largeur - 2 * Marge;
        int hauteurVignette = Math.Max(40, interne * taille.Hauteur / taille.Largeur);
        int hauteur = Entete + hauteurVignette + Marge;

        PositionNative.Placer(Descripteur,
            Math.Max(0, ancreX - Largeur / 2), plafond - hauteur, Largeur, hauteur);

        ApercuNative.Placer(_miniature,
            Marge, Entete, Marge + interne, Entete + hauteurVignette);
    }

    internal void Cacher()
    {
        Retirer();
        Hide();
    }

    private void Retirer()
    {
        ApercuNative.Retirer(_miniature);
        _miniature = IntPtr.Zero;
    }
}
