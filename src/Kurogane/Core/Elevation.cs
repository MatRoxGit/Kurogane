using System.Security;
using Microsoft.Win32;

namespace Kurogane.Core;

/// <summary>
/// Detection des droits d'ecriture dans HKLM.
///
/// On teste la permission reelle en ouvrant une cle qui existe toujours,
/// plutot que de controler l'appartenance au groupe Administrateurs : c'est
/// la permission qui compte, pas le groupe.
/// </summary>
public static class Elevation
{
    private static bool? _cache;

    public static bool CanWriteHklm()
    {
        if (_cache is bool v) return v;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var k = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", writable: true);
            _cache = k is not null;
        }
        catch (UnauthorizedAccessException) { _cache = false; }
        catch (SecurityException)           { _cache = false; }

        return _cache.Value;
    }
}
