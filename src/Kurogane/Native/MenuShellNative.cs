using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Kurogane.Native;

/// <summary>Une entrée du menu du shell, lue et prête à être redessinée.</summary>
internal sealed record EntreeShell(
    int Id,
    string Libelle,
    string Raccourci,
    bool Separateur,
    bool Actif,
    bool Coche,
    IntPtr SousMenu,
    int Position);

/// <summary>
/// Le menu contextuel du shell Windows — ses entrées, pas son dessin.
///
/// Notre menu couvre les actions courantes. Il ne peut pas couvrir toutes
/// celles de Windows, et c'est structurel : « Envoyer vers »,
/// « Compresser », et surtout ce que les logiciels installés ajoutent —
/// 7-Zip, Git, un antivirus. Aucun inventaire écrit à l'avance ne
/// connaîtra ce qui n'est pas encore installé.
///
/// Première version : on demandait le menu au shell et on le laissait
/// l'afficher lui-même, comme le fait Windows 11 derrière « Afficher plus
/// d'options ». Toutes les entrées y étaient, mais le menu était dessiné
/// par Windows — hors thème, au milieu d'une fenêtre qui ne l'est pas.
///
/// Version actuelle : on demande toujours le menu au shell, mais on ne le
/// laisse plus le dessiner. On lit sa structure — libellés, raccourcis,
/// séparateurs, états, sous-menus — et l'explorateur la redessine avec ses
/// propres gabarits. L'exécution retourne au shell, qui seul sait ce que
/// fait chaque verbe.
///
/// L'objet tient le menu natif et l'objet COM en vie tant qu'il n'est pas
/// libéré : l'identifiant d'une commande n'a de sens que pour l'instance
/// qui l'a produit.
/// </summary>
internal sealed class MenuShellNative : IDisposable
{
    // ------------------------------------------------------------ interop

    private static readonly Guid IdMenu =
        new("000214e4-0000-0000-c000-000000000046");   // IID_IContextMenu

    private static readonly Guid IdObjetUI =
        new("3981e225-f559-11d3-8e3a-00c04f6837d5");   // BHID_SFUIObject

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        // Seule la première méthode nous sert, mais l'ordre de la table
        // virtuelle est ce qui compte : on ne peut pas sauter une entrée,
        // seulement s'arrêter après celle dont on a besoin.
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint position, uint idPremier,
                             uint idDernier, uint drapeaux);

        [PreserveSig]
        int InvokeCommand(ref INVOKECOMMANDINFO info);

        [PreserveSig]
        int GetCommandString(UIntPtr id, uint type, IntPtr reserve,
                             IntPtr nom, uint taille);
    }

    /// <summary>
    /// IContextMenu3 — la table virtuelle reprend celle d'IContextMenu puis
    /// ajoute le traitement des messages. C'est par là qu'on fait remplir
    /// un sous-menu : le shell ne le garnit qu'au moment où il croit que
    /// Windows va le déplier.
    /// </summary>
    [ComImport, Guid("BCFCE0A0-EC17-11d0-8D10-00A0C90F2719"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint position, uint idPremier,
                             uint idDernier, uint drapeaux);

        [PreserveSig]
        int InvokeCommand(ref INVOKECOMMANDINFO info);

        [PreserveSig]
        int GetCommandString(UIntPtr id, uint type, IntPtr reserve,
                             IntPtr nom, uint taille);

        [PreserveSig]
        int HandleMenuMsg(uint message, IntPtr wparam, IntPtr lparam);

        [PreserveSig]
        int HandleMenuMsg2(uint message, IntPtr wparam, IntPtr lparam,
                           out IntPtr resultat);
    }

    private static readonly Guid IdElement =
        new("43826d1e-e718-42ee-bc55-a1e261c37bfe");   // IID_IShellItem

    private static readonly Guid IdDossier =
        new("000214E6-0000-0000-C000-000000000046");   // IID_IShellFolder

    private static readonly Guid IdObjet =
        new("3981e224-f559-11d3-8e3a-00c04f6837d5");   // BHID_SFObject

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr contexte, ref Guid bhid,
                                        ref Guid riid, out IntPtr objet);
        [PreserveSig] int GetParent(out IntPtr parent);
        [PreserveSig] int GetDisplayName(uint forme, out IntPtr nom);
        [PreserveSig] int GetAttributes(uint masque, out uint attributs);
        [PreserveSig] int Compare(IntPtr autre, uint indice, out int ordre);
    }

    /// <summary>
    /// IShellFolder, déclarée jusqu'à <c>CreateViewObject</c> — la
    /// sixième de la table virtuelle. C'est par elle qu'on obtient le menu
    /// du FOND d'un dossier, celui qui porte « Nouveau », « Actualiser »
    /// et « Personnaliser ». Le menu d'un élément sélectionné est un autre
    /// objet, obtenu autrement : les confondre donnait jusqu'ici le menu
    /// du dossier courant là où il fallait celui de son fond.
    /// </summary>
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr fenetre, IntPtr contexte,
                                           [MarshalAs(UnmanagedType.LPWStr)] string nom,
                                           out uint consommes, out IntPtr pidl,
                                           ref uint attributs);
        [PreserveSig] int EnumObjects(IntPtr fenetre, uint drapeaux, out IntPtr enumeration);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr contexte, ref Guid riid,
                                       out IntPtr objet);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr contexte, ref Guid riid,
                                        out IntPtr objet);
        [PreserveSig] int CompareIDs(IntPtr parametre, IntPtr un, IntPtr deux);
        [PreserveSig] int CreateViewObject(IntPtr fenetre, ref Guid riid, out IntPtr objet);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHGetDesktopFolder(
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder racine);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string chemin, IntPtr contexte,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem element);

    /// <summary>
    /// CMINVOKECOMMANDINFO. Tous les champs en IntPtr : la variante étendue
    /// mélange chaînes ANSI et Unicode dans une même structure, source
    /// d'erreurs de marshalling pour un gain nul ici — on ne transmet qu'un
    /// identifiant de commande.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct INVOKECOMMANDINFO
    {
        public int Taille;
        public int Masque;
        public IntPtr Fenetre;
        public IntPtr Verbe;
        public IntPtr Parametres;
        public IntPtr Dossier;
        public int Affichage;
        public int Raccourci;
        public IntPtr Icone;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public int cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHParseDisplayName(
        string nom, IntPtr contexte, out IntPtr pidl,
        uint attendus, out uint obtenus);

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromIDLists(
        uint nombre,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray tableau);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr menu, uint element,
                                               bool parPosition,
                                               ref MENUITEMINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")]
    private static extern int GetMenuString(IntPtr menu, uint element,
                                            StringBuilder? tampon, int taille,
                                            uint drapeaux);

    private const uint CmfNormal = 0x00000000;
    private const uint CmfEtendu = 0x00000100;   // CMF_EXTENDEDVERBS
    private const int AffichageNormal = 1;       // SW_SHOWNORMAL

    private const uint IdPremier = 1;
    private const uint IdDernier = 0x7FFF;

    private const uint MiimEtat     = 0x00000001;   // MIIM_STATE
    private const uint MiimId       = 0x00000002;   // MIIM_ID
    private const uint MiimSousMenu = 0x00000004;   // MIIM_SUBMENU
    private const uint MiimTypeSeul = 0x00000100;   // MIIM_FTYPE

    private const uint MftSeparateur = 0x00000800;  // MFT_SEPARATOR

    private const uint MfsGrise    = 0x00000001;    // MFS_GRAYED
    private const uint MfsInactif  = 0x00000002;    // MFS_DISABLED
    private const uint MfsCochee   = 0x00000008;    // MFS_CHECKED

    private const uint MfParPosition = 0x00000400;  // MF_BYPOSITION

    private const uint WM_INITMENUPOPUP = 0x0117;

    // ----------------------------------------------------------- instance

    private readonly List<IntPtr> _pidls;
    private readonly IShellItemArray? _tableau;
    private readonly object _objet;
    private readonly IContextMenu _menu;
    private readonly IContextMenu3? _trois;
    private readonly IntPtr _natif;

    private bool _libere;

    private MenuShellNative(List<IntPtr> pidls, IShellItemArray? tableau,
                            object objet, IContextMenu menu,
                            IContextMenu3? trois, IntPtr natif)
    {
        _pidls = pidls;
        _tableau = tableau;
        _objet = objet;
        _menu = menu;
        _trois = trois;
        _natif = natif;
    }

    /// <summary>
    /// Demande au shell le menu de ces chemins. Retourne <c>null</c> s'il
    /// n'a pas pu être construit — à l'appelant de le dire plutôt que
    /// d'échouer en silence.
    /// </summary>
    internal static MenuShellNative? Construire(IReadOnlyList<string> chemins,
                                                bool etendu)
    {
        if (chemins.Count == 0) return null;

        var pidls = new List<IntPtr>();
        IShellItemArray? tableau = null;
        object? objet = null;
        IntPtr natif = IntPtr.Zero;

        try
        {
            foreach (string chemin in chemins)
            {
                SHParseDisplayName(chemin, IntPtr.Zero, out IntPtr pidl, 0, out _);
                if (pidl == IntPtr.Zero) throw new InvalidOperationException(chemin);

                pidls.Add(pidl);
            }

            SHCreateShellItemArrayFromIDLists((uint)pidls.Count, pidls.ToArray(),
                                              out tableau);

            var bhid = IdObjetUI;
            var iid = IdMenu;

            if (tableau.BindToHandler(IntPtr.Zero, ref bhid, ref iid,
                                      out IntPtr pointeur) < 0
                || pointeur == IntPtr.Zero)
                throw new InvalidOperationException("BindToHandler");

            objet = Marshal.GetObjectForIUnknown(pointeur);
            Marshal.Release(pointeur);

            if (objet is not IContextMenu contextuel)
                throw new InvalidOperationException("IContextMenu");

            natif = CreatePopupMenu();
            if (natif == IntPtr.Zero)
                throw new InvalidOperationException("CreatePopupMenu");

            uint drapeaux = CmfNormal | (etendu ? CmfEtendu : 0u);

            if (contextuel.QueryContextMenu(natif, 0, IdPremier, IdDernier,
                                            drapeaux) < 0)
                throw new InvalidOperationException("QueryContextMenu");

            return new MenuShellNative(pidls, tableau, objet, contextuel,
                                       objet as IContextMenu3, natif);
        }
        catch (Exception)
        {
            // Un gestionnaire tiers mal écrit ne doit pas emporter
            // l'explorateur : on renonce au menu, pas à la session.
            if (natif != IntPtr.Zero) DestroyMenu(natif);
            if (objet is not null) Marshal.ReleaseComObject(objet);
            if (tableau is not null) Marshal.ReleaseComObject(tableau);

            foreach (IntPtr pidl in pidls) Marshal.FreeCoTaskMem(pidl);

            return null;
        }
    }

    /// <summary>
    /// Le menu du FOND d'un dossier — « Nouveau », « Actualiser »,
    /// « Coller », et pour le bureau « Paramètres d'affichage » et
    /// « Personnaliser ».
    ///
    /// Ce n'est pas le menu du dossier vu depuis son parent, que donne
    /// <see cref="Construire"/> : c'est celui que le shell construit pour
    /// la vue elle-même, et il passe par <c>CreateViewObject</c>.
    /// </summary>
    internal static MenuShellNative? ConstruireFond(string dossier, IntPtr poignee,
                                                    bool etendu)
    {
        try
        {
            SHCreateItemFromParsingName(dossier, IntPtr.Zero, IdElement,
                                        out IShellItem element);

            var bhid = IdObjet;
            var iid = IdDossier;

            if (element.BindToHandler(IntPtr.Zero, ref bhid, ref iid,
                                      out IntPtr pointeur) < 0
                || pointeur == IntPtr.Zero)
                return null;

            object brut = Marshal.GetObjectForIUnknown(pointeur);
            Marshal.Release(pointeur);

            return DepuisDossier(brut, poignee, etendu);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Le menu du BUREAU, qui n'est pas celui du dossier « Desktop ».
    ///
    /// Le dossier du disque donne « Nouveau », « Coller », « Propriétés ».
    /// Le bureau est un nœud de l'espace de noms, et c'est lui qui porte
    /// ce que l'utilisateur attend d'un clic droit sur son fond d'écran.
    /// Les confondre donnait un menu réduit au minimum.
    /// </summary>
    internal static MenuShellNative? ConstruireBureau(IntPtr poignee, bool etendu)
    {
        try
        {
            SHGetDesktopFolder(out IShellFolder racine);
            return DepuisDossier(racine, poignee, etendu);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Tire le menu de fond d'un IShellFolder et le prépare. L'objet
    /// dossier est libéré ici : seul le menu doit survivre à l'appel.
    /// </summary>
    private static MenuShellNative? DepuisDossier(object brut, IntPtr poignee,
                                                  bool etendu)
    {
        object? objet = null;
        IntPtr natif = IntPtr.Zero;

        try
        {
            if (brut is not IShellFolder racine)
                throw new InvalidOperationException("IShellFolder");

            var iidMenu = IdMenu;

            if (racine.CreateViewObject(poignee, ref iidMenu,
                                        out IntPtr pointeur) < 0
                || pointeur == IntPtr.Zero)
                throw new InvalidOperationException("CreateViewObject");

            objet = Marshal.GetObjectForIUnknown(pointeur);
            Marshal.Release(pointeur);

            if (objet is not IContextMenu contextuel)
                throw new InvalidOperationException("IContextMenu");

            natif = CreatePopupMenu();
            if (natif == IntPtr.Zero)
                throw new InvalidOperationException("CreatePopupMenu");

            uint drapeaux = CmfNormal | (etendu ? CmfEtendu : 0u);

            if (contextuel.QueryContextMenu(natif, 0, IdPremier, IdDernier,
                                            drapeaux) < 0)
                throw new InvalidOperationException("QueryContextMenu");

            return new MenuShellNative(new List<IntPtr>(), null, objet, contextuel,
                                       objet as IContextMenu3, natif);
        }
        catch (Exception)
        {
            if (natif != IntPtr.Zero) DestroyMenu(natif);
            if (objet is not null) Marshal.ReleaseComObject(objet);

            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(brut);
        }
    }

    /// <summary>Les entrées de premier niveau.</summary>
    internal IReadOnlyList<EntreeShell> Racine() => Lire(_natif);

    /// <summary>
    /// Les entrées d'un sous-menu. Il faut d'abord prévenir le shell que
    /// Windows s'apprête à le déplier, sinon il est vide : « Envoyer vers »
    /// ne se remplit qu'à ce moment-là.
    /// </summary>
    internal IReadOnlyList<EntreeShell> Deplier(EntreeShell entree)
    {
        if (entree.SousMenu == IntPtr.Zero) return Array.Empty<EntreeShell>();

        try
        {
            if (_trois is not null)
                _trois.HandleMenuMsg2(WM_INITMENUPOPUP, entree.SousMenu,
                                      (IntPtr)entree.Position, out _);
        }
        catch (Exception)
        {
            // Sous-menu non garni : on lira ce qu'il y a, quitte à ce qu'il
            // soit vide. Mieux qu'une exception au milieu d'un menu ouvert.
        }

        return Lire(entree.SousMenu);
    }

    private static IReadOnlyList<EntreeShell> Lire(IntPtr menu)
    {
        var entrees = new List<EntreeShell>();
        int nombre = GetMenuItemCount(menu);

        for (int i = 0; i < nombre; i++)
        {
            var info = new MENUITEMINFO
            {
                cbSize = Marshal.SizeOf<MENUITEMINFO>(),
                fMask = MiimEtat | MiimId | MiimSousMenu | MiimTypeSeul,
            };

            if (!GetMenuItemInfo(menu, (uint)i, true, ref info)) continue;

            bool separateur = (info.fType & MftSeparateur) != 0;

            string brut = "";

            if (!separateur)
            {
                int taille = GetMenuString(menu, (uint)i, null, 0, MfParPosition);

                if (taille > 0)
                {
                    var tampon = new StringBuilder(taille + 1);
                    GetMenuString(menu, (uint)i, tampon, tampon.Capacity,
                                  MfParPosition);
                    brut = tampon.ToString();
                }

                // Une entrée dessinée par son propriétaire n'a pas de
                // texte à lire. On la garde quand même : la perdre
                // trahirait la promesse de ne rien perdre.
                if (brut.Length == 0) brut = "(élément Windows)";
            }

            // Le texte porte l'accélérateur en & et le raccourci après une
            // tabulation. Les deux se séparent ici, pas à l'affichage.
            string libelle = brut;
            string raccourci = "";

            int tab = libelle.IndexOf('\t');

            if (tab >= 0)
            {
                raccourci = libelle[(tab + 1)..];
                libelle = libelle[..tab];
            }

            libelle = libelle.Replace("&", "");

            entrees.Add(new EntreeShell(
                Id: (int)info.wID,
                Libelle: libelle,
                Raccourci: raccourci,
                Separateur: separateur,
                Actif: (info.fState & (MfsGrise | MfsInactif)) == 0,
                Coche: (info.fState & MfsCochee) != 0,
                SousMenu: info.hSubMenu,
                Position: i));
        }

        return entrees;
    }

    /// <summary>Exécute le verbe choisi. Le shell seul sait ce qu'il fait.</summary>
    internal bool Executer(EntreeShell entree, IntPtr poignee)
    {
        if (_libere || entree.Separateur || entree.Id <= 0) return false;

        try
        {
            var info = new INVOKECOMMANDINFO
            {
                Taille = Marshal.SizeOf<INVOKECOMMANDINFO>(),
                Fenetre = poignee,
                Verbe = (IntPtr)(entree.Id - (int)IdPremier),
                Affichage = AffichageNormal,
            };

            return _menu.InvokeCommand(ref info) >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_libere) return;
        _libere = true;

        if (_natif != IntPtr.Zero) DestroyMenu(_natif);

        Marshal.ReleaseComObject(_objet);

        if (_tableau is not null) Marshal.ReleaseComObject(_tableau);

        foreach (IntPtr pidl in _pidls) Marshal.FreeCoTaskMem(pidl);
        _pidls.Clear();
    }
}
