using Kurogane.Core;
using Microsoft.Win32;

namespace Kurogane.Probe;

/// <summary>
/// Sonde de NIVEAU 3 : prend possession des cles candidates, y ecrit l'icone
/// temoin, et laisse l'ecran dire laquelle pilote reellement les entrees de
/// l'Acces rapide.
///
/// Deux familles sont candidates et on ignore laquelle agit :
///   HKLM\SOFTWARE\Classes\CLSID\{CLSID}\DefaultIcon
///   HKLM\...\Explorer\FolderDescriptions\{FOLDERID}\Icon
///
/// Les sonder toutes les deux en une passe evite de prendre possession deux
/// fois. Chaque prise est capturee et restituable.
/// </summary>
public static class OwnershipProbe
{
    /// <summary>CLSID des dossiers tels qu'ils apparaissent dans « Ce PC ».</summary>
    private static readonly (string Nom, string Clsid, string FolderId)[] Dossiers =
    {
        ("Documents",       "{d3162b92-9365-467a-956b-92703aca08af}", "{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"),
        ("Telechargements", "{088e3905-0323-4b02-9826-5d99428e115f}", "{374DE290-123F-4565-9164-39C4925E467B}"),
        ("Images",          "{24ad3ad4-a569-4530-98e1-ab02f9417aa8}", "{33E28130-4E1E-4676-835A-98395C3BC9FA}"),
        ("Musique",         "{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}", "{4BD8D571-6D19-48D3-BE97-422220080E43}"),
        ("Videos",          "{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}", "{18989B1D-99B5-455B-841C-AB7C74E4DDFC}"),
    };

    public static List<(string Group, int Level, IChange Change)> Targets(string iconPath)
    {
        string icone = $"{iconPath},0";
        var liste = new List<(string, int, IChange)>();

        foreach (var (nom, clsid, folderId) in Dossiers)
        {
            Ajouter(liste, nom, "classes",
                $@"SOFTWARE\Classes\CLSID\{clsid}\DefaultIcon", "", icone);

            Ajouter(liste, nom, "folderdesc",
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions\{folderId}",
                "Icon", icone);
        }

        return liste;
    }

    private static void Ajouter(List<(string, int, IChange)> liste, string nom, string famille,
                                string sousCle, string valeur, string icone)
    {
        // Une cle absente n'est pas une cible : inutile d'en prendre
        // possession pour y ecrire dans le vide.
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(sousCle, writable: false))
        {
            if (k is null) return;
        }

        string groupe = $"niveau 3 — {famille}";

        liste.Add((groupe, 3, new OwnershipChange
        {
            Module      = groupe,
            Hive        = RegistryHive.LocalMachine,
            SubKey      = sousCle,
            Description = $"{nom} — prise de possession",
        }));

        liste.Add((groupe, 3, new RegistryChange
        {
            Module          = groupe,
            Hive            = RegistryHive.LocalMachine,
            SubKey          = sousCle,
            Name            = valeur,
            Kind            = RegistryValueKind.ExpandString,
            NewValue        = icone,
            Description     = $"{nom} — icone temoin",
            DeferWriteCheck = true,   // la permission viendra de la ligne precedente
        }));
    }
}
