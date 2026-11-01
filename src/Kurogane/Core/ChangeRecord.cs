using System.IO;
using System.Text.Json.Serialization;

using System.Text.RegularExpressions;

namespace Kurogane.Core;

/// <summary>Nature de la chose modifiee.</summary>
public enum ChangeKind
{
    RegistryValue,
    File,
    RegistrySecurity,
}

/// <summary>
/// Trace d'une modification unitaire : ce qui existait AVANT, ce qu'on ecrit.
/// C'est l'unite de restauration, et elle doit suffire a elle seule — y
/// compris dans le cas « ca n'existait pas », qui se defait par une
/// suppression et non par l'ecriture d'une valeur supposee.
/// </summary>
public sealed class ChangeRecord
{
    public ChangeKind Kind { get; set; } = ChangeKind.RegistryValue;

    public string Module { get; set; } = "";
    public string Target { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>false = la cible n'existait pas ; restaurer = la supprimer.</summary>
    public bool Existed { get; set; }

    // --- registre ---
    public string? Hive { get; set; }
    public string? SubKey { get; set; }
    public string? Name { get; set; }
    public string? OriginalValueKind { get; set; }
    public string? NewValueKind { get; set; }

    // --- fichier ou dossier ---
    public string? Path { get; set; }

    /// <summary>Attributs d'origine (FileAttributes), null si la cible n'existait pas.</summary>
    public string? OriginalAttributes { get; set; }

    /// <summary>Attributs a poser. null = ne pas y toucher.</summary>
    public string? NewAttributes { get; set; }

    /// <summary>true pour un dossier : son contenu n'est jamais touche.</summary>
    public bool IsDirectory { get; set; }

    // --- contenu, encode selon le type ---
    public string? OriginalValue { get; set; }
    public string? NewValue { get; set; }

    /// <summary>true quand la cible est deja exactement dans l'etat vise.</summary>
    [JsonIgnore]
    public bool IsNoOp =>
        Existed
        && string.Equals(OriginalValueKind, NewValueKind, StringComparison.Ordinal)
        && ValeursEquivalentes(OriginalValue, NewValue)
        && (NewAttributes is null
            || string.Equals(OriginalAttributes, NewAttributes, StringComparison.Ordinal));

    private static readonly Regex CheminWindows = new(@"[A-Za-z]:\\", RegexOptions.Compiled);

    /// <summary>
    /// Compare deux valeurs, en tenant compte du fait qu'un chemin de fichier
    /// sous Windows ne distingue pas la casse : « C:\kurogane\x.ico » et
    /// « C:\Kurogane\x.ico » désignent le même fichier.
    ///
    /// Sans cette nuance, renommer le dossier du projet avec une majuscule
    /// produit une modification fantôme par icône — rien n'est réellement
    /// changé, mais le point de restauration enfle et le plan devient
    /// illisible.
    ///
    /// La tolérance est volontairement étroite : elle ne s'applique que si les
    /// DEUX valeurs contiennent un chemin absolu. Partout ailleurs, la
    /// comparaison reste octet pour octet.
    /// </summary>
    private static bool ValeursEquivalentes(string? avant, string? apres)
    {
        if (string.Equals(avant, apres, StringComparison.Ordinal)) return true;
        if (avant is null || apres is null) return false;

        if (!CheminWindows.IsMatch(avant) || !CheminWindows.IsMatch(apres)) return false;

        return string.Equals(avant, apres, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rendu lisible pour le plan et le journal.</summary>
    public string Render()
    {
        // Un dossier n'a pas de contenu a montrer : seuls ses attributs changent.
        if (IsDirectory)
            return $"{Target}\n      attributs : {OriginalAttributes ?? "(aucun)"} -> {NewAttributes}";

        // Pour un fichier texte, un diff ligne a ligne dit infiniment plus
        // qu'un base64 tronque : c'est ce qu'on relit avant de valider.
        if (Kind == ChangeKind.File && DiffTexte() is { } diff)
            return $"{Target}\n{diff}";

        var avant = Existed ? Abrege(OriginalValue) : "(absente)";
        var s = $"{Target}\n      avant : {avant}\n      après : {Abrege(NewValue)}";

        if (NewAttributes is not null && !string.Equals(OriginalAttributes, NewAttributes, StringComparison.Ordinal))
            s += $"\n      attributs : {OriginalAttributes ?? "(aucun)"} -> {NewAttributes}";

        return s;
    }

    /// <summary>
    /// Lignes retirees et ajoutees, ou null si le contenu n'est pas du texte.
    /// </summary>
    private string? DiffTexte()
    {
        try
        {
            var avant = Decoder(OriginalValue);
            var apres = Decoder(NewValue);
            if (avant is null && apres is null) return null;

            var la = avant?.Split('\n').Select(l => l.TrimEnd('\r')).ToList() ?? new();
            var lb = apres?.Split('\n').Select(l => l.TrimEnd('\r')).ToList() ?? new();

            var retirees = la.Where(l => !lb.Contains(l) && l.Trim().Length > 0).ToList();
            var ajoutees = lb.Where(l => !la.Contains(l) && l.Trim().Length > 0).ToList();

            if (retirees.Count == 0 && ajoutees.Count == 0)
                return "      (contenu inchangé)";

            var sb = new System.Text.StringBuilder();
            foreach (var l in retirees.Take(6)) sb.AppendLine($"      - {Abrege(l)}");
            foreach (var l in ajoutees.Take(6)) sb.AppendLine($"      + {Abrege(l)}");
            if (retirees.Count > 6 || ajoutees.Count > 6) sb.AppendLine("      ...");

            return sb.ToString().TrimEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// Decode un contenu base64 en texte. La nomenclature decide de
    /// l'encodage : un desktop.ini est tantot en UTF-16, tantot en ANSI.
    /// </summary>
    private static string? Decoder(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;

        var o = Convert.FromBase64String(base64);

        if (o.Length >= 2 && o[0] == 0xFF && o[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(o, 2, o.Length - 2);

        if (o.Length >= 3 && o[0] == 0xEF && o[1] == 0xBB && o[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(o, 3, o.Length - 3);

        // Pas de nomenclature : du texte 8 bits, ou du binaire. Un octet nul
        // tranche — un fichier texte n'en contient pas.
        if (o.Contains((byte)0)) return null;

        return System.Text.Encoding.Latin1.GetString(o);
    }

    private static string Abrege(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "(vide)";
        v = v.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\u0000", "\\0");
        return v.Length <= 72 ? v : v[..69] + "...";
    }
}

/// <summary>
/// Une modification prevue, quelle que soit sa nature. Le contrat est le meme
/// pour toutes : savoir se capturer, savoir si elle est permise, savoir
/// s'appliquer. Et pour toutes, la restauration passe par Changes.Revert.
/// </summary>
public interface IChange
{
    string Module { get; }
    string Target { get; }
    string Description { get; }

    /// <summary>Lit l'etat d'origine. Aucun effet de bord.</summary>
    ChangeRecord Capture();

    /// <summary>Teste la permission sans rien modifier.</summary>
    bool CanWrite();

    void Apply();

    /// <summary>
    /// true quand la permission d'ecrire sera obtenue par une modification
    /// ANTERIEURE du meme plan — typiquement une prise de possession. Le
    /// controle prealable serait alors un faux negatif.
    /// </summary>
    bool DeferWriteCheck => false;
}

/// <summary>Restauration, aiguillee sur la nature de l'enregistrement.</summary>
public static class Changes
{
    public static void Revert(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.RegistryValue: RegistryChange.Revert(r); break;
            case ChangeKind.File:          FileChange.Revert(r);      break;
            case ChangeKind.RegistrySecurity: OwnershipChange.Revert(r); break;
            default:
                throw new InvalidOperationException($"Nature inconnue : {r.Kind}");
        }
    }
}
