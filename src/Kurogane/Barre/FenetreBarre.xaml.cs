using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kurogane.Core;
using Kurogane.Theme;

namespace Kurogane.Barre;

public partial class FenetreBarre : Window
{
    private readonly BarreSection _config;
    private readonly ThemeDefinition _theme;
    private readonly Journal _journal;

    private readonly FenetreApercu _apercu = new();
    private readonly Telemetrie _mesures = new();
    private readonly Random _hasard = new();

    /// <summary>
    /// Boutons de fenêtre déjà affichés, indexés par fenêtre. On réconcilie
    /// plutôt que de reconstruire : rebâtir la liste chaque seconde ferait
    /// scintiller la barre et annulerait le survol en cours.
    /// </summary>
    private readonly Dictionary<IntPtr, Button> _boutons = new();

    private DispatcherTimer? _horloge, _veilleFenetres, _parasites, _veilleMesures;
    private bool _curseurVisible = true;
    private DispatcherTimer? _guetteurMenu;
    private bool _amorceAlternee;

    public FenetreBarre(ThemeDefinition theme, BarreSection config, Journal journal)
    {
        _theme   = theme;
        _config  = config;
        _journal = journal;

        InitializeComponent();

        BoutonDemarrer.Click  += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN);
        BoutonRecherche.Click += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN,
                                                                    AppBarNative.VK_S);

        MenuGestionnaire.Click += (_, _) => Lancer("taskmgr.exe", null);
        MenuReglages.Click     += (_, _) => Lancer("ms-settings:taskbar", null);
        MenuBureau.Click       += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN,
                                                                     AppBarNative.VK_D);
        MenuQuitter.Click      += (_, _) => Close();

        SurveillerMenu(MenuPrincipal);

        RemplirEpingles();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Hors Alt+Tab, et sans vol de focus : cliquer sur une barre des
            // tâches ne désactive jamais l'application en cours.
            AppBarNative.AjouterStyles(hwnd,
                AppBarNative.WS_EX_TOOLWINDOW | AppBarNative.WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) =>
        {
            Battre(); DemarrerMinuteur(ref _horloge, 500, Battre);
            Rafraichir(); DemarrerMinuteur(ref _veilleFenetres, 800, Rafraichir);
            Mesurer(); DemarrerMinuteur(ref _veilleMesures, 1000, Mesurer);
            DemarrerMinuteur(ref _parasites, 3000, Parasiter);
            AjusterLargeurs();
        };

        SizeChanged += (_, _) => AjusterLargeurs();

        Closed += (_, _) =>
        {
            foreach (var m in new[] { _horloge, _veilleFenetres, _veilleMesures,
                                      _parasites, _guetteurMenu })
                m?.Stop();

            _apercu.Close();
        };
    }

    private static void DemarrerMinuteur(ref DispatcherTimer? minuteur, int millisecondes,
                                         Action action)
    {
        minuteur = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(millisecondes) };
        minuteur.Tick += (_, _) => action();
        minuteur.Start();
    }

    // ------------------------------------------------------------------
    //  Menus contextuels
    // ------------------------------------------------------------------

    /// <summary>
    /// Ferme un menu quand on clique ailleurs.
    ///
    /// La fenêtre porte WS_EX_NOACTIVATE pour ne jamais voler le focus à
    /// l'application en cours. Conséquence : elle ne perd jamais
    /// l'activation, et WPF ne reçoit donc pas l'événement qui ferme
    /// normalement un menu contextuel. Il reste ouvert jusqu'à ce qu'on
    /// reclique dans la barre.
    ///
    /// On surveille donc nous-mêmes : à chaque pression de bouton hors du
    /// menu, on le referme. Ce n'est pas élégant, mais c'est la contrepartie
    /// assumée de ne pas voler le focus — et le vol de focus serait un
    /// défaut bien plus gênant.
    /// </summary>
    private void SurveillerMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) =>
        {
            _guetteurMenu?.Stop();
            _guetteurMenu = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(80),
            };

            _guetteurMenu.Tick += (_, _) =>
            {
                if (!menu.IsOpen) { _guetteurMenu?.Stop(); return; }
                if (!SourisEnfoncee()) return;

                // Un clic dans le menu lui appartient : on le laisse agir.
                if (menu.IsMouseOver) return;

                menu.IsOpen = false;
                _guetteurMenu?.Stop();
            };

            _guetteurMenu.Start();
        };

        menu.Closed += (_, _) => _guetteurMenu?.Stop();
    }

    private static bool SourisEnfoncee() =>
        Mouse.LeftButton == MouseButtonState.Pressed
     || Mouse.RightButton == MouseButtonState.Pressed
     || Mouse.MiddleButton == MouseButtonState.Pressed;

    // ------------------------------------------------------------------
    //  Adaptation à la largeur
    // ------------------------------------------------------------------

    private const double OngletLarge  = 200;
    private const double OngletEtroit = 44;
    private const double SeuilTexte   = 96;

    /// <summary>
    /// Répartit l'espace restant entre les onglets, et escamote les modules
    /// secondaires quand l'écran est trop étroit.
    ///
    /// C'est la même logique que la barre de Windows : les onglets
    /// rétrécissent jusqu'à ne plus montrer que l'icône, plutôt que de
    /// déborder. Sans ça, la barre est inutilisable dès qu'on change de
    /// résolution ou qu'on ouvre une dizaine de fenêtres.
    /// </summary>
    private void AjusterLargeurs()
    {
        double largeur = ActualWidth;

        // Modules secondaires : les premiers sacrifiés.
        BlocMesures.Visibility = largeur < 1100 ? Visibility.Collapsed : Visibility.Visible;
        BoutonRecherche.Visibility = largeur < 820 ? Visibility.Collapsed : Visibility.Visible;

        int nombre = Fenetres.Children.Count;
        if (nombre == 0) return;

        double disponible = ZoneFenetres.ActualWidth;
        if (disponible <= 0) return;

        double marges = 4;   // la marge horizontale du style Onglet
        double part = Math.Clamp(disponible / nombre - marges, OngletEtroit, OngletLarge);

        foreach (var enfant in Fenetres.Children)
        {
            if (enfant is not Button bouton) continue;

            bouton.Width = part;

            // Sous un certain seuil le titre ne tient plus : l'icône seule
            // reste lisible, un titre coupé à deux lettres ne l'est pas.
            if (bouton.Tag is TextBlock etiquette)
            {
                bool place = part >= SeuilTexte;
                etiquette.Visibility = place ? Visibility.Visible : Visibility.Collapsed;
                bouton.HorizontalContentAlignment = place
                    ? HorizontalAlignment.Left
                    : HorizontalAlignment.Center;
                bouton.Padding = place
                    ? new Thickness(12, 0, 14, 0)
                    : new Thickness(0);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Chronomètre
    // ------------------------------------------------------------------

    private void Battre()
    {
        var maintenant = DateTime.Now;

        Heure.Text = maintenant.ToString("HH:mm:ss");

        string suffixe = _config.SuffixeDate is { } s ? $" // {s}" : "";
        DateDuJour.Text = maintenant.ToString("yyyy.MM.dd") + suffixe;

        // Curseur de console : il clignote, sinon ce n'est qu'un trait.
        _curseurVisible = !_curseurVisible;
        Curseur.Opacity = _curseurVisible ? 1.0 : 0.0;

        // La veilleuse bat deux fois moins vite que le curseur : deux
        // clignotements synchrones se liraient comme un seul.
        Veilleuse.Opacity = maintenant.Second % 2 == 0 ? 1.0 : 0.35;
    }

    // ------------------------------------------------------------------
    //  Mesures
    // ------------------------------------------------------------------

    private void Mesurer()
    {
        double cpu = _mesures.Processeur();
        JaugeCpu.Valeur = cpu;
        ValeurCpu.Text  = $"{cpu * 100:0}%";

        var (part, utilise, total) = _mesures.Memoire();
        JaugeMemoire.Valeur = part;
        ValeurMemoire.Text  = $"{part * 100:0}%";

        JaugeMemoire.ToolTip = $"{utilise:0.0} Go sur {total:0.0} Go";

        // La seconde ligne du module d'amorçage alterne entre l'état déclaré
        // par le thème et le temps de fonctionnement : une ligne fixe ne dit
        // rien, une ligne qui change se lit.
        _amorceAlternee = !_amorceAlternee;

        if (_amorceAlternee && _config.Etat is { Length: > 0 } etat)
        {
            Amorce.Text = etat;
        }
        else
        {
            var duree = Telemetrie.Fonctionnement();
            Amorce.Text = $"UP+{(int)duree.TotalHours:00}:{duree.Minutes:00}:{duree.Seconds:00}";
        }
    }

    // ------------------------------------------------------------------
    //  Fenêtres ouvertes
    // ------------------------------------------------------------------

    private void Rafraichir()
    {
        var moi = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ouvertes = FenetresOuvertes.Lister(moi);
        var vues = new HashSet<IntPtr>();

        foreach (var f in ouvertes)
        {
            vues.Add(f.Hwnd);

            if (_boutons.TryGetValue(f.Hwnd, out var existant))
            {
                // Seul le titre change en cours de vie : un onglet, un
                // document. On met à jour sans recréer le bouton.
                if (existant.ToolTip as string != f.Titre)
                {
                    existant.ToolTip = f.Titre;
                    if (existant.Tag is TextBlock etiquette) etiquette.Text = Abreger(f.Titre);
                }
                continue;
            }

            var texte = new TextBlock
            {
                Text       = Abreger(f.Titre),
                Foreground = (Brush)FindResource("TexteVif"),
                FontFamily = (FontFamily)FindResource("Mono"),
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var pile = new StackPanel { Orientation = Orientation.Horizontal };

            if (f.Icone is not null)
                pile.Children.Add(new Image
                {
                    Source  = f.Icone,
                    Width   = 18,
                    Height  = 18,
                    Margin  = new Thickness(0, 0, 8, 0),
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                });

            pile.Children.Add(texte);

            var bouton = new Button
            {
                Style   = (Style)FindResource("Onglet"),
                Height  = 34,
                ToolTip = f.Titre,
                Content = pile,
                Tag     = texte,
            };

            // La largeur est fixée par AjusterLargeurs, qui connaît l'espace
            // disponible et le nombre d'onglets.
            

            var cible = f.Hwnd;
            bouton.Click      += (_, _) => FenetresOuvertes.Activer(cible);
            bouton.MouseEnter += (envoyeur, _) => MontrerApercu((Button)envoyeur, cible);
            bouton.MouseLeave += (_, _) => _apercu.Cacher();

            var fermer = new MenuItem
            {
                Style  = (Style)FindResource("EntreeMenu"),
                Header = "// FERMER LA FENÊTRE",
            };
            fermer.Click += (_, _) => FenetresOuvertes.Fermer(cible);

            var menu = new ContextMenu
            {
                Style = (Style)FindResource("MenuBarre"),
                Items = { fermer },
            };

            SurveillerMenu(menu);
            bouton.ContextMenu = menu;

            _boutons[f.Hwnd] = bouton;
            Fenetres.Children.Add(bouton);
        }

        foreach (var disparue in _boutons.Keys.Where(h => !vues.Contains(h)).ToList())
        {
            Fenetres.Children.Remove(_boutons[disparue]);
            _boutons.Remove(disparue);
        }

        Separateur.Visibility = _boutons.Count > 0 && Epingles.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        AjusterLargeurs();
    }

    private static string Abreger(string titre) =>
        titre.Length <= 22 ? titre : titre[..21] + "…";

    /// <summary>
    /// Place l'aperçu au-dessus du bouton. PointToScreen rend des pixels
    /// réels, ce qui évite toute conversion d'échelle.
    /// </summary>
    private void MontrerApercu(Button bouton, IntPtr cible)
    {
        try
        {
            var centre = bouton.PointToScreen(new Point(bouton.ActualWidth / 2, 0));
            var haut   = PointToScreen(new Point(0, 0));

            _apercu.Montrer(cible, bouton.ToolTip as string ?? "",
                            (int)centre.X, (int)haut.Y);
        }
        catch (InvalidOperationException)
        {
            // Bouton détaché de l'arbre visuel : la fenêtre vient de
            // disparaître, il n'y a plus rien à montrer.
        }
    }

    // ------------------------------------------------------------------
    //  Baies d'applications
    // ------------------------------------------------------------------

    private void RemplirEpingles()
    {
        if (_config.Epingles is not { Count: > 0 } liste) return;

        foreach (var e in liste)
        {
            if (e.Commande is not { Length: > 0 } commande) continue;

            var bouton = new Button
            {
                Style   = (Style)FindResource("Vignette"),
                Width   = 38,
                Height  = 40,
                ToolTip = e.Libelle ?? commande,
                Content = Visuel(e),
            };

            bouton.Click += (_, _) => Lancer(commande, e.Arguments);
            Epingles.Children.Add(bouton);
        }
    }

    /// <summary>
    /// Une icône si le thème en désigne une et qu'elle se charge, sinon le
    /// libellé. Un bouton sans contenu serait pire qu'un bouton textuel.
    /// </summary>
    private UIElement Visuel(EpingleBarre e)
    {
        if (e.Icone is { Length: > 0 } fichier)
        {
            try
            {
                string chemin = _theme.ResolveIcon(fichier);

                if (File.Exists(chemin))
                    return new Image
                    {
                        Source  = new BitmapImage(new Uri(chemin)),
                        Width   = 24,
                        Height  = 24,
                        Stretch = Stretch.Uniform,
                    };

                _journal.Warn($"épingle « {e.Libelle} » : {chemin} est introuvable");
            }
            catch (Exception ex)
            {
                _journal.Warn($"épingle « {e.Libelle} » : {ex.Message}");
            }
        }

        string libelle = e.Libelle ?? "?";

        return new TextBlock
        {
            Text       = libelle[..Math.Min(3, libelle.Length)].ToUpperInvariant(),
            Foreground = (Brush)FindResource("Rouge"),
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize   = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
        };
    }

    private void Lancer(string commande, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = commande,
                Arguments       = arguments ?? "",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _journal.Error($"lancement de « {commande} » : {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    //  Parasites
    // ------------------------------------------------------------------

    private static readonly string[] Brouillage =
        { "S¥S.ERR", "0xC8102E", "N0_S1GNAL", "SYS.B00T", "▓▒░ERR", "KRG//??" };

    /// <summary>
    /// Deux effets, lents et rares. Une barre qui scintille en permanence
    /// devient illisible au bout de dix minutes : le parasitage doit se
    /// remarquer du coin de l'œil, pas occuper le regard.
    /// </summary>
    private void Parasiter()
    {
        if (_hasard.Next(4) == 0) Brouiller();
        if (_hasard.Next(9) == 0) Balayer();
    }

    /// <summary>Brouillage bref de la ligne d'amorçage, par sauts.</summary>
    private void Brouiller()
    {
        string original = Amorce.Text;

        var clignotement = new DoubleAnimationUsingKeyFrames();
        foreach (var (instant, opacite) in new (double, double)[]
                 { (0.00, 0.2), (0.05, 1.0), (0.10, 0.4), (0.15, 1.0) })
            clignotement.KeyFrames.Add(new DiscreteDoubleKeyFrame(
                opacite, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(instant))));

        Amorce.BeginAnimation(OpacityProperty, clignotement);
        Amorce.Text = Brouillage[_hasard.Next(Brouillage.Length)];

        var retour = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        retour.Tick += (_, _) => { Amorce.Text = original; retour.Stop(); };
        retour.Start();
    }

    /// <summary>Faisceau qui traverse la barre une fois, lentement.</summary>
    private void Balayer()
    {
        double largeur = ActualWidth;
        if (largeur <= 0) return;

        Faisceau.Opacity = 1.0;

        var course = new DoubleAnimation(-200, largeur + 200,
            new Duration(TimeSpan.FromSeconds(1.4)));

        course.Completed += (_, _) => Faisceau.Opacity = 0.0;
        PositionFaisceau.BeginAnimation(TranslateTransform.XProperty, course);
    }
}
