using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Opérations de fichiers par le shell, et non par <c>System.IO</c>.
///
/// Ce choix est délibéré. <c>File.Delete</c> efface sans retour ;
/// <c>SHFileOperation</c> envoie à la corbeille, affiche la progression de
/// Windows, résout les conflits de noms avec sa propre boîte de dialogue et
/// demande confirmation là où Windows la demande.
///
/// L'enjeu n'est pas la commodité. Kurogane exécute ici une décision de
/// l'utilisateur, pas une décision propre : il doit donc se comporter comme
/// l'Explorateur, ni plus prudent ni moins. Réécrire ces opérations nous
/// ferait diverger du système sur des détails que l'utilisateur connaît.
/// </summary>
internal static class FileOperationNative
{
    private const uint FO_MOVE   = 0x0001;
    private const uint FO_COPY   = 0x0002;
    private const uint FO_DELETE = 0x0003;
    private const uint FO_RENAME = 0x0004;

    /// <summary>Passer par la corbeille plutôt que d'effacer.</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>Renommer automatiquement en cas de collision à la copie.</summary>
    private const ushort FOF_RENAMEONCOLLISION = 0x0008;

    /// <summary>
    /// Sans ce drapeau, une suppression définitive de plusieurs éléments ne
    /// demande rien. Windows ne confirme de lui-même que la corbeille.
    /// </summary>
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Résultat d'une opération : ce que l'appelant doit savoir.</summary>
    internal sealed record Issue(bool Reussi, bool Annule, int Code)
    {
        /// <summary>
        /// Annulé n'est pas échoué. Un utilisateur qui ferme la boîte de
        /// confirmation a obtenu ce qu'il voulait : que rien ne se passe.
        /// </summary>
        internal bool Silencieux => Annule || Reussi;
    }

    /// <summary>
    /// Les chemins sont concaténés et terminés par un double zéro : c'est la
    /// convention de SHFileOperation, et l'oublier fait lire la mémoire
    /// au-delà du tampon.
    /// </summary>
    private static string Liste(IEnumerable<string> chemins) =>
        string.Join('\0', chemins) + "\0\0";

    private static Issue Executer(uint fonction, IEnumerable<string> source,
        string? destination, ushort drapeaux, IntPtr proprietaire)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd    = proprietaire,
            wFunc   = fonction,
            pFrom   = Liste(source),
            pTo     = destination is null ? null : destination + "\0\0",
            fFlags  = drapeaux,
        };

        int code = SHFileOperation(ref op);

        return new Issue(code == 0 && !op.fAnyOperationsAborted,
                         op.fAnyOperationsAborted, code);
    }

    /// <summary>Vers la corbeille. Windows demande confirmation lui-même.</summary>
    internal static Issue Corbeille(IEnumerable<string> chemins, IntPtr proprietaire) =>
        Executer(FO_DELETE, chemins, null, FOF_ALLOWUNDO, proprietaire);

    /// <summary>
    /// Suppression définitive. L'utilisateur qui la demande sait ce qu'il
    /// fait — c'est Maj+Suppr, la convention du système — mais Windows pose
    /// quand même sa question, et on la lui laisse poser.
    /// </summary>
    internal static Issue Effacer(IEnumerable<string> chemins, IntPtr proprietaire) =>
        Executer(FO_DELETE, chemins, null, FOF_WANTNUKEWARNING, proprietaire);

    internal static Issue Copier(IEnumerable<string> chemins, string dossier,
        IntPtr proprietaire) =>
        Executer(FO_COPY, chemins, dossier, FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION,
            proprietaire);

    internal static Issue Deplacer(IEnumerable<string> chemins, string dossier,
        IntPtr proprietaire) =>
        Executer(FO_MOVE, chemins, dossier, FOF_ALLOWUNDO, proprietaire);

    /// <summary>
    /// Renommer passe aussi par le shell : il prévient les autres
    /// applications du changement, ce qu'un File.Move ne fait pas.
    /// </summary>
    internal static Issue Renommer(string chemin, string nouveauChemin,
        IntPtr proprietaire) =>
        Executer(FO_RENAME, new[] { chemin }, nouveauChemin, FOF_ALLOWUNDO,
            proprietaire);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OPENASINFO info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    private const int OAIF_EXEC = 0x04;
    private const int OAIF_HIDE_REGISTRATION = 0x20;

    /// <summary>Boîte « Ouvrir avec » de Windows.</summary>
    internal static void OuvrirAvec(string chemin, IntPtr proprietaire)
    {
        var info = new OPENASINFO
        {
            pcszFile   = chemin,
            pcszClass  = null,
            oaifInFlags = OAIF_EXEC | OAIF_HIDE_REGISTRATION,
        };

        SHOpenWithDialog(proprietaire, ref info);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHObjectProperties(IntPtr hwnd, uint type,
        string objet, string? page);

    private const uint SHOP_FILEPATH = 0x00000002;

    /// <summary>Fiche de propriétés de Windows.</summary>
    internal static bool Proprietes(string chemin, IntPtr proprietaire) =>
        SHObjectProperties(proprietaire, SHOP_FILEPATH, chemin, null);
}
