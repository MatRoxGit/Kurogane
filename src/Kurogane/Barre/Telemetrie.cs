using System.Runtime.InteropServices;

namespace Kurogane.Barre;

/// <summary>
/// Mesures système, lues par Win32 et rien d'autre.
///
/// Pourquoi pas la température : sous Windows elle exige soit un pilote
/// noyau, soit une bibliothèque tierce — donc un paquet NuGet, que ce
/// projet s'interdit. La classe WMI des zones thermiques ACPI demanderait
/// System.Management, également un paquet, et une machine virtuelle
/// n'expose de toute façon aucun capteur thermique.
///
/// Ce qui est mesuré ici est réel, natif, et disponible partout : la charge
/// du processeur, l'occupation mémoire et le temps de fonctionnement.
/// </summary>
internal sealed class Telemetrie
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Bas, Haut; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME inactif, out FILETIME noyau,
                                              out FILETIME utilisateur);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  Taille;
        public uint  PourcentageUtilise;
        public ulong PhysiqueTotal, PhysiqueLibre;
        public ulong PageTotal, PageLibre;
        public ulong VirtuelTotal, VirtuelLibre, VirtuelEtendu;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX etat);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    private static ulong Combiner(FILETIME t) => ((ulong)t.Haut << 32) | t.Bas;

    private ulong _inactifPrecedent, _occupePrecedent;

    /// <summary>
    /// Charge du processeur, de 0 à 1.
    ///
    /// GetSystemTimes rend des compteurs cumulés depuis le démarrage : la
    /// charge instantanée est la variation entre deux appels. Le premier
    /// appel n'a donc rien à comparer et rend zéro — c'est normal, pas une
    /// panne.
    /// </summary>
    internal double Processeur()
    {
        if (!GetSystemTimes(out var inactif, out var noyau, out var utilisateur)) return 0;

        ulong i = Combiner(inactif);
        ulong total = Combiner(noyau) + Combiner(utilisateur);
        ulong occupe = total - i;   // le temps noyau inclut déjà l'inactif

        ulong deltaOccupe  = occupe - _occupePrecedent;
        ulong deltaInactif = i - _inactifPrecedent;
        ulong deltaTotal   = deltaOccupe + deltaInactif;

        _occupePrecedent  = occupe;
        _inactifPrecedent = i;

        return deltaTotal == 0 ? 0 : Math.Clamp((double)deltaOccupe / deltaTotal, 0, 1);
    }

    /// <summary>Occupation mémoire, de 0 à 1, et le détail en gigaoctets.</summary>
    internal (double Part, double UtiliseGo, double TotalGo) Memoire()
    {
        var etat = new MEMORYSTATUSEX { Taille = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref etat)) return (0, 0, 0);

        double go = 1024.0 * 1024.0 * 1024.0;
        double total = etat.PhysiqueTotal / go;
        double utilise = (etat.PhysiqueTotal - etat.PhysiqueLibre) / go;

        return (Math.Clamp(etat.PourcentageUtilise / 100.0, 0, 1), utilise, total);
    }

    /// <summary>Temps écoulé depuis le démarrage de Windows.</summary>
    internal static TimeSpan Fonctionnement() =>
        TimeSpan.FromMilliseconds(GetTickCount64());
}
