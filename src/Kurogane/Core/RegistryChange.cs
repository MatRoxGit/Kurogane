using System.Globalization;
using Microsoft.Win32;

namespace Kurogane.Core;

/// <summary>
/// Une écriture de registre prévue, avec sa capture et son inverse.
/// Invariant du projet : on ne construit jamais une écriture sans savoir
/// la défaire. Capture() lit l'état d'origine, Revert() le rétablit.
/// </summary>
public sealed class RegistryChange
{
    public required string Module { get; init; }
    public required RegistryHive Hive { get; init; }
    public required string SubKey { get; init; }
    public required string Name { get; init; }
    public required RegistryValueKind Kind { get; init; }
    public required object NewValue { get; init; }
    public required string Description { get; init; }

    public string Target => $"{HiveName(Hive)}\\{SubKey}\\{Name}";

    // ---------- capture ----------

    /// <summary>
    /// Lit la valeur actuelle et produit l'enregistrement de restauration.
    /// N'écrit rien. C'est la seule opération exécutée en mode simulation.
    /// </summary>
    public ChangeRecord Capture()
    {
        using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(SubKey, writable: false);

        object? current = key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        bool existed = current is not null;
        RegistryValueKind originalKind = existed ? key!.GetValueKind(Name) : Kind;

        return new ChangeRecord
        {
            Module            = Module,
            Target            = Target,
            Description       = Description,
            Hive              = HiveName(Hive),
            SubKey            = SubKey,
            Name              = Name,
            Existed           = existed,
            OriginalValueKind = originalKind.ToString(),
            OriginalValue     = existed ? Encode(current!, originalKind) : null,
            NewValueKind      = Kind.ToString(),
            NewValue          = Encode(NewValue, Kind),
        };
    }

    // ---------- application ----------

    public void Apply()
    {
        using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        using var key = baseKey.CreateSubKey(SubKey, writable: true)
            ?? throw new InvalidOperationException(
                $"Ouverture en écriture impossible : {HiveName(Hive)}\\{SubKey}");

        key.SetValue(Name, NewValue, Kind);
    }

    // ---------- restauration ----------

    public static void Revert(ChangeRecord r)
    {
        RegistryHive hive = ParseHive(r.Hive);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        if (r.Existed)
        {
            using var key = baseKey.CreateSubKey(r.SubKey, writable: true)
                ?? throw new InvalidOperationException(
                    $"Restauration impossible : {r.Hive}\\{r.SubKey}");

            var kind = Enum.Parse<RegistryValueKind>(r.OriginalValueKind!);
            key.SetValue(r.Name, Decode(r.OriginalValue!, kind), kind);
        }
        else
        {
            // La valeur n'existait pas avant nous : la bonne restauration est
            // de la retirer. On ne supprime jamais la clé parente, même si on
            // l'a créée — une clé vide est inerte, une clé supprimée peut
            // casser autre chose.
            using var key = baseKey.OpenSubKey(r.SubKey, writable: true);
            key?.DeleteValue(r.Name, throwOnMissingValue: false);
        }
    }

    // ---------- encodage ----------

    public static string Encode(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord       => Convert.ToInt64(value, CultureInfo.InvariantCulture)
                                                .ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord       => Convert.ToInt64(value, CultureInfo.InvariantCulture)
                                                .ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.Binary      => Convert.ToBase64String((byte[])value),
        RegistryValueKind.MultiString => string.Join("\u0000", (string[])value),
        _                             => value?.ToString() ?? string.Empty,
    };

    public static object Decode(string encoded, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord       => unchecked((int)long.Parse(encoded, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord       => long.Parse(encoded, CultureInfo.InvariantCulture),
        RegistryValueKind.Binary      => Convert.FromBase64String(encoded),
        RegistryValueKind.MultiString => encoded.Split('\u0000'),
        _                             => encoded,
    };

    // ---------- ruches ----------

    public static string HiveName(RegistryHive h) => h switch
    {
        RegistryHive.CurrentUser  => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.ClassesRoot  => "HKCR",
        RegistryHive.Users        => "HKU",
        _                         => h.ToString(),
    };

    public static RegistryHive ParseHive(string name) => name.ToUpperInvariant() switch
    {
        "HKCU" => RegistryHive.CurrentUser,
        "HKLM" => RegistryHive.LocalMachine,
        "HKCR" => RegistryHive.ClassesRoot,
        "HKU"  => RegistryHive.Users,
        _      => throw new FormatException($"Ruche inconnue : {name}"),
    };
}
