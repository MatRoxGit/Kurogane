using System.Runtime.InteropServices;

namespace Kurogane.Barre;

/// <summary>
/// Miniatures DWM : l'aperçu en direct d'une fenêtre, dessiné par le
/// compositeur dans une autre fenêtre.
///
/// C'est le mécanisme qu'emploie la barre de Windows elle-même. On
/// n'effectue aucune capture d'écran : on demande au gestionnaire de
/// composition de redessiner le contenu vivant de la fenêtre source dans un
/// rectangle de la nôtre. L'aperçu est donc animé, et gratuit.
///
/// Contrainte à connaître : la fenêtre de destination ne doit PAS être en
/// transparence par couche — AllowsTransparency dans WPF. Une fenêtre
/// transparente est composée hors du chemin DWM, et la miniature n'y
/// apparaît jamais.
/// </summary>
internal static class ApercuNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE { public int Largeur, Hauteur; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Proprietes
    {
        public int  Drapeaux;
        public RECT Destination;
        public RECT Source;
        public byte Opacite;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool ZoneClienteSeule;
    }

    private const int DESTINATION = 0x01;
    private const int OPACITE     = 0x04;
    private const int VISIBLE     = 0x08;
    private const int ZONE_CLIENTE = 0x10;

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source,
        out IntPtr miniature);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr miniature);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr miniature,
        ref Proprietes proprietes);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr miniature, out SIZE taille);

    /// <summary>Renvoie le descripteur de miniature, ou zéro en cas d'échec.</summary>
    internal static IntPtr Inscrire(IntPtr destination, IntPtr source) =>
        DwmRegisterThumbnail(destination, source, out var m) == 0 ? m : IntPtr.Zero;

    internal static void Retirer(IntPtr miniature)
    {
        if (miniature != IntPtr.Zero) DwmUnregisterThumbnail(miniature);
    }

    internal static SIZE TailleSource(IntPtr miniature) =>
        DwmQueryThumbnailSourceSize(miniature, out var t) == 0 ? t : new SIZE();

    /// <summary>Place la miniature dans la fenêtre de destination, en pixels réels.</summary>
    internal static void Placer(IntPtr miniature, int gauche, int haut, int droite, int bas)
    {
        var p = new Proprietes
        {
            Drapeaux         = DESTINATION | OPACITE | VISIBLE | ZONE_CLIENTE,
            Destination      = new RECT { Left = gauche, Top = haut, Right = droite, Bottom = bas },
            Opacite          = 255,
            Visible          = true,
            ZoneClienteSeule = true,
        };

        DwmUpdateThumbnailProperties(miniature, ref p);
    }
}
