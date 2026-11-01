using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Kurogane.Core;

/// <summary>
/// Prise de possession d'un fichier protégé, et restitution exacte de son
/// descripteur de sécurité.
///
/// Contrairement aux clés de registre du niveau 3, ici c'est bien
/// TrustedInstaller qui détient les fichiers de C:\Windows\SystemResources.
///
/// Même discipline qu'ailleurs dans le projet :
///   - le descripteur d'origine est capturé en entier, sous forme SDDL,
///     avant toute modification ;
///   - la restitution remet d'abord la liste d'accès, puis le propriétaire —
///     l'inverse nous priverait du droit d'écrire la liste ;
///   - rendre la propriété exige SeRestorePrivilege, vérifié AVANT la prise.
/// </summary>
public static class FileOwnership
{
    private const AccessControlSections Sections =
        AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;

    /// <summary>Descripteur de sécurité complet, à conserver pour la restitution.</summary>
    public static string CapturerSddl(string chemin) =>
        new FileSecurity(chemin, Sections).GetSecurityDescriptorSddlForm(Sections);

    /// <summary>
    /// Donne la propriété au groupe Administrateurs et s'y accorde le
    /// contrôle total. Sans la seconde étape, la prise de possession seule ne
    /// donne toujours pas le droit d'écrire : propriété et permission sont
    /// deux choses distinctes.
    /// </summary>
    public static void Prendre(string chemin)
    {
        Privileges.EnablePourPriseDePossession();

        var info = new FileInfo(chemin);
        var administrateurs = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // Le propriétaire se pose seul : un descripteur neuf évite de
        // réécrire au passage une liste d'accès qu'on n'a pas le droit de
        // modifier tant qu'on n'est pas propriétaire.
        var proprietaire = new FileSecurity();
        proprietaire.SetOwner(administrateurs);
        info.SetAccessControl(proprietaire);

        var securite = new FileSecurity(chemin, AccessControlSections.Access);
        securite.AddAccessRule(new FileSystemAccessRule(
            administrateurs, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(securite);
    }

    // ------------------------------------------------------------------
    //  Dossiers
    // ------------------------------------------------------------------
    //
    //  Renommer un fichier ne suffit pas à l'opération : le renommage crée
    //  une entrée dans le répertoire, ce qui demande un droit d'écriture SUR
    //  LE DOSSIER. Or le groupe Administrateurs n'a que la lecture sur
    //  C:\Windows\SystemResources. Sans ces trois méthodes, la substitution
    //  échoue dès le renommage.

    public static string CapturerSddlDossier(string chemin) =>
        new DirectorySecurity(chemin, Sections).GetSecurityDescriptorSddlForm(Sections);

    public static void PrendreDossier(string chemin)
    {
        Privileges.EnablePourPriseDePossession();

        var info = new DirectoryInfo(chemin);
        var administrateurs = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var proprietaire = new DirectorySecurity();
        proprietaire.SetOwner(administrateurs);
        info.SetAccessControl(proprietaire);

        var securite = new DirectorySecurity(chemin, AccessControlSections.Access);
        securite.AddAccessRule(new FileSystemAccessRule(
            administrateurs, FileSystemRights.FullControl,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(securite);
    }

    public static void RestituerDossier(string chemin, string sddl)
    {
        var info = new DirectoryInfo(chemin);

        var liste = new DirectorySecurity();
        liste.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        info.SetAccessControl(liste);

        var proprietaire = new DirectorySecurity();
        proprietaire.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner);
        info.SetAccessControl(proprietaire);
    }

    /// <summary>Réapplique un descripteur capturé : liste d'accès d'abord, propriétaire ensuite.</summary>
    public static void Restituer(string chemin, string sddl)
    {
        var info = new FileInfo(chemin);

        var liste = new FileSecurity();
        liste.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        info.SetAccessControl(liste);

        var proprietaire = new FileSecurity();
        proprietaire.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner);
        info.SetAccessControl(proprietaire);
    }
}
