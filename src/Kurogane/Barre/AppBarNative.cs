using System.Runtime.InteropServices;

namespace Kurogane.Barre;

/// <summary>
/// Masquage et restitution de la barre des tâches de Windows.
///
/// On ne touche à rien de persistant : la fenêtre est simplement masquée à
/// l'exécution, et réaffichée à l'arrêt. La bande d'écran qu'elle réserve,
/// elle, reste réservée — cette réservation est indépendante de la
/// visibilité de la fenêtre. C'est exactement ce qu'on veut : notre barre
/// vient se loger dans la bande déjà mise de côté, sans avoir à négocier la
/// zone de travail ni à déplacer les fenêtres de l'utilisateur.
/// </summary>
internal static class AppBarNative
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int commande);

    internal static void Masquer(IntPtr hwnd) => ShowWindow(hwnd, SW_HIDE);
    internal static void Afficher(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOW);

    // ---- Styles de fenêtre

    private const int GWL_EXSTYLE = -20;

    internal const int WS_EX_TOOLWINDOW = 0x00000080;   // hors Alt+Tab
    internal const int WS_EX_NOACTIVATE = 0x08000000;   // ne vole jamais le focus

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr valeur);

    /// <summary>
    /// Ajoute des styles étendus. Sans NOACTIVATE, cliquer sur la barre
    /// retirerait le focus à l'application en cours — ce qu'une barre des
    /// tâches ne fait jamais.
    /// </summary>
    internal static void AjouterStyles(IntPtr hwnd, int styles)
    {
        var actuel = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

        // Le cast en uint évite l'extension de signe : un style dont le bit
        // de poids fort est à 1 remplirait sinon les 32 bits hauts du long.
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(actuel | (uint)styles));
    }

    // ---- Raccourcis clavier simulés

    private const int INPUT_KEYBOARD   = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_S    = 0x53;
    internal const ushort VK_D    = 0x44;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int Type;
        public KEYBDINPUT Clavier;
        public int Bourrage1, Bourrage2;   // taille de l'union MOUSEINPUT
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint   Drapeaux;
        public uint   Temps;
        public IntPtr Extra;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nombre, INPUT[] entrees, int taille);

    /// <summary>
    /// Envoie une combinaison avec la touche Windows. C'est ainsi qu'on
    /// ouvre le menu Démarrer et la recherche : on ne réimplémente pas ces
    /// surfaces, on demande à Windows de les ouvrir.
    /// </summary>
    internal static void Combinaison(params ushort[] touches)
    {
        var entrees = new List<INPUT>();

        foreach (var t in touches)
            entrees.Add(new INPUT { Type = INPUT_KEYBOARD, Clavier = new KEYBDINPUT { Vk = t } });

        for (int i = touches.Length - 1; i >= 0; i--)
            entrees.Add(new INPUT
            {
                Type = INPUT_KEYBOARD,
                Clavier = new KEYBDINPUT { Vk = touches[i], Drapeaux = KEYEVENTF_KEYUP },
            });

        var tableau = entrees.ToArray();
        SendInput((uint)tableau.Length, tableau, Marshal.SizeOf<INPUT>());
    }
}

internal static class PositionNative
{
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER   = 0x0004;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr apres,
        int x, int y, int largeur, int hauteur, uint drapeaux);

    /// <summary>
    /// Place la fenêtre en pixels réels. WPF raisonne en unités
    /// indépendantes du périphérique ; passer par Win32 évite toute
    /// conversion et donc toute erreur de mise à l'échelle.
    /// </summary>
    internal static void Placer(IntPtr hwnd, int x, int y, int largeur, int hauteur) =>
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, largeur, hauteur, SWP_NOACTIVATE);
}
