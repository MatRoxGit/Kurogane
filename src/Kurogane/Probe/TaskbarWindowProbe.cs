using System.Diagnostics;
using Kurogane.Core;
using Kurogane.Native;

namespace Kurogane.Probe;

/// <summary>
/// Hiérarchie des fenêtres de la barre des tâches — LECTURE SEULE.
///
/// Préalable au restylage à chaud. Les outils qui transforment réellement
/// l'apparence de la barre — TranslucentTB, RoundedTB — n'écrivent rien
/// nulle part : ils agissent sur la fenêtre vivante. Pour ça il faut
/// d'abord savoir quelles fenêtres existent, lesquelles sont visibles, et
/// à quel processus elles appartiennent.
///
/// Aucune fenêtre n'est modifiée ici.
/// </summary>
internal static class TaskbarWindowProbe
{
    private static readonly string[] Racines =
    {
        "Shell_TrayWnd",            // barre principale
        "Shell_SecondaryTrayWnd",   // barres des écrans secondaires
    };

    internal static int Executer(Journal journal)
    {
        journal.Step("Hiérarchie des fenêtres de la barre des tâches");
        journal.Info("Lecture seule : aucune fenêtre n'est modifiée.");
        journal.Blank();

        int trouvees = 0;

        foreach (var classe in Racines)
        {
            IntPtr h = IntPtr.Zero;

            while ((h = WindowNative.FindWindowEx(IntPtr.Zero, h, classe, null)) != IntPtr.Zero)
            {
                trouvees++;
                Decrire(journal, h, classe);
            }
        }

        if (trouvees == 0)
        {
            journal.Error("Aucune fenêtre de barre des tâches trouvée.");
            return 1;
        }

        journal.Step("Lecture");
        journal.Info("Les classes à retenir pour la suite :");
        journal.Info("  Shell_TrayWnd          la barre elle-même, cible du restylage");
        journal.Info("  Windows.UI.Composition.DesktopWindowContentBridge");
        journal.Info("                         le contenu XAML moderne, posé par-dessus");
        journal.Info("  TrayNotifyWnd          la zone de notification");
        journal.Info("  Start / TrayButton     boutons natifs, s'il en reste");
        journal.Blank();
        journal.Info("Une fenêtre fille visible peut être masquée sans rien écrire —");
        journal.Info("c'est la voie pour les widgets, que UCPD interdit par le registre.");

        return 0;
    }

    private static void Decrire(Journal journal, IntPtr racine, string classe)
    {
        var (l, t, w, hh) = Geometrie(racine);
        WindowNative.GetWindowThreadProcessId(racine, out uint pid);

        journal.Step($"{classe}   0x{racine.ToInt64():X}");
        journal.Info($"{w}×{hh} px en ({l},{t}) — processus {Processus(pid)} ({pid})");
        journal.Blank();

        var enfants = WindowNative.Enfants(racine);
        journal.Ok($"{enfants.Count} fenêtres filles");

        foreach (var e in enfants)
        {
            var (el, et, ew, eh) = Geometrie(e);
            string nom = WindowNative.Classe(e);
            string titre = WindowNative.Titre(e);
            string visible = WindowNative.IsWindowVisible(e) ? "visible" : "masquée";

            WindowNative.GetWindowThreadProcessId(e, out uint epid);
            string autre = epid != pid ? $"  [processus {Processus(epid)}]" : "";

            journal.Info($"  {nom,-52} {ew,5}×{eh,-4} {visible,-8}"
                       + (titre.Length > 0 ? $" « {titre} »" : "") + autre);
        }

        journal.Blank();
    }

    private static (int L, int T, int W, int H) Geometrie(IntPtr hwnd)
    {
        if (!WindowNative.GetWindowRect(hwnd, out var r)) return (0, 0, 0, 0);
        return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static string Processus(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return "?"; }
    }
}
