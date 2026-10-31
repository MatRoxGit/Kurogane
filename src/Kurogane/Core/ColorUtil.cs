using System.Globalization;

namespace Kurogane.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Conversions de couleurs vers les formats attendus par Windows.
///
/// Piege central : les deux familles de valeurs n'utilisent pas le meme ordre.
///   - DWM\AccentColor et Explorer\Accent\AccentColorMenu : 0xFFBBGGRR (ABGR)
///   - DWM\ColorizationColor                              : 0xAARRGGBB (ARGB)
/// Inverser les deux donne un bleu la ou on attend un rouge.
/// </summary>
public static class ColorUtil
{
    public static Rgb Parse(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !s.All(Uri.IsHexDigit))
            throw new FormatException($"Couleur invalide : « {hex} » (format attendu : #RRGGBB).");

        return new Rgb(
            byte.Parse(s[..2],            NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 0xFFBBGGRR — pour AccentColor / AccentColorMenu / StartColorMenu.
    /// L'octet de poids fort vaut 0xFF : c'est ce que Windows ecrit lui-meme
    /// dans ces trois valeurs. Le laisser a 0x00 produit une couleur que le
    /// shell peut traiter comme entierement transparente.
    /// </summary>
    public static int ToAbgr(Rgb c) =>
        unchecked((int)(0xFF000000u | ((uint)c.B << 16) | ((uint)c.G << 8) | c.R));

    /// <summary>
    /// 0xAARRGGBB — pour ColorizationColor / ColorizationAfterglow.
    /// Alpha 0xC4 par defaut : c'est la valeur que Windows ecrit lui-meme
    /// quand l'utilisateur choisit une couleur.
    /// </summary>
    public static int ToArgb(Rgb c, byte alpha = 0xC4) =>
        unchecked((int)(((uint)alpha << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

    // ------------------------------------------------------------------
    //  AccentPalette
    // ------------------------------------------------------------------
    //
    //  Structure non documentee par Microsoft, reconstituee par recoupement de
    //  deux installations reelles : Windows 11 22H2 (build 22621, accent bleu
    //  par defaut #0078D4) et 26H2 (build 26300, accent personnalise).
    //
    //  32 octets = 8 entrees de 4 octets, dans l'ordre R, G, B, 0x00.
    //  Les index 0 a 6 vont du plus CLAIR au plus SOMBRE.
    //
    //  L'accent lui-meme est a l'index 3 ; le menu Demarrer utilise l'index 4,
    //  une nuance plus sombre — d'ou StartColorMenu qui ne vaut PAS l'accent.
    //
    //  Algorithme : saturation constante, luminosite decalee, teinte legerement
    //  pivotee (vers le cyan pour les nuances claires, vers le violet pour les
    //  sombres). Les deux tables ci-dessous sont relevees sur la palette bleue
    //  generee par Windows ; appliquees a #0078D4 elles la reproduisent octet
    //  pour octet, les sept nuances.
    //
    //  L'index 7 portait une couleur etrangere a la rampe sur les DEUX releves
    //  (orange sur l'un, magenta sur l'autre). Ce n'est pas une nuance de
    //  l'accent et son usage nous echappe : on preserve la valeur en place.

    /// <summary>Index de l'accent lui-meme dans le nuancier.</summary>
    public const int AccentIndex = 3;

    /// <summary>Index utilise par le menu Demarrer (une nuance plus sombre).</summary>
    public const int StartMenuIndex = 4;

    private static readonly double[] LightnessOffsets =
        { +0.38431, +0.23333, +0.07059, 0.0, -0.03922, -0.12941, -0.21176 };

    private static readonly double[] HueOffsetsDeg =
        { -14.2, -5.5, -1.1, 0.0, +1.8, +8.5, +19.0 };

    /// <summary>
    /// Ordre des octets de chaque entree. Confirme en RGBA sur les deux
    /// releves. Le point de bascule reste en place au cas ou une version
    /// ulterieure de Windows differe.
    /// </summary>
    public enum ByteOrder { Rgba, Bgra }

    public static readonly ByteOrder PaletteOrder = ByteOrder.Rgba;

    /// <summary>Nuance du rang demande (0 = la plus claire, 6 = la plus sombre).</summary>
    public static Rgb PaletteShade(Rgb accent, int index)
    {
        if (index < 0 || index >= LightnessOffsets.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Rang hors du nuancier : {index} (attendu 0 a {LightnessOffsets.Length - 1}).");

        var (h, s, l) = ToHsl(accent);
        return FromHsl(h + HueOffsetsDeg[index], s,
                       Math.Clamp(l + LightnessOffsets[index], 0.0, 1.0));
    }

    /// <summary>
    /// Construit les 32 octets d'AccentPalette. <paramref name="existing"/> est
    /// la valeur actuellement en place : ses quatre derniers octets sont repris
    /// tels quels, l'index 7 n'etant pas une nuance de l'accent. Si elle est
    /// absente ou malformee, on y place la nuance la plus sombre.
    /// </summary>
    public static byte[] BuildAccentPalette(Rgb accent, byte[]? existing = null)
    {
        var bytes = new byte[32];

        for (int i = 0; i < 7; i++)
            Write(bytes, i, PaletteShade(accent, i));

        if (existing is { Length: 32 })
            Array.Copy(existing, 28, bytes, 28, 4);
        else
            Write(bytes, 7, PaletteShade(accent, 6));

        return bytes;
    }

    private static void Write(byte[] bytes, int index, Rgb c)
    {
        int o = index * 4;

        if (PaletteOrder == ByteOrder.Rgba)
        {
            bytes[o + 0] = c.R;
            bytes[o + 1] = c.G;
            bytes[o + 2] = c.B;
        }
        else
        {
            bytes[o + 0] = c.B;
            bytes[o + 1] = c.G;
            bytes[o + 2] = c.R;
        }

        bytes[o + 3] = 0x00;
    }

    // ------------------------------------------------------------------
    //  RVB <-> TSL
    // ------------------------------------------------------------------

    private static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;
        double d = max - min;

        if (d < 1e-9) return (0.0, 0.0, l);

        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r)      h = (g - b) / d + (g < b ? 6.0 : 0.0);
        else if (max == g) h = (b - r) / d + 2.0;
        else               h = (r - g) / d + 4.0;

        return (h * 60.0, s, l);
    }

    private static Rgb FromHsl(double hDeg, double s, double l)
    {
        double h = (hDeg % 360.0 + 360.0) % 360.0;
        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double x = c * (1.0 - Math.Abs(h / 60.0 % 2.0 - 1.0));
        double m = l - c / 2.0;

        (double R, double G, double B) p = h switch
        {
            < 60.0  => (c, x, 0.0),
            < 120.0 => (x, c, 0.0),
            < 180.0 => (0.0, c, x),
            < 240.0 => (0.0, x, c),
            < 300.0 => (x, 0.0, c),
            _       => (c, 0.0, x),
        };

        return new Rgb(Channel(p.R + m), Channel(p.G + m), Channel(p.B + m));
    }

    private static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
}
