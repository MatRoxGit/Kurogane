using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Kurogane.Core;

/// <summary>
/// Activation des privileges de jeton necessaires a la prise de possession.
///
/// Un administrateur POSSEDE ces privileges mais ils sont desactives par
/// defaut dans son jeton : il faut les activer explicitement. C'est une
/// protection, pas un obstacle — et c'est la raison pour laquelle un simple
/// SetAccessControl echoue meme en session elevee.
///
/// SeTakeOwnership permet de s'attribuer la propriete d'une cle.
/// SeRestore permet de la rendre a son titulaire d'origine — sans lui, la
/// restauration est impossible. C'est pourquoi on exige les DEUX avant
/// d'ecrire quoi que ce soit : prendre sans pouvoir rendre serait une
/// modification irreversible.
/// </summary>
public static class Privileges
{
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string Restore       = "SeRestorePrivilege";
    public const string Backup        = "SeBackupPrivilege";

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED    = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "LookupPrivilegeValueW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systeme, string nom, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool desactiverTout,
        ref TOKEN_PRIVILEGES nouvelEtat, uint longueur, IntPtr ancienEtat, IntPtr longueurRetour);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>
    /// Active un privilege dans le jeton du processus. Retourne false si le
    /// compte ne le detient pas, sans lever d'exception.
    /// </summary>
    public static bool Enable(string nom)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(),
                                  TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;

            if (!LookupPrivilegeValue(null, nom, out LUID luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED },
            };

            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges reussit meme quand il n'a rien active :
            // seul GetLastError dit la verite.
            const int ERROR_NOT_ALL_ASSIGNED = 1300;
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// Active les privileges indispensables a une prise de possession
    /// reversible. Echoue si la restitution ne serait pas possible.
    /// </summary>
    public static void EnablePourPriseDePossession()
    {
        if (!Enable(Restore))
            throw new InvalidOperationException(
                $"Privilege {Restore} indisponible : sans lui la propriete ne pourrait pas "
              + "etre rendue a TrustedInstaller. La prise de possession est refusee.");

        if (!Enable(TakeOwnership))
            throw new InvalidOperationException(
                $"Privilege {TakeOwnership} indisponible. Session administrateur requise.");

        Enable(Backup);
    }
}
