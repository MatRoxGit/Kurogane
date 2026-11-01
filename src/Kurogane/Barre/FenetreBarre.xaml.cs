using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private DispatcherTimer? _horloge;
    private DispatcherTimer? _veilleFenetres;

    /// <summary>
    /// Boutons déjà affichés, indexés par fenêtre. On réconcilie plutôt que
    /// de reconstruire : rebâtir la liste chaque seconde ferait scintiller
    /// la barre et annulerait le survol en cours.
    /// </summary>
    private readonly Dictionary<IntPtr, Button> _boutons = new();

    public FenetreBarre(ThemeDefinition theme, BarreSection config, Journal journal)
    {
        _theme   = theme;
        _config  = config;
        _journal = journal;

        InitializeComponent();

        if (config.Noeud is { } n) Noeud.Text = n;
        if (config.Etat  is { } e) Etat.Text  = e;

        BoutonDemarrer.Click  += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN);
        BoutonRecherche.Click += (_, _) => AppBarNative.Combinaison(AppBarNative.VK_LWIN,
                                                                    AppBarNative.VK_S);

        RemplirEpingles();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

            // Hors Alt+Tab, et surtout sans vol de focus : cliquer sur une
            // barre des tâches ne désactive jamais l'application en cours.
            AppBarNative.AjouterStyles(hwnd,
                AppBarNative.WS_EX_TOOLWINDOW | AppBarNative.WS_EX_NOACTIVATE);
        };

        Loaded += (_, _) =>
        {
            DemarrerHorloge();
            DemarrerVeilleFenetres();
        };

        Closed += (_, _) =>
        {
            _horloge?.Stop();
            _veilleFenetres?.Stop();
        };
    }

    private void DemarrerHorloge()
    {
        void Battre()
        {
            var maintenant = DateTime.Now;
            Heure.Text = maintenant.ToString("HH:mm:ss");

            string suffixe = _config.SuffixeDate is { } s ? $" // {s}" : "";
            DateDuJour.Text = maintenant.ToString("yyyy.MM.dd") + suffixe;
        }

        Battre();

        _horloge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _horloge.Tick += (_, _) => Battre();
        _horloge.Start();
    }

    // ------------------------------------------------------------------
    //  Fenêtres ouvertes
    // ------------------------------------------------------------------

    private void DemarrerVeilleFenetres()
    {
        Rafraichir();

        _veilleFenetres = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _veilleFenetres.Tick += (_, _) => Rafraichir();
        _veilleFenetres.Start();
    }

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
                    if (existant.Content is StackPanel pile
                        && pile.Children.Count > 0
                        && pile.Children[^1] is TextBlock texte)
                        texte.Text = Abreger(f.Titre);
                }
                continue;
            }

            var bouton = new Button
            {
                Style   = (Style)FindResource("Cellule"),
                Height  = 38,
                Padding = new Thickness(8, 0, 8, 0),
                ToolTip = f.Titre,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = ContenuFenetre(f),
            };

            var cible = f.Hwnd;
            bouton.Click += (_, _) => FenetresOuvertes.Activer(cible);

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
    }

    private static string Abreger(string titre) =>
        titre.Length <= 22 ? titre : titre[..21] + "…";

    private object ContenuFenetre(FenetresOuvertes.Entree f)
    {
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

        pile.Children.Add(new TextBlock
        {
            Text       = Abreger(f.Titre),
            Foreground = (Brush)FindResource("TexteVif"),
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize   = 10,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return pile;
    }

    private void RemplirEpingles()
    {
        if (_config.Epingles is not { Count: > 0 } liste) return;

        foreach (var e in liste)
        {
            if (e.Commande is not { Length: > 0 } commande) continue;

            var bouton = new Button
            {
                Style   = (Style)FindResource("Cellule"),
                Width   = 42,
                Height  = 38,
                Padding = new Thickness(0),
                ToolTip = e.Libelle ?? commande,
                Content = Contenu(e),
            };

            bouton.Click += (_, _) => Lancer(commande, e.Arguments);
            Epingles.Children.Add(bouton);
        }
    }

    /// <summary>
    /// Une icône si le thème en désigne une et qu'elle se charge, sinon le
    /// libellé. Un bouton sans contenu serait pire qu'un bouton textuel.
    /// </summary>
    private object Contenu(EpingleBarre e)
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
                        Width   = 22,
                        Height  = 22,
                        Stretch = Stretch.Uniform,
                    };

                _journal.Warn($"épingle « {e.Libelle} » : {chemin} est introuvable");
            }
            catch (Exception ex)
            {
                _journal.Warn($"épingle « {e.Libelle} » : {ex.Message}");
            }
        }

        return new TextBlock
        {
            Text       = (e.Libelle ?? "?")[..Math.Min(3, (e.Libelle ?? "?").Length)].ToUpperInvariant(),
            Foreground = (Brush)FindResource("Rouge"),
            FontFamily = (FontFamily)FindResource("Mono"),
            FontSize   = 10,
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
}
