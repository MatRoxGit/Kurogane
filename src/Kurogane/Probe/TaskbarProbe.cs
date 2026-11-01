using Kurogane.Core;
using Microsoft.Win32;

namespace Kurogane.Probe;

/// <summary>
/// Inventaire de la barre des tâches et du menu Démarrer — LECTURE SEULE.
///
/// Avant d'écrire la moindre valeur, on relève ce qui existe réellement sur
/// cette version de Windows et ce que ça vaut. Une valeur absente n'est pas
/// une anomalie : beaucoup de réglages n'apparaissent qu'une fois que
/// l'utilisateur y a touché, et Windows se comporte alors selon un défaut
/// implicite. C'est précisément le cas que la restauration doit savoir
/// rendre — par une suppression, pas par l'écriture d'un zéro.
///
/// Aucune écriture, aucun --commit.
/// </summary>
internal static class TaskbarProbe
{
    private const string Avance    = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Recherche = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Ancrage   = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
    private const string Perso     = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dev       = @"Software\Microsoft\Windows\CurrentVersion\Explorer\TaskbarDeveloperSettings";
    private const string Flux      = @"Software\Microsoft\Windows\CurrentVersion\Feeds";

    private sealed record Cible(
        string Section, string SousCle, string Nom, string Libelle,
        Func<object, string>? Lire = null);

    private static string Oui(object v) => Convert.ToInt32(v) != 0 ? "activé" : "désactivé";

    private static readonly Cible[] Cibles =
    {
        // ---- Disposition
        new("Disposition", Avance, "TaskbarAl", "Alignement des icônes",
            v => Convert.ToInt32(v) == 0 ? "à gauche" : "centré"),
        new("Disposition", Avance, "TaskbarSi", "Taille de la barre",
            v => Convert.ToInt32(v) switch { 0 => "petite", 1 => "moyenne", 2 => "grande",
                                             var n => $"inconnue ({n})" }),
        new("Disposition", Ancrage, "Settings", "Ancrage (masquage auto, bord)"),

        // ---- Boutons et zones
        new("Boutons", Avance, "ShowTaskViewButton", "Bouton Vue des tâches", Oui),
        new("Boutons", Avance, "TaskbarDa", "Widgets", Oui),
        new("Boutons", Avance, "TaskbarMn", "Discussion / Teams", Oui),
        new("Boutons", Recherche, "SearchboxTaskbarMode", "Zone de recherche",
            v => Convert.ToInt32(v) switch { 0 => "masquée", 1 => "icône seule",
                                             2 => "champ", 3 => "champ + libellé",
                                             var n => $"inconnu ({n})" }),
        new("Boutons", Flux, "ShellFeedsTaskbarViewMode", "Actualités (hérité)"),

        // ---- Regroupement
        new("Regroupement", Avance, "TaskbarGlomLevel", "Combiner les boutons",
            v => Convert.ToInt32(v) switch { 0 => "toujours", 1 => "quand la barre est pleine",
                                             2 => "jamais", var n => $"inconnu ({n})" }),
        new("Regroupement", Avance, "MMTaskbarGlomLevel", "Combiner — écrans secondaires"),
        new("Regroupement", Avance, "MMTaskbarEnabled", "Barre sur les écrans secondaires", Oui),
        new("Regroupement", Avance, "MMTaskbarMode", "Contenu des écrans secondaires"),

        // ---- Apparence
        new("Apparence", Perso, "ColorPrevalence", "Accent sur barre et Démarrer", Oui),
        new("Apparence", Perso, "EnableTransparency", "Transparence", Oui),
        new("Apparence", Avance, "TaskbarAnimations", "Animations", Oui),
        new("Apparence", Avance, "ShowSecondsInSystemClock", "Secondes dans l'horloge", Oui),

        // ---- Menu Démarrer
        new("Menu Démarrer", Avance, "Start_Layout", "Disposition",
            v => Convert.ToInt32(v) switch { 0 => "par défaut", 1 => "plus d'épinglages",
                                             2 => "plus de recommandations",
                                             var n => $"inconnu ({n})" }),
        new("Menu Démarrer", Avance, "Start_TrackDocs", "Fichiers récents", Oui),
        new("Menu Démarrer", Avance, "Start_TrackProgs", "Applications récentes", Oui),
        new("Menu Démarrer", Avance, "Start_AccountNotifications", "Notifications de compte", Oui),

        // ---- Explorateur, tant qu'on y est
        new("Explorateur", Avance, "LaunchTo", "Ouvrir sur",
            v => Convert.ToInt32(v) switch { 1 => "Ce PC", 2 => "Accès rapide",
                                             3 => "Téléchargements", var n => $"inconnu ({n})" }),
        new("Explorateur", Avance, "HideFileExt", "Masquer les extensions", Oui),
        new("Explorateur", Avance, "Hidden", "Fichiers cachés",
            v => Convert.ToInt32(v) == 1 ? "affichés" : "masqués"),

        // ---- Développeur
        new("Développeur", Dev, "TaskbarEndTask", "« Terminer la tâche » au clic droit", Oui),
    };

    internal static int Executer(Journal journal)
    {
        journal.Step("Inventaire de la barre des tâches et du menu Démarrer");
        journal.Info("Lecture seule. Aucune valeur n'est écrite.");
        journal.Blank();

        string? section = null;
        int presentes = 0, absentes = 0;

        foreach (var c in Cibles)
        {
            if (c.Section != section)
            {
                if (section is not null) journal.Blank();
                journal.Step(c.Section);
                section = c.Section;
            }

            var (existe, type, brut) = Lire(c.SousCle, c.Nom);

            if (!existe)
            {
                journal.Warn($"{c.Libelle,-38} absente — Windows applique son défaut implicite");
                absentes++;
                continue;
            }

            presentes++;

            string rendu = brut switch
            {
                byte[] b => $"{b.Length} octets" + Ancrages(c.Nom, b),
                _        => c.Lire is not null ? $"{c.Lire(brut!)}  ({brut})" : $"{brut}",
            };

            journal.Ok($"{c.Libelle,-38} {rendu}   [{type}]");
        }

        journal.Blank();
        journal.Step("Bilan");
        journal.Info($"{presentes} valeurs présentes, {absentes} absentes.");
        journal.Info("Une valeur absente se restaure par une suppression : c'est le cas");
        journal.Info("que le socle gère déjà, et la raison pour laquelle on relève l'état");
        journal.Info("avant d'écrire plutôt que de présumer un défaut.");

        return 0;
    }

    /// <summary>
    /// Décodage des deux seuls octets exploitables de StuckRects3\Settings.
    /// Le reste du blob est une structure interne non documentée qu'on ne
    /// touchera pas : on se contente de la relire et de la réécrire entière.
    /// </summary>
    private static string Ancrages(string nom, byte[] b)
    {
        if (nom != "Settings" || b.Length < 13) return "";

        string masquage = (b[8] & 0x01) != 0 ? "masquage auto" : "toujours visible";
        string bord = b[12] switch
        {
            0 => "à gauche", 1 => "en haut", 2 => "à droite", 3 => "en bas",
            var n => $"bord inconnu ({n})",
        };

        return $" — {masquage}, {bord}";
    }

    private static (bool Existe, string Type, object? Valeur) Lire(string sousCle, string nom)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(sousCle, writable: false);
            if (k is null) return (false, "", null);

            var v = k.GetValue(nom, null);
            if (v is null) return (false, "", null);

            return (true, k.GetValueKind(nom).ToString(), v);
        }
        catch (Exception)
        {
            return (false, "", null);
        }
    }
}
