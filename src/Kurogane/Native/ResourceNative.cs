using System.IO;
using System.Runtime.InteropServices;

namespace Kurogane.Native;

/// <summary>
/// Lecture des ressources d'un module PE, en lecture seule.
///
/// Le module est chargé avec LOAD_LIBRARY_AS_DATAFILE, c'est-à-dire mappé en
/// mémoire comme un simple fichier de données : aucun code du module n'est
/// exécuté, aucune dépendance n'est résolue, aucun point d'entrée n'est
/// appelé. C'est le mode prévu par Microsoft pour inspecter les ressources
/// d'un binaire sans le charger réellement.
///
/// Rien ici n'écrit quoi que ce soit. L'écriture de ressources passerait par
/// BeginUpdateResource, volontairement absent de ce fichier.
/// </summary>
internal static class ResourceNative
{
    internal const int RT_ICON       = 3;
    internal const int RT_GROUP_ICON = 14;

    private const uint LOAD_LIBRARY_AS_DATAFILE       = 0x00000002;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

    internal delegate bool EnumResNameProc(IntPtr hModule, IntPtr type, IntPtr name, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "LoadLibraryExW")]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "EnumResourceNamesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(IntPtr hModule, IntPtr type,
        EnumResNameProc callback, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "FindResourceW")]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "FindResourceExW")]
    private static extern IntPtr FindResourceEx(IntPtr hModule, IntPtr type, IntPtr name,
        ushort language);

    internal delegate bool EnumResLangProc(IntPtr hModule, IntPtr type, IntPtr name,
        ushort language, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "EnumResourceLanguagesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceLanguages(IntPtr hModule, IntPtr type, IntPtr name,
        EnumResLangProc callback, IntPtr param);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    /// <summary>
    /// Un module ouvert en lecture seule. À libérer par Dispose.
    /// </summary>
    internal sealed class Module : IDisposable
    {
        private IntPtr _handle;

        internal string Path { get; }

        private Module(IntPtr handle, string path)
        {
            _handle = handle;
            Path    = path;
        }

        /// <summary>
        /// Ouvre un module en données seules. Renvoie null si le fichier est
        /// absent ou illisible — ce qui est une réponse, pas une erreur : les
        /// fichiers .mun n'existent que depuis Windows 10 1903.
        /// </summary>
        internal static Module? Open(string path)
        {
            if (!File.Exists(path)) return null;

            var h = LoadLibraryEx(path, IntPtr.Zero,
                LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);

            return h == IntPtr.Zero ? null : new Module(h, path);
        }

        /// <summary>Identifiants des groupes d'icônes du module.</summary>
        internal List<int> GroupIconIds() => Ids(RT_GROUP_ICON);

        /// <summary>
        /// Identifiants des ressources d'un type donné. Un nom de ressource
        /// peut être un entier ou une chaîne ; on ne retient que les entiers,
        /// seule forme utilisée par les binaires du shell.
        /// </summary>
        internal List<int> Ids(int type)
        {
            var ids = new List<int>();

            bool Collect(IntPtr _, IntPtr __, IntPtr name, IntPtr ___)
            {
                // IS_INTRESOURCE : les 16 bits de poids fort sont nuls.
                if ((ulong)name >> 16 == 0) ids.Add((int)name);
                return true;
            }

            // Le délégué doit rester joignable pendant tout l'appel : sans
            // référence vivante, le ramasse-miettes peut le libérer alors que
            // le code natif détient encore son pointeur. Panne rare, non
            // déterministe, et très pénible à diagnostiquer.
            EnumResNameProc rappel = Collect;

            // Un module sans aucune ressource de ce type fait échouer l'appel
            // avec ERROR_RESOURCE_TYPE_NOT_FOUND : ce n'est pas une anomalie.
            EnumResourceNames(_handle, new IntPtr(type), rappel, IntPtr.Zero);
            GC.KeepAlive(rappel);

            ids.Sort();
            return ids;
        }

        /// <summary>
        /// Langues dans lesquelles une ressource existe. Une ressource écrite
        /// dans la mauvaise langue vient s'ajouter à l'originale au lieu de la
        /// remplacer : le fichier grossit et l'affichage ne change pas.
        /// </summary>
        internal List<ushort> Langues(int type, int id)
        {
            var langues = new List<ushort>();

            bool Collect(IntPtr _, IntPtr __, IntPtr ___, ushort langue, IntPtr ____)
            {
                langues.Add(langue);
                return true;
            }

            EnumResLangProc rappel = Collect;
            EnumResourceLanguages(_handle, new IntPtr(type), new IntPtr(id), rappel, IntPtr.Zero);
            GC.KeepAlive(rappel);

            return langues;
        }

        /// <summary>Octets bruts d'une ressource dans une langue précise.</summary>
        internal byte[]? Read(int type, int id, ushort langue)
        {
            var info = FindResourceEx(_handle, new IntPtr(type), new IntPtr(id), langue);
            return info == IntPtr.Zero ? null : Extraire(info);
        }

        /// <summary>Octets bruts d'une ressource, ou null si elle est absente.</summary>
        internal byte[]? Read(int type, int id)
        {
            var info = FindResource(_handle, new IntPtr(id), new IntPtr(type));
            return info == IntPtr.Zero ? null : Extraire(info);
        }

        private byte[]? Extraire(IntPtr info)
        {
            uint taille = SizeofResource(_handle, info);
            if (taille == 0) return null;

            var data = LoadResource(_handle, info);
            if (data == IntPtr.Zero) return null;

            var ptr = LockResource(data);
            if (ptr == IntPtr.Zero) return null;

            var bytes = new byte[taille];
            Marshal.Copy(ptr, bytes, 0, (int)taille);
            return bytes;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            FreeLibrary(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
