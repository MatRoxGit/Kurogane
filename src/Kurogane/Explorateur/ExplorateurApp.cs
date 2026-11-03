using System.IO;
using System.Windows;
using Kurogane.Core;
using Kurogane.Theme;

namespace Kurogane.Explorateur;

/// <summary>
/// Lance l'explorateur Kurogane.
///
/// Rien n'est écrit et rien n'est modifié dans le système : c'est une
/// fenêtre, pas un réglage. Le remplacement du gestionnaire de dossiers par
/// défaut — vérifié faisable et réversible le 2 novembre — viendra plus
/// tard, comme module de thème soumis au socle.
/// </summary>
public static class ExplorateurApp
{
    public static int Executer(Journal journal, ThemeDefinition? theme, string? depart)
    {
        journal.Step("Explorateur Kurogane");

        if (theme is not null)
            journal.Info($"thème « {theme.Name} » v{theme.Version}");
        else
            journal.Warn("aucun thème fourni : les cartes seront sans icônes");

        var source = new SourceFichiers();

        string chemin = Resoudre(journal, depart);
        journal.Info($"départ : {chemin}");

        journal.Blank();
        journal.Info("Lecture seule : rien n'est créé, renommé ni supprimé.");
        journal.Blank();

        try
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose,
            };

            var fenetre = new FenetreExplorateur(theme, source, chemin);

            journal.Ok("Fenêtre ouverte. Ferme-la pour rendre la main.");
            journal.Blank();

            application.Run(fenetre);
        }
        catch (Exception ex)
        {
            journal.Error($"{ex.GetType().Name} : {ex.Message}");
            return 1;
        }

        journal.Step("Arrêt");
        journal.Ok("Rien n'a été écrit : il n'y a aucun état à restaurer.");

        return 0;
    }

    /// <summary>
    /// Un chemin absent ou introuvable ramène au profil plutôt que de faire
    /// échouer le démarrage : une fenêtre ouverte sur le mauvais dossier se
    /// corrige d'un clic, une fenêtre qui refuse de s'ouvrir ne se corrige
    /// pas du tout.
    /// </summary>
    private static string Resoudre(Journal journal, string? demande)
    {
        string profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (demande is null) return profil;

        try
        {
            string complet = Path.GetFullPath(demande);
            if (Directory.Exists(complet)) return complet;

            journal.Warn($"dossier introuvable : {complet}");
        }
        catch (Exception ex)
        {
            journal.Warn($"chemin invalide : {ex.Message}");
        }

        journal.Info("repli sur le dossier du profil.");
        return profil;
    }
}
