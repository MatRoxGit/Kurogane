using System.Text.Json.Serialization;

namespace Kurogane.Core;

/// <summary>
/// Trace d'une modification unitaire : ce qui existait AVANT, ce qu'on écrit.
/// C'est l'unité de restauration. Un enregistrement suffit, à lui seul, à
/// remettre le système dans son état d'origine — y compris le cas
/// « la valeur n'existait pas », qui se restaure par une suppression.
/// </summary>
public sealed class ChangeRecord
{
    public string Module { get; set; } = "";
    public string Target { get; set; } = "";
    public string Description { get; set; } = "";

    public string Hive { get; set; } = "";
    public string SubKey { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>false = la valeur n'existait pas ; restaurer = la supprimer.</summary>
    public bool Existed { get; set; }

    public string? OriginalValueKind { get; set; }
    public string? OriginalValue { get; set; }

    public string NewValueKind { get; set; } = "";
    public string NewValue { get; set; } = "";

    /// <summary>
    /// true quand la valeur en place est déjà exactement celle qu'on écrirait.
    /// Une telle modification n'est pas appliquée : elle n'apporterait rien au
    /// rendu et encombrerait le point de restauration d'entrées sans objet.
    /// </summary>
    [JsonIgnore]
    public bool IsNoOp =>
        Existed
        && string.Equals(OriginalValueKind, NewValueKind, StringComparison.Ordinal)
        && string.Equals(OriginalValue, NewValue, StringComparison.Ordinal);

    /// <summary>Rendu lisible pour le plan et le journal.</summary>
    public string Render()
    {
        var before = Existed ? Abbreviate(OriginalValue) : "(absente)";
        return $"{Target}\n      avant : {before}\n      après : {Abbreviate(NewValue)}";
    }

    private static string Abbreviate(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "(vide)";
        v = v.Replace("\u0000", "\\0");
        return v.Length <= 72 ? v : v[..69] + "...";
    }
}
