using System.Diagnostics;
using System.IO;

namespace Kurogane.Explorateur;

/// <summary>
/// Listage du système de fichiers réel. Lecture seule : cette classe
/// n'ouvre, ne crée, ne renomme et ne supprime rien.
/// </summary>
public sealed class SourceFichiers : ISourceNoeuds
{
    /// <summary>
    /// Les dossiers du profil proposés en plus des disques. Ce sont des
    /// chemins réels, pas des éléments de l'espace de noms : « Ce PC » et
    /// « Réseau » viendront avec l'implémentation shell.
    /// </summary>
    private static readonly (string Sous, string Libelle, string Icone)[] Dossiers =
    {
        ("",              "ACCUEIL",    "accueil"),
        ("Desktop",       "BUREAU",     "bureau"),
        ("Documents",     "DOCUMENTS",  "documents"),
        ("Downloads",     "TELECHARG.", "telechargements"),
        ("Pictures",      "IMAGES",     "images"),
        ("Music",         "MUSIQUE",    "musique"),
        ("Videos",        "VIDEOS",     "videos"),
        ("Favorites",     "FAVORIS",    "favoris"),
        ("Links",         "LIENS",      "liens"),
        ("Searches",      "RECHERCHES", "recherches"),
        ("Saved Games",   "PARTIES",    "parties-enregistrees"),
        ("3D Objects",    "OBJETS 3D",  "objets-3d"),
    };

    /// <summary>
    /// Icône selon le type de lecteur. Windows distingue ces natures et les
    /// montre ; les confondre toutes sous un même disque perdrait une
    /// information que l'utilisateur lit d'un coup d'œil.
    /// </summary>
    private static string IconeLecteur(DriveType type) => type switch
    {
        DriveType.Removable => "cle-usb",
        DriveType.CDRom     => "lecteur-optique",
        DriveType.Network   => "lecteur-reseau",
        DriveType.Ram       => "image-disque",
        _                   => "lecteur",
    };

    public IReadOnlyList<Racine> Racines()
    {
        var liste = new List<Racine>();

        string profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var (sous, libelle, icone) in Dossiers)
        {
            // Les dossiers du profil portent un nom anglais sur le disque,
            // même sur un Windows français : c'est l'affichage qui est
            // traduit, par desktop.ini, pas le dossier lui-même.
            string chemin = sous.Length == 0 ? profil : Path.Combine(profil, sous);

            if (!Directory.Exists(chemin)) continue;

            liste.Add(new Racine("DOSSIER", libelle, chemin, icone));
        }

        foreach (var disque in DriveInfo.GetDrives())
        {
            // IsReady est faux pour un lecteur vide ou un réseau tombé :
            // l'interroger plus loin lèverait, et bloquerait le volet entier.
            if (!disque.IsReady) continue;

            string lettre = disque.Name.TrimEnd('\\', '/');

            liste.Add(new Racine("VOL", lettre.ToUpperInvariant(),
                disque.RootDirectory.FullName, IconeLecteur(disque.DriveType)));
        }

        return liste;
    }

    public async Task<Listage> Lister(string chemin, CancellationToken jeton)
    {
        var horloge = Stopwatch.StartNew();

        // Hors du fil d'interface : un dossier réseau lent y gèlerait la
        // fenêtre entière, et c'est exactement ce qu'on reproche à
        // l'Explorateur.
        var resultat = await Task.Run(() => Parcourir(chemin, jeton), jeton)
                                 .ConfigureAwait(false);

        horloge.Stop();

        return resultat with { Millisecondes = horloge.ElapsedMilliseconds };
    }

    private static Listage Parcourir(string chemin, CancellationToken jeton)
    {
        var dossiers = new List<Entree>();
        var fichiers = new List<Entree>();
        int erreurs = 0;

        IEnumerable<string> brut;

        try
        {
            brut = Directory.EnumerateFileSystemEntries(chemin);
        }
        catch (Exception)
        {
            // Le dossier lui-même est illisible : zéro entrée, une erreur.
            return new Listage(Array.Empty<Entree>(), 1, 0);
        }

        foreach (var entree in brut)
        {
            jeton.ThrowIfCancellationRequested();

            try
            {
                var info = new FileInfo(entree);
                bool estDossier = info.Attributes.HasFlag(FileAttributes.Directory);

                // Les entrées système cachées encombrent sans servir —
                // pagefile.sys, System Volume Information. Un réglage les
                // rendra visibles plus tard ; pour l'instant on allège.
                if (info.Attributes.HasFlag(FileAttributes.Hidden)
                    && info.Attributes.HasFlag(FileAttributes.System)) continue;

                var e = new Entree(
                    Path.GetFileName(entree),
                    entree,
                    estDossier,
                    estDossier ? 0 : info.Length,
                    info.LastWriteTime);

                if (estDossier) dossiers.Add(e); else fichiers.Add(e);
            }
            catch (Exception)
            {
                // Une entrée qui résiste — droits, lien cassé, nom trop
                // long — ne doit pas faire échouer le dossier entier.
                erreurs++;
            }
        }

        var comparateur = StringComparer.OrdinalIgnoreCase;
        dossiers.Sort((a, b) => comparateur.Compare(a.Nom, b.Nom));
        fichiers.Sort((a, b) => comparateur.Compare(a.Nom, b.Nom));

        dossiers.AddRange(fichiers);

        return new Listage(dossiers, erreurs, 0);
    }

    public string? Parent(string chemin)
    {
        try { return Directory.GetParent(chemin)?.FullName; }
        catch { return null; }
    }

    public IReadOnlyList<string> Segments(string chemin)
    {
        var pile = new List<string>();

        try
        {
            var courant = new DirectoryInfo(chemin);

            while (courant is not null)
            {
                // Le nom d'une racine est « C:\ » : on le réduit à « VOL_C: »
                // pour rester dans le vocabulaire de la maquette.
                pile.Insert(0, courant.Parent is null
                    ? "VOL_" + courant.Name.TrimEnd('\\', '/')
                    : courant.Name);

                courant = courant.Parent;
            }
        }
        catch
        {
            return new[] { chemin };
        }

        return pile;
    }
}
