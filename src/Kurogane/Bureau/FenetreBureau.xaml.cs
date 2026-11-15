using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Kurogane.Explorateur;
using Kurogane.Native;
using Kurogane.Theme;

namespace Kurogane.Bureau;

public partial class FenetreBureau : Window
{
    private readonly Icones _icones;

    private EntreeShell? _choix;

    /// <summary>Les trois tailles d'icônes, comme Windows.</summary>
    private static readonly (string Nom, double Icone, double Case, double Hauteur)[] Tailles =
    {
        ("GRANDES",  48, 116, 112),
        ("MOYENNES", 32,  92,  88),
        ("PETITES",  24,  76,  72),
    };

    public static readonly DependencyProperty BorduresProperty =
        DependencyProperty.Register(nameof(Bordures), typeof(Thickness),
                                    typeof(FenetreBureau),
                                    new PropertyMetadata(new Thickness(0)));

    /// <summary>
    /// Ce que la barre des tâches mange sur les bords. Le fond d'écran
    /// l'ignore, le contenu s'y plie.
    /// </summary>
    public Thickness Bordures
    {
        get => (Thickness)GetValue(BorduresProperty);
        set => SetValue(BorduresProperty, value);
    }

    public static readonly DependencyProperty TailleIconeProperty =
        DependencyProperty.Register(nameof(TailleIcone), typeof(double),
                                    typeof(FenetreBureau), new PropertyMetadata(32.0));

    public static readonly DependencyProperty TailleCaseProperty =
        DependencyProperty.Register(nameof(TailleCase), typeof(double),
                                    typeof(FenetreBureau), new PropertyMetadata(92.0));

    public static readonly DependencyProperty HauteurCaseProperty =
        DependencyProperty.Register(nameof(HauteurCase), typeof(double),
                                    typeof(FenetreBureau), new PropertyMetadata(88.0));

    public double TailleIcone
    {
        get => (double)GetValue(TailleIconeProperty);
        set => SetValue(TailleIconeProperty, value);
    }

    public double TailleCase
    {
        get => (double)GetValue(TailleCaseProperty);
        set => SetValue(TailleCaseProperty, value);
    }

    public double HauteurCase
    {
        get => (double)GetValue(HauteurCaseProperty);
        set => SetValue(HauteurCaseProperty, value);
    }

    private enum Critere { Nom, Taille, Type, Date }

    private Critere _critere = Critere.Nom;
    private bool _descendant;
    private int _taille = 1;

    private List<IconeVue> _vues = new();

    public FenetreBureau(ThemeDefinition theme)
    {
        InitializeComponent();

        _icones = new Icones(theme);

        Fond.Source = Papier(theme);

        // PreviewMouseRightButtonUp et non MouseRightButtonUp : la couche
        // n'a aucun enfant qui écoute, mais la version tunnelante évite
        // qu'un futur élément du bureau avale le geste sans le vouloir.
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;

            // Le clic droit sélectionne ce qui est dessous quand ça ne
            // l'est pas déjà : sans ça, le menu agirait sur une sélection
            // faite ailleurs, et invisible.
            var cible = Sous(e.OriginalSource);

            if (cible is not null
                && !Grille.SelectedItems.OfType<IconeVue>().Contains(cible))
                Grille.SelectedItem = cible;

            Menu(cible is not null);
        };

        // Cliquer hors des icônes désélectionne et arme la bande.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Sous(e.OriginalSource) is not null) return;

            Grille.SelectedItem = null;

            // Sans ce focus, les touches partent à la fenêtre précédemment
            // active : cliquer le bureau doit donner le clavier au bureau.
            Grille.Focus();

            _depart = e.GetPosition(this);
            _arme = true;
            CaptureMouse();
        };

        PreviewMouseMove += (_, e) =>
        {
            if (!_arme) return;

            var ici = e.GetPosition(this);

            // Seuil : sans lui, un clic qui bouge d'un pixel ouvrirait une
            // bande et avalerait la désélection.
            if (!_trace
                && Math.Abs(ici.X - _depart.X) < 5
                && Math.Abs(ici.Y - _depart.Y) < 5) return;

            _trace = true;

            var zone = new Rect(_depart, ici);

            Bande.Margin = new Thickness(zone.Left, zone.Top, 0, 0);
            Bande.Width = zone.Width;
            Bande.Height = zone.Height;
            Bande.Visibility = Visibility.Visible;

            Cerner(zone);
        };

        PreviewMouseLeftButtonUp += (_, _) => Relacher();
        LostMouseCapture += (_, _) => Relacher();

        Grille.MouseDoubleClick += (_, _) => Ouvrir();

        // Phase tunnelante : la liste traite Ctrl+A pour son propre compte,
        // et on préfère que notre sélection explicite passe d'abord. Le
        // bureau n'a aucune saisie de texte, donc rien ne se dispute les
        // touches que nous ne réclamons pas — les flèches continuent de
        // naviguer dans la grille.
        //
        // Une version « handledEventsToo » a existé ici, posée pour
        // contourner une interception qui n'existait pas : le clavier
        // était en QWERTY et Ctrl+A partait en Ctrl+Q. Retirée, parce
        // qu'un contournement sans cause est une dette qui survit à la
        // raison qui l'a fait naître.
        PreviewKeyDown += async (_, e) =>
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.F5) { e.Handled = true; await Lire(); }
            else if (e.Key == Key.Enter) { e.Handled = true; Ouvrir(); }
            else if (ctrl && e.Key is Key.C or Key.X)
            {
                e.Handled = true;
                Poser(couper: e.Key == Key.X);
            }
            else if (ctrl && e.Key == Key.A)
            {
                e.Handled = true;
                Tout();
            }
            else if (ctrl && e.Key == Key.V) { e.Handled = true; await Coller(); }
            else if (ctrl && e.Key == Key.Z) { e.Handled = true; await Annuler(); }
            else if (e.Key == Key.Delete)
            {
                e.Handled = true;

                await Supprimer(definitif:
                    (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
            }
            else if (ctrl)
            {
                // Un Ctrl+quelque chose qu'on ne traite pas se signale.
                // Sans cela, une touche avalée en amont et une condition
                // fautive ici donnent exactement le même symptôme : rien.
                // Gardé : c'est cette ligne qui a montré qu'un Ctrl+A
                // arrivait en Ctrl+Q. Un raccourci muet et un raccourci
                // mal envoyé donnent le même symptôme — rien — et seule
                // cette ligne les distingue.
                Dire($"TOUCHE NON TRAITÉE : CTRL+{e.Key}");
            }
        };

        Loaded += async (_, _) =>
        {
            Replier();
            await Lire();
        };
    }

    /// <summary>
    /// Ni caché ni système, comme le bureau de Windows.
    ///
    /// <c>Visibilite.Defaut</c> montre tout : c'est le bon réglage pour
    /// l'explorateur, où deux bascules laissent l'utilisateur décider. Un
    /// bureau n'a pas ces bascules, et afficher ce que Windows masque y
    /// fait apparaître deux <c>desktop.ini</c> — un par dossier fusionné.
    /// </summary>
    private static readonly Visibilite Discret = new(Caches: false, Systeme: false);

    /// <summary>
    /// Les deux dossiers que Windows fusionne pour composer le bureau :
    /// celui de l'utilisateur et celui de tous les utilisateurs. N'en lire
    /// qu'un ferait disparaître des raccourcis installés pour la machine.
    /// </summary>
    private static IEnumerable<string> Dossiers()
    {
        yield return Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);

        yield return Environment.GetFolderPath(
            Environment.SpecialFolder.CommonDesktopDirectory);
    }

    /// <summary>Relit le bureau et reconstruit la grille.</summary>
    private async Task Lire()
    {
        var source = new SourceFichiers();
        var vues = new List<IconeVue>();

        foreach (string dossier in Dossiers())
        {
            if (!Directory.Exists(dossier)) continue;

            var listage = await source.Lister(dossier, Discret, CancellationToken.None);

            foreach (var entree in listage.Entrees)
                vues.Add(new IconeVue
                {
                    Source = entree,
                    Nom = Afficher(entree),
                    Icone = _icones.Pour(entree),
                });
        }

        _vues = vues;
        Ordonner();
    }

    /// <summary>
    /// Applique le tri courant sans relire le disque. Changer de critère
    /// ne doit pas coûter une lecture : les données sont déjà là.
    /// </summary>
    private void Ordonner()
    {
        // Dossiers d'abord quel que soit le critère : la convention de
        // l'Explorateur, qu'on ne change pas sans raison.
        IOrderedEnumerable<IconeVue> ordonne =
            _vues.OrderBy(v => v.Source.EstDossier ? 0 : 1);

        ordonne = _critere switch
        {
            Critere.Taille => _descendant
                ? ordonne.ThenByDescending(v => v.Source.Taille)
                : ordonne.ThenBy(v => v.Source.Taille),

            Critere.Date => _descendant
                ? ordonne.ThenByDescending(v => v.Source.Modifie)
                : ordonne.ThenBy(v => v.Source.Modifie),

            Critere.Type => _descendant
                ? ordonne.ThenByDescending(v => v.Source.Extension,
                                           StringComparer.OrdinalIgnoreCase)
                : ordonne.ThenBy(v => v.Source.Extension,
                                 StringComparer.OrdinalIgnoreCase),

            _ => _descendant
                ? ordonne.ThenByDescending(v => v.Nom,
                                           StringComparer.CurrentCultureIgnoreCase)
                : ordonne.ThenBy(v => v.Nom,
                                 StringComparer.CurrentCultureIgnoreCase),
        };

        // Le nom départage toujours : deux fichiers de même taille ne
        // doivent pas changer de place d'un tri à l'autre.
        Grille.ItemsSource = ordonne
            .ThenBy(v => v.Nom, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Un raccourci s'affiche sans son extension. La montrer ferait
    /// apparaître « .lnk » sur la moitié du bureau, ce que Windows ne fait
    /// nulle part.
    /// </summary>
    private static string Afficher(Entree entree) =>
        entree.Extension is "lnk" or "url"
            ? Path.GetFileNameWithoutExtension(entree.Nom)
            : entree.Nom;

    private Point _depart;
    private bool _arme;
    private bool _trace;

    private void Relacher()
    {
        if (!_arme) return;

        _arme = false;
        _trace = false;

        Bande.Visibility = Visibility.Collapsed;

        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>
    /// Sélectionne les icônes que la bande touche.
    ///
    /// Les conteneurs sont peu nombreux — quelques dizaines — et tous
    /// matérialisés : on peut les interroger un par un. L'explorateur, lui,
    /// doit calculer les indices, parce que ses cartes hors écran
    /// n'existent pas.
    /// </summary>
    private void Cerner(Rect zone)
    {
        Grille.SelectedItems.Clear();

        foreach (var vue in Grille.Items.OfType<IconeVue>())
        {
            if (Grille.ItemContainerGenerator.ContainerFromItem(vue)
                is not ListBoxItem conteneur) continue;

            if (!conteneur.IsVisible) continue;

            var cadre = conteneur.TransformToAncestor(this)
                                 .TransformBounds(new Rect(conteneur.RenderSize));

            if (zone.IntersectsWith(cadre)) Grille.SelectedItems.Add(vue);
        }
    }

    /// <summary>L'icône sous le curseur, ou null si le clic est tombé sur le fond.</summary>
    private static IconeVue? Sous(object? origine)
    {
        var courant = origine as DependencyObject;

        while (courant is not null)
        {
            if (courant is ListBoxItem conteneur)
                return conteneur.DataContext as IconeVue;

            courant = VisualTreeHelper.GetParent(courant);
        }

        return null;
    }

    private MenuItem Entree(string titre, string? geste = null,
                            Action? faire = null, bool coche = false)
    {
        var item = new MenuItem
        {
            Style = (Style)FindResource("EntreeMenu"),
            Header = titre,
            InputGestureText = geste ?? "",
            IsChecked = coche,
        };

        if (faire is not null) item.Click += (_, _) => faire();

        return item;
    }

    /// <summary>
    /// Marque le choix actif. Notre gabarit de menu ne dessine pas de
    /// coche — il n'en avait jamais eu besoin — et <c>IsChecked</c> y
    /// serait donc invisible. Un marqueur dans le libellé tient la même
    /// fonction, et la chasse fixe garde l'alignement.
    /// </summary>
    private static string Marquer(string titre, bool actif) =>
        (actif ? "▪ " : "  ") + titre;

    private Separator Trait() => new()
    {
        Style = (Style)FindResource("SeparateurMenu"),
    };

    /// <summary>Le sous-menu « Affichage » : la taille des icônes.</summary>
    private MenuItem Affichage()
    {
        var racine = Entree("AFFICHAGE");

        for (int i = 0; i < Tailles.Length; i++)
        {
            int rang = i;

            racine.Items.Add(Entree(Marquer($"{Tailles[i].Nom} ICÔNES", _taille == i),
                                    null, () => Dimensionner(rang)));
        }

        return racine;
    }

    private void Dimensionner(int rang)
    {
        _taille = rang;

        var (_, icone, largeur, hauteur) = Tailles[rang];

        TailleIcone = icone;
        TailleCase = largeur;
        HauteurCase = hauteur;
    }

    /// <summary>Le sous-menu « Trier par » : critère et sens.</summary>
    private MenuItem Tri()
    {
        var racine = Entree("TRIER PAR");

        foreach (var (nom, critere) in new[]
                 {
                     ("NOM", Critere.Nom), ("TAILLE", Critere.Taille),
                     ("TYPE", Critere.Type), ("DATE", Critere.Date),
                 })
        {
            var choisi = critere;

            racine.Items.Add(Entree(Marquer(nom, _critere == choisi), null, () =>
            {
                _critere = choisi;
                Ordonner();
            }));
        }

        racine.Items.Add(Trait());

        racine.Items.Add(Entree(_descendant ? "CROISSANT" : "DÉCROISSANT", null, () =>
        {
            _descendant = !_descendant;
            Ordonner();
        }));

        return racine;
    }

    /// <summary>
    /// Ce qu'il faut pour défaire la dernière opération.
    ///
    /// Les chemins créés sont relevés en comparant le dossier avant et
    /// après, et non devinés à partir des sources : le shell renomme en
    /// cas de collision, et « copie de rapport.pdf » n'a plus le nom qu'on
    /// aurait calculé.
    /// </summary>
    private sealed record Retour(bool Deplacement, IReadOnlyList<string> Crees,
                                 string? Origine);

    private readonly Stack<Retour> _retours = new();

    /// <summary>
    /// Défait la dernière opération faite PAR Kurogane.
    ///
    /// Windows tient sa propre pile d'annulation, à laquelle aucune API
    /// publique ne donne accès : une suppression faite depuis le menu du
    /// shell ne se défait donc pas ici. Le dire est plus honnête que de
    /// laisser croire à un Ctrl+Z universel.
    /// </summary>
    private async Task Annuler()
    {
        if (_retours.Count == 0)
        {
            Dire("RIEN À ANNULER");
            return;
        }

        var retour = _retours.Pop();
        IntPtr poignee = new WindowInteropHelper(this).Handle;

        var issue = retour.Deplacement && retour.Origine is { } origine
            ? FileOperationNative.Deplacer(retour.Crees, origine, poignee)
            : FileOperationNative.Corbeille(retour.Crees, poignee);

        if (!issue.Silencieux) Dire($"ANNULATION REFUSÉE // CODE {issue.Code}");

        await Lire();
    }

    /// <summary>Les chemins présents dans le bureau de l'utilisateur.</summary>
    private static HashSet<string> Photographier(string dossier)
    {
        try
        {
            return new HashSet<string>(
                Directory.EnumerateFileSystemEntries(dossier),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Colle le presse-papiers sur le bureau.
    ///
    /// « Coller » ne figure dans aucun menu du shell : comme
    /// « Actualiser », c'est une commande de la vue, et la vue de Windows
    /// dort. « Copier » et « Couper », eux, viennent du menu de l'élément
    /// et marchaient déjà — d'où une asymétrie qui ne venait pas d'un
    /// oubli mais de deux menus d'origines différentes.
    ///
    /// Le collage vise le bureau de l'utilisateur, jamais celui de tous
    /// les utilisateurs : écrire dans le second demande des droits
    /// d'administrateur et affecterait les autres comptes.
    /// </summary>
    private async Task Coller()
    {
        if (PressePapiers.Lire() is not { } contenu)
        {
            // Le dire plutôt que de ne rien faire : un collage silencieux
            // laisse croire à une panne alors que le presse-papiers est
            // simplement vide de fichiers.
            Dire("PRESSE-PAPIERS SANS FICHIER");
            return;
        }

        string cible = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);

        IntPtr poignee = new WindowInteropHelper(this).Handle;

        var avant = Photographier(cible);

        var issue = contenu.Couper
            ? FileOperationNative.Deplacer(contenu.Chemins, cible, poignee)
            : FileOperationNative.Copier(contenu.Chemins, cible, poignee);

        if (!issue.Silencieux) Dire($"COLLAGE REFUSÉ // CODE {issue.Code}");

        var crees = Photographier(cible).Except(avant).ToList();

        if (crees.Count > 0)
            _retours.Push(new Retour(contenu.Couper, crees,
                Path.GetDirectoryName(contenu.Chemins[0])));

        await Lire();
    }

    /// <summary>
    /// Sélectionne tout.
    ///
    /// Sélection explicite plutôt que <c>SelectAll()</c> : la première
    /// tentative passait par lui et ne donnait rien, sans qu'on sache si
    /// la touche n'arrivait pas ou si l'appel ne faisait rien. Remplir
    /// <c>SelectedItems</c> à la main ne dépend d'aucun mode de sélection
    /// et se vérifie par son propre compte — le retour en barre d'état dit
    /// lequel des deux cas on est en train de regarder.
    /// </summary>
    private void Tout()
    {
        Grille.SelectedItems.Clear();

        foreach (var vue in Grille.Items.OfType<IconeVue>())
            Grille.SelectedItems.Add(vue);

        Dire($"{Grille.SelectedItems.Count} ÉLÉMENT(S) SÉLECTIONNÉ(S)");
    }

    /// <summary>
    /// Met la sélection au presse-papiers.
    ///
    /// Le menu du shell portait déjà « Copier » et « Couper » sur une
    /// icône, mais pas leurs raccourcis : un menu contextuel n'installe
    /// pas de raccourci clavier, il affiche seulement celui que
    /// l'application déclare. Les deux moitiés du geste venaient encore
    /// d'origines différentes.
    /// </summary>
    private void Poser(bool couper)
    {
        var chemins = Grille.SelectedItems.OfType<IconeVue>()
                                          .Select(v => v.Chemin).ToList();

        if (chemins.Count == 0) return;

        PressePapiers.Poser(chemins, couper);

        Dire(couper
            ? $"{chemins.Count} ÉLÉMENT(S) COUPÉ(S)"
            : $"{chemins.Count} ÉLÉMENT(S) COPIÉ(S)");
    }

    /// <summary>
    /// Met la sélection à la corbeille, ou la supprime définitivement sur
    /// Maj+Suppr.
    ///
    /// Supprimer est une décision de l'utilisateur, pas de Kurogane : on
    /// l'exécute comme le ferait l'Explorateur, y compris la suppression
    /// définitive. Se montrer plus prudent que lui sur un geste explicite
    /// serait un défaut, pas une vertu.
    ///
    /// Rien n'est empilé pour l'annulation : restaurer depuis la corbeille
    /// n'est pas à notre portée, et proposer un Ctrl+Z qui ne rendrait
    /// rien serait pire que de ne rien proposer.
    /// </summary>
    private async Task Supprimer(bool definitif)
    {
        var chemins = Grille.SelectedItems.OfType<IconeVue>()
                                          .Select(v => v.Chemin).ToList();

        if (chemins.Count == 0) return;

        IntPtr poignee = new WindowInteropHelper(this).Handle;

        var issue = definitif
            ? FileOperationNative.Effacer(chemins, poignee)
            : FileOperationNative.Corbeille(chemins, poignee);

        if (!issue.Silencieux) Dire($"SUPPRESSION REFUSÉE // CODE {issue.Code}");

        await Lire();
    }

    /// <summary>Ouvre la sélection avec l'application associée.</summary>
    private void Ouvrir()
    {
        foreach (var vue in Grille.SelectedItems.OfType<IconeVue>().ToList())
        {
            try
            {
                Process.Start(new ProcessStartInfo(vue.Chemin)
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Dire($"OUVERTURE REFUSÉE : {ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Replie le contenu dans la zone de travail.
    ///
    /// Les retraits viennent de Win32, donc en pixels réels ; WPF raisonne
    /// en unités indépendantes du périphérique. Sans la division par
    /// l'échelle, un écran à 150 % reculerait le contenu d'une fois et
    /// demie la hauteur de la barre.
    /// </summary>
    internal void Replier()
    {
        var (gauche, haut, droite, bas) = BureauNative.Retraits();

        double echelle = PresentationSource.FromVisual(this)
            is { CompositionTarget: { } cible }
            ? cible.TransformToDevice.M11
            : 1.0;

        if (echelle <= 0) echelle = 1.0;

        Bordures = new Thickness(gauche / echelle, haut / echelle,
                                 droite / echelle, bas / echelle);
    }

    /// <summary>État affiché en bas à gauche, pendant l'étape 1.</summary>
    public void Dire(string texte) => Etat.Text = texte;

    /// <summary>
    /// Le menu du bureau — le vrai, celui du shell, redessiné au thème.
    ///
    /// C'est le menu du FOND du dossier Bureau, pas celui du dossier vu
    /// depuis son parent : il porte « Nouveau », « Actualiser »,
    /// « Paramètres d'affichage » et « Personnaliser ». Les deux menus sont
    /// des objets différents chez le shell, obtenus différemment.
    /// </summary>
    private void Menu(bool surIcone)
    {
        IntPtr poignee = new WindowInteropHelper(this).Handle;

        // Maj enfoncé fait apparaître les verbes étendus, comme sous
        // Windows.
        bool etendu = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        var choisis = Grille.SelectedItems.OfType<IconeVue>()
                                          .Select(v => v.Chemin).ToList();

        // Deux menus distincts chez le shell, et c'est la distinction qui
        // avait manqué la première fois : celui d'un élément se demande à
        // partir des éléments, celui du fond à partir du dossier.
        var shell = surIcone && choisis.Count > 0
            ? MenuShellNative.Construire(choisis, etendu)
            : MenuShellNative.ConstruireBureau(poignee, etendu);

        if (shell is null)
        {
            Dire("MENU WINDOWS INDISPONIBLE");
            return;
        }

        _choix = null;

        var menu = MenuShellVue.Construire(this, shell, e => _choix = e);

        // « Actualiser » n'existe dans aucun menu du shell : c'est une
        // commande de la VUE, et la vue de Windows dort. Elle nous revient
        // donc, comme « Trier par » et « Affichage » le feront.
        // Les commandes de la VUE, en tête comme chez Windows. Aucune ne
        // vient du shell : elles agissent sur notre grille, et celle de
        // Windows dort.
        if (!surIcone)
        {
            var notres = new List<object>
            {
                Affichage(),
                Tri(),
                Entree("ACTUALISER", "F5", async () => await Lire()),
                Trait(),
                Entree("ANNULER", "Ctrl+Z", async () => await Annuler()),
                Entree("COLLER", "Ctrl+V", async () => await Coller()),
                Trait(),
            };

            ((MenuItem)notres[4]).IsEnabled = _retours.Count > 0;
            ((MenuItem)notres[5]).IsEnabled = PressePapiers.Disponible();

            for (int i = 0; i < notres.Count; i++) menu.Items.Insert(i, notres[i]);
        }

        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;

        menu.Closed += async (_, _) =>
        {
            var choisi = _choix;
            _choix = null;

            // Exécuter après la fermeture : un verbe qui ouvre une boîte de
            // dialogue — « Paramètres d'affichage », « Personnaliser » — la
            // verrait sinon s'afficher derrière un menu encore ouvert.
            if (choisi is not null && !shell.Executer(choisi, poignee))
                Dire("COMMANDE WINDOWS REFUSÉE");

            shell.Dispose();

            // Rendre le clavier à la grille. Sans cela, un « Copier » fait
            // dans ce menu laisse le focus nulle part, et le Ctrl+V qui
            // suit ne parvient jamais au bureau — alors qu'un Ctrl+V venu
            // d'ailleurs, lui, fonctionne. L'asymétrie ne venait pas du
            // presse-papiers.
            Grille.Focus();

            // Le verbe a pu créer, renommer ou supprimer sur le bureau.
            if (choisi is not null) await Lire();
        };

        menu.IsOpen = true;
    }

    /// <summary>
    /// Le fond d'écran déclaré par le thème. La couche le peint elle-même
    /// parce qu'elle recouvre celui de Windows : sans cela, occuper le
    /// bureau reviendrait à l'effacer.
    /// </summary>
    private static BitmapImage? Papier(ThemeDefinition theme)
    {
        string? declare = theme.Wallpaper?.Path;
        if (string.IsNullOrWhiteSpace(declare)) return null;

        try
        {
            string chemin = theme.Resolve(declare);
            if (!File.Exists(chemin)) return null;

            var image = new BitmapImage();
            image.BeginInit();

            // Chargement immédiat : sans cela le fichier reste ouvert, et
            // le thème ne pourrait plus le remplacer tant que la couche
            // tourne.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(chemin);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // Image absente ou illisible : l'obsidienne de la fenêtre fera
            // l'affaire. Une couche sans fond d'écran reste utilisable.
            return null;
        }
    }
}
