using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kurogane.Barre;
using Kurogane.Theme;

namespace Kurogane.Explorateur;

/// <summary>
/// Explorateur Kurogane — navigation en lecture seule.
///
/// Rien n'écrit sur le disque : on liste, on ouvre avec l'application
/// système, et c'est tout. Créer, renommer et supprimer viendront plus tard,
/// avec les précautions que ça impose — corbeille plutôt que suppression
/// définitive, aucun écrasement silencieux.
///
/// L'affichage n'est volontairement pas virtualisé à ce stade. Le poser
/// d'emblée aurait été construire sur une supposition ; le chrono de la
/// barre d'état est là pour mesurer d'abord, décider ensuite.
/// </summary>
public partial class FenetreExplorateur : Window
{
    private readonly ThemeDefinition? _theme;
    private readonly ISourceNoeuds _source;

    private readonly List<string> _historique = new();
    private int _position = -1;

    private CancellationTokenSource? _encours;

    public FenetreExplorateur(ThemeDefinition? theme, ISourceNoeuds source, string depart)
    {
        _theme = theme;
        _source = source;

        InitializeComponent();

        BtnReduire.Click  += (_, _) => WindowState = WindowState.Minimized;
        BtnAgrandir.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnFermer.Click   += (_, _) => Close();

        BtnPrecedent.Click += (_, _) => Reculer();
        BtnSuivant.Click   += (_, _) => Avancer();
        BtnParent.Click    += (_, _) => Monter();

        SizeChanged += (_, _) => AjusterEncoche();

        // Le double-clic est écouté sur la liste et non sur chaque carte :
        // avec la virtualisation, les cartes vont et viennent, et des
        // abonnements posés à leur création fuiraient ou disparaîtraient.
        Cartes.PreviewMouseLeftButtonDown += async (_, e) =>
        {
            if (e.ClickCount < 2) return;
            if (Cible(e.OriginalSource) is not { } vue) return;

            e.Handled = true;
            await Ouvrir(vue.Source);
        };

        Cartes.SelectionChanged += (_, _) => Detailler();
        Cartes.PreviewKeyDown += Clavier;

        Puce.Text = $"PID:{Environment.ProcessId} // RING-3";

        RemplirRacines();

        Loaded += async (_, _) => await Naviguer(depart);
    }

    private void AjusterEncoche() =>
        Encoche.Width = Math.Max(120, ActualWidth * 0.29);

    // ---------------------------------------------------------------- volet

    private void RemplirRacines()
    {
        foreach (var racine in _source.Racines())
            Noeuds.Children.Add(Noeud(racine));
    }

    private UIElement Noeud(Racine racine)
    {
        var ligne = new StackPanel { Orientation = Orientation.Horizontal };

        var vignette = new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (Icone(racine.Icone + ".ico") is { } dessin)
        {
            var image = new Image { Source = dessin, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            vignette.Child = image;
        }
        else
        {
            // Icône pas encore dessinée : le carré d'origine, plutôt qu'un
            // vide qui décalerait l'alignement de la ligne.
            vignette.Child = new TextBlock
            {
                Text = "▱",
                FontSize = 12,
                Foreground = (Brush)FindResource("TexteEteint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        ligne.Children.Add(vignette);

        ligne.Children.Add(Mono(racine.Libelle, "TexteVif", 11));
        ligne.Children.Add(Mono(" // ", "RougeEteint", 11));
        ligne.Children.Add(Mono(racine.Detail, "TexteVif", 11));

        var noeud = new ContentControl
        {
            Style = (Style)FindResource("Noeud"),
            Content = ligne,
            Cursor = Cursors.Hand,
            Tag = racine.Chemin,
        };

        noeud.MouseLeftButtonUp += async (_, _) => await Naviguer(racine.Chemin);

        return noeud;
    }

    private TextBlock Mono(string texte, string pinceau, double taille) => new()
    {
        Text = texte,
        Foreground = (Brush)FindResource(pinceau),
        FontFamily = (FontFamily)FindResource("Mono"),
        FontSize = taille,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ----------------------------------------------------------- navigation

    private async Task Naviguer(string chemin, bool enregistrer = true)
    {
        if (enregistrer)
        {
            // Naviguer après un retour arrière coupe la branche suivante :
            // c'est le comportement attendu d'un historique de navigation.
            if (_position < _historique.Count - 1)
                _historique.RemoveRange(_position + 1, _historique.Count - _position - 1);

            _historique.Add(chemin);
            _position = _historique.Count - 1;
        }

        MajBoutons();
        Ariane(chemin);

        // Une navigation en remplace une autre : sans annulation, un dossier
        // lent finirait par écraser l'affichage d'un dossier demandé après.
        _encours?.Cancel();
        _encours = new CancellationTokenSource();
        var jeton = _encours.Token;

        Cartes.ItemsSource = null;
        Compte.Text = "LECTURE…";
        Erreurs.Text = "";
        Detail.Text = "—";
        Chrono.Text = "—";

        Listage listage;

        try
        {
            listage = await _source.Lister(chemin, jeton);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (jeton.IsCancellationRequested) return;

        var rendu = Stopwatch.StartNew();

        var vues = new List<CarteVue>(listage.Entrees.Count);

        foreach (var entree in listage.Entrees)
            vues.Add(new CarteVue
            {
                Source = entree,
                Nom    = entree.Nom,
                Meta   = entree.EstDossier ? "NOEUD" : Format(entree),
                Date   = entree.Modifie.ToString("yyyy.MM.dd"),
                Icone  = ChargerIcone(entree),
            });

        Cartes.ItemsSource = vues;

        // La mesure doit inclure la mise en page, sinon elle ne compte que
        // la construction de la liste et déclare un rendu instantané qui
        // ne l'est pas.
        Cartes.UpdateLayout();

        rendu.Stop();

        int noeuds   = listage.Entrees.Count(e => e.EstDossier);
        int fichiers = listage.Entrees.Count - noeuds;
        long poids   = listage.Entrees.Where(e => !e.EstDossier).Sum(e => e.Taille);

        Compte.Text  = $"{listage.Entrees.Count} OBJETS CHARGÉS";
        Erreurs.Text = $"{listage.Erreurs} ERREUR" + (listage.Erreurs > 1 ? "S" : "");
        _resume = $"{noeuds} NOEUDS // {fichiers} FICHIERS // {Poids(poids)}";
        Detail.Text = _resume;

        // Sans le focus, les flèches et Entrée ne parviennent pas à la
        // liste : l'utilisateur devrait cliquer une fois avant de pouvoir
        // se servir du clavier.
        Cartes.Focus();
        Chrono.Text  = $"LECTURE {listage.Millisecondes} MS // RENDU {rendu.ElapsedMilliseconds} MS";
    }

    /// <summary>
    /// Résumé du dossier, réaffiché quand plus rien n'est sélectionné.
    /// </summary>
    private string _resume = "—";

    /// <summary>
    /// La barre d'état parle de la sélection quand il y en a une, du dossier
    /// sinon. C'est l'endroit où l'utilisateur regarde ; y laisser une
    /// mention décorative serait du gâchis.
    /// </summary>
    private void Detailler()
    {
        var choix = Cartes.SelectedItems.OfType<CarteVue>().ToList();

        if (choix.Count == 0) { Detail.Text = _resume; return; }

        if (choix.Count == 1)
        {
            var e = choix[0].Source;

            Detail.Text = e.EstDossier
                ? $"SÉLECTION : {e.Nom} // NOEUD"
                : $"SÉLECTION : {e.Nom} // {Poids(e.Taille)} // {e.Modifie:yyyy.MM.dd HH:mm}";

            return;
        }

        long poids = choix.Where(c => !c.Source.EstDossier).Sum(c => c.Source.Taille);

        Detail.Text = $"SÉLECTION : {choix.Count} OBJETS // {Poids(poids)}";
    }

    /// <summary>
    /// Navigation en grille. Les touches par défaut d'une ListBox se
    /// déplacent d'un élément : dans une grille, Haut et Bas doivent sauter
    /// une ligne entière.
    /// </summary>
    private async void Clavier(object expediteur, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Cartes.SelectedItem is CarteVue vue)
            {
                e.Handled = true;
                await Ouvrir(vue.Source);
            }

            return;
        }

        if (e.Key == Key.Back) { e.Handled = true; Monter(); return; }

        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
        if (Cartes.Items.Count == 0) return;

        int colonnes = Grille?.Colonnes ?? 1;
        int actuel = Math.Max(0, Cartes.SelectedIndex);

        int vise = e.Key switch
        {
            Key.Left  => actuel - 1,
            Key.Right => actuel + 1,
            Key.Up    => actuel - colonnes,
            _         => actuel + colonnes,
        };

        if (vise < 0 || vise >= Cartes.Items.Count) { e.Handled = true; return; }

        e.Handled = true;
        Cartes.SelectedIndex = vise;
        Cartes.ScrollIntoView(Cartes.Items[vise]);
    }

    /// <summary>Le panneau de cartes, une fois le gabarit appliqué.</summary>
    private PanneauCartes? Grille =>
        _grille ??= Trouver<PanneauCartes>(Cartes);

    private PanneauCartes? _grille;

    private static T? Trouver<T>(DependencyObject racine) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(racine); i++)
        {
            var enfant = VisualTreeHelper.GetChild(racine, i);

            if (enfant is T trouve) return trouve;
            if (Trouver<T>(enfant) is { } profond) return profond;
        }

        return null;
    }

    private void Ariane(string chemin)
    {
        FilDAriane.Children.Clear();

        var segments = _source.Segments(chemin);

        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0)
                FilDAriane.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("Separateur"),
                });

            bool dernier = i == segments.Count - 1;

            FilDAriane.Children.Add(new TextBlock
            {
                Text = segments[i].ToUpperInvariant(),
                Style = (Style)FindResource("Segment"),
                Foreground = (Brush)FindResource(dernier ? "Rouge" : "TexteVif"),
            });
        }
    }

    private void MajBoutons()
    {
        BtnPrecedent.IsEnabled = _position > 0;
        BtnSuivant.IsEnabled   = _position >= 0 && _position < _historique.Count - 1;
        BtnParent.IsEnabled    = _position >= 0 && _source.Parent(_historique[_position]) is not null;
    }

    private async void Reculer()
    {
        if (_position <= 0) return;

        _position--;
        await Naviguer(_historique[_position], enregistrer: false);
    }

    private async void Avancer()
    {
        if (_position >= _historique.Count - 1) return;

        _position++;
        await Naviguer(_historique[_position], enregistrer: false);
    }

    private async void Monter()
    {
        if (_position < 0) return;
        if (_source.Parent(_historique[_position]) is not { } parent) return;

        await Naviguer(parent);
    }

    // --------------------------------------------------------------- cartes

    /// <summary>
    /// Remonte l'arbre visuel depuis l'élément cliqué jusqu'à la carte qui
    /// le contient.
    /// </summary>
    private static CarteVue? Cible(object origine)
    {
        var noeud = origine as DependencyObject;

        while (noeud is not null)
        {
            if (noeud is FrameworkElement element && element.DataContext is CarteVue vue)
                return vue;

            noeud = VisualTreeHelper.GetParent(noeud);
        }

        return null;
    }

    /// <summary>
    /// Un dossier se parcourt ici ; un fichier s'ouvre avec l'application
    /// que Windows lui associe. L'explorateur ne prétend pas afficher des
    /// contenus qu'il ne sait pas lire.
    /// </summary>
    private async Task Ouvrir(Entree entree)
    {
        if (entree.EstDossier)
        {
            await Naviguer(entree.Chemin);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(entree.Chemin) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Chrono.Text = $"OUVERTURE REFUSÉE : {ex.GetType().Name}";
        }
    }

    private static string Format(Entree entree)
    {
        string ext = entree.Extension;
        string taille = Poids(entree.Taille);

        return ext.Length == 0 ? taille : $"{ext.ToUpperInvariant()} {taille}";
    }

    private static string Poids(long octets) => octets switch
    {
        < 1024          => $"{octets} O",
        < 1024 * 1024   => $"{octets / 1024.0:0.#} KO",
        < 1024L * 1024 * 1024 => $"{octets / (1024.0 * 1024):0.#} MO",
        _               => $"{octets / (1024.0 * 1024 * 1024):0.#} GO",
    };

    // --------------------------------------------------------------- icônes

    /// <summary>
    /// Icône du pack Kurogane selon l'extension. Le cache rend l'opération
    /// quasi gratuite : une vingtaine d'icônes distinctes couvrent un dossier
    /// entier, quel que soit le nombre de fichiers.
    /// </summary>
    private BitmapSource? ChargerIcone(Entree entree)
    {
        if (_theme?.Icons is null) return null;

        // Repli sur l'icône générique si celle de la famille n'a pas encore
        // été dessinée : une case vide ne dit rien à l'utilisateur. Le
        // manque, lui, est signalé là où il doit l'être — par probe-pack,
        // pas par un trou dans l'affichage.
        return Icone(TypesFichiers.Pour(entree) + ".ico")
            ?? Icone(TypesFichiers.Generique + ".ico");
    }

    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private BitmapSource? Icone(string fichier)
    {
        // Sans ce cache, un dossier de mille entrées relirait le même .ico
        // mille fois depuis le disque.
        if (_cache.TryGetValue(fichier, out var connue)) return connue;

        BitmapSource? image = null;

        try
        {
            string chemin = _theme!.ResolveIcon(fichier);

            if (File.Exists(chemin))
            {
                using var flux = File.OpenRead(chemin);

                var decodeur = new IconBitmapDecoder(flux,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                int ecart = int.MaxValue;

                foreach (var frame in decodeur.Frames)
                {
                    int d = Math.Abs(frame.PixelWidth - 32);
                    if (d >= ecart) continue;

                    ecart = d;
                    image = frame;
                }
            }
        }
        catch
        {
            // Une icône illisible laisse une vignette vide, jamais une
            // exception qui ferait tomber la fenêtre.
        }

        _cache[fichier] = image;
        return image;
    }
}
