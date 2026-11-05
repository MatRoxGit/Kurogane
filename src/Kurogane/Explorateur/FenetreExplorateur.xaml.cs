using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kurogane.Barre;
using Kurogane.Native;
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
        BtnActualiser.Click += async (_, _) => await Rafraichir();

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

        Filtre.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Fouiller();
                return;
            }

            if (e.Key != Key.Escape) return;

            e.Handled = true;

            // Échap sort d'abord de la recherche, et seulement ensuite vide
            // le champ : deux états distincts, deux gestes distincts. Le
            // motif survit au retour, pour continuer à filtrer le dossier.
            if (_resultats)
            {
                Arreter();
                await Rafraichir();
                return;
            }

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

        // Les deux arbres se comportent pareil ; seule leur source diffère.
        foreach (var arbre in new[] { ArbreRapide, Arbre })
        {
            arbre.AddHandler(TreeViewItem.ExpandedEvent,
                             new RoutedEventHandler(SurExpansion));

            var courant = arbre;

            courant.SelectedItemChanged += async (_, e) =>
            {
                if (e.NewValue is not NoeudArbre noeud) return;
                if (noeud.EstFictif) return;

                // Deux arbres, une seule sélection visible : sans cela, la
                // ligne choisie dans l'autre resterait allumée et on ne
                // saurait plus où l'on est.
                Eteindre(courant == Arbre ? ArbreRapide : Arbre);

                if (_position >= 0 && _historique[_position] == noeud.Chemin) return;

                // Pas de vol de focus : qui clique dans l'arbre veut
                // continuer au clavier dans l'arbre. Le focus suit
                // l'endroit où l'on agit.
                await Naviguer(noeud.Chemin, rendreFocus: false);
            };
        }

        // Sans ce filtre, la fenêtre maximisée couvre le moniteur entier et
        // son bas passe sous la barre des tâches.
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(MaximisationNative.Filtrer);
        };

        _visibilite = new Visibilite(
            theme?.Explorateur?.AfficherCaches ?? true,
            theme?.Explorateur?.AfficherSysteme ?? true);

        VoirCaches.IsChecked  = _visibilite.Caches;
        VoirSysteme.IsChecked = _visibilite.Systeme;

        VoirCaches.Click  += async (_, _) => await Revoir();
        VoirSysteme.Click += async (_, _) => await Revoir();

        Actions();
        RemplirRacines();

        Loaded += async (_, _) => await Naviguer(depart);

        Closed += (_, _) =>
        {
            _apaisement?.Stop();
            _veilleur?.Dispose();
            _encours?.Cancel();
        };
    }

    private void AjusterEncoche() =>
        Encoche.Width = Math.Max(120, ActualWidth * 0.29);

    // -------------------------------------------------------------- actions

    /// <summary>
    /// Fenêtre propriétaire des boîtes de dialogue du shell. Sans elle,
    /// elles s'ouvrent détachées et peuvent passer derrière l'explorateur.
    /// </summary>
    private IntPtr Poignee => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    private string? Courant => _position >= 0 ? _historique[_position] : null;

    private List<string> Choisis() => Cartes.SelectedItems
        .OfType<CarteVue>()
        .Select(c => c.Chemin)
        .ToList();

    private void Actions()
    {
        // Le clic droit sélectionne ce qui est sous le curseur quand ça ne
        // l'est pas déjà. Sans ça, le menu agirait sur une sélection
        // invisible, faite ailleurs — la pire façon de supprimer un fichier.
        Cartes.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (Cible(e.OriginalSource) is not { } vue) return;
            if (Cartes.SelectedItems.OfType<CarteVue>().Contains(vue)) return;

            Cartes.SelectedItem = vue;
        };

        Menu.Opened += (_, _) => AjusterMenu();

        Cartes.PreviewMouseLeftButtonDown += DebutLasso;
        Cartes.PreviewMouseMove           += PendantLasso;
        Cartes.PreviewMouseLeftButtonUp   += FinLasso;

        MenuOuvrir.Click     += async (_, _) => await OuvrirSelection();
        MenuOuvrirAvec.Click += (_, _) => Premier(c =>
            FileOperationNative.OuvrirAvec(c, Poignee));
        MenuProprietes.Click += (_, _) => Premier(c =>
            FileOperationNative.Proprietes(c, Poignee));

        MenuCopier.Click += (_, _) => PressePapiers.Poser(Choisis(), couper: false);
        MenuCouper.Click += (_, _) => PressePapiers.Poser(Choisis(), couper: true);
        MenuColler.Click += async (_, _) => await Coller();

        MenuChemin.Click += (_, _) =>
        {
            var chemins = Choisis();
            if (chemins.Count == 0) return;

            try { Clipboard.SetText(string.Join(Environment.NewLine, chemins)); }
            catch (Exception ex) { Chrono.Text = $"PRESSE-PAPIERS : {ex.GetType().Name}"; }
        };

        MenuNouveauDossier.Click += async (_, _) => await NouveauDossier();
        MenuRenommer.Click += (_, _) => RenommerSelection();

        MenuPlusOptions.Click += async (_, _) =>
        {
            // Laisser notre menu se refermer d'abord : deux menus ouverts
            // en même temps se disputent la souris et le clavier.
            await System.Windows.Threading.Dispatcher.Yield(
                DispatcherPriority.Input);
            PlusOptions();
        };

        MenuSupprimer.Click += async (_, _) => await Supprimer(definitif: false);
        MenuEffacer.Click   += async (_, _) => await Supprimer(definitif: true);
    }

    /// <summary>
    /// Active ou désactive les entrées selon ce qui est sélectionné et ce
    /// que contient le presse-papiers. Un menu qui propose une action
    /// impossible fait perdre un clic et de la confiance.
    /// </summary>
    private void AjusterMenu()
    {
        int n = Cartes.SelectedItems.Count;
        bool un = n == 1;
        bool aumoins = n > 0;

        bool fichierUnique = un
            && Cartes.SelectedItem is CarteVue v && !v.Source.EstDossier;

        MenuOuvrir.IsEnabled     = un;
        MenuOuvrirAvec.IsEnabled = fichierUnique;
        MenuProprietes.IsEnabled = un;

        MenuCopier.IsEnabled    = aumoins;
        MenuCouper.IsEnabled    = aumoins;
        MenuChemin.IsEnabled    = aumoins;
        MenuSupprimer.IsEnabled = aumoins;
        MenuEffacer.IsEnabled   = aumoins;

        MenuColler.IsEnabled = Courant is not null && PressePapiers.Disponible();
        MenuRenommer.IsEnabled = un;
        MenuNouveauDossier.IsEnabled = Courant is not null;
    }

    private void Premier(Action<string> action)
    {
        if (Cartes.SelectedItem is not CarteVue vue) return;

        try { action(vue.Chemin); }
        catch (Exception ex) { Chrono.Text = $"ÉCHEC : {ex.GetType().Name}"; }
    }

    private async Task OuvrirSelection()
    {
        if (Cartes.SelectedItem is not CarteVue vue) return;
        await Ouvrir(vue.Source);
    }

    /// <summary>
    /// Supprime la sélection. Windows pose ses propres questions et affiche
    /// sa propre progression : Kurogane exécute une décision de
    /// l'utilisateur, il n'a pas à se montrer plus prudent que l'Explorateur.
    /// </summary>
    private async Task Supprimer(bool definitif)
    {
        var chemins = Choisis();
        if (chemins.Count == 0) return;

        var issue = definitif
            ? FileOperationNative.Effacer(chemins, Poignee)
            : FileOperationNative.Corbeille(chemins, Poignee);

        if (!issue.Silencieux)
            Chrono.Text = $"SUPPRESSION REFUSÉE // CODE {issue.Code}";

        await Rafraichir();
    }

    /// <summary>
    /// Crée un dossier dans le dossier courant, puis ouvre la saisie de
    /// son nom.
    ///
    /// Le nom suit la convention de Windows — « Nouveau dossier », puis
    /// « Nouveau dossier (2) » — plutôt que d'échouer sur une collision ou
    /// d'écraser quoi que ce soit.
    /// </summary>
    private async Task NouveauDossier()
    {
        if (Courant is not { } parent) return;

        string nom = "Nouveau dossier";
        string cible = Path.Combine(parent, nom);

        for (int n = 2; Directory.Exists(cible) || File.Exists(cible); n++)
        {
            nom = $"Nouveau dossier ({n})";
            cible = Path.Combine(parent, nom);

            // Garde-fou : un dossier saturé ne doit pas faire boucler sans fin.
            if (n > 999) { Chrono.Text = "CRÉATION ABANDONNÉE // TROP DE COLLISIONS"; return; }
        }

        try
        {
            Directory.CreateDirectory(cible);
        }
        catch (Exception ex)
        {
            Chrono.Text = $"CRÉATION REFUSÉE : {ex.GetType().Name}";
            return;
        }

        await Rafraichir();

        // Sélectionner le dossier créé : sans ça, il se perd dans la liste
        // et l'utilisateur doit le chercher.
        if (Cartes.ItemsSource is IEnumerable<CarteVue> vues
            && vues.FirstOrDefault(v => v.Chemin.Equals(cible,
                StringComparison.OrdinalIgnoreCase)) is { } neuf)
        {
            // Enchaîner sur la saisie du nom : un dossier qui s'appelle
            // « Nouveau dossier » n'est utile à personne, et c'est ce que
            // fait l'Explorateur.
            Editer(neuf);
        }
    }

    private async Task Coller()
    {
        if (Courant is not { } dossier) return;
        if (PressePapiers.Lire() is not { } contenu) return;

        var issue = contenu.Couper
            ? FileOperationNative.Deplacer(contenu.Chemins, dossier, Poignee)
            : FileOperationNative.Copier(contenu.Chemins, dossier, Poignee);

        if (!issue.Silencieux)
            Chrono.Text = $"COLLAGE REFUSÉ // CODE {issue.Code}";

        await Rafraichir();
    }

    /// <summary>
    /// Relit le dossier courant sans toucher à l'historique : une opération
    /// de fichier n'est pas une navigation.
    /// </summary>
    private async Task Rafraichir()
    {
        if (Courant is not { } chemin) return;

        // La sélection est retenue par chemin et non par objet : le listage
        // reconstruit des CarteVue neuves, et comparer les références ne
        // retrouverait rien.
        var retenus = Choisis().ToHashSet(StringComparer.OrdinalIgnoreCase);

        await Naviguer(chemin, enregistrer: false, rendreFocus: false);

        if (retenus.Count == 0) return;

        foreach (var vue in Cartes.Items.OfType<CarteVue>())
            if (retenus.Contains(vue.Chemin))
                Cartes.SelectedItems.Add(vue);
    }

    // --------------------------------------------------------- surveillance

    private FileSystemWatcher? _veilleur;
    private DispatcherTimer? _apaisement;

    /// <summary>
    /// Surveille le dossier affiché, pour qu'un changement venu d'ailleurs —
    /// l'Explorateur de Windows, un script, une décompression — apparaisse
    /// sans qu'on ait à actualiser.
    /// </summary>
    private void Surveiller(string chemin)
    {
        _veilleur?.Dispose();
        _veilleur = null;

        try
        {
            var veilleur = new FileSystemWatcher(chemin)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };

            // Les événements arrivent sur un fil du système : tout ce qui
            // touche à l'interface doit repasser par le répartiteur.
            void Signaler() => _ = Dispatcher.BeginInvoke(new Action(Differer));

            veilleur.Created += (_, _) => Signaler();
            veilleur.Deleted += (_, _) => Signaler();
            veilleur.Changed += (_, _) => Signaler();
            veilleur.Renamed += (_, _) => Signaler();

            // Débordement du tampon ou dossier disparu : relire est la seule
            // réponse utile, et dira ce qu'il en est.
            veilleur.Error += (_, _) => Signaler();

            veilleur.EnableRaisingEvents = true;
            _veilleur = veilleur;
        }
        catch (Exception)
        {
            // Certains emplacements ne se surveillent pas. L'explorateur
            // reste utilisable, simplement sans mise à jour automatique.
        }
    }

    /// <summary>
    /// Délai d'apaisement. Copier cent fichiers lève cent événements ;
    /// relire à chaque fois ferait clignoter la liste et gâcherait le
    /// travail que la virtualisation vient d'économiser.
    /// </summary>
    private void Differer()
    {
        if (_apaisement is null)
        {
            _apaisement = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };

            _apaisement.Tick += async (_, _) =>
            {
                // Une saisie en cours interdit de relire : le
                // rafraîchissement reconstruirait les cartes et effacerait
                // ce que l'utilisateur est en train de taper. On repousse.
                if (_edition is not null) return;

                // Des résultats de recherche ne sont pas le contenu du
                // dossier surveillé : les remplacer par lui serait un
                // écrasement, pas une mise à jour.
                if (_resultats) return;

                _apaisement!.Stop();
                await Rafraichir();
            };
        }

        _apaisement.Stop();
        _apaisement.Start();
    }

    // ------------------------------------------------------- visibilité

    /// <summary>
    /// Ce qu'on accepte de voir. L'état vit dans la fenêtre et non dans le
    /// thème : le thème donne le point de départ, l'utilisateur décide
    /// ensuite, et une bascule faite pour regarder un dossier n'a pas à
    /// réécrire un fichier de configuration.
    /// </summary>
    private Visibilite _visibilite = Visibilite.Defaut;

    /// <summary>Relit tout après un changement de visibilité.</summary>
    private async Task Revoir()
    {
        _visibilite = new Visibilite(VoirCaches.IsChecked == true,
                                     VoirSysteme.IsChecked == true);

        // L'arbre est reconstruit : ses branches déjà ouvertes ont été
        // lues sous l'ancienne règle, et les laisser serait montrer deux
        // vérités dans la même fenêtre. Les replier est le prix à payer.
        Arbre.Items.Clear();
        ArbreRapide.Items.Clear();
        RemplirRacines();

        await Rafraichir();
    }

    // -------------------------------------------------------- recherche

    /// <summary>
    /// Au-delà, on n'aide plus personne : une grille de cinq mille cartes
    /// ne se lit pas, et le tri à chaque battement finirait par coûter
    /// plus cher que le parcours lui-même.
    /// </summary>
    private const int MaximumResultats = 5000;

    private CancellationTokenSource? _fouille;

    /// <summary>Vrai quand la grille montre des résultats, pas un dossier.</summary>
    private bool _resultats;

    /// <summary>
    /// Fabrique la carte d'une entrée. Pendant une recherche, la ligne de
    /// détail porte l'emplacement plutôt que le poids : savoir OÙ est le
    /// fichier est précisément ce qu'on cherchait.
    /// </summary>
    private CarteVue Carte(Entree entree, string? racine = null)
    {
        string meta;

        if (racine is null)
        {
            meta = entree.EstDossier ? "NOEUD" : Format(entree);
        }
        else
        {
            string parent = Path.GetDirectoryName(entree.Chemin) ?? "";

            meta = parent.Length > racine.Length
                ? parent[(racine.TrimEnd('\\').Length + 1)..]
                : ".";
        }

        return new CarteVue
        {
            Source = entree,
            Nom    = entree.Nom,
            Meta   = meta,
            Date   = entree.Modifie.ToString("yyyy.MM.dd"),
            Icone  = ChargerIcone(entree),
            Attenue = entree.Cachee,
        };
    }

    /// <summary>Interrompt une recherche et rend la grille au dossier.</summary>
    private void Arreter()
    {
        _fouille?.Cancel();
        _fouille = null;
        _resultats = false;
    }

    /// <summary>
    /// Cherche récursivement depuis le dossier courant, avec le motif du
    /// champ. Les résultats s'affichent au fil du parcours : attendre la
    /// fin sur une arborescence profonde reviendrait à ne rien afficher.
    /// </summary>
    private async Task Fouiller()
    {
        if (Courant is not { } depart) return;

        string motif = Filtre.Text.Trim();
        if (motif.Length == 0) return;

        Arreter();

        // Une navigation en cours n'a plus lieu d'être : elle écraserait
        // les résultats en arrivant.
        _encours?.Cancel();

        var jeton = new CancellationTokenSource();
        _fouille = jeton;
        _resultats = true;

        _toutes = new List<CarteVue>();
        Cartes.ItemsSource = null;
        Erreurs.Text = "";
        Detail.Text = "—";

        var horloge = Stopwatch.StartNew();
        int dossiers = 0;
        bool sature = false;

        try
        {
            await foreach (var lot in RechercheRecursive.Courir(
                depart, motif, MaximumResultats, _visibilite, jeton.Token))
            {
                // Une autre recherche, ou une navigation, est passée devant.
                if (!ReferenceEquals(_fouille, jeton)) return;

                foreach (var entree in lot.Trouves) _toutes.Add(Carte(entree, depart));

                dossiers = lot.Dossiers;
                sature = lot.Sature;

                Appliquer();

                Chrono.Text = $"PARCOURS {dossiers} DOSSIERS // " +
                              $"{horloge.ElapsedMilliseconds} MS";
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé : ce qui a été trouvé reste affiché.
        }
        finally
        {
            horloge.Stop();

            if (ReferenceEquals(_fouille, jeton))
            {
                _fouille = null;
                jeton.Dispose();

                Appliquer();

                Chrono.Text = $"PARCOURS {dossiers} DOSSIERS // " +
                              $"{horloge.ElapsedMilliseconds} MS" +
                              (sature ? $" // ARRÊTÉ À {MaximumResultats}" : "");
            }
        }
    }

    // ------------------------------------------------------ menu Windows

    private EntreeShell? _choixShell;

    /// <summary>
    /// Ouvre le menu complet du shell, redessiné au thème.
    ///
    /// Notre menu ne prétend pas couvrir tous les verbes de Windows : les
    /// logiciels installés en ajoutent, et aucun inventaire écrit à
    /// l'avance ne les connaîtra. Le shell, lui, sait. On lui demande donc
    /// le menu qu'il aurait affiché — puis on le dessine nous-mêmes, pour
    /// qu'il ne tranche pas au milieu d'une fenêtre qui porte le thème.
    /// L'exécution lui revient : lui seul sait ce que fait chaque verbe.
    /// </summary>
    private void PlusOptions(bool auClavier = false)
    {
        IReadOnlyList<string> chemins = Choisis();

        // Rien de sélectionné : le menu porte sur le dossier courant.
        // Ce n'est pas tout à fait le menu du fond de fenêtre de
        // l'Explorateur — « Nouveau > » n'y figure pas — mais c'est le
        // menu réel du dossier.
        if (chemins.Count == 0 && Courant is { } parent)
            chemins = new[] { parent };

        if (chemins.Count == 0) return;

        // Maj enfoncé fait apparaître les verbes étendus, comme sous
        // Windows.
        bool etendu = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        var shell = MenuShellNative.Construire(chemins, etendu);

        if (shell is null)
        {
            Chrono.Text = "MENU WINDOWS INDISPONIBLE";
            return;
        }

        _choixShell = null;

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("MenuKurogane"),
        };

        // Au clavier il n'y a pas de curseur à suivre : le menu s'ancre
        // sous la carte sélectionnée, comme le fait Windows.
        if (auClavier
            && Cartes.SelectedItem is CarteVue ancre
            && Cartes.ItemContainerGenerator.ContainerFromItem(ancre)
               is ListBoxItem conteneur)
        {
            menu.PlacementTarget = conteneur;
            menu.Placement = System.Windows.Controls.Primitives
                                   .PlacementMode.Bottom;
        }
        else
        {
            menu.PlacementTarget = Cartes;
            menu.Placement = System.Windows.Controls.Primitives
                                   .PlacementMode.MousePoint;
        }

        Garnir(menu.Items, shell, shell.Racine());

        menu.Closed += async (_, _) =>
        {
            var choisi = _choixShell;
            _choixShell = null;

            // Exécuter après la fermeture : un verbe qui ouvre une boîte
            // de dialogue la verrait sinon s'afficher derrière un menu
            // encore ouvert.
            if (choisi is not null && !shell.Executer(choisi, Poignee))
                Chrono.Text = "COMMANDE WINDOWS REFUSÉE";

            shell.Dispose();

            // Le verbe a pu renommer, supprimer ou créer : on relit.
            if (choisi is not null) await Rafraichir();
        };

        menu.IsOpen = true;
    }

    /// <summary>
    /// Transpose les entrées lues chez le shell en entrées Kurogane.
    ///
    /// Les sous-menus sont garnis à l'ouverture, pas d'avance : le shell
    /// ne les remplit qu'au moment où il croit qu'on va les déplier, et
    /// tout déplier d'emblée coûterait à chaque clic droit ce que coûte
    /// « Envoyer vers ».
    /// </summary>
    private void Garnir(ItemCollection cible, MenuShellNative shell,
                        IReadOnlyList<EntreeShell> entrees)
    {
        foreach (var entree in entrees)
        {
            if (entree.Separateur)
            {
                cible.Add(new Separator
                {
                    Style = (Style)FindResource("SeparateurMenu"),
                });

                continue;
            }

            var element = new MenuItem
            {
                Style = (Style)FindResource("EntreeMenu"),

                // Majuscules comme le reste de l'interface ; le souligné
                // est doublé, sinon WPF le prend pour une touche d'accès.
                Header = entree.Libelle.ToUpper().Replace("_", "__"),
                InputGestureText = entree.Raccourci,
                IsEnabled = entree.Actif,
                IsChecked = entree.Coche,
            };

            if (entree.SousMenu != IntPtr.Zero)
            {
                // Marque-place : sans enfant, WPF n'affiche pas la flèche
                // et ne déclenche jamais l'ouverture.
                element.Items.Add(new MenuItem
                {
                    Style = (Style)FindResource("EntreeMenu"),
                    Header = "…",
                    IsEnabled = false,
                });

                element.SubmenuOpened += (expediteur, _) =>
                {
                    if (expediteur is not MenuItem parent) return;
                    if (parent.Tag is true) return;

                    parent.Tag = true;
                    parent.Items.Clear();
                    Garnir(parent.Items, shell, shell.Deplier(entree));
                };
            }
            else
            {
                element.Click += (_, _) => _choixShell = entree;
            }

            cible.Add(element);
        }
    }

    // -------------------------------------------------------------- édition

    private CarteVue? _edition;
    private TextBox? _champ;

    /// <summary>
    /// Ouvre la saisie du nom sur une carte.
    ///
    /// Le champ n'existe qu'une fois le conteneur matérialisé et le gabarit
    /// appliqué : le chercher tout de suite ne trouverait rien. D'où le
    /// report à la priorité Loaded.
    /// </summary>
    private void Editer(CarteVue vue)
    {
        Fermer();

        _edition = vue;
        vue.EnEdition = true;

        Cartes.SelectedItem = vue;
        Cartes.ScrollIntoView(vue);

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (Cartes.ItemContainerGenerator.ContainerFromItem(vue)
                is not ListBoxItem conteneur) return;

            if (Trouver<TextBox>(conteneur) is not { } champ) return;

            _champ = champ;
            champ.PreviewKeyDown    += ToucheEdition;
            champ.LostKeyboardFocus += PerteEdition;

            // Sélectionner le nom sans l'extension, comme le fait
            // l'Explorateur : renommer « rapport.pdf » porte presque
            // toujours sur « rapport », pas sur « .pdf ».
            string nom = vue.Saisie;
            int point = vue.Source.EstDossier ? -1 : nom.LastIndexOf('.');

            if (point > 0) champ.Select(0, point);
            else champ.SelectAll();

            // Le champ vient d'apparaître ; si la mise en page n'a pas
            // encore eu lieu, le focus est refusé. Une seconde tentative
            // après le traitement des entrées suffit.
            if (!champ.Focus())
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input,
                                           () => champ.Focus());
        });
    }

    /// <summary>Referme la saisie sans rien écrire.</summary>
    private void Fermer()
    {
        if (_champ is { } champ)
        {
            champ.PreviewKeyDown    -= ToucheEdition;
            champ.LostKeyboardFocus -= PerteEdition;
            _champ = null;
        }

        if (_edition is { } vue) vue.EnEdition = false;
        _edition = null;
    }

    private async void ToucheEdition(object expediteur, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Fermer();
            Cartes.Focus();
            return;
        }

        if (e.Key != Key.Enter) return;

        e.Handled = true;
        await Valider();
        Cartes.Focus();
    }

    private async void PerteEdition(object expediteur, KeyboardFocusChangedEventArgs e) =>
        await Valider();

    /// <summary>
    /// Applique le nom saisi. Le renommage passe par le shell comme le
    /// reste : il prévient les autres applications du changement, ce qu'un
    /// File.Move ne fait pas.
    /// </summary>
    private async Task Valider()
    {
        if (_edition is not { } vue) return;

        string voulu = vue.Saisie.Trim();
        var source = vue.Source;

        // Refermer avant d'agir : le rafraîchissement qui suit détruirait
        // le conteneur, et les abonnements avec. Le focus n'est pas touché
        // ici : quand la validation vient d'un clic ailleurs, le rendre à
        // la liste arracherait le focus de là où l'utilisateur vient de
        // cliquer.
        Fermer();

        if (voulu.Length == 0 || voulu == source.Nom) return;

        if (voulu.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Chrono.Text = "NOM INVALIDE // CARACTÈRES INTERDITS";
            return;
        }

        string parent = Path.GetDirectoryName(source.Chemin) ?? "";
        string cible = Path.Combine(parent, voulu);

        if (File.Exists(cible) || Directory.Exists(cible))
        {
            Chrono.Text = "NOM DÉJÀ PRIS";
            return;
        }

        var issue = FileOperationNative.Renommer(source.Chemin, cible, Poignee);

        if (!issue.Silencieux)
            Chrono.Text = $"RENOMMAGE REFUSÉ // CODE {issue.Code}";

        await Rafraichir();
    }

    private void RenommerSelection()
    {
        if (Cartes.SelectedItem is CarteVue vue) Editer(vue);
    }

    // ---------------------------------------------------------------- lasso

    private Point? _ancre;
    private bool _lassoActif;

    /// <summary>
    /// Sous ce seuil, le geste est un clic et non un tracé. Sans lui, le
    /// moindre tremblement pendant un clic démarrerait une sélection.
    /// </summary>
    private const double SeuilTrace = 5;

    private void DebutLasso(object expediteur, MouseButtonEventArgs e)
    {
        // Sur une carte : c'est la ListBox qui gère, pas nous.
        if (Cible(e.OriginalSource) is not null) return;

        // Clic dans le vide : on défait la sélection, ce qu'on attend de
        // n'importe quel gestionnaire de fichiers.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
            Cartes.SelectedItems.Clear();

        if (Grille is null) return;

        _ancre = e.GetPosition(Grille);
        _lassoActif = false;

        Cartes.Focus();
    }

    private void PendantLasso(object expediteur, MouseEventArgs e)
    {
        if (_ancre is not { } depart || Grille is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { Terminer(); return; }

        var ici = e.GetPosition(Grille);

        if (!_lassoActif)
        {
            if (Math.Abs(ici.X - depart.X) < SeuilTrace
                && Math.Abs(ici.Y - depart.Y) < SeuilTrace) return;

            _lassoActif = true;
            Bande.Visibility = Visibility.Visible;
            Cartes.CaptureMouse();
        }

        var zone = new Rect(depart, ici);

        // La bande se dessine dans les coordonnées du calque, la sélection
        // se calcule dans celles du panneau : les deux diffèrent de la
        // marge intérieure du défileur.
        var surCalque = Grille.TranslatePoint(new Point(zone.X, zone.Y), Calque);

        Canvas.SetLeft(Bande, surCalque.X);
        Canvas.SetTop(Bande, surCalque.Y);
        Bande.Width  = zone.Width;
        Bande.Height = zone.Height;

        var retenus = Grille.IndicesDans(zone);

        Cartes.SelectedItems.Clear();

        foreach (int i in retenus)
            if (i < Cartes.Items.Count)
                Cartes.SelectedItems.Add(Cartes.Items[i]);
    }

    private void FinLasso(object expediteur, MouseButtonEventArgs e)
    {
        if (_lassoActif) e.Handled = true;
        Terminer();
    }

    private void Terminer()
    {
        if (_lassoActif) Cartes.ReleaseMouseCapture();

        _ancre = null;
        _lassoActif = false;
        Bande.Visibility = Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- volet

    /// <summary>Le groupe des entrées épinglées, tenues par l'utilisateur.</summary>
    private const string GroupeRapide = "RAPIDE";

    private void RemplirRacines()
    {
        foreach (var racine in _source.Racines())
        {
            var noeud = Brancher(racine.Libelle, racine.Detail,
                                 racine.Chemin, racine.Icone);

            if (racine.Libelle == GroupeRapide) ArbreRapide.Items.Add(noeud);
            else Arbre.Items.Add(noeud);
        }

        // Sans épingle, la section disparaît : un titre au-dessus du vide
        // se lit comme une panne.
        bool rapide = ArbreRapide.Items.Count > 0;

        TitreRapide.Visibility = rapide ? Visibility.Visible : Visibility.Collapsed;
        ArbreRapide.Visibility = TitreRapide.Visibility;

        // Le premier titre porte la marge haute ; sans accès rapide, elle
        // revient à celui des nœuds système.
        TitreSysteme.Margin = rapide ? new Thickness(18, 18, 0, 10)
                                     : new Thickness(18, 16, 0, 10);
    }

    /// <summary>
    /// Éteint la sélection d'un arbre, à n'importe quelle profondeur. Un
    /// TreeView ne laisse pas écrire SelectedItem : il faut décocher le
    /// conteneur, et donc le retrouver.
    /// </summary>
    private static void Eteindre(ItemsControl arbre)
    {
        if (arbre is TreeView vue && vue.SelectedItem is null) return;

        foreach (object element in arbre.Items)
        {
            if (arbre.ItemContainerGenerator.ContainerFromItem(element)
                is not TreeViewItem conteneur) continue;

            if (conteneur.IsSelected) conteneur.IsSelected = false;
            if (conteneur.HasItems) Eteindre(conteneur);
        }
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
            sous = await _source.Dossiers(noeud.Chemin, _visibilite,
                                          CancellationToken.None);
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

        // Changer de dossier met fin à une recherche : ses résultats ne
        // parlent plus de ce qu'on regarde.
        Arreter();

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
            listage = await _source.Lister(chemin, _visibilite, jeton);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (jeton.IsCancellationRequested) return;

        var rendu = Stopwatch.StartNew();

        var vues = new List<CarteVue>(listage.Entrees.Count);

        foreach (var entree in listage.Entrees)
            vues.Add(Carte(entree));

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
        Surveiller(chemin);

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
    /// <summary>
    /// Le motif courant, exposé pour que chaque carte puisse souligner la
    /// partie du nom qui y répond. Propriété de dépendance et non champ :
    /// les liaisons des cartes doivent être prévenues à chaque frappe.
    /// </summary>
    public static readonly DependencyProperty MotifProperty =
        DependencyProperty.Register(nameof(Motif), typeof(string),
                                    typeof(FenetreExplorateur),
                                    new PropertyMetadata(""));

    public string Motif
    {
        get => (string)GetValue(MotifProperty);
        set => SetValue(MotifProperty, value);
    }

    private void Appliquer()
    {
        string motif = Filtre.Text.Trim();
        Motif = motif;

        Invite.Visibility = motif.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var retenues = _toutes.Where(c =>
            Filtrage.Correspond(c.Nom, c.Source.Extension, motif));

        var visibles = Trier(retenues).ToList();

        Cartes.ItemsSource = visibles;

        Compte.Text = _resultats
            ? (_fouille is not null
                ? $"RECHERCHE // {visibles.Count} TROUVÉS"
                : $"{visibles.Count} TROUVÉS")
            : motif.Length == 0
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
        // PreviewKeyDown descend du parent vers l'enfant : branché sur la
        // liste, il voit les touches avant le champ de saisie d'une carte.
        // Sans cette garde, taper « Suppr » dans un nom supprimerait le
        // fichier et « Entrée » l'ouvrirait. Pendant une édition, le champ
        // est seul maître des touches.
        if (_edition is not null) return;

        // Avec Alt enfoncé, WPF rapporte Key.System et range la vraie touche
        // dans SystemKey. Tester e.Key seul laisserait Alt+Entrée passer
        // pour une touche inconnue.
        var touche = e.Key == Key.System ? e.SystemKey : e.Key;

        if (touche == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            Premier(c => FileOperationNative.Proprietes(c, Poignee));
            return;
        }

        if (touche == Key.Enter)
        {
            if (Cartes.SelectedItem is CarteVue vue)
            {
                e.Handled = true;
                await Ouvrir(vue.Source);
            }

            return;
        }

        if (touche == Key.Back) { e.Handled = true; Monter(); return; }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool maj  = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (touche == Key.Delete)
        {
            e.Handled = true;
            await Supprimer(definitif: maj);
            return;
        }

        if (touche == Key.F5) { e.Handled = true; await Rafraichir(); return; }

        if (touche == Key.F2) { e.Handled = true; RenommerSelection(); return; }

        if (ctrl && touche == Key.H)
        {
            e.Handled = true;
            VoirCaches.IsChecked = VoirCaches.IsChecked != true;
            await Revoir();
            return;
        }

        if (touche == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = true;
            PlusOptions(auClavier: true);
            return;
        }

        if (ctrl && touche == Key.C && maj)
        {
            e.Handled = true;
            var chemins = Choisis();
            if (chemins.Count > 0)
                try { Clipboard.SetText(string.Join(Environment.NewLine, chemins)); }
                catch (Exception ex) { Chrono.Text = $"PRESSE-PAPIERS : {ex.GetType().Name}"; }
            return;
        }

        if (ctrl && touche is Key.C or Key.X)
        {
            e.Handled = true;
            PressePapiers.Poser(Choisis(), couper: touche == Key.X);
            return;
        }

        if (ctrl && touche == Key.V) { e.Handled = true; await Coller(); return; }

        if (ctrl && maj && touche == Key.N)
        {
            e.Handled = true;
            await NouveauDossier();
            return;
        }

        if (touche is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;
        if (Cartes.Items.Count == 0) return;

        int colonnes = Grille?.Colonnes ?? 1;
        int actuel = Math.Max(0, Cartes.SelectedIndex);

        int vise = touche switch
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
            var (libelle, cible) = segments[i];

            var texte = new TextBlock
            {
                Text = libelle.ToUpperInvariant(),
                Style = (Style)FindResource("Segment"),
                Foreground = (Brush)FindResource(dernier ? "Rouge" : "TexteVif"),
            };

            // Le dernier segment est le dossier courant : le rendre
            // cliquable promettrait une navigation qui ne mène nulle part.
            if (!dernier)
            {
                texte.Cursor = Cursors.Hand;
                texte.MouseLeftButtonUp += async (_, _) => await Naviguer(cible);

                texte.MouseEnter += (envoyeur, _) =>
                    ((TextBlock)envoyeur).Foreground = (Brush)FindResource("Rouge");
                texte.MouseLeave += (envoyeur, _) =>
                    ((TextBlock)envoyeur).Foreground = (Brush)FindResource("TexteVif");
            }

            FilDAriane.Children.Add(texte);
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
