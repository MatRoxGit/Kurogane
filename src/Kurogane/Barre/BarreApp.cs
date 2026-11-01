using System.Windows;
using System.Windows.Threading;
using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;

namespace Kurogane.Barre;

/// <summary>
/// Remplace la barre des tâches de Windows par celle de Kurogane.
///
/// Parti pris : on masque la fenêtre de Windows, on ne la désinstalle pas.
/// La bande d'écran qu'elle réserve reste réservée — cette réservation ne
/// dépend pas de la visibilité de la fenêtre — et notre barre vient s'y
/// loger exactement. Résultat : aucune négociation de zone de travail,
/// aucune fenêtre de l'utilisateur déplacée, et un retour arrière qui tient
/// en un appel.
///
/// Rien n'est écrit sur le disque. À l'arrêt, la barre de Windows
/// réapparaît. Un plantage du processus la laisserait masquée : un
/// redémarrage d'explorer la rend de toute façon.
/// </summary>
public static class BarreApp
{
    private const string Classe = "Shell_TrayWnd";
    private const string ClasseSecondaire = "Shell_SecondaryTrayWnd";

    public static int Executer(Journal journal, ThemeDefinition theme)
    {
        var config = theme.Barre ?? new BarreSection();

        journal.Step("Barre des tâches Kurogane");
        journal.Info($"thème « {theme.Name} » v{theme.Version}");

        if (theme.Barre is null)
            journal.Warn("ce thème ne déclare pas de section « barre » : "
                       + "la barre sera vide de ses épingles");

        journal.Blank();

        var barres = Barres();
        if (barres.Count == 0)
        {
            journal.Error("Aucune barre des tâches Windows trouvée.");
            return 1;
        }

        if (!WindowNative.GetWindowRect(barres[0], out var zone))
        {
            journal.Error("Géométrie de la barre Windows illisible.");
            return 1;
        }

        int x = zone.Left, y = zone.Top;
        int largeur = zone.Right - zone.Left;
        int hauteur = zone.Bottom - zone.Top;

        journal.Ok($"bande réservée : {largeur}×{hauteur} en ({x},{y})");

        foreach (var h in barres) AppBarNative.Masquer(h);
        journal.Ok($"{barres.Count} barre(s) Windows masquée(s)");

        // À partir d'ici la barre de Windows est masquée. Quoi qu'il arrive
        // ensuite — panne de WPF, exception, arrêt brutal du processus — elle
        // doit être rendue. Laisser un utilisateur sans barre des tâches
        // serait la pire sortie possible pour un projet qui promet la
        // réversibilité.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Rendre(null);

        DispatcherTimer? veille = null;

        try
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var fenetre = new FenetreBarre(theme, config, journal);

            fenetre.SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(fenetre).Handle;
                PositionNative.Placer(hwnd, x, y, largeur, hauteur);
            };

            // Explorer peut redémarrer, ou Windows réafficher sa barre. On
            // vérifie régulièrement, comme le fait déjà le résident de
            // restylage — la même redondance pour la même raison.
            veille = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            veille.Tick += (_, _) => Replacer(fenetre);
            veille.Start();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                fenetre.Dispatcher.Invoke(fenetre.Close);
            };

            journal.Ok("Barre affichée. Ctrl+C pour rendre celle de Windows.");
            journal.Blank();

            application.Run(fenetre);
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
            journal.Blank();
            journal.Warn("La barre de Windows va être rendue avant de quitter.");
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
    /// Rend la barre de Windows. Appelable plusieurs fois sans dommage :
    /// réafficher une fenêtre déjà visible ne fait rien.
    /// </summary>
    private static void Rendre(Journal? journal)
    {
        foreach (var h in Barres()) AppBarNative.Afficher(h);

        journal?.Step("Arrêt");
        journal?.Ok("Barre de Windows rendue.");
        journal?.Info("Rien n'a été écrit : il n'y a aucun état à restaurer.");
    }

    /// <summary>
    /// Remasque la barre de Windows si elle est revenue, et recale la nôtre
    /// si la bande réservée a changé de taille ou de place.
    /// </summary>
    private static void Replacer(Window fenetre)
    {
        var barres = Barres();
        if (barres.Count == 0) return;

        foreach (var h in barres)
            if (WindowNative.IsWindowVisible(h))
                AppBarNative.Masquer(h);

        if (!WindowNative.GetWindowRect(barres[0], out var zone)) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(fenetre).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (!WindowNative.GetWindowRect(hwnd, out var actuelle)) return;

        bool identique = actuelle.Left == zone.Left && actuelle.Top == zone.Top
                      && actuelle.Right == zone.Right && actuelle.Bottom == zone.Bottom;

        if (!identique)
            PositionNative.Placer(hwnd, zone.Left, zone.Top,
                zone.Right - zone.Left, zone.Bottom - zone.Top);
    }

    private static List<IntPtr> Barres()
    {
        var liste = new List<IntPtr>();

        foreach (var classe in new[] { Classe, ClasseSecondaire })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = WindowNative.FindWindowEx(IntPtr.Zero, h, classe, null)) != IntPtr.Zero)
                liste.Add(h);
        }

        return liste;
    }
}
