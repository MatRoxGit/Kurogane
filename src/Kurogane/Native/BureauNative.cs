using System.Runtime.InteropServices;
using System.Text;

namespace Kurogane.Native;

/// <summary>
/// Le bureau de Windows : le trouver, l'endormir, le rendre.
///
/// Le bureau n'est pas une image mais une pile de fenêtres appartenant à
/// <c>explorer.exe</c> : un <c>Progman</c>, souvent une quinzaine de
/// <c>WorkerW</c>, et quelque part là-dedans un <c>SHELLDLL_DefView</c> qui
/// contient la <c>SysListView32</c> des icônes. <c>probe-bureau</c> a montré
/// que masquer cette vue prend, tient, et se défait proprement.
///
/// Deux précautions que le relevé impose :
///
/// La vue se cherche à chaque fois. Sur la machine sondée elle était sous
/// <c>Progman</c>, mais elle migre entre <c>Progman</c> et un
/// <c>WorkerW</c> selon le fond d'écran et l'historique de la session.
/// Retenir sa poignée d'un lancement à l'autre reviendrait à parier.
///
/// La couche n'est pas fille de <c>Progman</c>. C'est la technique des
/// moteurs de fonds d'écran animés, mais elle lie notre fenêtre au sort du
/// shell : <c>explorer.exe</c> redémarre, <c>Progman</c> disparaît, et ses
/// filles avec lui. On garde donc une fenêtre indépendante, placée juste
/// au-dessus de <c>Progman</c> dans l'ordre d'empilement.
/// </summary>
internal static class BureauNative
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr fenetre, int commande);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr fenetre, IntPtr apres,
        int x, int y, int largeur, int hauteur, uint drapeaux);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param,
                                                    out WindowNative.RECT valeur,
                                                    uint ecriture);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr fenetre, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr fenetre, int index, IntPtr valeur);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr fenetre, uint message,
                                             IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(WindowNative.EnumWindowProc rappel, IntPtr param);

    private const int MasquerFenetre = 0;   // SW_HIDE
    private const int MontrerFenetre = 5;   // SW_SHOW

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder   = 0x0004;

    private const uint LvmGetItemCount = 0x1004;

    // Métriques de l'écran virtuel : la couche couvre tous les moniteurs,
    // comme le bureau lui-même.
    private const int XVirtuel = 76, YVirtuel = 77, CxVirtuel = 78, CyVirtuel = 79;

    private const int StylesEtendus = -20;          // GWL_EXSTYLE
    private const long FenetreOutil  = 0x00000080;  // WS_EX_TOOLWINDOW

    private const int WM_WINDOWPOSCHANGING = 0x0046;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr Fenetre;
        public IntPtr Apres;
        public int X, Y, Cx, Cy;
        public uint Drapeaux;
    }

    /// <summary>La fenêtre racine du bureau.</summary>
    internal static IntPtr Progman() => WindowNative.FindWindow("Progman", null);

    /// <summary>
    /// La vue des icônes, cherchée là où elle est et non là où elle était
    /// la dernière fois.
    /// </summary>
    internal static IntPtr VueIcones()
    {
        var racines = new List<IntPtr>();

        IntPtr progman = Progman();
        if (progman != IntPtr.Zero) racines.Add(progman);

        WindowNative.EnumWindowProc collecte = (fenetre, _) =>
        {
            if (WindowNative.Classe(fenetre) == "WorkerW") racines.Add(fenetre);
            return true;
        };

        EnumWindows(collecte, IntPtr.Zero);
        GC.KeepAlive(collecte);

        foreach (IntPtr racine in racines)
        {
            IntPtr vue = WindowNative.FindWindowEx(racine, IntPtr.Zero,
                                                   "SHELLDLL_DefView", null);

            if (vue != IntPtr.Zero) return vue;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Combien d'icônes le bureau porte. Lisible sans rien injecter :
    /// LVM_GETITEMCOUNT ne transporte aucun pointeur. Leur texte, lui,
    /// en demanderait un — et donc de la mémoire partagée dans le processus
    /// du shell. C'est la frontière que le projet ne franchit pas, et la
    /// raison pour laquelle nos icônes viendront du dossier Bureau.
    /// </summary>
    internal static int Icones(IntPtr vue)
    {
        if (vue == IntPtr.Zero) return 0;

        IntPtr liste = WindowNative.FindWindowEx(vue, IntPtr.Zero,
                                                 "SysListView32", null);

        if (liste == IntPtr.Zero) return 0;

        return (int)SendMessage(liste, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
    }

    internal static bool Endormir(IntPtr vue) =>
        vue != IntPtr.Zero && ShowWindow(vue, MasquerFenetre);

    internal static bool Reveiller(IntPtr vue) =>
        vue != IntPtr.Zero && ShowWindow(vue, MontrerFenetre);

    internal static bool Eveillee(IntPtr vue) =>
        vue != IntPtr.Zero && WindowNative.IsWindowVisible(vue);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr fenetre, uint message, uint action, IntPtr info);

    private const uint Autoriser = 1;          // MSGFLT_ALLOW
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;

    /// <summary>
    /// Laisse passer le glisser-déposer venu d'un processus moins
    /// privilégié.
    ///
    /// Windows isole les privilèges d'interface : un processus en
    /// intégrité moyenne — l'Explorateur, par exemple — ne peut pas
    /// déposer sur la fenêtre d'un processus élevé. Les messages sont
    /// filtrés avant d'arriver, et la fenêtre ne voit rien : pas d'erreur,
    /// pas de refus visible, juste un dépôt qui n'arrive jamais.
    ///
    /// Lancer Kurogane sans élévation est la vraie réponse — la couche
    /// bureau n'a besoin d'aucun droit particulier. Ce filtre n'est qu'un
    /// filet pour le cas où elle le serait quand même.
    /// </summary>
    internal static void AutoriserDepots(IntPtr fenetre)
    {
        if (fenetre == IntPtr.Zero) return;

        foreach (uint message in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            ChangeWindowMessageFilterEx(fenetre, message, Autoriser, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr fenetre, uint type);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private const uint Racine = 2;   // GA_ROOT

    /// <summary>
    /// La fenêtre de premier niveau sous le curseur.
    ///
    /// La couche couvre tout l'écran : « le curseur a quitté la fenêtre »
    /// n'a donc aucun sens ici. Ce qui compte est de savoir si une autre
    /// fenêtre s'est glissée au-dessus de nous à cet endroit — c'est elle
    /// qui doit recevoir le glissement.
    ///
    /// La requête reste valable pendant une capture de souris : elle est
    /// géométrique, elle ne dépend pas de qui reçoit les événements.
    /// </summary>
    internal static IntPtr SousCurseur()
    {
        if (!GetCursorPos(out POINT ou)) return IntPtr.Zero;

        IntPtr fenetre = WindowFromPoint(ou);

        return fenetre == IntPtr.Zero ? IntPtr.Zero : GetAncestor(fenetre, Racine);
    }

    /// <summary>
    /// La zone de travail de l'écran principal : l'écran moins la barre
    /// des tâches.
    ///
    /// La couche couvre tout l'écran virtuel, barre comprise, pour que le
    /// fond d'écran ne s'arrête pas net. Mais son CONTENU doit s'arrêter
    /// là où la barre commence, sinon les icônes du bas et la ligne d'état
    /// se retrouvent dessous — invisibles sans qu'on comprenne pourquoi.
    /// </summary>
    internal static (int Gauche, int Haut, int Droite, int Bas) Retraits()
    {
        if (!SystemParametersInfo(SpiZoneTravail, 0, out var travail, 0))
            return (0, 0, 0, 0);

        var (x, y, largeur, hauteur) = Etendue();

        // Les retraits se comptent par rapport à l'écran virtuel, qui peut
        // commencer en coordonnées négatives quand un second moniteur est
        // à gauche ou au-dessus.
        return (Math.Max(0, travail.Left - x),
                Math.Max(0, travail.Top - y),
                Math.Max(0, x + largeur - travail.Right),
                Math.Max(0, y + hauteur - travail.Bottom));
    }

    private const uint SpiZoneTravail = 0x0030;   // SPI_GETWORKAREA

    /// <summary>L'écran virtuel entier, en pixels réels.</summary>
    internal static (int X, int Y, int Largeur, int Hauteur) Etendue() =>
        (GetSystemMetrics(XVirtuel), GetSystemMetrics(YVirtuel),
         GetSystemMetrics(CxVirtuel), GetSystemMetrics(CyVirtuel));

    /// <summary>
    /// Place la couche sur tout l'écran virtuel, juste au-dessus de
    /// Progman. Pas HWND_BOTTOM : ce serait passer SOUS Progman, qui peint
    /// le fond d'écran — la couche serait invisible.
    /// </summary>
    internal static void Placer(IntPtr couche)
    {
        IntPtr progman = Progman();
        if (couche == IntPtr.Zero || progman == IntPtr.Zero) return;

        var (x, y, largeur, hauteur) = Etendue();

        SetWindowPos(couche, progman, x, y, largeur, hauteur, SwpNoActivate);
    }

    /// <summary>
    /// Efface la couche du sélecteur de fenêtres. Un bureau n'est pas une
    /// application : le laisser dans Alt+Tab en ferait une fenêtre de plus.
    ///
    /// WS_EX_NOACTIVATE avait été posé dans le même geste, et c'était une
    /// erreur. Une fenêtre non activable ne reçoit jamais le focus clavier,
    /// donc aucune touche n'arrivait : ni Suppr, ni F5, ni Ctrl+V. Le
    /// bureau de Windows, lui, s'active quand on clique dessus — c'est même
    /// ainsi qu'on quitte l'application au premier plan. Le maintien au
    /// fond est assuré par le filtre d'ordre d'empilement, pas par un refus
    /// d'activation.
    /// </summary>
    internal static void Discretion(IntPtr couche)
    {
        if (couche == IntPtr.Zero) return;

        long styles = (long)GetWindowLongPtr(couche, StylesEtendus);

        SetWindowLongPtr(couche, StylesEtendus, (IntPtr)(styles | FenetreOutil));
    }

    /// <summary>
    /// Maintient la couche au fond. Un clic, ou toute autre fenêtre qui
    /// change d'ordre, pousserait sinon la couche vers le haut de la pile :
    /// elle recouvrirait les applications au lieu de les porter.
    ///
    /// Le filtre réécrit la demande au lieu de la corriger après coup, ce
    /// qui évite le clignotement d'une fenêtre remontée puis redescendue.
    /// </summary>
    internal static IntPtr Filtrer(IntPtr fenetre, int message, IntPtr wparam,
                                   IntPtr lparam, ref bool traite)
    {
        if (message != WM_WINDOWPOSCHANGING) return IntPtr.Zero;

        IntPtr progman = Progman();
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        var position = Marshal.PtrToStructure<WINDOWPOS>(lparam);

        position.Apres = progman;
        position.Drapeaux &= ~SwpNoZOrder;

        Marshal.StructureToPtr(position, lparam, false);

        return IntPtr.Zero;
    }
}
