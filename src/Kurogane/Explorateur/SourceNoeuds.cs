using System.IO;

namespace Kurogane.Explorateur;

/// <summary>Une entrée listable : fichier ou dossier.</summary>
public sealed record Entree(
    string Nom,
    string Chemin,
    bool EstDossier,
    long Taille,
    DateTime Modifie,
    bool Cachee = false)
{
    /// <summary>Extension en minuscules, sans le point. Vide pour un dossier.</summary>
    public string Extension => EstDossier
        ? ""
        : Path.GetExtension(Nom).TrimStart('.').ToLowerInvariant();
}

/// <summary>
/// Un point d'entrée du volet gauche. L'icône est portée par la source et
/// non devinée par l'écran : c'est elle qui sait qu'un lecteur est amovible
/// ou optique.
/// </summary>
public sealed record Racine(string Libelle, string Detail, string Chemin, string Icone);

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
/// <summary>
/// Ce qu'on accepte de voir. Deux classes indépendantes, chacune avec sa
/// bascule : les entrées <b>cachées</b> et les entrées <b>système</b>.
/// Une entrée qui porte les deux attributs demande les deux bascules.
///
/// La première version liait les deux — « cachées » d'un côté, « cachées
/// ET système » de l'autre, comme Windows. Elle avait un trou : un dossier
/// marqué Système sans être Caché, ce qu'est à peu près tout System32,
/// n'était filtré par aucune des deux. Les bascules ne changeaient alors
/// rien de visible, ce qui est pire qu'une absence de réglage.
///
/// Cette règle s'écarte de l'Explorateur, qui montre toujours les fichiers
/// système non cachés. Elle a pour elle de faire exactement ce que ses
/// étiquettes annoncent.
/// </summary>
public readonly record struct Visibilite(bool Caches, bool Systeme)
{
    /// <summary>
    /// Tout est montré. Les bascules retranchent ; elles n'ajoutent pas.
    /// Partir de la vue complète évite qu'un dossier paraisse vide sans
    /// qu'on sache pourquoi.
    /// </summary>
    public static Visibilite Defaut => new(true, true);

    /// <summary>Vrai si une entrée portant ces attributs doit être écartée.</summary>
    public bool Ecarter(bool cachee, bool systeme) =>
        (cachee && !Caches) || (systeme && !Systeme);
}

public interface ISourceNoeuds
{
    IReadOnlyList<Racine> Racines();

    Task<Listage> Lister(string chemin, Visibilite visibilite, CancellationToken jeton);

    /// <summary>
    /// Les sous-dossiers seulement. L'arbre n'affiche pas les fichiers :
    /// les lister pour les écarter ensuite ferait payer un dossier chargé
    /// sans rien montrer de plus.
    /// </summary>
    Task<IReadOnlyList<Entree>> Dossiers(string chemin, Visibilite visibilite,
                                        CancellationToken jeton);

    string? Parent(string chemin);

    /// <summary>
    /// Le chemin découpé pour le fil d'Ariane, chaque segment accompagné du
    /// chemin complet jusqu'à lui — sans quoi un segment ne serait qu'un
    /// libellé, et cliquer dessus n'aurait nulle part où aller.
    /// </summary>
    IReadOnlyList<(string Libelle, string Chemin)> Segments(string chemin);
}
