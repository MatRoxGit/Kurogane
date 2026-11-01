using Kurogane.Native;

namespace Kurogane.Core;

/// <summary>
/// Compagnon résident : maintient le restylage de la barre des tâches.
///
/// Le restylage ne survit pas à un redémarrage d'explorer, ni à un
/// changement de résolution — rien n'étant écrit, rien ne persiste. Ce
/// processus comble l'écart : il repose le style chaque fois que la barre
/// réapparaît.
///
/// Deux déclencheurs, volontairement redondants :
///
///   - le message « TaskbarCreated », que le shell diffuse à toutes les
///     fenêtres de premier niveau quand il recrée la barre. C'est la voie
///     rapide, mais elle suppose que le shell le diffuse bien ;
///   - une vérification périodique qui compare la fenêtre et sa géométrie
///     à ce qui a été appliqué. Plus lente, mais elle rattrape tout ce que
///     le message laisserait passer — changement de DPI, de résolution,
///     recréation silencieuse.
///
/// Rien n'est écrit sur le disque. À l'arrêt, le style est retiré et la
/// barre retrouve son apparence d'origine.
/// </summary>
public static class TaskbarResident
{
    private const uint IntervalleMs = 2000;
    private static readonly IntPtr IdMinuteur = new(1);

    // Le délégué doit rester joignable tant que la fenêtre vit : sans
    // référence vivante, le ramasse-miettes peut le libérer alors que
    // Windows détient encore son pointeur.
    private static MessageWindowNative.WndProc? _procedure;

    private static uint _taskbarCreated;
    private static TaskbarStyle.Reglage? _reglage;
    private static Journal? _journal;
    private static IntPtr _fenetre;

    private static IntPtr _derniereBarre;
    private static int _derniereLargeur, _derniereHauteur;
    private static int _reapplications;

    public static int Executer(Journal journal, TaskbarStyle.Reglage reglage)
    {
        _journal = journal;
        _reglage = reglage;

        journal.Step("Compagnon résident — barre des tâches");
        journal.Info("Le style est reposé à chaque réapparition de la barre.");
        journal.Info("Rien n'est écrit : à l'arrêt, la barre retrouve son apparence.");
        journal.Blank();

        if (!CreerFenetre(journal)) return 1;

        _taskbarCreated = MessageWindowNative.RegisterWindowMessage("TaskbarCreated");
        MessageWindowNative.SetTimer(_fenetre, IdMinuteur, IntervalleMs, IntPtr.Zero);

        // Ctrl+C doit retirer le style avant de rendre la main, sans quoi on
        // laisserait une barre découpée derrière soi.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            journal.Blank();
            journal.Info("Arrêt demandé.");
            MessageWindowNative.PostMessage(_fenetre, MessageWindowNative.WM_DESTROY,
                                            IntPtr.Zero, IntPtr.Zero);
        };

        Appliquer(premiere: true);

        journal.Blank();
        journal.Ok("En veille. Ctrl+C pour arrêter et rendre la barre d'origine.");
        journal.Blank();

        while (MessageWindowNative.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            MessageWindowNative.DispatchMessage(ref message);

        journal.Step("Arrêt");
        journal.Info($"{_reapplications} réapplication(s) pendant la session.");
        TaskbarStyle.Reinitialiser(journal);

        return 0;
    }

    private static bool CreerFenetre(Journal journal)
    {
        _procedure = Procedure;

        var classe = new MessageWindowNative.WNDCLASSEX
        {
            cbSize        = System.Runtime.InteropServices.Marshal.SizeOf<MessageWindowNative.WNDCLASSEX>(),
            lpfnWndProc   = _procedure,
            lpszClassName = "KuroganeResident",
        };

        if (MessageWindowNative.RegisterClassEx(ref classe) == 0)
        {
            journal.Error("Enregistrement de la classe de fenêtre refusé.");
            return false;
        }

        _fenetre = MessageWindowNative.CreateWindowEx(0, "KuroganeResident", null, 0,
            0, 0, 0, 0, MessageWindowNative.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_fenetre == IntPtr.Zero)
        {
            journal.Error("Création de la fenêtre de messages refusée.");
            return false;
        }

        return true;
    }

    private static IntPtr Procedure(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            _journal?.Info("La barre a été recréée par le shell.");

            // Le shell diffuse le message avant d'avoir fini sa mise en page.
            // Appliquer immédiatement poserait une région aux mauvaises
            // dimensions, qu'il faudrait de toute façon refaire.
            System.Threading.Thread.Sleep(1500);
            Appliquer(premiere: false);
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case MessageWindowNative.WM_TIMER:
                if (ABouge()) Appliquer(premiere: false);
                return IntPtr.Zero;

            case MessageWindowNative.WM_DISPLAYCHANGE:
                _journal?.Info("Changement d'affichage.");
                Appliquer(premiere: false);
                return IntPtr.Zero;

            case MessageWindowNative.WM_DESTROY:
                MessageWindowNative.KillTimer(hwnd, IdMinuteur);
                MessageWindowNative.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return MessageWindowNative.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// La barre a-t-elle changé depuis la dernière application ? Une fenêtre
    /// différente signale une recréation, une géométrie différente un
    /// changement de résolution ou de mise à l'échelle.
    /// </summary>
    private static bool ABouge()
    {
        var barre = WindowNative.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (barre == IntPtr.Zero) return false;

        if (!WindowNative.GetWindowRect(barre, out var r)) return false;

        int largeur = r.Right - r.Left;
        int hauteur = r.Bottom - r.Top;

        return barre != _derniereBarre
            || largeur != _derniereLargeur
            || hauteur != _derniereHauteur;
    }

    private static void Appliquer(bool premiere)
    {
        if (_reglage is null || _journal is null) return;

        if (!premiere) _reapplications++;

        TaskbarStyle.Appliquer(_journal, _reglage, silencieux: !premiere);

        var barre = WindowNative.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        _derniereBarre = barre;

        if (barre != IntPtr.Zero && WindowNative.GetWindowRect(barre, out var r))
        {
            _derniereLargeur = r.Right - r.Left;
            _derniereHauteur = r.Bottom - r.Top;
        }
    }
}
