using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Kurogane.Core;

namespace Kurogane.Probe;

/// <summary>
/// Sonde le bureau de Windows — LECTURE SEULE par défaut.
///
/// Le bureau n'est pas une image : c'est une pile de fenêtres appartenant
/// à <c>explorer.exe</c>. Un <c>Progman</c>, parfois un ou plusieurs
/// <c>WorkerW</c>, et quelque part là-dedans un <c>SHELLDLL_DefView</c>
/// qui contient la <c>SysListView32</c> des icônes. Thématiser le menu
/// contextuel de cette liste demanderait de se greffer dans le processus
/// du shell — ce que le projet s'interdit.
///
/// La voie compatible est de posséder le bureau : dessiner les icônes
/// nous-mêmes et endormir la vue de Windows. Avant d'écrire cette couche,
/// trois choses doivent être établies plutôt que supposées :
///
/// 1. où vit réellement <c>SHELLDLL_DefView</c> sur cette machine — il
///    migre entre <c>Progman</c> et un <c>WorkerW</c> selon le fond
///    d'écran et l'historique de la session ;
/// 2. si masquer cette vue tient, ou si le shell la rétablit ;
/// 3. si le masquage se défait proprement.
///
/// Le point 2 décide de tout le reste. S'il ne tient pas, la couche
/// Kurogane s'afficherait sous — ou par-dessus — des icônes Windows
/// toujours vivantes.
/// </summary>
internal static class BureauProbe
{
    private delegate bool RappelFenetre(IntPtr fenetre, IntPtr parametre);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(RappelFenetre rappel, IntPtr parametre);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, RappelFenetre rappel,
                                                IntPtr parametre);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr fenetre, StringBuilder nom, int taille);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr fenetre, StringBuilder nom, int taille);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr fenetre);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr fenetre);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr fenetre, out RECT zone);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr fenetre, int commande);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr apres,
                                              string? classe, string? titre);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr fenetre, uint message,
                                             IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr fenetre, out uint processus);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Gauche, Haut, Droite, Bas; }

    private const int MasquerFenetre = 0;    // SW_HIDE
    private const int MontrerFenetre = 5;    // SW_SHOW

    private const uint LvmGetItemCount = 0x1004;   // LVM_GETITEMCOUNT

    private sealed record Noeud(IntPtr Poignee, string Classe, string Titre,
                                bool Visible, RECT Zone, uint Processus);

    private static string Nom(IntPtr fenetre)
    {
        var tampon = new StringBuilder(256);
        GetClassName(fenetre, tampon, tampon.Capacity);
        return tampon.ToString();
    }

    private static string Titre(IntPtr fenetre)
    {
        var tampon = new StringBuilder(256);
        GetWindowText(fenetre, tampon, tampon.Capacity);
        return tampon.ToString();
    }

    private static Noeud Decrire(IntPtr fenetre)
    {
        GetWindowRect(fenetre, out RECT zone);
        GetWindowThreadProcessId(fenetre, out uint processus);

        return new Noeud(fenetre, Nom(fenetre), Titre(fenetre),
                         IsWindowVisible(fenetre), zone, processus);
    }

    private static List<IntPtr> Enfants(IntPtr parent)
    {
        var trouves = new List<IntPtr>();

        EnumChildWindows(parent, (fenetre, _) =>
        {
            trouves.Add(fenetre);
            return true;
        }, IntPtr.Zero);

        return trouves;
    }

    /// <summary>Inventaire, et si demandé un essai de masquage réversible.</summary>
    internal static int Executer(Journal journal, bool essai)
    {
        journal.Step("BUREAU — INVENTAIRE");

        // -------------------------------------------------- les racines

        var racines = new List<Noeud>();

        EnumWindows((fenetre, _) =>
        {
            string classe = Nom(fenetre);

            if (classe is "Progman" or "WorkerW")
                racines.Add(Decrire(fenetre));

            return true;
        }, IntPtr.Zero);

        journal.Info($"Fenêtres racines du bureau : {racines.Count}");
        journal.Blank();

        foreach (var racine in racines)
        {
            journal.Info($"  {racine.Classe,-10} 0x{racine.Poignee:X}  " +
                          $"PID {racine.Processus}  " +
                          $"{(racine.Visible ? "visible" : "masquée")}  " +
                          $"{racine.Zone.Droite - racine.Zone.Gauche}" +
                          $"×{racine.Zone.Bas - racine.Zone.Haut}");

            if (racine.Titre.Length > 0)
                journal.Info($"             titre « {racine.Titre} »");
        }

        // ---------------------------------- où se trouve la vue des icônes

        journal.Blank();
        journal.Step("LA VUE DES ICÔNES");

        IntPtr vue = IntPtr.Zero;
        IntPtr hote = IntPtr.Zero;

        foreach (var racine in racines)
        {
            foreach (IntPtr enfant in Enfants(racine.Poignee))
            {
                if (Nom(enfant) != "SHELLDLL_DefView") continue;

                vue = enfant;
                hote = racine.Poignee;
                break;
            }

            if (vue != IntPtr.Zero) break;
        }

        if (vue == IntPtr.Zero)
        {
            journal.Info("Aucun SHELLDLL_DefView trouvé.");
            journal.Info("Les icônes du bureau sont peut-être déjà masquées,");
            journal.Info("ou le shell n'est pas explorer.exe.");
            return 0;
        }

        journal.Info($"SHELLDLL_DefView 0x{vue:X}, hébergé par " +
                      $"{Nom(hote)} 0x{hote:X}");

        IntPtr liste = FindWindowEx(vue, IntPtr.Zero, "SysListView32", null);

        if (liste != IntPtr.Zero)
        {
            IntPtr nombre = SendMessage(liste, LvmGetItemCount,
                                        IntPtr.Zero, IntPtr.Zero);

            journal.Info($"SysListView32 0x{liste:X} — {nombre} icônes");
            journal.Info("Le compte se lit sans injection : LVM_GETITEMCOUNT");
            journal.Info("ne transporte aucun pointeur. Lire le TEXTE d'un");
            journal.Info("élément en demanderait un, et donc de la mémoire");
            journal.Info("partagée dans le processus du shell — c'est la");
            journal.Info("frontière que le projet ne franchit pas. Nos icônes");
            journal.Info("viendront du dossier Bureau, pas de cette liste.");
        }
        else
        {
            journal.Info("Pas de SysListView32 sous la vue.");
        }

        // ------------------------------------------- l'état côté registre

        journal.Blank();
        journal.Step("RÉGLAGE DÉCLARÉ");

        using (var cle = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
        {
            object? cache = cle?.GetValue("HideIcons");

            journal.Info(cache is null
                ? "HideIcons : absent (icônes affichées)"
                : $"HideIcons : {cache}");
        }

        if (!essai)
        {
            journal.Blank();
            journal.Info("Essai de masquage non demandé. --essai pour le faire.");
            return 0;
        }

        // ---------------------------------------- l'essai, posé puis défait

        journal.Blank();
        journal.Step("ESSAI — MASQUAGE RÉVERSIBLE");
        journal.Info("La vue est masquée trois secondes, puis rétablie.");
        journal.Info("Rien n'est écrit : ShowWindow ne laisse pas de trace.");
        journal.Blank();

        bool avant = IsWindowVisible(vue);
        journal.Info($"Avant  : {(avant ? "visible" : "masquée")}");

        ShowWindow(vue, MasquerFenetre);
        System.Threading.Thread.Sleep(300);

        bool juste = IsWindowVisible(vue);
        journal.Info($"Après  : {(juste ? "visible" : "masquée")}" +
                      (juste ? "  ← le masquage n'a pas pris" : ""));

        System.Threading.Thread.Sleep(2700);

        bool tenu = IsWindow(vue) && !IsWindowVisible(vue);
        journal.Info($"3 s    : {(tenu ? "toujours masquée" : "rétablie par le shell")}");

        ShowWindow(vue, MontrerFenetre);
        System.Threading.Thread.Sleep(300);

        bool rendu = IsWindowVisible(vue);
        journal.Info($"Rendu  : {(rendu ? "visible" : "toujours masquée")}" +
                      (rendu ? "" : "  ← À SIGNALER"));

        journal.Blank();
        journal.Step("CE QUE L'ESSAI DÉCIDE");

        if (!juste)
            journal.Info("Le masquage prend. " + (tenu
                ? "Il tient : une couche Kurogane peut occuper le bureau."
                : "Il ne tient pas : le shell rétablit la vue, il faudra " +
                  "soit la masquer en continu, soit passer par HideIcons."));
        else
            journal.Info("Le masquage ne prend pas : cette voie est fermée, " +
                          "il faudra en chercher une autre.");

        if (!rendu)
            journal.Info("ATTENTION : la vue n'est pas revenue. " +
                          "Redémarrer l'Explorateur la rétablira.");

        return 0;
    }
}
