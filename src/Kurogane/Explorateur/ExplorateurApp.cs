using System.Windows;
using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;

namespace Kurogane.Explorateur;

/// <summary>
/// Lance l'explorateur Kurogane.
///
/// Rien n'est écrit, rien n'est modifié dans le système : c'est une fenêtre,
/// pas un réglage. Le remplacement du gestionnaire de dossiers par défaut —
/// vérifié faisable et réversible le 2 novembre — viendra plus tard, comme
/// module de thème soumis au socle.
/// </summary>
public static class ExplorateurApp
{
    public static int Executer(Journal journal, ThemeDefinition? theme)
    {
        journal.Step("Explorateur Kurogane");

        if (theme is not null)
            journal.Info($"thème « {theme.Name} » v{theme.Version}");
        else
            journal.Warn("aucun thème fourni : les cartes seront sans icônes");

        journal.Blank();
        journal.Info("Étape 1 — coquille : le contenu est figé, rien ne navigue.");
        journal.Blank();

        try
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose,
            };

            var fenetre = new FenetreExplorateur(theme);

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
}
