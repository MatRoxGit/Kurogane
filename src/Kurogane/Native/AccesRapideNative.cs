using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// L'accès rapide de Windows, lu dans l'espace de noms du shell.
///
/// On aurait pu tenir notre propre liste d'épingles dans un fichier de
/// configuration. Lire celle de Windows vaut mieux pour deux raisons :
/// l'utilisateur retrouve ce qu'il avait déjà épinglé, et « Épingler à
/// l'accès rapide » du menu contextuel — que Kurogane expose désormais —
/// écrit justement dans cette liste. Épingler depuis Kurogane fonctionne
/// donc sans une ligne de plus.
///
/// L'accès rapide n'est pas un dossier du disque : c'est un élément de
/// l'espace de noms, désigné par un CLSID. Il faut passer par le shell
/// pour l'énumérer. C'est le premier morceau de la bascule prévue par
/// <c>ISourceNoeuds</c> — « Ce PC » et « Réseau » suivront le même
/// chemin.
/// </summary>
internal static class AccesRapideNative
{
    // Deux écritures du même nœud. La première est celle qu'on voit
    // partout ; la seconde est la forme canonique que le bureau sait
    // analyser. Essayer les deux coûte moins cher qu'un volet muet si
    // l'une n'est pas reconnue.
    private static readonly string[] Chemins =
    {
        "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}",
        "::{679f85cb-0220-4080-b29b-5540cc05aab6}",
    };

    private static readonly Guid IdElement =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");   // IID_IShellItem

    private static readonly Guid IdEnumeration =
        new("70629033-e363-4a28-a567-0db78006e6d7");   // IID_IEnumShellItems

    private static readonly Guid IdEnumerer =
        new("94f60519-2850-4924-aa5a-d15e84868039");   // BHID_EnumItems

    private const uint CheminFichier = 0x80058000;     // SIGDN_FILESYSPATH
    private const uint NomAffiche    = 0x00000000;     // SIGDN_NORMALDISPLAY

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr contexte, ref Guid bhid, ref Guid riid,
                          out IntPtr objet);

        [PreserveSig]
        int GetParent(out IShellItem parent);

        [PreserveSig]
        int GetDisplayName(uint forme, out IntPtr nom);

        [PreserveSig]
        int GetAttributes(uint masque, out uint attributs);

        [PreserveSig]
        int Compare(IShellItem autre, uint indice, out int ordre);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid Ensemble;
        public uint Identifiant;
    }

    /// <summary>
    /// IShellItem2 ajoute l'accès aux propriétés. Seul <c>GetBool</c> nous
    /// sert, mais il est le dix-huitième de la table virtuelle : les dix-sept
    /// précédents doivent être déclarés dans l'ordre, même inutilisés. Leurs
    /// paramètres passent en IntPtr — on ne les appellera pas.
    /// </summary>
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        [PreserveSig] int BindToHandler(IntPtr contexte, ref Guid bhid,
                                        ref Guid riid, out IntPtr objet);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint forme, out IntPtr nom);
        [PreserveSig] int GetAttributes(uint masque, out uint attributs);
        [PreserveSig] int Compare(IntPtr autre, uint indice, out int ordre);

        [PreserveSig] int GetPropertyStore(uint options, ref Guid riid, out IntPtr magasin);
        [PreserveSig] int GetPropertyStoreWithCreateObject(uint options, IntPtr fabrique,
                                                           ref Guid riid, out IntPtr magasin);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr cles, uint nombre, uint options,
                                                   ref Guid riid, out IntPtr magasin);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY cle, ref Guid riid,
                                                      out IntPtr liste);
        [PreserveSig] int Update(IntPtr contexte);
        [PreserveSig] int GetProperty(ref PROPERTYKEY cle, IntPtr valeur);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY cle, out Guid identifiant);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY cle, out long date);
        [PreserveSig] int GetInt32(ref PROPERTYKEY cle, out int valeur);
        [PreserveSig] int GetString(ref PROPERTYKEY cle, out IntPtr valeur);
        [PreserveSig] int GetUInt32(ref PROPERTYKEY cle, out uint valeur);
        [PreserveSig] int GetUInt64(ref PROPERTYKEY cle, out ulong valeur);
        [PreserveSig] int GetBool(ref PROPERTYKEY cle, out int valeur);
    }

    /// <summary>
    /// System.IsPinnedToNameSpaceTree — ce que coche « Épingler à l'accès
    /// rapide ». C'est la seule chose qui distingue une épingle posée par
    /// l'utilisateur d'un dossier que Windows a jugé fréquent et ajouté de
    /// lui-même. Les deux cohabitent dans le même nœud.
    /// </summary>
    private static readonly PROPERTYKEY Epinglee = new()
    {
        Ensemble = new Guid("5D76B67F-9B3D-44BB-B6AE-25DA4F638A67"),
        Identifiant = 2,
    };

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint nombre, out IShellItem element, out uint obtenus);

        [PreserveSig]
        int Skip(uint nombre);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumShellItems copie);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string chemin, IntPtr contexte,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem element);

    /// <summary>
    /// Les dossiers épinglés, dans l'ordre où Windows les présente.
    ///
    /// Le nœud « Accueil » mêle dossiers épinglés et fichiers récents : on
    /// ne garde que ce qui est un dossier réel du disque. Retourne une
    /// liste vide si le shell ne répond pas — un volet sans accès rapide
    /// reste utilisable, une exception au démarrage non.
    /// </summary>
    internal static IReadOnlyList<(string Nom, string Chemin)> Lire(int maximum = 12)
    {
        var epingles = new List<(string, string)>();
        var tous = new List<(string, string)>();
        int repondu = 0;

        IShellItem? racine = null;
        IEnumShellItems? enumeration = null;

        try
        {
            foreach (string candidat in Chemins)
            {
                try
                {
                    SHCreateItemFromParsingName(candidat, IntPtr.Zero,
                                                IdElement, out racine);
                    break;
                }
                catch (Exception) { racine = null; }
            }

            if (racine is null) return epingles;

            var bhid = IdEnumerer;
            var iid = IdEnumeration;

            if (racine.BindToHandler(IntPtr.Zero, ref bhid, ref iid,
                                     out IntPtr pointeur) < 0
                || pointeur == IntPtr.Zero)
                return epingles;

            enumeration = (IEnumShellItems)Marshal.GetObjectForIUnknown(pointeur);
            Marshal.Release(pointeur);

            // Deux bornes : assez d'épingles, ou assez d'éléments parcourus.
            // Le nœud peut contenir des centaines de fichiers récents, et on
            // n'a pas à les traverser tous pour trouver une douzaine de
            // dossiers.
            while (epingles.Count < maximum && tous.Count < maximum * 5
                   && enumeration.Next(1, out IShellItem element, out uint obtenus) == 0
                   && obtenus == 1)
            {
                try
                {
                    if (element.GetDisplayName(CheminFichier, out IntPtr brut) < 0)
                        continue;

                    string chemin = Marshal.PtrToStringUni(brut) ?? "";
                    Marshal.FreeCoTaskMem(brut);

                    // Les fichiers récents partagent ce nœud avec les
                    // dossiers épinglés : seuls les seconds ont leur place
                    // dans un volet de navigation.
                    if (chemin.Length == 0 || !Directory.Exists(chemin)) continue;

                    string nom = chemin;

                    if (element.GetDisplayName(NomAffiche, out IntPtr libelle) >= 0)
                    {
                        nom = Marshal.PtrToStringUni(libelle) ?? chemin;
                        Marshal.FreeCoTaskMem(libelle);
                    }

                    tous.Add((nom, chemin));

                    // Épinglé par l'utilisateur, ou seulement jugé fréquent
                    // par Windows ? L'accès rapide est une liste qu'on
                    // tient, pas une statistique qu'on subit.
                    bool? epingle = Epingle(element);

                    if (epingle is not null) repondu++;
                    if (epingle == true) epingles.Add((nom, chemin));
                }
                finally
                {
                    Marshal.ReleaseComObject(element);
                }
            }
        }
        catch (Exception)
        {
            // Accès rapide désactivé par stratégie, ou shell indisponible.
            // Ce n'est pas une erreur : c'est une absence.
        }
        finally
        {
            if (enumeration is not null) Marshal.ReleaseComObject(enumeration);
            if (racine is not null) Marshal.ReleaseComObject(racine);
        }

        // Si aucun élément n'a su répondre, la propriété n'est pas
        // disponible ici : on montre tout plutôt qu'un volet vide. Afficher
        // un dossier de trop vaut mieux que perdre une épingle.
        return repondu == 0 ? tous : epingles;
    }

    /// <summary>
    /// Vrai si épinglé, faux si non, <c>null</c> si l'élément n'a pas su
    /// répondre — les trois cas comptent, et le troisième ne doit pas se
    /// confondre avec le deuxième.
    /// </summary>
    private static bool? Epingle(IShellItem element)
    {
        if (element is not IShellItem2 complet) return null;

        try
        {
            var cle = Epinglee;
            return complet.GetBool(ref cle, out int valeur) < 0
                ? null
                : valeur != 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

}
