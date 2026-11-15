using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;

namespace Kurogane.Bureau;

/// <summary>
/// La couche bureau de Kurogane — étape 1 : occuper la surface, endormir
/// la vue de Windows, la rendre intacte.
///
/// Parti pris identique à celui de la barre : on masque la vue des icônes,
/// on ne la supprime pas. Rien n'est écrit sur le disque — <c>ShowWindow</c>
/// ne laisse aucune trace — donc il n'y a rien à restaurer, seulement une
/// fenêtre à réafficher. C'est ce que <c>probe-bureau --essai</c> a établi
/// avant qu'une ligne soit écrite : le masquage prend, tient au-delà de
/// trois secondes, et la vue revient intacte.
///
/// Ce que cette étape ne fait pas : afficher les icônes. Elles viendront du
/// dossier Bureau, et c'est un chantier à part.
/// </summary>
public static class BureauApp
{
    /// <summary>
    /// Vrai dès que la vue a été endormie. Statique parce que
    /// <see cref="Rendre"/> doit pouvoir agir depuis ProcessExit, qui n'a
    /// accès à aucun contexte.
    /// </summary>
    private static int _endormie;

    public static int Executer(Journal journal, ThemeDefinition theme)
    {
        journal.Step("Couche bureau Kurogane");
        journal.Info($"thème « {theme.Name} » v{theme.Version}");
        journal.Blank();

        IntPtr vue = BureauNative.VueIcones();

        if (vue == IntPtr.Zero)
        {
            journal.Error("Aucun SHELLDLL_DefView trouvé.");
            journal.Info("Les icônes du bureau sont peut-être déjà masquées, "
                       + "ou le shell n'est pas explorer.exe.");
            return 1;
        }

        int icones = BureauNative.Icones(vue);
        var (x, y, largeur, hauteur) = BureauNative.Etendue();

        journal.Ok($"vue des icônes trouvée — {icones} icône(s)");
        journal.Ok($"écran virtuel : {largeur}×{hauteur} en ({x},{y})");

        if (!BureauNative.Endormir(vue))
        {
            journal.Error("Le masquage de la vue a été refusé.");
            return 1;
        }

        System.Threading.Interlocked.Exchange(ref _endormie, 1);
        journal.Ok("vue de Windows endormie");

        // À partir d'ici le bureau de l'utilisateur est masqué. Quoi qu'il
        // arrive — panne de WPF, exception, arrêt du processus — il doit
        // être rendu. Laisser quelqu'un sans icônes de bureau serait la
        // pire sortie possible pour un projet qui promet la réversibilité.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Rendre(null);

        DispatcherTimer? veille = null;

        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var fenetre = new FenetreBureau(theme);

            fenetre.Dire($"VUE WINDOWS EN SOMMEIL  //  {icones} ICÔNES  //  "
                       + "CLIC DROIT POUR LE MENU  //  CTRL+C POUR RENDRE");

            fenetre.SourceInitialized += (_, _) =>
            {
                IntPtr couche = new WindowInteropHelper(fenetre).Handle;

                BureauNative.Discretion(couche);
                BureauNative.Placer(couche);

                // Le filtre maintient la couche au fond. Sans lui, le
                // premier clic la ferait remonter au-dessus des fenêtres
                // de l'utilisateur, et elle les recouvrirait.
                if (HwndSource.FromHwnd(couche) is { } source)
                    source.AddHook(BureauNative.Filtrer);
            };

            // Explorer redémarre, l'utilisateur rebranche un écran, la
            // résolution change : la vue reparaît et la couche n'est plus
            // à la bonne taille. Même redondance que pour la barre, pour
            // la même raison — un relevé ponctuel ne vaut que l'instant
            // où il a été fait.
            veille = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            veille.Tick += (_, _) => Surveiller(fenetre);
            veille.Start();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                fenetre.Dispatcher.Invoke(fenetre.Close);
            };

            journal.Ok("Couche affichée. Ctrl+C pour rendre le bureau de Windows.");
            journal.Info("Si ce processus est tué sans pouvoir se défendre : "
                       + "« kurogane bureau --reveiller », ou un redémarrage "
                       + "de l'Explorateur.");
            journal.Blank();

            application.Run(fenetre);
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
            journal.Blank();
            journal.Warn("Le bureau de Windows va être rendu avant de quitter.");
            return 1;
        }
        finally
        {
            veille?.Stop();
            Rendre(journal);
        }

        return 0;
    }

    /// <summary>
    /// Rend le bureau de Windows. Appelable plusieurs fois sans dommage :
    /// réafficher une fenêtre déjà visible ne fait rien.
    ///
    /// La vue est cherchée au moment de rendre, et non retenue au moment
    /// d'endormir : si l'Explorateur a redémarré entre-temps, l'ancienne
    /// poignée ne désigne plus rien, et c'est la nouvelle vue qu'il faut
    /// réveiller.
    /// </summary>
    private static void Rendre(Journal? journal)
    {
        // L'échange atomique garantit qu'un seul des deux appelants — le
        // « finally » et ProcessExit — fasse le travail.
        if (System.Threading.Interlocked.Exchange(ref _endormie, 0) == 0) return;

        BureauNative.Reveiller(BureauNative.VueIcones());

        journal?.Step("Arrêt");
        journal?.Ok("Bureau de Windows rendu.");
        journal?.Info("Rien n'a été écrit : il n'y a aucun état à restaurer.");
    }

    /// <summary>
    /// Remet la vue en sommeil si elle est revenue, et recale la couche si
    /// l'écran virtuel a changé.
    /// </summary>
    private static void Surveiller(Window fenetre)
    {
        IntPtr vue = BureauNative.VueIcones();

        if (BureauNative.Eveillee(vue)) BureauNative.Endormir(vue);

        IntPtr couche = new WindowInteropHelper(fenetre).Handle;
        if (couche == IntPtr.Zero) return;

        var (x, y, largeur, hauteur) = BureauNative.Etendue();

        if (!WindowNative.GetWindowRect(couche, out var actuelle)) return;

        bool identique = actuelle.Left == x && actuelle.Top == y
                      && actuelle.Right - actuelle.Left == largeur
                      && actuelle.Bottom - actuelle.Top == hauteur;

        if (!identique)
        {
            BureauNative.Placer(couche);

            // La barre des tâches a pu changer de taille ou de bord en
            // même temps que l'écran.
            if (fenetre is FenetreBureau bureau) bureau.Replier();
        }
    }

    /// <summary>
    /// Porte de sortie : réveille la vue sans rien lancer. Pour le cas où
    /// un processus aurait été tué avant d'avoir pu se défendre.
    /// </summary>
    public static int Reveiller(Journal journal)
    {
        journal.Step("Réveil du bureau");

        IntPtr vue = BureauNative.VueIcones();

        if (vue == IntPtr.Zero)
        {
            journal.Error("Aucun SHELLDLL_DefView trouvé.");
            return 1;
        }

        if (BureauNative.Eveillee(vue))
        {
            journal.Ok("La vue était déjà visible : rien à faire.");
            return 0;
        }

        BureauNative.Reveiller(vue);
        journal.Ok("Vue des icônes rendue.");

        return 0;
    }
}
