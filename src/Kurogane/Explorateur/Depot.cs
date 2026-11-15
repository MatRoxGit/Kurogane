using System.IO;
using System.Windows;

namespace Kurogane.Explorateur;

/// <summary>
/// Les règles d'un dépôt de fichiers, en un seul endroit.
///
/// Le bureau et l'explorateur reçoivent les mêmes glissements et doivent
/// en tirer les mêmes conclusions. Deux implémentations divergeraient au
/// premier ajustement — c'est déjà arrivé trois fois sur ce projet, pour
/// les règles de filtrage, les gabarits de menu et le chargement des
/// icônes.
/// </summary>
internal static class Depot
{
    /// <summary>Les chemins publiés par le glissement, ou rien.</summary>
    internal static IReadOnlyList<string> Chemins(IDataObject? donnees)
    {
        if (donnees is null || !donnees.GetDataPresent(DataFormats.FileDrop))
            return Array.Empty<string>();

        return donnees.GetData(DataFormats.FileDrop) as string[]
            ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    /// <summary>
    /// Ce qu'un dépôt ferait, selon les touches et le volume d'origine.
    ///
    /// La règle de Windows, qui surprend quand on ne la connaît pas :
    /// déplacer si la source est sur le même volume, copier sinon. Maj et
    /// Ctrl la forcent dans un sens ou dans l'autre.
    /// </summary>
    internal static DragDropEffects Effet(DragEventArgs e, string? destination)
    {
        var chemins = Chemins(e.Data);

        if (chemins.Count == 0 || string.IsNullOrEmpty(destination))
            return DragDropEffects.None;

        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;

        return string.Equals(Path.GetPathRoot(chemins[0]),
                             Path.GetPathRoot(destination),
                             StringComparison.OrdinalIgnoreCase)
            ? DragDropEffects.Move
            : DragDropEffects.Copy;
    }

    /// <summary>
    /// Ce qu'il reste à transférer une fois écartés les cas qui ne sont
    /// pas des transferts.
    ///
    /// Un fichier déjà dans le dossier visé n'a pas à être recopié : le
    /// geste visait une place, pas un transfert. Et un dossier déposé sur
    /// lui-même n'irait nulle part — le shell refuserait, mais autant ne
    /// pas le lui demander.
    /// </summary>
    internal static List<string> Retenir(IReadOnlyList<string> chemins, string destination)
    {
        var retenus = new List<string>();

        foreach (string chemin in chemins)
        {
            if (string.Equals(chemin, destination, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(Path.GetDirectoryName(chemin), destination,
                              StringComparison.OrdinalIgnoreCase))
                continue;

            retenus.Add(chemin);
        }

        return retenus;
    }
}
