using System.Globalization;

namespace Kurogane.Core;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// Conversions de couleurs vers les formats attendus par Windows.
///
/// Piège central : les deux familles de valeurs n'utilisent pas le même ordre.
///   - DWM\AccentColor et Explorer\Accent\AccentColorMenu : 0x00BBGGRR (ABGR)
///   - DWM\ColorizationColor                              : 0xAARRGGBB (ARGB)
/// Inverser les deux donne un bleu là où on attend un rouge — défaut visible
/// mais silencieux côté code.
/// </summary>
public static class ColorUtil
{
    public static Rgb Parse(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !s.All(Uri.IsHexDigit))
            throw new FormatException($"Couleur invalide : « {hex} » (format attendu : #RRGGBB).");

        return new Rgb(
            byte.Parse(s[..2],           NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>0x00BBGGRR — pour AccentColor / AccentColorMenu / StartColorMenu.</summary>
    public static int ToAbgr(Rgb c) => unchecked((c.B << 16) | (c.G << 8) | c.R);

    /// <summary>0xFFRRGGBB — pour ColorizationColor / ColorizationAfterglow.</summary>
    public static int ToArgb(Rgb c) =>
        unchecked((int)(0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B));

    /// <summary>
    /// Ordre des octets de chaque entrée d'AccentPalette.
    /// À VÉRIFIER sur la VM au premier passage : si la teinte ressort inversée
    /// (rouge affiché bleu), basculer PaletteOrder sur Bgra. Un seul point à
    /// changer, volontairement isolé ici.
    /// </summary>
    public enum ByteOrder { Rgba, Bgra }

    public const ByteOrder PaletteOrder = ByteOrder.Rgba;

    /// <summary>
    /// AccentPalette : 32 octets = 8 nuances de 4 octets, de la plus sombre à
    /// la plus claire. L'accent de référence est placé à l'index 5, position
    /// utilisée par défaut par le shell.
    /// </summary>
    public static byte[] BuildAccentPalette(Rgb accent)
    {
        // Rampe multiplicative autour de l'accent (index 5 = facteur 1.0).
        double[] factors = { 0.30, 0.45, 0.62, 0.78, 0.90, 1.00, 1.18, 1.40 };

        var bytes = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            var shade = Scale(accent, factors[i]);
            int o = i * 4;

            if (PaletteOrder == ByteOrder.Rgba)
            {
                bytes[o + 0] = shade.R;
                bytes[o + 1] = shade.G;
                bytes[o + 2] = shade.B;
            }
            else
            {
                bytes[o + 0] = shade.B;
                bytes[o + 1] = shade.G;
                bytes[o + 2] = shade.R;
            }
            bytes[o + 3] = 0xFF;
        }
        return bytes;
    }

    private static Rgb Scale(Rgb c, double f) => new(
        Clamp(c.R * f), Clamp(c.G * f), Clamp(c.B * f));

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
