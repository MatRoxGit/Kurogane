using System.Diagnostics;

namespace Kurogane.Core;

/// <summary>
/// Redémarrage de l'Explorateur.
///
/// Plusieurs réglages de la barre des tâches ne sont relus qu'au démarrage
/// du shell : l'alignement, la taille, la zone de recherche. Une
/// notification ne suffit pas, il faut relancer le processus.
///
/// Effet de bord assumé : toutes les fenêtres de l'Explorateur se ferment.
/// C'est pourquoi l'opération est annoncée dans le journal avant d'avoir
/// lieu, et jamais déclenchée en simulation.
/// </summary>
public static class Shell
{
    public static void RedemarrerExplorateur(Journal journal)
    {
        journal.Info("Redémarrage de l'Explorateur — les fenêtres ouvertes vont se fermer.");

        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); p.WaitForExit(5_000); } catch { /* déjà arrêté */ }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", UseShellExecute = true });
            journal.Ok("Explorateur redémarré");
        }
        catch (Exception ex)
        {
            journal.Error($"Redémarrage impossible : {ex.Message}");
            journal.Info("Relance-le à la main : start explorer");
        }
    }
}
