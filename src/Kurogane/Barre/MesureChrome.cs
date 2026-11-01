using System.Runtime.InteropServices;
using Kurogane.Core;

namespace Kurogane.Barre;

/// <summary>
/// Lit à l'écran la couleur que porte réellement une barre de titre.
///
/// Un code de retour DWM dit que la valeur a été acceptée, pas qu'elle est
/// affichée : une application qui peint son propre caption la recouvre sans
/// que personne ne signale rien. Seul le pixel tranche. C'est la méthode
/// qui avait déjà réglé la question de la transparence de la barre des
/// tâches, où « appliqué » et « visible » s'étaient révélés deux choses
/// différentes.
///
/// Limite à garder en tête : on lit l'écran, donc une fenêtre recouverte
/// par une autre renvoie la couleur de celle qui est devant.
/// </summary>
internal static class MesureChrome
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribut,
        out RECT valeur, int taille);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")]  private static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>Le rectangle visible de la fenêtre, bordure comprise.</summary>
    private const int EXTENDED_FRAME_BOUNDS = 9;

    private const uint CLR_INVALID = 0xFFFFFFFF;

    internal sealed record Mesure(Rgb? Caption, Rgb? Bordure, int Largeur, int Hauteur);

    internal static Mesure? Lire(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, EXTENDED_FRAME_BOUNDS, out var cadre,
                Marshal.SizeOf<RECT>()) != 0)
            return null;

        int largeur = cadre.Right - cadre.Left;
        int hauteur = cadre.Bottom - cadre.Top;
        if (largeur < 240 || hauteur < 80) return null;

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;

        try
        {
            // Le fond du caption : une ligne horizontale qui évite l'icône à
            // gauche et les trois boutons à droite. Le titre coupe la ligne,
            // d'où la couleur dominante plutôt qu'un point unique.
            var caption = new List<uint>();
            int y = cadre.Top + 12;
            for (int x = cadre.Left + 90; x < cadre.Right - 200; x += 6)
                caption.Add(GetPixel(dc, x, y));

            // Le liseré : les deux colonnes les plus à gauche, sous le
            // caption pour ne pas confondre les deux.
            var bordure = new List<uint>();
            for (int i = 0; i < 16; i++)
            {
                int yy = cadre.Top + hauteur / 3 + i * 3;
                bordure.Add(GetPixel(dc, cadre.Left, yy));
            }

            return new Mesure(Dominante(caption), Dominante(bordure), largeur, hauteur);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static Rgb? Dominante(IEnumerable<uint> brut)
    {
        var comptes = new Dictionary<uint, int>();

        foreach (var v in brut)
        {
            if (v == CLR_INVALID) continue;
            comptes[v] = comptes.GetValueOrDefault(v) + 1;
        }

        if (comptes.Count == 0) return null;

        uint c = comptes.MaxBy(p => p.Value).Key;

        // COLORREF : 0x00BBGGRR, le rouge est l'octet de poids faible.
        return new Rgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
    }
}
