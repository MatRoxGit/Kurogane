using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Kurogane.Core;

/// <summary>
/// Prise de possession d'une cle de registre protegee.
///
/// NIVEAU 3. C'est la seule operation du projet qui modifie la SECURITE du
/// systeme et non son apparence. Elle n'est jamais appliquee sans une double
/// autorisation explicite, et AUCUN module de theme ne l'utilise.
///
/// Ce qui protege reellement ces cles, verifie le 31/10/2026 : leur
/// proprietaire est NT AUTHORITY\SYSTEM, et c'est la LISTE D'ACCES qui bloque
/// — TrustedInstaller y detient le controle total, le groupe Administrateurs
/// n'a que la lecture. On parle souvent de « cles TrustedInstaller » ; c'est
/// un raccourci trompeur, la propriete et le droit d'ecriture sont deux
/// choses distinctes.
///
/// Resultat de l'enquete : prendre possession de ces cles et y ecrire une
/// icone n'a AUCUN effet sur les entrees de l'Acces rapide, qui etaient la
/// seule raison d'envisager ce niveau. Ce code reste disponible comme outil
/// de diagnostic, pas comme fonctionnalite.
///
/// Discipline de reversibilite, plus stricte qu'ailleurs :
///   - le descripteur de securite d'origine (proprietaire ET liste d'acces)
///     est capture en entier sous forme SDDL avant toute modification ;
///   - la restitution remet d'abord la liste d'acces, puis le proprietaire :
///     l'inverse nous priverait du droit d'ecrire la liste ;
///   - rendre la propriete a son titulaire d'origine exige SeRestorePrivilege, qui
///     est verifie AVANT la prise, jamais apres. Prendre sans pouvoir rendre
///     serait exactement la modification irreversible que ce projet refuse.
///
/// Limite connue et non contournable : les mises a jour cumulatives de
/// Windows reecrivent ces cles et peuvent reinitialiser leurs permissions.
/// </summary>
public sealed class OwnershipChange : IChange
{
    public required string Module { get; init; }
    public required RegistryHive Hive { get; init; }
    public required string SubKey { get; init; }
    public required string Description { get; init; }

    public string Target => $"{RegistryChange.HiveName(Hive)}\\{SubKey}  [securite]";

    private const AccessControlSections Sections =
        AccessControlSections.Owner | AccessControlSections.Access;

    // ---------- capture ----------

    public ChangeRecord Capture()
    {
        using var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
        using var k = baseKey.OpenSubKey(SubKey, RegistryKeyPermissionCheck.ReadSubTree,
                                         RegistryRights.ReadPermissions)
            ?? throw new InvalidOperationException(
                $"Cle introuvable ou illisible : {RegistryChange.HiveName(Hive)}\\{SubKey}");

        var securite = k.GetAccessControl(Sections);
        string sddl = securite.GetSecurityDescriptorSddlForm(Sections);
        string proprietaire = securite.GetOwner(typeof(NTAccount))?.Value ?? "(inconnu)";

        return new ChangeRecord
        {
            Kind              = ChangeKind.RegistrySecurity,
            Module            = Module,
            Target            = Target,
            Description       = Description,
            Hive              = RegistryChange.HiveName(Hive),
            SubKey            = SubKey,
            Name              = "",
            Existed           = true,
            OriginalValueKind = "Sddl",
            NewValueKind      = "Sddl",
            OriginalValue     = sddl,
            NewValue          = $"proprietaire -> {Administrateurs().Value}, controle total",
            OriginalAttributes = proprietaire,
        };
    }

    // ---------- controle ----------

    /// <summary>
    /// On ne teste pas l'ecriture de la cle elle-meme : par definition on ne
    /// l'a pas encore. On verifie ce qui conditionne la reversibilite.
    /// </summary>
    public bool CanWrite()
    {
        try
        {
            Privileges.EnablePourPriseDePossession();
            return true;
        }
        catch { return false; }
    }

    // ---------- application ----------

    public void Apply()
    {
        Privileges.EnablePourPriseDePossession();

        var admins = Administrateurs();

        // Deux ouvertures distinctes : on ne peut pas modifier la liste
        // d'acces avant d'etre proprietaire.
        using (var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                          RegistryRights.TakeOwnership)
               ?? throw new InvalidOperationException($"Ouverture impossible : {SubKey}"))
        {
            var s = k.GetAccessControl(AccessControlSections.None);
            s.SetOwner(admins);
            k.SetAccessControl(s);
        }

        using (var baseKey = RegistryKey.OpenBaseKey(Hive, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                          RegistryRights.ChangePermissions)
               ?? throw new InvalidOperationException($"Ouverture impossible : {SubKey}"))
        {
            var s = k.GetAccessControl(AccessControlSections.Access);
            s.AddAccessRule(new RegistryAccessRule(
                admins, RegistryRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            k.SetAccessControl(s);
        }
    }

    // ---------- restauration ----------

    public static void Revert(ChangeRecord r)
    {
        if (r.Hive is not { } ruche || r.SubKey is not { } sousCle || r.OriginalValue is not { } sddl)
            throw new InvalidDataException(
                $"Descripteur de securite absent du point de restauration pour « {r.Target} ».");

        Privileges.EnablePourPriseDePossession();

        var hive = RegistryChange.ParseHive(ruche);

        // Liste d'acces D'ABORD, proprietaire ENSUITE : remettre le
        // proprietaire en premier nous priverait du droit d'ecrire la liste.
        using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(sousCle, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                          RegistryRights.ChangePermissions))
        {
            if (k is not null)
            {
                var s = new RegistrySecurity();
                s.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
                k.SetAccessControl(s);
            }
        }

        using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(sousCle, RegistryKeyPermissionCheck.ReadWriteSubTree,
                                          RegistryRights.TakeOwnership))
        {
            if (k is not null)
            {
                var s = new RegistrySecurity();
                s.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner);
                k.SetAccessControl(s);
            }
        }
    }

    private static NTAccount Administrateurs() =>
        (NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            .Translate(typeof(NTAccount));
}
