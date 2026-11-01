using System.IO;
using System.Text;

namespace Kurogane.Core;

/// <summary>
/// Ecriture d'un fichier, ou pose d'attributs sur un fichier ou un dossier.
///
/// Meme discipline que pour le registre : on lit l'etat d'origine avant
/// d'ecrire, et l'operation inverse traite le cas « le fichier n'existait
/// pas » par une suppression, jamais par l'ecriture d'un contenu suppose.
///
/// Le contenu d'origine est conserve en entier dans le point de
/// restauration. C'est volontaire : un desktop.ini porte souvent un
/// LocalizedResourceName qu'il serait desastreux de perdre.
/// </summary>
public sealed class FileChange : IChange
{
    public required string Module { get; init; }
    public required string Path { get; init; }
    public required string Description { get; init; }

    /// <summary>Contenu a ecrire. null pour un dossier, ou pour ne toucher qu'aux attributs.</summary>
    public byte[]? Content { get; init; }

    /// <summary>Attributs a poser. null pour ne pas y toucher.</summary>
    public FileAttributes? Attributes { get; init; }

    /// <summary>true quand la cible est un dossier : son contenu n'est jamais touche.</summary>
    public bool IsDirectory { get; init; }

    public string Target => Path;

    // ---------- capture ----------

    public ChangeRecord Capture()
    {
        bool existe = IsDirectory ? Directory.Exists(Path) : File.Exists(Path);

        string? attrsOrigine = null;
        string? contenuOrigine = null;

        FileAttributes baseAttrs = default;

        if (existe)
        {
            baseAttrs = File.GetAttributes(Path);
            attrsOrigine = baseAttrs.ToString();

            if (!IsDirectory)
                contenuOrigine = Convert.ToBase64String(File.ReadAllBytes(Path));
        }

        // On enregistre le jeu d'attributs RESULTANT, pas celui demande : les
        // attributs s'ajoutent aux existants. Sans ca, un dossier deja en
        // ReadOnly produirait une modification fantome que le filtre des
        // valeurs conformes ne saurait pas ecarter.
        string? attrsCible = Attributes is { } a ? Resultat(baseAttrs, a).ToString() : null;

        return new ChangeRecord
        {
            Kind               = ChangeKind.File,
            Module             = Module,
            Target             = Target,
            Description        = Description,
            Path               = Path,
            IsDirectory        = IsDirectory,
            Existed            = existe,
            OriginalAttributes = attrsOrigine,
            NewAttributes      = attrsCible,
            OriginalValue      = contenuOrigine,
            NewValue           = Content is null ? contenuOrigine : Convert.ToBase64String(Content),
            OriginalValueKind  = "File",
            NewValueKind       = "File",
        };
    }

    // ---------- controle d'ecriture ----------

    public bool CanWrite()
    {
        try
        {
            string dossier = IsDirectory ? Path : (System.IO.Path.GetDirectoryName(Path) ?? "");

            if (!Directory.Exists(dossier)) return false;

            // Un fichier en lecture seule bloquerait l'ecriture ; on le sait
            // des maintenant plutot qu'au milieu du plan.
            if (!IsDirectory && File.Exists(Path)
                && File.GetAttributes(Path).HasFlag(FileAttributes.ReadOnly))
                return true;   // on retire l'attribut avant d'ecrire, cf. Apply

            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException)                 { return false; }
    }

    // ---------- application ----------

    public void Apply()
    {
        if (IsDirectory)
        {
            if (Attributes is { } ad)
                File.SetAttributes(Path, Resultat(File.GetAttributes(Path), ad));
            return;
        }

        FileAttributes? origine = File.Exists(Path) ? File.GetAttributes(Path) : null;

        // Un desktop.ini existant est cache et systeme : Windows refuse de le
        // reecrire tant qu'il porte ces attributs.
        if (origine is not null)
            File.SetAttributes(Path, FileAttributes.Normal);

        if (Content is not null)
            File.WriteAllBytes(Path, Content);

        if (Attributes is { } a)
            File.SetAttributes(Path, Resultat(origine ?? default, a));
        else if (origine is { } o)
            File.SetAttributes(Path, o);
    }

    /// <summary>
    /// Attributs existants PLUS ceux demandes. Normal est retire : il ne vaut
    /// que seul, et le conserver produirait un jeu d'attributs incoherent.
    /// </summary>
    private static FileAttributes Resultat(FileAttributes existants, FileAttributes ajout) =>
        (existants & ~FileAttributes.Normal) | ajout;

    // ---------- restauration ----------

    public static void Revert(ChangeRecord r)
    {
        string chemin = r.Path
            ?? throw new InvalidOperationException("Enregistrement fichier sans chemin.");

        if (r.IsDirectory)
        {
            if (Directory.Exists(chemin) && r.OriginalAttributes is { } ad)
                File.SetAttributes(chemin, Enum.Parse<FileAttributes>(ad));
            return;
        }

        if (!r.Existed)
        {
            // Le fichier n'existait pas avant nous : la bonne restauration est
            // de le retirer.
            if (File.Exists(chemin))
            {
                File.SetAttributes(chemin, FileAttributes.Normal);
                File.Delete(chemin);
            }
            return;
        }

        if (File.Exists(chemin))
            File.SetAttributes(chemin, FileAttributes.Normal);

        if (r.OriginalValue is { } contenu)
            File.WriteAllBytes(chemin, Convert.FromBase64String(contenu));

        if (r.OriginalAttributes is { } attrs)
            File.SetAttributes(chemin, Enum.Parse<FileAttributes>(attrs));
    }

    // ---------- fabrique : desktop.ini ----------

    /// <summary>
    /// Produit le contenu d'un desktop.ini portant l'icone demandee, en
    /// FUSIONNANT avec le fichier existant : la ligne IconResource est
    /// remplacee ou ajoutee, tout le reste est conserve mot pour mot.
    ///
    /// Les dossiers du profil portent deja un desktop.ini avec un
    /// LocalizedResourceName : l'ecraser remplacerait « Documents » par
    /// « Documents » en anglais, ou pire.
    ///
    /// Encode en UTF-16 LE avec BOM : c'est la seule forme Unicode que
    /// GetPrivateProfileString, qui lit ces fichiers, sait traiter.
    /// </summary>
    public static byte[] DesktopIni(string? existant, string cheminIcone)
    {
        const string Section = "[.ShellClassInfo]";
        string ligne = $"IconResource={cheminIcone},0";

        var lignes = string.IsNullOrEmpty(existant)
            ? new List<string>()
            : existant.Replace("\r\n", "\n").Split('\n').ToList();

        int iSection = lignes.FindIndex(l =>
            l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));

        if (iSection < 0)
        {
            if (lignes.Count > 0 && !string.IsNullOrWhiteSpace(lignes[^1]))
                lignes.Add("");
            lignes.Add(Section);
            lignes.Add(ligne);
        }
        else
        {
            // Fin de la section : prochaine ligne commencant par '['.
            int fin = lignes.FindIndex(iSection + 1, l => l.TrimStart().StartsWith('['));
            if (fin < 0) fin = lignes.Count;

            int iIcone = lignes.FindIndex(iSection + 1, fin - iSection - 1, l =>
                l.TrimStart().StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase));

            if (iIcone >= 0) lignes[iIcone] = ligne;
            else             lignes.Insert(fin, ligne);

            // Les dossiers du profil portent AUSSI la paire heritee
            // IconFile / IconIndex, qui pointe vers une autre icone. La
            // laisser en place, c'est deux instructions contradictoires dans
            // le meme fichier — et rien ne garantit laquelle l'emporte.
            // Le contenu d'origine complet est dans le point de restauration.
            lignes.RemoveAll(l =>
                l.TrimStart().StartsWith("IconFile=",  StringComparison.OrdinalIgnoreCase) ||
                l.TrimStart().StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase));
        }

        string texte = string.Join("\r\n", lignes).TrimEnd('\r', '\n') + "\r\n";
        return new UnicodeEncoding(bigEndian: false, byteOrderMark: true).GetPreamble()
               .Concat(Encoding.Unicode.GetBytes(texte))
               .ToArray();
    }
}
