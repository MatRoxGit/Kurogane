namespace Kurogane.Explorateur;

/// <summary>
/// Quelle icône du pack pour quelle extension.
///
/// La table vit dans le code et non dans le thème : les noms d'icônes sont
/// déjà les noms de catégories — « texte.ico », « archive.ico » — donc une
/// section de thème n'ajouterait qu'une indirection. Le jour où un thème
/// voudra ses propres catégories, elle s'ajoutera sans rien casser.
///
/// Aucun appel au shell ici. Extraire l'icône réelle d'un fichier coûte un
/// appel système par fichier, et on vient de passer la soirée à ramener le
/// rendu de 671 à 53 ms : ce coût-là sera mesuré avant d'être payé.
/// </summary>
internal static class TypesFichiers
{
    internal const string Generique = "fichier-generique";
    internal const string Dossier   = "dossier";

    private static readonly (string Icone, string[] Extensions)[] Familles =
    {
        ("texte", new[]
            { "txt", "log", "nfo", "md", "markdown", "ini", "cfg", "conf", "toml", "env" }),

        ("document", new[]
            { "doc", "docx", "odt", "rtf", "pages", "wpd", "tex" }),

        ("tableur", new[]
            { "xls", "xlsx", "xlsm", "xlsb", "csv", "tsv", "ods", "numbers" }),

        ("presentation", new[]
            { "ppt", "pptx", "pps", "ppsx", "odp", "key" }),

        ("pdf", new[] { "pdf", "xps", "oxps" }),

        ("archive", new[]
            { "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "cab", "jar", "lzh", "zst" }),

        ("image-fichier", new[]
            { "png", "jpg", "jpeg", "jfif", "gif", "bmp", "webp", "tif", "tiff",
              "svg", "ico", "cur", "heic", "heif", "avif", "psd", "ai", "raw",
              "cr2", "nef", "dng" }),

        ("audio", new[]
            { "mp3", "wav", "flac", "ogg", "oga", "opus", "m4a", "aac", "wma",
              "mid", "midi", "aiff", "ape" }),

        ("video-fichier", new[]
            { "mp4", "m4v", "mkv", "avi", "mov", "wmv", "webm", "flv", "mpg",
              "mpeg", "ts", "3gp", "vob" }),

        ("executable", new[]
            { "exe", "msi", "msix", "appx", "com", "scr", "msp", "apk" }),

        ("script", new[]
            { "ps1", "psm1", "psd1", "bat", "cmd", "sh", "bash", "zsh", "vbs",
              "wsf", "ahk" }),

        ("code", new[]
            { "cs", "csproj", "sln", "vb", "fs", "js", "mjs", "cjs", "jsx",
              "ts", "tsx", "py", "pyw", "java", "kt", "c", "h", "cpp", "hpp",
              "cc", "go", "rs", "rb", "php", "lua", "swift", "pl", "r", "dart",
              "html", "htm", "xhtml", "css", "scss", "sass", "less",
              "json", "jsonc", "xml", "xaml", "yml", "yaml", "sql", "gradle",
              "cmake", "make", "dockerfile", "diff", "patch" }),

        ("police", new[] { "ttf", "otf", "ttc", "woff", "woff2", "fon", "fnt", "pfb" }),

        ("image-disque", new[]
            { "iso", "img", "vhd", "vhdx", "vmdk", "wim", "esd", "dmg", "bin", "cue" }),

        ("objets-3d", new[]
            { "obj", "stl", "fbx", "3ds", "dae", "blend", "gltf", "glb", "ply", "step" }),

        // verrou tient lieu d'icône de certificat en attendant la sienne.
        ("verrou", new[] { "cer", "crt", "pem", "pfx", "p12", "p7b", "key", "asc", "gpg" }),

        ("raccourci", new[] { "lnk", "url", "website", "desktop" }),

        ("bibliotheque", new[] { "dll", "sys", "ocx", "drv", "cpl", "ax", "winmd" }),

        ("parametres", new[] { "reg", "inf", "manifest", "config", "plist" }),

        ("base-de-donnees", new[]
            { "db", "db3", "sqlite", "sqlite3", "mdb", "accdb", "dbf", "sdf", "mdf" }),

        ("courriel", new[] { "eml", "msg", "pst", "ost", "mbox", "emlx" }),

        ("calendrier", new[] { "ics", "vcs", "ical" }),

        ("contact", new[] { "vcf", "vcard" }),
    };

    private static readonly Dictionary<string, string> Table = Construire();

    private static Dictionary<string, string> Construire()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (icone, extensions) in Familles)
            foreach (var ext in extensions)
                table[ext] = icone;   // la dernière famille l'emporte, par construction

        return table;
    }

    /// <summary>Nom de base de l'icône, sans extension de fichier.</summary>
    internal static string Pour(Entree entree)
    {
        if (entree.EstDossier) return Dossier;

        string ext = entree.Extension;

        return ext.Length > 0 && Table.TryGetValue(ext, out var icone) ? icone : Generique;
    }

    /// <summary>Nombre d'extensions reconnues — utile au journal.</summary>
    internal static int Reconnues => Table.Count;

    /// <summary>
    /// Les noms d'icônes utilisés par la table, dossier et générique
    /// compris. Sert à la sonde du pack, qui vérifie qu'ils existent tous.
    /// </summary>
    internal static IEnumerable<string> Noms() =>
        Familles.Select(f => f.Icone).Append(Generique).Append(Dossier).Distinct();
}
