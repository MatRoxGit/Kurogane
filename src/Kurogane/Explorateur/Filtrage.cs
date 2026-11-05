using System;

namespace Kurogane.Explorateur;

/// <summary>
/// Les règles du motif, en un seul endroit.
///
/// Le champ filtre la liste déjà chargée ; la recherche récursive parcourt
/// le disque. Deux chemins différents, mais ils doivent reconnaître
/// exactement le même motif : pendant une recherche, le champ continue de
/// restreindre ce qui remonte. Deux implémentations qui divergeraient
/// feraient disparaître des résultats sans que rien ne l'explique.
/// </summary>
internal static class Filtrage
{
    /// <summary>
    /// Deux formes reconnues : « *.ext » porte sur l'extension, tout le
    /// reste est cherché dans le nom. Jamais sensible à la casse.
    ///
    /// L'extension se compare par début et non en entier. On tape un motif
    /// caractère par caractère : exiger l'extension complète ferait
    /// afficher zéro résultat sur « *.d » et « *.dl » avant de tout faire
    /// apparaître sur « *.dll ». Un filtre qui ne dit rien tant qu'il n'est
    /// pas fini n'aide pas à chercher.
    /// </summary>
    internal static bool Correspond(string nom, string extension, string motif)
    {
        if (motif.Length == 0) return true;

        if (Extension(motif) is { } debut)
            return extension.StartsWith(debut, StringComparison.OrdinalIgnoreCase);

        return nom.Contains(motif, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Où se trouve la correspondance dans le nom, pour la montrer. Rend
    /// une longueur nulle quand il n'y a rien à souligner.
    ///
    /// Sans cela, un filtre sur « me » renvoie « aadWamExtension.dll » et
    /// l'utilisateur ne voit pas pourquoi — la correspondance est au milieu
    /// d'un nom tronqué.
    /// </summary>
    internal static (int Debut, int Longueur) Position(string nom, string motif)
    {
        if (motif.Length == 0) return (-1, 0);

        if (Extension(motif) is { } debut)
        {
            int point = nom.LastIndexOf('.');
            if (point < 0 || debut.Length == 0) return (-1, 0);

            int reste = nom.Length - point - 1;

            return (point + 1, Math.Min(debut.Length, reste));
        }

        int trouve = nom.IndexOf(motif, StringComparison.OrdinalIgnoreCase);

        return trouve < 0 ? (-1, 0) : (trouve, motif.Length);
    }

    /// <summary>
    /// La partie extension d'un motif « *.xxx », ou <c>null</c> si le motif
    /// n'est pas de cette forme.
    /// </summary>
    private static string? Extension(string motif) =>
        motif.StartsWith("*.", StringComparison.Ordinal) ? motif[2..] : null;
}
