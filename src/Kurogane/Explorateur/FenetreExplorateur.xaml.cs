using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    /// <summary>
    /// Ouvrir d'un seul clic plutôt que de deux. Réglage du thème : les deux
    /// habitudes existent, et imposer l'une des deux serait arbitraire.
    /// </summary>
    private readonly bool _simpleClic;

    private readonly List<string> _historique = new();
    private int _position = -1;

    private CancellationTokenSource? _encours;

    public FenetreExplorateur(ThemeDefinition? theme, ISourceNoeuds source, string depart)
    {
        _theme = theme;
        _source = source;
        _simpleClic = theme?.Explorateur?.OuvertureSimpleClic ?? false;

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
            if (_simpleClic || e.ClickCount < 2) return;
            if (Cible(e.OriginalSource) is not { } vue) return;

            e.Handled = true;
            await Ouvrir(vue.Source);
        };

        // En mode simple clic, l'ouverture se fait au relâchement et non à
        // l'enfoncement : ouvrir sous le doigt avant qu'il se lève surprend,
        // et empêche d'annuler en glissant hors de la carte.
        Cartes.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (!_simpleClic) return;

            // Ctrl et Maj restent réservés à la sélection : sans ça, le mode
            // simple clic rendrait toute sélection multiple impossible.
            if (Keyboard.Modifiers is not ModifierKeys.None) return;
            if (Cible(e.OriginalSource) is not { } vue) return;

            e.Handled = true;
            await Ouvrir(vue.Source);
        };

        Cartes.SelectionChanged += (_, _) => Detailler();
        Cartes.PreviewKeyDown += Clavier;

        Filtre.TextChanged += (_, _) => Appliquer();

        foreach (var (bouton, critere) in new (RadioButton, Critere)[]
                 {
                     (TriNom,    Critere.Nom),
                     (TriTaille, Critere.Taille),
                     (TriDate,   Critere.Date),
                     (TriType,   Critere.Type),
                 })
        {
            var choisi = critere;
            bouton.Checked += (_, _) => { _critere = choisi; Appliquer(); Ordonnancer(); };
        }

        BtnSens.Click += (_, _) =>
        {
            _croissant = !_croissant;
            BtnSens.Content = _croissant ? "▲" : "▼";
            Appliquer();
            Ordonnancer();
        };

        // Un clic n'importe où dans la boîte donne le focus au champ, pas
        // seulement sur les quelques pixels qu'occupe le texte.
        BoiteFiltre.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Filtre.IsKeyboardFocusWithin) return;

            e.Handled = true;
            Filtre.Focus();
            Filtre.CaretIndex = Filtre.Text.Length;
        };

        Filtre.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            e.Handled = true;
            Filtre.Clear();
            Cartes.Focus();
        };

        // Ctrl+F depuis n'importe où amène au filtre, comme partout.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.F
                || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            e.Handled = true;
            Filtre.Focus();
            Filtre.SelectAll();
        };

        Puce.Text = $"PID:{Environment.ProcessId} // RING-3";

        Arbre.AddHandler(TreeViewItem.ExpandedEvent, new RoutedEventHandler(SurExpansion));

        Arbre.SelectedItemChanged += async (_, e) =>
        {
            if (e.NewValue is not NoeudArbre noeud) return;
            if (noeud.EstFictif) return;
            if (_position >= 0 && _historique[_position] == noeud.Chemin) return;

            // Pas de vol de focus : qui clique dans l'arbre veut continuer
            // au clavier dans l'arbre. Le focus suit l'endroit où l'on agit.
            await Naviguer(noeud.Chemin, rendreFocus: false);
        };

        RemplirRacines();

        Loaded += async (_, _) => await Naviguer(depart);
    }

    private void AjusterEncoche() =>
        Encoche.Width = Math.Max(120, ActualWidth * 0.29);

    // ---------------------------------------------------------------- volet

    private void RemplirRacines()
    {
        foreach (var racine in _source.Racines())
            Arbre.Items.Add(Brancher(racine.Libelle, racine.Detail,
                racine.Chemin, racine.Icone));
    }

    private NoeudArbre Brancher(string libelle, string detail, string chemin, string icone)
    {
        var noeud = new NoeudArbre
        {
            Libelle = libelle,
            Detail  = detail,
            Chemin  = chemin,
            Icone   = Icone(icone + ".ico"),
        };

        noeud.Enfants.Add(NoeudArbre.Fictif(chemin));

        return noeud;
    }

    /// <summary>
    /// Lit le contenu d'une branche au moment où on l'ouvre.
    ///
    /// L'événement remonte depuis le nœud déplié, donc un seul abonnement
    /// sur l'arbre suffit — en poser un par nœud fuirait au fil des
    /// dépliages.
    /// </summary>
    private async void SurExpansion(object expediteur, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TreeViewItem item) return;
        if (item.DataContext is not NoeudArbre noeud) return;
        if (noeud.Charge) return;

        noeud.Charge = true;

        IReadOnlyList<Entree> sous;

        try
        {
            sous = await _source.Dossiers(noeud.Chemin, CancellationToken.None);
        }
        catch (Exception)
        {
            noeud.Enfants.Clear();
            return;
        }

        noeud.Enfants.Clear();

        foreach (var d in sous)
            noeud.Enfants.Add(Brancher("DOSSIER", d.Nom.ToUpperInvariant(),
                d.Chemin, "dossier"));
    }

    // ----------------------------------------------------------- navigation

    private async Task Naviguer(string chemin, bool enregistrer = true,
        bool rendreFocus = true)
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
        _toutes = new List<CarteVue>();

        // Un filtre hérité du dossier précédent masquerait le nouveau sans
        // que rien ne l'explique.
        if (Filtre.Text.Length > 0) Filtre.Clear();
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

        _toutes = vues;
        Appliquer();

        // La mesure doit inclure la mise en page, sinon elle ne compte que
        // la construction de la liste et déclare un rendu instantané qui
        // ne l'est pas.
        Cartes.UpdateLayout();

        rendu.Stop();

        int noeuds   = listage.Entrees.Count(e => e.EstDossier);
        int fichiers = listage.Entrees.Count - noeuds;
        long poids   = listage.Entrees.Where(e => !e.EstDossier).Sum(e => e.Taille);

        Erreurs.Text = $"{listage.Erreurs} ERREUR" + (listage.Erreurs > 1 ? "S" : "");
        _resume = $"{noeuds} NOEUDS // {fichiers} FICHIERS // {Poids(poids)}";
        Detail.Text = _resume;

        // Le focus part à la liste, pour que les flèches et Entrée servent
        // sans un clic préalable.
        //
        // Différé à la priorité Input : quand la navigation vient d'un clic
        // dans l'arbre, le TreeViewItem prend le focus APRÈS l'événement de
        // sélection. L'appeler tout de suite le ferait reprendre aussitôt.
        // Rejet explicite : BeginInvoke renvoie une opération attendable, et
        // on ne veut justement pas l'attendre — la navigation est finie, le
        // focus se pose quand le clic en cours aura fini de se propager.
        if (rendreFocus)
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Cartes.Focus());
        Chrono.Text  = $"LECTURE {listage.Millisecondes} MS // RENDU {rendu.ElapsedMilliseconds} MS";
    }

    /// <summary>
    /// Résumé du dossier, réaffiché quand plus rien n'est sélectionné.
    /// </summary>
    private string _resume = "—";

    /// <summary>Libellé du sens de tri, adapté au critère courant.</summary>
    private void Ordonnancer() => Ordre.Text = (_critere, _croissant) switch
    {
        (Critere.Taille, true)  => "PETIT → GRAND",
        (Critere.Taille, false) => "GRAND → PETIT",
        (Critere.Date, true)    => "ANCIEN → RÉCENT",
        (Critere.Date, false)   => "RÉCENT → ANCIEN",
        (_, true)               => "A → Z",
        _                       => "Z → A",
    };

    private enum Critere { Nom, Taille, Date, Type }

    private Critere _critere = Critere.Nom;
    private bool _croissant = true;

    /// <summary>
    /// Trie, dossiers en tête quelle que soit la clé.
    ///
    /// Mélanger dossiers et fichiers dans un tri par taille mettrait les
    /// dossiers — de taille nulle ici — tous au même bout, ce qui n'apprend
    /// rien. Les séparer est aussi ce que fait tout gestionnaire de
    /// fichiers, et déroger à cette habitude sans raison coûterait plus
    /// qu'elle ne rapporte.
    /// </summary>
    private IEnumerable<CarteVue> Trier(IEnumerable<CarteVue> source)
    {
        var casse = StringComparer.OrdinalIgnoreCase;

        Func<CarteVue, IComparable> cle = _critere switch
        {
            Critere.Taille => c => c.Source.Taille,
            Critere.Date   => c => c.Source.Modifie,
            Critere.Type   => c => c.Source.Extension,
            _              => c => c.Nom,
        };

        var ordonne = source
            .OrderBy(c => c.Source.EstDossier ? 0 : 1);

        // Le nom départage toujours : deux fichiers de même taille ou de
        // même date s'afficheraient sinon dans un ordre instable d'une
        // lecture à l'autre.
        ordonne = _croissant
            ? ordonne.ThenBy(cle).ThenBy(c => c.Nom, casse)
            : ordonne.ThenByDescending(cle).ThenBy(c => c.Nom, casse);

        return ordonne;
    }

    /// <summary>
    /// Le dossier entier, indépendamment du filtre. Filtrer ne relit jamais
    /// le disque : la liste est déjà là, et un aller-retour par frappe
    /// rendrait le champ inutilisable sur un dossier lent.
    /// </summary>
    private List<CarteVue> _toutes = new();

    /// <summary>
    /// Applique le filtre courant. Deux formes reconnues : « *.ext » ne
    /// garde que cette extension, tout le reste est cherché dans le nom,
    /// sans tenir compte de la casse ni de l'accentuation du motif.
    /// </summary>
    private void Appliquer()
    {
        string motif = Filtre.Text.Trim();

        Invite.Visibility = motif.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        IEnumerable<CarteVue> retenues = _toutes;

        if (motif.StartsWith("*.", StringComparison.Ordinal) && motif.Length > 2)
        {
            string ext = motif[2..].TrimEnd('_');

            retenues = _toutes.Where(c =>
                c.Source.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
        }
        else if (motif.Length > 0)
        {
            retenues = _toutes.Where(c =>
                c.Nom.Contains(motif, StringComparison.OrdinalIgnoreCase));
        }

        var visibles = Trier(retenues).ToList();

        Cartes.ItemsSource = visibles;

        Compte.Text = motif.Length == 0
            ? $"{_toutes.Count} OBJETS CHARGÉS"
            : $"{visibles.Count} / {_toutes.Count} FILTRÉS";
    }

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
