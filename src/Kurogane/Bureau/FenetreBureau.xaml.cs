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

    private readonly Dispositions _places = Dispositions.Charger();

    /// <summary>
    /// Le pas de la grille. Les cellules sont plus larges et plus hautes
    /// que les cases pour laisser respirer : deux icônes collées se lisent
    /// comme une seule.
    /// </summary>
    private double PasX => TailleCase + 12;
    private double PasY => HauteurCase + 12;

    /// <summary>
    /// Combien de lignes tiennent dans la hauteur disponible. Sert au
    /// placement automatique, qui remplit une colonne avant de passer à la
    /// suivante — comme Windows.
    /// </summary>
    private int Lignes()
    {
        double hauteur = Grille.ActualHeight > 0 ? Grille.ActualHeight : ActualHeight - 140;

        return Math.Max(1, (int)(hauteur / PasY));
    }

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

        // Cliquer sur une icône arme un déplacement, cliquer à côté arme
        // la bande de sélection.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Sous(e.OriginalSource) is { } prise)
            {
                // Armé, pas capturé : la liste doit d'abord faire sa
                // sélection. La capture viendra au franchissement du seuil,
                // si franchissement il y a.
                _saisie = prise;
                _depuis = e.GetPosition(Grille);

                // Relevée maintenant, pas au franchissement du seuil : la
                // liste réduit une multi-sélection à l'élément cliqué dès
                // l'appui. Windows diffère cette réduction au relâchement,
                // justement pour qu'on puisse tirer plusieurs icônes.
                _prises = Grille.SelectedItems.OfType<IconeVue>().ToList();

                return;
            }

            Grille.SelectedItem = null;

            // Sans ce focus, les touches partent à la fenêtre précédemment
            // active : cliquer le bureau doit donner le clavier au bureau.
            Grille.Focus();

            _depart = e.GetPosition(this);
            _arme = true;
            CaptureMouse();
        };

        PreviewMouseMove += async (_, e) =>
        {
            if (_saisie is { } tiree)
            {
                var ou = e.GetPosition(Grille);

                // Même seuil que la bande : sans lui, un clic qui tremble
                // déplacerait l'icône d'un pixel et consommerait le
                // double-clic.
                if (!_deplace
                    && Math.Abs(ou.X - _depuis.X) < 5
                    && Math.Abs(ou.Y - _depuis.Y) < 5) return;

                if (!_deplace)
                {
                    _deplace = true;

                    // Le groupe à déplacer se construit à partir de
                    // l'icône saisie, et non de SelectedItems.
                    //
                    // La première version s'y fiait et emmenait zéro
                    // icône : la sélection était vide au moment du
                    // franchissement du seuil. Peu importe pourquoi — une
                    // opération qui sait déjà ce qu'elle a attrapé n'a pas
                    // à le redemander à l'état d'un autre contrôle. La
                    // sélection ne sert plus qu'à étendre le geste quand
                    // l'icône saisie en fait partie.
                    var groupe = _prises.Contains(tiree)
                        ? _prises
                        : new List<IconeVue> { tiree };

                    // Rendre à la liste la sélection qu'elle a défaite à
                    // l'appui. Sans cela le groupe se déplace bien, mais
                    // une seule icône reste allumée : le geste et son
                    // image diraient deux choses différentes.
                    Grille.SelectedItems.Clear();
                    foreach (var vue in groupe) Grille.SelectedItems.Add(vue);

                    _origines.Clear();

                    foreach (var vue in groupe)
                        _origines[vue] = (vue.X, vue.Y);

                    CaptureMouse();
                }

                double dx = ou.X - _depuis.X;
                double dy = ou.Y - _depuis.Y;

                foreach (var (vue, origine) in _origines)
                {
                    vue.X = origine.X + dx;
                    vue.Y = origine.Y + dy;
                }

                // Une autre fenêtre est passée sous le curseur : ce n'est
                // plus un rangement, c'est un transfert. Notre déplacement
                // interne capture la souris et ne peut rien quitter ; on
                // rend les icônes à leur place et on passe la main au
                // glissement du système, seul capable de traverser les
                // fenêtres.
                if (Ailleurs()) await Emporter();

                return;
            }

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

        // Déposer AVANT de relâcher : rendre la capture déclenche
        // LostMouseCapture, qui dépose à son tour. Dans cet ordre, le
        // travail est fait une fois et le second appel ne trouve rien.
        PreviewMouseLeftButtonUp += async (_, _) => { await Deposer(); Relacher(); };

        LostMouseCapture += async (_, e) =>
        {
            // LostMouseCapture remonte. La liste détient la capture depuis
            // le clic ; la lui prendre déclenche sa perte, qui arrive ici
            // et annulait le déplacement à l'instant où il commençait —
            // d'où un groupe rempli puis vidé dans la même ligne de code.
            // Seule la perte de NOTRE capture nous concerne.
            if (!ReferenceEquals(e.OriginalSource, this)) return;

            Relacher();
            await Deposer();
        };

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

        // Dépôt venu de l'extérieur — l'Explorateur de Windows, le nôtre,
        // n'importe quelle application qui publie des fichiers.
        AllowDrop = true;

        // La grille couvre presque toute la fenêtre. Sans AllowDrop sur
        // elle, un glissement qui la survole est refusé avant d'atteindre
        // la fenêtre, et le dépôt n'a jamais lieu.
        Grille.AllowDrop = true;

        foreach (UIElement cible in new UIElement[] { this, Grille })
        {
            cible.DragEnter += (_, e) =>
            {
                e.Effects = Depot.Effet(e, Bureau());
                e.Handled = true;

                Dire(e.Effects == DragDropEffects.None
                    ? "DÉPÔT REFUSÉ // PAS DE FICHIERS"
                    : $"DÉPÔT ENTRANT // {e.Effects}");
            };

            cible.DragOver += (_, e) =>
            {
                e.Effects = Depot.Effet(e, Bureau());
                e.Handled = true;
            };

            cible.Drop += async (_, e) => await Recevoir(e);
        }

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
        Grille.ItemsSource = _vues;

        Disposer();
    }

    /// <summary>
    /// Donne une cellule à chaque icône : celle qu'elle avait, ou la
    /// première libre.
    ///
    /// Une place enregistrée peut être revendiquée par deux chemins — un
    /// fichier supprimé puis recréé ailleurs, un fichier déplacé à la main
    /// hors de Kurogane. La première arrivée la garde, la seconde retombe
    /// sur une place libre : mieux vaut une icône déplacée que deux icônes
    /// superposées.
    /// </summary>
    private void Disposer()
    {
        var prises = new HashSet<(int, int)>();
        var orphelines = new List<IconeVue>();

        foreach (var vue in _vues)
        {
            var place = _places.Pour(vue.Chemin);

            if (place is null || !prises.Add((place.Colonne, place.Ligne)))
            {
                orphelines.Add(vue);
                continue;
            }

            vue.Colonne = place.Colonne;
            vue.Ligne = place.Ligne;
        }

        int lignes = Lignes();
        int colonne = 0, ligne = 0;

        foreach (var vue in orphelines)
        {
            while (prises.Contains((colonne, ligne)))
            {
                ligne++;
                if (ligne >= lignes) { ligne = 0; colonne++; }
            }

            vue.Colonne = colonne;
            vue.Ligne = ligne;

            prises.Add((colonne, ligne));
            _places.Poser(vue.Chemin, colonne, ligne);
        }

        _places.Nettoyer(_vues.Select(v => v.Chemin));
        _places.Enregistrer();

        Situer();
    }

    /// <summary>
    /// Traduit les cellules en coordonnées. C'est la seule fonction qui
    /// connaisse les pixels : tout le reste raisonne en cellules, qui
    /// survivent à un changement de taille d'icônes ou de résolution.
    /// </summary>
    private void Situer()
    {
        foreach (var vue in _vues)
        {
            vue.X = vue.Colonne * PasX;
            vue.Y = vue.Ligne * PasY;
        }
    }

    /// <summary>
    /// Range tout dans l'ordre de tri courant. C'est ce que fait « Trier
    /// par » : avec des positions libres, trier ne peut pas se contenter
    /// de réordonner une liste — il faut reposer les icônes.
    /// </summary>
    private void Ranger()
    {
        int lignes = Lignes();
        int colonne = 0, ligne = 0;

        foreach (var vue in Ordonner())
        {
            vue.Colonne = colonne;
            vue.Ligne = ligne;

            _places.Poser(vue.Chemin, colonne, ligne);

            ligne++;
            if (ligne >= lignes) { ligne = 0; colonne++; }
        }

        _places.Enregistrer();
        Situer();
    }

    /// <summary>
    /// L'ordre de tri courant, sans relire le disque : les données sont
    /// déjà là. Rend une séquence au lieu de l'afficher — depuis que les
    /// positions sont libres, trier ne veut plus dire réordonner une liste
    /// mais reposer des icônes.
    /// </summary>
    private IEnumerable<IconeVue> Ordonner()
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
        return ordonne.ThenBy(v => v.Nom, StringComparer.CurrentCultureIgnoreCase);
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

    private IconeVue? _saisie;
    private Point _depuis;
    private bool _deplace;

    /// <summary>La sélection telle qu'elle était à l'appui du bouton.</summary>
    private List<IconeVue> _prises = new();

    private readonly Dictionary<IconeVue, (double X, double Y)> _origines = new();

    /// <summary>Vrai si une autre fenêtre est sous le curseur.</summary>
    private bool Ailleurs()
    {
        IntPtr dessous = BureauNative.SousCurseur();
        if (dessous == IntPtr.Zero) return false;

        return dessous != new WindowInteropHelper(this).Handle;
    }

    /// <summary>
    /// Convertit le déplacement interne en glissement système.
    ///
    /// Les icônes reprennent leur place : ce qui part n'est pas leur
    /// position mais les fichiers eux-mêmes, et c'est la fenêtre
    /// d'arrivée qui décidera du sort. Si le dépôt revient chez nous,
    /// Recevoir ignore les chemins déjà sur le bureau.
    /// </summary>
    private async Task Emporter()
    {
        var chemins = _origines.Keys.Select(v => v.Chemin).ToArray();

        _origines.Clear();
        _deplace = false;
        _saisie = null;
        _prises = new List<IconeVue>();

        Relacher();
        Situer();

        if (chemins.Length == 0) return;

        DragDrop.DoDragDrop(Grille,
            new DataObject(DataFormats.FileDrop, chemins),
            DragDropEffects.Copy | DragDropEffects.Move);

        // Un déplacement vers ailleurs vide le bureau de ces fichiers.
        await Lire();
    }

    /// <summary>
    /// Conclut un déplacement. Trois cas, et ils ne font pas la même
    /// chose.
    ///
    /// <b>Sur un dossier</b> : ce n'est plus un geste de mise en page mais
    /// une opération sur fichiers. Le dossier accueille ce qu'on lui
    /// dépose, comme partout ailleurs dans Windows.
    ///
    /// <b>Sur une autre icône</b> : les deux échangent leur place. Faire
    /// glisser l'intrus vers la case libre suivante serait plus simple,
    /// mais l'utilisateur a désigné une place — la lui refuser au profit
    /// d'une autre serait lui désobéir poliment.
    ///
    /// <b>Sur une case libre</b> : aimantation, et glissement vers la
    /// suivante libre si plusieurs icônes convergent. Superposer deux
    /// icônes serait permis par le Canvas, et illisible à l'écran.
    /// </summary>
    private async Task Deposer()
    {
        if (!_deplace) { _saisie = null; _prises = new List<IconeVue>(); return; }

        _deplace = false;

        var groupe = _origines.Keys.ToList();
        var tiree = _saisie;

        _saisie = null;
        _prises = new List<IconeVue>();

        int colonne = Math.Max(0, (int)Math.Round((tiree?.X ?? 0) / PasX));
        int ligne = Math.Max(0, (int)Math.Round((tiree?.Y ?? 0) / PasY));

        var occupant = _vues.FirstOrDefault(v =>
            !groupe.Contains(v) && v.Colonne == colonne && v.Ligne == ligne);

        // ---------------------------------------------- un dossier accueille
        if (occupant is { Source.EstDossier: true })
        {
            _origines.Clear();

            // Remettre les icônes sur leurs cases avant d'agir : si
            // l'opération échoue ou que l'utilisateur l'annule, rien ne
            // doit rester en suspens au milieu du bureau.
            Situer();

            IntPtr poignee = new WindowInteropHelper(this).Handle;

            var issue = FileOperationNative.Deplacer(
                groupe.Select(v => v.Chemin).ToList(), occupant.Chemin, poignee);

            Dire(issue.Silencieux
                ? $"{groupe.Count} ÉLÉMENT(S) VERS {occupant.Nom}"
                : $"DÉPLACEMENT REFUSÉ // CODE {issue.Code}");

            await Lire();
            return;
        }

        // ------------------------------------------------- échange de places
        if (occupant is not null && groupe.Count == 1)
        {
            var seule = groupe[0];

            int ancienneColonne = seule.Colonne;
            int ancienneLigne = seule.Ligne;

            seule.Colonne = colonne;
            seule.Ligne = ligne;

            occupant.Colonne = ancienneColonne;
            occupant.Ligne = ancienneLigne;

            _places.Poser(seule.Chemin, colonne, ligne);
            _places.Poser(occupant.Chemin, ancienneColonne, ancienneLigne);

            _origines.Clear();
            _places.Enregistrer();
            Situer();

            Dire($"{seule.Nom} ↔ {occupant.Nom}");
            return;
        }

        // --------------------------------------------------- case par case
        var prises = new HashSet<(int, int)>(
            _vues.Where(v => !_origines.ContainsKey(v))
                 .Select(v => (v.Colonne, v.Ligne)));

        int lignes = Lignes();

        foreach (var vue in _origines.Keys)
        {
            int c = Math.Max(0, (int)Math.Round(vue.X / PasX));
            int l = Math.Max(0, (int)Math.Round(vue.Y / PasY));

            while (prises.Contains((c, l)))
            {
                l++;
                if (l >= lignes) { l = 0; c++; }
            }

            vue.Colonne = c;
            vue.Ligne = l;

            prises.Add((c, l));
            _places.Poser(vue.Chemin, c, l);
        }

        int posees = _origines.Count;
        _origines.Clear();

        _places.Enregistrer();
        Situer();

        Dire($"{posees} ICÔNE(S) POSÉE(S)");
    }

    /// <summary>
    /// Rend la capture de la souris et referme la bande.
    ///
    /// La première version sortait quand la bande n'était pas armée, donc
    /// un déplacement d'icône — qui capture aussi — ne rendait jamais rien.
    /// La fenêtre gardait la capture, et le clic suivant lui était adressé
    /// au lieu d'aller à l'icône : plus aucun déplacement ne démarrait, et
    /// l'icône semblait revenir à sa place alors qu'elle n'avait pas bougé.
    /// </summary>
    private void Relacher()
    {
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

            // Même précaution que dans l'explorateur : un Run n'est pas
            // un Visual, et le gabarit du bureau pourrait en porter un
            // jour. Le coût est nul, la panne serait identique.
            courant = courant is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(courant)
                : LogicalTreeHelper.GetParent(courant);
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

        // Les cellules ne bougent pas, mais leur traduction en pixels si.
        Situer();
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
                Ranger();
            }));
        }

        racine.Items.Add(Trait());

        racine.Items.Add(Entree(_descendant ? "CROISSANT" : "DÉCROISSANT", null, () =>
        {
            _descendant = !_descendant;
            Ranger();
        }));

        racine.Items.Add(Trait());
        racine.Items.Add(Entree("RANGER MAINTENANT", null, Ranger));

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

    private static string Bureau() => Environment.GetFolderPath(
        Environment.SpecialFolder.DesktopDirectory);

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
    /// Reçoit un dépôt et pose les fichiers à l'endroit lâché.
    ///
    /// Les nouveaux chemins sont relevés en comparant le dossier avant et
    /// après, comme pour le collage : le shell renomme en cas de
    /// collision, et le nom calculé ne serait pas le bon.
    /// </summary>
    private async Task Recevoir(DragEventArgs e)
    {
        e.Handled = true;

        string bureau = Bureau();

        var effet = Depot.Effet(e, bureau);
        if (effet == DragDropEffects.None) return;

        var venus = Depot.Retenir(Depot.Chemins(e.Data), bureau);
        if (venus.Count == 0) return;

        IntPtr poignee = new WindowInteropHelper(this).Handle;
        var ou = e.GetPosition(Grille);

        var avant = Photographier(bureau);

        var issue = effet == DragDropEffects.Move
            ? FileOperationNative.Deplacer(venus, bureau, poignee)
            : FileOperationNative.Copier(venus, bureau, poignee);

        if (!issue.Silencieux)
        {
            Dire($"DÉPÔT REFUSÉ // CODE {issue.Code}");
            return;
        }

        // Poser les arrivants sous le curseur, avant de relire : Disposer
        // respecte une place déjà enregistrée.
        int colonne = Math.Max(0, (int)Math.Round(ou.X / PasX));
        int ligne = Math.Max(0, (int)Math.Round(ou.Y / PasY));

        var prises = new HashSet<(int, int)>(_vues.Select(v => (v.Colonne, v.Ligne)));
        int lignes = Lignes();

        foreach (string arrive in Photographier(bureau).Except(avant))
        {
            while (prises.Contains((colonne, ligne)))
            {
                ligne++;
                if (ligne >= lignes) { ligne = 0; colonne++; }
            }

            _places.Poser(arrive, colonne, ligne);
            prises.Add((colonne, ligne));
        }

        _places.Enregistrer();

        await Lire();
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
