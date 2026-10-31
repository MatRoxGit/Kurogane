using System.Globalization;
using System.Security;
using Microsoft.Win32;

namespace Kurogane.Core;

/// <summary>
/// Une écriture de registre prévue, avec sa capture et son inverse.
/// Invariant du projet : on ne construit jamais une écriture sans savoir
/// la défaire. Capture() lit l'état d'origine, Revert() le rétablit.
/// </summary>
public sealed class RegistryChange : IChange
{
    public required string Module { get; init; }
    public required RegistryHive Hive { get; init; }
    public required string SubKey { get; init; }
    public required string Name { get; init; }
    public required RegistryValueKind Kind { get; init; }
    public required object NewValue { get; init; }
    public required string Description { get; init; }

    /// <summary>
    /// true quand la permission sera obtenue par une prise de possession
    /// placee plus tot dans le meme plan.
    /// </summary>
    public bool DeferWriteCheck { get; init; }

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
            Kind              = ChangeKind.RegistryValue,
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

    // ---------- controle d'ecriture ----------

    /// <summary>
    /// Teste si cette ecriture est permise, sans rien modifier.
    ///
    /// L'elevation ne suffit pas a le savoir : la permission se joue cle par
    /// cle. Certaines cles de HKLM\\SOFTWARE\\Classes appartiennent a
    /// TrustedInstaller et resistent a un administrateur. On remonte donc
    /// jusqu'a la premiere cle existante du chemin et on tente de l'ouvrir en
    /// ecriture.
    /// </summary>
    public bool CanWrite()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);

            string chemin = SubKey;
            while (chemin.Length > 0)
            {
                using (var lecture = baseKey.OpenSubKey(chemin, writable: false))
                {
                    if (lecture is not null)
                    {
                        using var ecriture = baseKey.OpenSubKey(chemin, writable: true);
                        return ecriture is not null;
                    }
                }

                int i = chemin.LastIndexOf('\\');
                if (i < 0) break;
                chemin = chemin[..i];
            }

            // Aucune cle du chemin n'existe : indecidable sans ecrire.
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (SecurityException)           { return false; }
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
        // Un enregistrement de registre doit porter ses trois coordonnees. Un
        // point de restauration incomplet est un defaut grave : mieux vaut
        // s'arreter en le nommant que deviner.
        if (r.Hive is not { } ruche || r.SubKey is not { } sousCle || r.Name is not { } nom)
            throw new InvalidDataException(
                $"Enregistrement de registre incomplet pour « {r.Target} » : "
              + "ruche, chemin ou nom de valeur absent du point de restauration.");

        RegistryHive hive = ParseHive(ruche);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        if (r.Existed)
        {
            if (r.OriginalValueKind is not { } typeOrigine || r.OriginalValue is not { } valeurOrigine)
                throw new InvalidDataException(
                    $"Valeur d'origine absente du point de restauration pour « {r.Target} ».");

            using var key = baseKey.CreateSubKey(sousCle, writable: true)
                ?? throw new InvalidOperationException(
                    $"Restauration impossible : {ruche}\\{sousCle}");

            var kind = Enum.Parse<RegistryValueKind>(typeOrigine);
            key.SetValue(nom, Decode(valeurOrigine, kind), kind);
        }
        else
        {
            // La valeur n'existait pas avant nous : la bonne restauration est
            // de la retirer. On ne supprime jamais la cle parente, meme si on
            // l'a creee — une cle vide est inerte, une cle supprimee peut
            // casser autre chose.
            using var key = baseKey.OpenSubKey(sousCle, writable: true);
            key?.DeleteValue(nom, throwOnMissingValue: false);
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
