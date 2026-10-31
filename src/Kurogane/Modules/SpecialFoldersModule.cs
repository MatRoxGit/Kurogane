using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Icones des dossiers du profil : Documents, Telechargements, Images,
/// Musique, Videos.
///
/// Sous Windows 11 ces dossiers ne sont plus enfants de « Ce PC » mais de
/// l'Acces rapide, et les CLSID qui fonctionnaient sous Windows 10 n'ont plus
/// d'effet — verifie par la sonde le 31/10/2026. Le mecanisme qui tient est
/// le desktop.ini depose dans le dossier lui-meme : documente, sans
/// elevation, et reversible.
///
/// Deux ecritures par dossier, chacune capturee et reversible separement :
/// le desktop.ini, et l'attribut ReadOnly du dossier sans lequel le shell ne
/// lit pas ce fichier.
/// </summary>
public sealed class SpecialFoldersModule : IThemeModule
{
    public string Name => "dossiers-profil";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) =>
        theme.Icons is { } i &&
        (i.Documents ?? i.Telechargements ?? i.Images ?? i.Musique ?? i.Videos) is not null;

    public IEnumerable<IChange> Plan(ThemeDefinition theme)
    {
        var i = theme.Icons!;

        foreach (var (fichier, dossier, libelle) in new[]
        {
            (i.Documents,       CheminProfil(Environment.SpecialFolder.MyDocuments), "Documents"),
            (i.Telechargements, CheminTelechargements(),                             "Telechargements"),
            (i.Images,          CheminProfil(Environment.SpecialFolder.MyPictures),  "Images"),
            (i.Musique,         CheminProfil(Environment.SpecialFolder.MyMusic),     "Musique"),
            (i.Videos,          CheminProfil(Environment.SpecialFolder.MyVideos),    "Videos"),
        })
        {
            if (fichier is null) continue;

            if (string.IsNullOrEmpty(dossier) || !Directory.Exists(dossier))
                throw new DirectoryNotFoundException(
                    $"Dossier du profil introuvable pour « {libelle} » : {dossier}");

            string icone = theme.ResolveIcon(fichier);

            if (!File.Exists(icone))
                throw new FileNotFoundException(
                    $"Icone introuvable : {icone}\n" +
                    $"(declaree dans le theme « {theme.Name} » comme « {fichier} »)");

            string ini = System.IO.Path.Combine(dossier, "desktop.ini");

            // Fusion avec l'existant : ces dossiers portent deja un
            // LocalizedResourceName qu'il ne faut surtout pas perdre.
            string? actuel = File.Exists(ini)
                ? LireIni(ini)
                : null;

            yield return new FileChange
            {
                Module      = Name,
                Path        = ini,
                Content     = FileChange.DesktopIni(actuel, icone),
                Attributes  = FileAttributes.Hidden | FileAttributes.System,
                Description = $"{libelle} — {System.IO.Path.GetFileName(icone)}",
            };

            // Sans cet attribut sur le dossier, le shell ignore le desktop.ini.
            yield return new FileChange
            {
                Module      = Name,
                Path        = dossier,
                IsDirectory = true,
                Attributes  = FileAttributes.ReadOnly,
                Description = $"{libelle} — attribut ReadOnly du dossier",
            };
        }
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.RefreshShellIcons();
        journal.Ok("Cache d'icones du shell invalide");
    }

    // ------------------------------------------------------------------

    private static string CheminProfil(Environment.SpecialFolder f) =>
        Environment.GetFolderPath(f);

    /// <summary>
    /// Telechargements n'a pas d'entree dans Environment.SpecialFolder. On lit
    /// son emplacement reel dans « User Shell Folders » plutot que de supposer
    /// %USERPROFILE%\Downloads, que l'utilisateur a pu deplacer.
    /// </summary>
    private static string CheminTelechargements()
    {
        const string Cle = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
        const string Guid = "{374DE290-123F-4565-9164-39C4925E467B}";

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var k = baseKey.OpenSubKey(Cle, writable: false);

        var brut = k?.GetValue(Guid, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

        return string.IsNullOrEmpty(brut)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : Environment.ExpandEnvironmentVariables(brut);
    }

    /// <summary>
    /// Lit un desktop.ini existant. Ces fichiers sont tantot en ANSI, tantot
    /// en UTF-16 avec BOM : on laisse .NET detecter la nomenclature.
    /// </summary>
    private static string LireIni(string chemin)
    {
        using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var lecteur = new StreamReader(flux, System.Text.Encoding.Default, detectEncodingFromByteOrderMarks: true);
        return lecteur.ReadToEnd();
    }
}
