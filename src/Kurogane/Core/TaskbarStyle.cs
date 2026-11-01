using Kurogane.Native;

namespace Kurogane.Core;

/// <summary>
/// Restylage à chaud de la barre des tâches : barre flottante à coins
/// arrondis, et teinte de composition.
///
/// Rien n'est persisté. C'est un changement sur la fenêtre vivante, qui
/// disparaît dès qu'explorer redémarre — ce qui en fait, paradoxalement, le
/// mécanisme le plus réversible du projet : il n'y a aucun état à restaurer,
/// seulement un effet à cesser d'appliquer.
///
/// Conséquence de cette nature : l'effet ne survit ni à un redémarrage
/// d'explorer, ni à un changement de résolution, ni au passage en mode
/// tablette. Un compagnon résident devra le reposer sur l'événement
/// « TaskbarCreated ». Cette commande est le préalable : elle sert à voir
/// le résultat avant d'écrire le résident.
/// </summary>
public static class TaskbarStyle
{
    public sealed record Reglage(
        int  MargeLaterale,
        int  MargeBasse,
        int  Rayon,
        Rgb? Teinte,
        byte Opacite,
        bool Acrylique);

    private const string ClassePrincipale = "Shell_TrayWnd";
    private const string ClasseSecondaire = "Shell_SecondaryTrayWnd";

    /// <param name="silencieux">
    /// Réduit le journal à une ligne. Le compagnon résident repose le style
    /// plusieurs fois par session : le détail complet à chaque fois noierait
    /// les événements qui comptent.
    /// </param>
    public static int Appliquer(Journal journal, Reglage r, bool silencieux = false)
    {
        if (!silencieux)
        {
            journal.Step("Restylage de la barre des tâches");
            journal.Info("Aucune écriture : l'effet porte sur la fenêtre vivante.");
            journal.Blank();
        }

        var barres = Barres();
        if (barres.Count == 0)
        {
            journal.Error("Aucune barre des tâches trouvée.");
            return 1;
        }

        foreach (var (hwnd, classe) in barres)
        {
            if (!WindowNative.GetWindowRect(hwnd, out var rect))
            {
                journal.Warn($"{classe} : géométrie illisible, ignorée");
                continue;
            }

            int largeur = rect.Right - rect.Left;
            int hauteur = rect.Bottom - rect.Top;

            if (!silencieux) journal.Step($"{classe}   {largeur}×{hauteur}");

            // --- Découpage : la région est en coordonnées de la fenêtre.
            //     Découper le parent découpe aussi ses enfants, pont XAML
            //     compris — c'est ce qui donne la barre flottante.
            if (r.MargeLaterale > 0 || r.MargeBasse > 0 || r.Rayon > 0)
            {
                int l = r.MargeLaterale;
                int h = r.MargeBasse;

                if (largeur - 2 * l < 32 || hauteur - 2 * h < 8)
                {
                    journal.Warn("marges trop grandes pour cette barre, découpage ignoré");
                }
                else
                {
                    var region = WindowStyleNative.CreateRoundRectRgn(
                        l, h, largeur - l, hauteur - h, r.Rayon * 2, r.Rayon * 2);

                    if (region == IntPtr.Zero)
                        journal.Error("création de la région impossible");
                    else if (!WindowStyleNative.PoserRegion(hwnd, region))
                    {
                        journal.Error("pose de la région refusée");
                        WindowStyleNative.DeleteObject(region);
                    }
                    else if (silencieux)
                        journal.Ok($"style reposé — {largeur - 2 * l}×{hauteur - 2 * h}");
                    else
                        journal.Ok($"barre flottante : {largeur - 2 * l}×{hauteur - 2 * h}, "
                                 + $"rayon {r.Rayon}");
                }
            }

            // --- Teinte
            if (r.Teinte is { } couleur)
            {
                int abgr = unchecked((int)(((uint)r.Opacite << 24)
                                         | ((uint)couleur.B << 16)
                                         | ((uint)couleur.G << 8)
                                         | couleur.R));

                var etat = r.Acrylique
                    ? WindowStyleNative.Accent.Acrylique
                    : WindowStyleNative.Accent.DegradeTransparent;

                // Le pont XAML peint un fond opaque par-dessus la fenêtre
                // parente : teinter le parent seul ne se voit pas. Mesuré le
                // 01/11/2026 — la barre restait uniformément (27,27,27)
                // alors que le fond d'écran variait derrière.
                //
                // On applique donc la composition aux deux, parent et pont.
                int poses = 0;

                if (WindowStyleNative.PoserAccent(hwnd, etat, abgr)) poses++;

                var pont = WindowNative.FindWindowEx(hwnd, IntPtr.Zero,
                    "Windows.UI.Composition.DesktopWindowContentBridge", null);

                if (pont != IntPtr.Zero && WindowStyleNative.PoserAccent(pont, etat, abgr))
                    poses++;

                bool pontTeinte = poses == 2;

                if (poses > 0 && !silencieux)
                {
                    journal.Ok($"teinte {couleur} à {r.Opacite * 100 / 255} % "
                             + $"({(r.Acrylique ? "acrylique" : "dégradé")}) — "
                             + $"{poses} fenêtre(s) sur 2");

                    // Mesuré le 01/11/2026 sur Windows 11 22H2 : sans le pont,
                    // la barre reste parfaitement opaque — (28,28,28) sur toute
                    // sa largeur alors que le fond d'écran varie derrière. Le
                    // fond est peint par le compositeur XAML, à une couche où
                    // l'attribut n'a plus prise. Autant le dire franchement
                    // plutôt que de laisser croire à un succès.
                    if (!pontTeinte)
                    {
                        journal.Warn("le pont XAML a refusé la composition : la teinte");
                        journal.Warn("n'aura très probablement aucun effet visible.");
                        journal.Info("Sur Windows 11, la couleur de la barre suit l'accent");
                        journal.Info("du système — section « accent », taskbarAndStart.");
                    }
                }
                if (poses == 0)
                    journal.Warn("teinte refusée — SetWindowCompositionAttribute n'est pas "
                               + "documenté, l'échec est une réponse possible");
            }

            if (!silencieux) journal.Blank();
        }

        if (silencieux) return 0;

        journal.Step("Portée de l'effet");
        journal.Info("Il ne survit pas à un redémarrage d'explorer, ni à un changement");
        journal.Info("de résolution. Pour qu'il tienne, lance le compagnon résident :");
        journal.Info("  kurogane resident --theme themes\\kurogane-obsidian.json");
        journal.Blank();
        journal.Info("Pour tout défaire : kurogane taskbar-style --reinitialiser");

        return 0;
    }

    public static int Reinitialiser(Journal journal)
    {
        journal.Step("Retour à la barre d'origine");

        var barres = Barres();
        if (barres.Count == 0) { journal.Error("Aucune barre des tâches trouvée."); return 1; }

        foreach (var (hwnd, classe) in barres)
        {
            WindowStyleNative.PoserRegion(hwnd, IntPtr.Zero);
            WindowStyleNative.PoserAccent(hwnd, WindowStyleNative.Accent.Desactive, 0);
            journal.Ok($"{classe} : région retirée, composition désactivée");
        }

        journal.Blank();
        journal.Info("Si un résidu subsiste, un redémarrage d'explorer rend la barre");
        journal.Info("à son état d'origine : rien n'a jamais été écrit nulle part.");

        return 0;
    }

    private static List<(IntPtr Hwnd, string Classe)> Barres()
    {
        var liste = new List<(IntPtr, string)>();

        foreach (var classe in new[] { ClassePrincipale, ClasseSecondaire })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = WindowNative.FindWindowEx(IntPtr.Zero, h, classe, null)) != IntPtr.Zero)
                liste.Add((h, classe));
        }

        return liste;
    }
}
