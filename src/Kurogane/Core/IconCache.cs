using System.Diagnostics;

namespace Kurogane.Core;

/// <summary>
/// Purge du cache d'icones du shell.
///
/// Pourquoi cette commande existe : le registre et les desktop.ini stockent un
/// CHEMIN, pas un contenu. Remplacer un fichier .ico sans changer son nom ne
/// modifie aucune valeur — « apply » n'a donc rien a faire et le repond
/// honnetement, mais Windows continue d'afficher l'ancienne image depuis son
/// cache. C'est le cas typique quand on itere sur les icones.
///
/// Aucune empreinte de fichier ne reglerait le probleme : au moment ou apply
/// s'execute, le fichier sur le disque est deja le nouveau. Ce que le shell a
/// en memoire n'est pas observable depuis le registre.
/// </summary>
public static class IconCache
{
    /// <summary>
    /// Demande au shell de reconstruire son cache d'icones. Methode documentee
    /// par Microsoft, sans arret d'explorer.
    /// </summary>
    public static bool Rafraichir(Journal journal)
    {
        Native.NativeMethods.RefreshShellIcons();

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName        = "ie4uinit.exe",
                Arguments       = "-show",
                UseShellExecute = false,
                CreateNoWindow  = true,
            });

            p?.WaitForExit(10_000);
            journal.Ok("Cache d'icônes reconstruit (ie4uinit -show)");
            return true;
        }
        catch (Exception ex)
        {
            journal.Warn($"ie4uinit indisponible : {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Purge complete : arret d'explorer, suppression des bases de cache,
    /// redemarrage. Plus efficace, mais ferme toutes les fenetres de
    /// l'Explorateur — d'ou le fait que ce ne soit jamais automatique.
    /// </summary>
    public static void PurgerEtRedemarrer(Journal journal)
    {
        journal.Step("Purge complète du cache d'icônes");

        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); p.WaitForExit(5_000); } catch { /* deja arrete */ }
        }

        string dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Explorer");

        int supprimes = 0;
        if (Directory.Exists(dossier))
        {
            foreach (var f in Directory.GetFiles(dossier, "iconcache*.db"))
            {
                try { File.Delete(f); supprimes++; }
                catch (IOException) { /* verrouille, sera reconstruit malgre tout */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        journal.Ok($"{supprimes} base(s) de cache supprimée(s)");

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true });
            journal.Ok("Explorateur redémarré");
        }
        catch (Exception ex)
        {
            journal.Error($"Redémarrage d'explorer impossible : {ex.Message}");
            journal.Info("Relance-le à la main : start explorer");
        }
    }
}
