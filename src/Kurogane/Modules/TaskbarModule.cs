using Kurogane.Core;
using Kurogane.Native;
using Kurogane.Theme;
using Microsoft.Win32;

namespace Kurogane.Modules;

/// <summary>
/// Barre des tâches et menu Démarrer. Niveau 1 : tout se passe dans HKCU,
/// aucune élévation.
///
/// Deux points méritent d'être connus avant de lire le code.
///
/// Les deux tiers de ces valeurs n'existent pas sur une installation neuve —
/// Windows applique alors un défaut implicite, et la valeur n'apparaît qu'une
/// fois que l'utilisateur y a touché. Le socle sait restaurer ce cas, par une
/// suppression et non par l'écriture d'un zéro. C'est tout l'intérêt d'avoir
/// relevé l'état avant d'écrire plutôt que de présumer.
///
/// Le masquage automatique n'est pas une valeur à lui seul : c'est un bit
/// dans un blob binaire de 48 octets, StuckRects3\Settings, dont le reste est
/// une structure interne non documentée. On lit le blob, on bascule ce seul
/// bit, et on réécrit l'ensemble tel quel. La restauration remet les 48
/// octets d'origine à l'identique.
/// </summary>
public sealed class TaskbarModule : IThemeModule
{
    private const string Avance    = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Recherche = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string Ancrage   = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";

    public string Name => "taskbar";
    public int Level => 1;

    public bool AppliesTo(ThemeDefinition theme) =>
        theme.Taskbar is not null || theme.StartMenu is not null;

    public IEnumerable<IChange> Plan(ThemeDefinition theme)
    {
        if (theme.Taskbar is { } b)
        {
            if (b.Alignment is { } a)
                yield return Mot(Avance, "TaskbarAl", a, "Alignement",
                    ("left", 0, "icônes à gauche"), ("center", 1, "icônes centrées"));

            if (b.Size is { } s)
                yield return Mot(Avance, "TaskbarSi", s, "Taille de la barre",
                    ("small", 0, "barre petite"), ("medium", 1, "barre moyenne"),
                    ("large", 2, "barre grande"));

            if (b.Search is { } r)
                yield return Mot(Recherche, "SearchboxTaskbarMode", r, "Zone de recherche",
                    ("hidden", 0, "recherche masquée"), ("icon", 1, "recherche en icône"),
                    ("box", 2, "recherche en champ"), ("boxLabel", 3, "recherche en champ avec libellé"));

            if (b.CombineButtons is { } c)
                yield return Mot(Avance, "TaskbarGlomLevel", c, "Regroupement des boutons",
                    ("always", 0, "boutons toujours combinés"),
                    ("whenFull", 1, "boutons combinés quand la barre est pleine"),
                    ("never", 2, "boutons jamais combinés"));

            // TaskbarDa est intercepté par le User Choice Protection Driver
            // (ucpd.sys, introduit par Microsoft en 2024) : l'écriture échoue
            // avec UnauthorizedAccessException, y compris depuis un terminal
            // administrateur, et y compris dans HKCU. La stratégie de
            // remplacement habituelle — HKLM\SOFTWARE\Policies\Microsoft\Dsh
            // AllowNewsAndInterests — est rapportée comme bloquée elle aussi.
            //
            // Le réglage reste accepté dans un thème : une installation sans
            // UCPD l'appliquera. Mais il n'est PAS livré dans le thème par
            // défaut, parce qu'on ignore si le retour en arrière passerait —
            // et proposer un aller sans retour contredirait la garantie
            // centrale du projet.
            if (b.Widgets is { } w)
                yield return Bit(Avance, "TaskbarDa", w, "Widgets");

            if (b.Chat is { } t)
                yield return Bit(Avance, "TaskbarMn", t, "Bouton Discussion");

            if (b.TaskView is { } v)
                yield return Bit(Avance, "ShowTaskViewButton", v, "Bouton Vue des tâches");

            if (b.SecondsInClock is { } sec)
                yield return Bit(Avance, "ShowSecondsInSystemClock", sec, "Secondes dans l'horloge");

            if (b.Animations is { } an)
                yield return Bit(Avance, "TaskbarAnimations", an, "Animations de la barre");

            if (b.AutoHide is { } masquage)
            {
                var change = Masquage(masquage);
                if (change is not null) yield return change;
            }
        }

        if (theme.StartMenu is { } d)
        {
            if (d.Layout is { } l)
                yield return Mot(Avance, "Start_Layout", l, "Disposition du menu Démarrer",
                    ("default", 0, "disposition par défaut"),
                    ("morePins", 1, "plus d'épinglages"),
                    ("moreRecommendations", 2, "plus de recommandations"));

            if (d.RecentFiles is { } f)
                yield return Bit(Avance, "Start_TrackDocs", f, "Fichiers récents");

            if (d.RecentApps is { } a)
                yield return Bit(Avance, "Start_TrackProgs", a, "Applications récentes");

            if (d.AccountNotifications is { } n)
                yield return Bit(Avance, "Start_AccountNotifications", n, "Notifications de compte");
        }
    }

    public void PostApply(ThemeDefinition theme, Journal journal)
    {
        NativeMethods.BroadcastThemeChange();

        // L'alignement, la taille et la zone de recherche ne sont relus qu'au
        // démarrage du shell. Une notification ne suffit pas.
        Shell.RedemarrerExplorateur(journal);
    }

    // ------------------------------------------------------------------

    private RegistryChange Bit(string cle, string nom, bool actif, string libelle) =>
        new()
        {
            Module = Name, Hive = RegistryHive.CurrentUser, SubKey = cle,
            Name = nom, Kind = RegistryValueKind.DWord,
            NewValue = actif ? 1 : 0,
            Description = $"{libelle} : {(actif ? "activé" : "désactivé")}",
        };

    /// <summary>
    /// Traduit un mot du fichier de thème en valeur numérique. Un mot inconnu
    /// lève une exception nommant les valeurs acceptées, plutôt que de
    /// retomber silencieusement sur un défaut.
    /// </summary>
    private RegistryChange Mot(string cle, string nom, string valeur, string libelle,
                               params (string Mot, int Valeur, string Texte)[] choix)
    {
        foreach (var (mot, v, texte) in choix)
            if (valeur.Equals(mot, StringComparison.OrdinalIgnoreCase))
                return new RegistryChange
                {
                    Module = Name, Hive = RegistryHive.CurrentUser, SubKey = cle,
                    Name = nom, Kind = RegistryValueKind.DWord,
                    NewValue = v, Description = texte,
                };

        throw new FormatException(
            $"{libelle} : « {valeur} » n'est pas reconnu. "
          + $"Valeurs acceptées : {string.Join(", ", choix.Select(c => c.Mot))}.");
    }

    /// <summary>
    /// Bascule le bit de masquage automatique dans StuckRects3\Settings.
    /// Renvoie null si le blob est absent ou trop court : on ne fabrique pas
    /// une structure dont on ignore le format.
    /// </summary>
    private RegistryChange? Masquage(bool actif)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Ancrage, writable: false);
        if (k?.GetValue("Settings") is not byte[] actuel || actuel.Length < 9) return null;

        var modifie = (byte[])actuel.Clone();
        modifie[8] = actif ? (byte)(modifie[8] | 0x01) : (byte)(modifie[8] & ~0x01);

        return new RegistryChange
        {
            Module = Name, Hive = RegistryHive.CurrentUser, SubKey = Ancrage,
            Name = "Settings", Kind = RegistryValueKind.Binary,
            NewValue = modifie,
            Description = actif ? "Masquage automatique activé" : "Barre toujours visible",
        };
    }
}
