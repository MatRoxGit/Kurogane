using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kurogane.Barre;
using Kurogane.Theme;

namespace Kurogane.Explorateur;

/// <summary>
/// Coquille de l'explorateur Kurogane — étape 1.
///
/// Le contenu est figé : huit cartes, un chemin d'exemple, des compteurs
/// écrits en dur. Rien ne lit le disque et rien ne navigue. C'est délibéré :
/// on valide l'apparence avant d'investir dans le comportement, comme on
/// l'a fait pour la barre des tâches, où deux allers-retours sur le style
/// ont coûté bien moins cher qu'une refonte après coup.
///
/// Seules les icônes sont réelles, lues dans le dossier du thème. Un visuel
/// jugé sur des carrés gris ne dit pas grand-chose.
/// </summary>
public partial class FenetreExplorateur : Window
{
    private readonly ThemeDefinition? _theme;

    /// <summary>
    /// Les huit entrées de la maquette, avec les noms de fichiers réellement
    /// présents dans le pack. Un fichier manquant donne une carte vide, pas
    /// une exception.
    /// </summary>
    private static readonly (string Fichier, string Libelle)[] Contenu =
    {
        ("ce-pc.ico",            "ce-pc.ico"),
        ("dossier.ico",          "dossier.ico"),
        ("bureau.ico",           "bureau.ico"),
        ("raccourci-fleche.ico", "raccourci-fleche.ico"),
        ("corbeille-pleine.ico", "corbeille-pleine.ico"),
        ("documents.ico",        "documents.ico"),
        ("images.ico",           "images.ico"),
        ("lecteur.ico",          "lecteur.ico"),
    };

    public FenetreExplorateur(ThemeDefinition? theme)
    {
        _theme = theme;

        InitializeComponent();

        BtnReduire.Click  += (_, _) => WindowState = WindowState.Minimized;
        BtnAgrandir.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnFermer.Click   += (_, _) => Close();

        SizeChanged += (_, _) => AjusterEncoche();

        Remplir();
    }

    /// <summary>
    /// L'encoche du rail suit la largeur de la fenêtre. Une largeur fixe
    /// paraîtrait juste à 1040 px et fausse partout ailleurs — la barre des
    /// tâches nous a déjà appris à ne rien figer en pixels.
    /// </summary>
    private void AjusterEncoche() =>
        Encoche.Width = Math.Max(120, ActualWidth * 0.29);

    private void Remplir()
    {
        foreach (var (fichier, libelle) in Contenu)
            Cartes.Items.Add(Carte(fichier, libelle));

        Compte.Text = $"{Contenu.Length} OBJETS CHARGÉS";
    }

    private UIElement Carte(string fichier, string libelle)
    {
        var contenu = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var vignette = new Border
        {
            Width = 34,
            Height = 34,
            Margin = new Thickness(0, 0, 12, 0),
            Background = (Brush)FindResource("Creux"),
            BorderBrush = (Brush)FindResource("RougeEteint"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (ChargerIcone(fichier) is { } source)
        {
            vignette.Child = new Image
            {
                Source = source,
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            RenderOptions.SetBitmapScalingMode(vignette.Child, BitmapScalingMode.HighQuality);
        }

        contenu.Children.Add(vignette);

        var texte = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        texte.Children.Add(new TextBlock
        {
            Text = libelle,
            Foreground = (Brush)FindResource("TexteVif"),
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150,
        });

        var etat = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };

        etat.Children.Add(Petit("32-BIT", "TexteEteint"));
        etat.Children.Add(Petit(" // ",   "Rouge"));
        etat.Children.Add(Petit("OK",     "TexteEteint"));

        texte.Children.Add(etat);
        contenu.Children.Add(texte);

        return new Biseau
        {
            Coupe = 8,
            Width = 236,
            Height = 62,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12, 0, 12, 0),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = (Brush)FindResource("Soudure"),
            BorderThickness = new Thickness(1),
            Child = contenu,
        };
    }

    private TextBlock Petit(string texte, string pinceau) => new()
    {
        Text = texte,
        Foreground = (Brush)FindResource(pinceau),
        FontFamily = (FontFamily)FindResource("Mono"),
        FontSize = 9.5,
    };

    /// <summary>
    /// Charge une icône du pack. Un .ico contient plusieurs tailles : on
    /// prend la plus proche de 32 px plutôt que la première venue, qui peut
    /// être un 16 px tout flou une fois agrandi.
    /// </summary>
    private BitmapSource? ChargerIcone(string fichier)
    {
        if (_theme?.Icons is null) return null;

        try
        {
            string chemin = _theme.ResolveIcon(fichier);
            if (!File.Exists(chemin)) return null;

            using var flux = File.OpenRead(chemin);

            var decodeur = new IconBitmapDecoder(flux,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            BitmapFrame? meilleur = null;
            int ecart = int.MaxValue;

            foreach (var image in decodeur.Frames)
            {
                int d = Math.Abs(image.PixelWidth - 32);
                if (d >= ecart) continue;

                ecart = d;
                meilleur = image;
            }

            return meilleur;
        }
        catch
        {
            // Une icône illisible ne doit pas empêcher la fenêtre de
            // s'ouvrir : la carte restera simplement vide.
            return null;
        }
    }
}
