using System.IO;

namespace Kurogane.Explorateur;

/// <summary>Une entrée listable : fichier ou dossier.</summary>
public sealed record Entree(
    string Nom,
    string Chemin,
    bool EstDossier,
    long Taille,
    DateTime Modifie)
{
    /// <summary>Extension en minuscules, sans le point. Vide pour un dossier.</summary>
    public string Extension => EstDossier
        ? ""
        : Path.GetExtension(Nom).TrimStart('.').ToLowerInvariant();
}

/// <summary>Un point d'entrée du volet gauche.</summary>
public sealed record Racine(string Libelle, string Detail, string Chemin);

/// <summary>
/// Résultat d'un listage. Les erreurs sont comptées, pas levées : un dossier
/// dont trois sous-dossiers sont inaccessibles doit s'afficher avec le reste,
/// et le compteur le dire. Refuser d'afficher quoi que ce soit parce qu'une
/// entrée résiste serait pire que le silence.
/// </summary>
public sealed record Listage(IReadOnlyList<Entree> Entrees, int Erreurs, long Millisecondes);

/// <summary>
/// D'où viennent les entrées affichées.
///
/// Cette interface existe pour une raison précise : l'explorateur vise à
/// terme le remplacement de celui de Windows, ce qui imposera l'espace de
/// noms du shell en COM — « Ce PC », « Réseau », les bibliothèques ne sont
/// pas des chemins de fichiers. Commencer par là ralentirait tout ; changer
/// l'écran après coup coûterait une réécriture. L'interface absorbe le
/// changement : une implémentation système de fichiers aujourd'hui, une
/// implémentation shell ensuite, le même écran.
/// </summary>
public interface ISourceNoeuds
{
    IReadOnlyList<Racine> Racines();

    Task<Listage> Lister(string chemin, CancellationToken jeton);

    string? Parent(string chemin);

    /// <summary>Le chemin découpé pour le fil d'Ariane.</summary>
    IReadOnlyList<string> Segments(string chemin);
}
