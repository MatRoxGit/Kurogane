using System.IO;
using Kurogane.Core;
using Microsoft.Win32;

namespace Kurogane.Probe;

/// <summary>
/// Sonde de substitution d'icones.
///
/// Microsoft ne documente aucune liste des icones remplacables, et les tables
/// d'index qui circulent remontent a Windows XP. Plutot que de s'y fier, on
/// ecrit une icone temoin dans toutes les cibles candidates d'un coup et on
/// regarde lesquelles ont pris : le systeme lui-meme repond a la question.
///
/// La sonde n'a aucun privilege particulier : elle passe par le meme chemin
/// que l'application d'un theme, donc par le point de restauration.
/// </summary>
public static class IconProbe
{
    private const string DesktopClsid =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\CLSID\{0}\DefaultIcon";

    private const string ShellIcons =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    private const string DriveIcons =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons";

    // ------------------------------------------------------------------
    //  Icone temoin
    // ------------------------------------------------------------------

    /// <summary>
    /// Produit une icone volontairement hideuse : magenta barre de blanc,
    /// cerne de noir. Aucune icone de Windows ne lui ressemble, donc aucun
    /// doute possible sur ce qui a ete substitue et ce qui ne l'a pas ete.
    /// </summary>
    public static string EnsureWitnessIcon()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "probe");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "temoin.ico");

        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        IcoWriter.Write(path, sizes.Select(s => (s, DrawWitness(s))).ToList());

        return path;
    }

    internal static byte[] DrawWitness(int size)
    {
        var px = new byte[size * size * 4];
        int thickness = Math.Max(1, size / 8);

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            bool border = x == 0 || y == 0 || x == size - 1 || y == size - 1;
            bool cross  = Math.Abs(x - y) < thickness
                       || Math.Abs(x + y - (size - 1)) < thickness;

            byte b, g, r;
            if (border)     { b = 0;   g = 0;   r = 0;   }
            else if (cross) { b = 255; g = 255; r = 255; }
            else            { b = 255; g = 0;   r = 255; }

            int o = (y * size + x) * 4;
            px[o + 0] = b;
            px[o + 1] = g;
            px[o + 2] = r;
            px[o + 3] = 255;
        }

        return px;
    }

    // ------------------------------------------------------------------
    //  Detection des droits
    // ------------------------------------------------------------------

    /// <summary>
    /// Teste l'ecriture dans HKLM en ouvrant une cle qui existe toujours.
    /// Plus fiable qu'un controle d'appartenance au groupe Administrateurs :
    /// on mesure ce qui compte, la permission reelle, pas une approximation.
    /// </summary>
    public static bool CanWriteHklm() => Elevation.CanWriteHklm();

    // ------------------------------------------------------------------
    //  Cibles candidates
    // ------------------------------------------------------------------

    /// <summary>
    /// GUID des dossiers du profil tels qu'ils apparaissent dans « Ce PC » et
    /// dans le volet de navigation. Ce ne sont PAS les GUID d'icones du
    /// Bureau, ni les FOLDERID de l'API Known Folders.
    /// </summary>
    private static readonly (string Nom, string Guid)[] DossiersSpeciaux =
    {
        ("Bureau",          "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"),
        ("Documents",       "{d3162b92-9365-467a-956b-92703aca08af}"),
        ("Telechargements", "{088e3905-0323-4b02-9826-5d99428e115f}"),
        ("Musique",         "{3dfdf296-dbec-4fb4-81d1-6a3438bcf4de}"),
        ("Images",          "{24ad3ad4-a569-4530-98e1-ab02f9417aa8}"),
        ("Videos",          "{f86fa3ab-70d2-4fc7-9c99-fcbf05467f3a}"),
    };

    public static List<(string Group, int Level, RegistryChange Change)> Targets(
        string iconPath, bool includeHklm)
    {
        string icon = $"{iconPath},0";
        var list = new List<(string, int, RegistryChange)>();

        // --- Niveau 1 : icones du Bureau (HKCU) -----------------------
        // Mecanisme employe par le panneau « Parametres des icones du
        // Bureau » de Windows : le plus sur du lot.

        void Desktop(string label, string guid, string valueName = "")
            => list.Add(("bureau", 1, new RegistryChange
            {
                Module      = "bureau",
                Hive        = RegistryHive.CurrentUser,
                SubKey      = string.Format(DesktopClsid, guid),
                Name        = valueName,
                Kind        = RegistryValueKind.ExpandString,
                NewValue    = icon,
                Description = label,
            }));

        Desktop("Ce PC",                   "{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
        Desktop("Reseau",                  "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
        Desktop("Dossier utilisateur",     "{59031a47-3f72-44a7-89c5-5595fe6b30ee}");
        Desktop("Panneau de configuration","{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}");

        // La Corbeille porte deux etats sous la meme cle. Le suffixe « ,0 »
        // sur chaque chemin conditionne la bascule automatique entre les deux.
        Desktop("Corbeille (valeur par defaut)", "{645FF040-5081-101B-9F08-00AA002F954E}");
        Desktop("Corbeille vide",  "{645FF040-5081-101B-9F08-00AA002F954E}", "empty");
        Desktop("Corbeille pleine","{645FF040-5081-101B-9F08-00AA002F954E}", "full");

        // --- Niveau 1 : type de fichier (HKCU\Software\Classes) --------
        // HKCR est une vue fusionnee ou HKCU prime sur HKLM : on peut donc
        // substituer une icone de type de fichier sans droits admin.

        string? progId = Registry.ClassesRoot.OpenSubKey(".txt")?.GetValue("") as string;
        if (!string.IsNullOrWhiteSpace(progId))
        {
            list.Add(("type de fichier", 1, new RegistryChange
            {
                Module      = "type de fichier",
                Hive        = RegistryHive.CurrentUser,
                SubKey      = $@"Software\Classes\{progId}\DefaultIcon",
                Name        = "",
                Kind        = RegistryValueKind.ExpandString,
                NewValue    = icon,
                Description = $"Fichiers .txt (ProgID {progId})",
            }));
        }

        // --- Niveau 1 : dossiers speciaux (HKCU\\Software\\Classes) ----------
        // Bureau, Documents, Telechargements, Musique, Images, Videos passent
        // par CLSID\\{GUID}\\DefaultIcon et NON par Shell Icons 3 : l'icone de
        // dossier generique ne les touche pas. GUID propres a ces entrees,
        // distincts des GUID d'icones du Bureau.
        //
        // HKCR est une vue fusionnee ou HKCU prime sur HKLM. On sonde les deux
        // pour savoir si la substitution sans elevation suffit.

        foreach (var (nom, guid) in DossiersSpeciaux)
        {
            list.Add(("dossiers speciaux HKCU", 1, new RegistryChange
            {
                Module      = "dossiers speciaux HKCU",
                Hive        = RegistryHive.CurrentUser,
                SubKey      = $@"Software\Classes\CLSID\{guid}\DefaultIcon",
                Name        = "",
                Kind        = RegistryValueKind.ExpandString,
                NewValue    = icon,
                Description = $"{nom} (par utilisateur)",
            }));
        }

        if (!includeHklm)
            return list;

        // Les memes cles cote HKLM appartiennent a TrustedInstaller : refusees
        // meme en administrateur, verifie le 31/10/2026. Voie fermee, on ne
        // les sonde plus.

        // --- Niveau 2 : Shell Icons (HKLM) ----------------------------
        // Index non documentes. On sonde ceux qui reviennent le plus dans la
        // litterature ; les autres ne valent pas le risque d'ecriture.

        void Shell(int index, string label)
            => list.Add(("shell icons", 2, new RegistryChange
            {
                Module      = "shell icons",
                Hive        = RegistryHive.LocalMachine,
                SubKey      = ShellIcons,
                Name        = index.ToString(),
                Kind        = RegistryValueKind.ExpandString,
                NewValue    = icon,
                Description = $"Index {index} — {label}",
            }));

        Shell(1,  "document sans association");
        Shell(2,  "application");
        Shell(3,  "dossier ferme");
        Shell(4,  "dossier ouvert");
        Shell(29, "superposition de raccourci");

        // --- Niveau 2 : lecteurs (HKLM), lettre par lettre ------------

        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed) continue;

            string letter = d.Name.TrimEnd('\\', ':');
            list.Add(("lecteurs", 2, new RegistryChange
            {
                Module      = "lecteurs",
                Hive        = RegistryHive.LocalMachine,
                SubKey      = $@"{DriveIcons}\{letter}\DefaultIcon",
                Name        = "",
                Kind        = RegistryValueKind.ExpandString,
                NewValue    = icon,
                Description = $"Lecteur {letter}:",
            }));
        }

        return list;
    }
}
