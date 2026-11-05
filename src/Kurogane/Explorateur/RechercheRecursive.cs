using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Kurogane.Explorateur;

/// <summary>
/// Parcours récursif d'une arborescence, rendu par lots.
///
/// Rendre la main régulièrement n'est pas un raffinement : une recherche
/// dans <c>C:\Windows</c> dure des dizaines de secondes, et un explorateur
/// qui se fige pendant ce temps est cassé. Les résultats remontent donc au
/// fil de l'eau, et le parcours s'arrête net sur demande.
///
/// Le découpage se fait au temps, pas au nombre de dossiers : un dossier
/// de quatre entrées et un dossier de quatre mille ne coûtent pas la même
/// chose, et compter les dossiers donnerait des battements très inégaux.
/// </summary>
internal static class RechercheRecursive
{
    /// <summary>Un battement de recherche.</summary>
    internal sealed record Lot(
        IReadOnlyList<Entree> Trouves,
        int Dossiers,
        bool Sature);

    private static readonly TimeSpan Battement = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Ce qui survit d'un battement au suivant. Une classe plutôt que des
    /// paramètres par référence : une expression lambda ne peut pas capturer
    /// un <c>ref</c>.
    /// </summary>
    private sealed class Etat
    {
        public readonly Stack<string> Pile = new();
        public int Dossiers;
    }

    internal static async IAsyncEnumerable<Lot> Courir(
        string depart, string motif, int maximum, Visibilite visibilite,
        [EnumeratorCancellation] CancellationToken jeton)
    {
        var etat = new Etat();
        etat.Pile.Push(depart);

        int total = 0;

        while (etat.Pile.Count > 0 && total < maximum
               && !jeton.IsCancellationRequested)
        {
            List<Entree> trouves;

            // Le yield ne peut pas vivre dans un try doté d'un catch :
            // l'attente est isolée ici, la remontée se fait après.
            try
            {
                trouves = await Task.Run(
                    () => Balayer(etat, motif, maximum - total, visibilite, jeton),
                    jeton);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            total += trouves.Count;

            yield return new Lot(trouves, etat.Dossiers, total >= maximum);
        }
    }

    private static List<Entree> Balayer(Etat etat, string motif, int reste,
                                        Visibilite visibilite, CancellationToken jeton)
    {
        var trouves = new List<Entree>();
        var horloge = Stopwatch.StartNew();

        // Seuls les points d'analyse sont écartés par l'énumération, sans
        // quoi une jonction renverrait le parcours sur ses pas. Cachés et
        // système sont filtrés plus bas, à la main : AttributesToSkip
        // écarte dès qu'UN attribut correspond, ce qui masquerait les
        // fichiers système non cachés que le listage, lui, montre. Deux
        // règles divergentes seraient pires qu'une règle imparfaite.
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        while (etat.Pile.Count > 0 && trouves.Count < reste
               && horloge.Elapsed < Battement
               && !jeton.IsCancellationRequested)
        {
            string dossier = etat.Pile.Pop();
            etat.Dossiers++;

            IEnumerable<FileSystemInfo> contenu;

            try
            {
                contenu = new DirectoryInfo(dossier).EnumerateFileSystemInfos("*", options);
            }
            catch (Exception)
            {
                // Dossier disparu ou refusé entre-temps : on passe. Une
                // recherche ne s'interrompt pas pour un répertoire.
                continue;
            }

            foreach (var info in contenu)
            {
                if (jeton.IsCancellationRequested) break;

                bool estDossier;
                bool cachee;
                bool systeme;
                long taille = 0;
                DateTime modifie;

                try
                {
                    var attributs = info.Attributes;

                    estDossier = (attributs & FileAttributes.Directory) != 0;
                    cachee  = (attributs & FileAttributes.Hidden) != 0;
                    systeme = (attributs & FileAttributes.System) != 0;
                    modifie = info.LastWriteTime;

                    if (!estDossier && info is FileInfo fichier) taille = fichier.Length;
                }
                catch (Exception)
                {
                    // L'entrée a disparu entre l'énumération et la lecture.
                    continue;
                }

                // Un dossier qu'on n'afficherait pas n'est pas non plus
                // parcouru : la recherche ne doit pas rapporter ce que la
                // navigation masque.
                if (visibilite.Ecarter(cachee, systeme)) continue;

                if (estDossier) etat.Pile.Push(info.FullName);

                string nom = info.Name;

                string extension = estDossier
                    ? ""
                    : Path.GetExtension(nom).TrimStart('.').ToLowerInvariant();

                if (!Filtrage.Correspond(nom, extension, motif)) continue;

                trouves.Add(new Entree(nom, info.FullName, estDossier, taille,
                                       modifie, cachee || systeme));

                if (trouves.Count >= reste) break;
            }
        }

        return trouves;
    }
}
