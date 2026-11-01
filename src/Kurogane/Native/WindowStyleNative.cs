using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Restylage d'une fenêtre vivante : découpage par région et effet de
/// composition.
///
/// Rien n'est écrit sur le disque ni dans le registre. Ces appels modifient
/// une fenêtre en mémoire, le temps qu'elle vit. Arrêter d'appliquer suffit
/// à tout défaire, et un redémarrage d'explorer rend de toute façon la barre
/// à son état d'origine.
///
/// SetWindowCompositionAttribute n'est pas documenté par Microsoft : il est
/// exporté par user32 et utilisé depuis des années par TranslucentTB. Il
/// fonctionne, mais rien ne garantit sa stabilité d'une version à l'autre —
/// d'où un échec traité comme une réponse, pas comme une anomalie.
/// </summary>
internal static class WindowStyleNative
{
    // ---- Région

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateRoundRectRgn(int gauche, int haut, int droite, int bas,
                                                     int largeurEllipse, int hauteurEllipse);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr objet);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redessiner);

    /// <summary>
    /// Applique une région à la fenêtre. Le système s'approprie la région :
    /// une fois posée, elle ne doit plus être libérée par nous.
    /// Une région nulle rend la fenêtre entière.
    /// </summary>
    internal static bool PoserRegion(IntPtr hwnd, IntPtr region) =>
        SetWindowRgn(hwnd, region, true) != 0;

    // ---- Composition

    private enum Attribut { AccentPolicy = 19 }

    internal enum Accent
    {
        Desactive       = 0,
        Degrade         = 1,
        DegradeTransparent = 2,
        Flou            = 3,
        Acrylique       = 4,
        FondHote        = 5,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public Accent Etat;
        public int    Drapeaux;
        public int    Couleur;      // 0xAABBGGRR
        public int    Animation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DonneesComposition
    {
        public Attribut Attribut;
        public IntPtr   Donnees;
        public int      Taille;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd,
        ref DonneesComposition donnees);

    /// <summary>
    /// Pose un effet de composition. <paramref name="couleur"/> est en
    /// 0xAABBGGRR — même ordre que AccentColor, l'inverse de ColorizationColor.
    /// </summary>
    internal static bool PoserAccent(IntPtr hwnd, Accent etat, int couleur)
    {
        var politique = new AccentPolicy
        {
            Etat      = etat,
            Drapeaux  = 2,          // applique la couleur au fond de la fenêtre
            Couleur   = couleur,
            Animation = 0,
        };

        int taille = Marshal.SizeOf<AccentPolicy>();
        IntPtr tampon = Marshal.AllocHGlobal(taille);

        try
        {
            Marshal.StructureToPtr(politique, tampon, false);

            var donnees = new DonneesComposition
            {
                Attribut = Attribut.AccentPolicy,
                Donnees  = tampon,
                Taille   = taille,
            };

            return SetWindowCompositionAttribute(hwnd, ref donnees) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(tampon);
        }
    }
}
