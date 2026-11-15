using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kurogane.Bureau;

/// <summary>Une place sur la grille du bureau.</summary>
public sealed class Place
{
    public int Colonne { get; set; }
    public int Ligne { get; set; }
}

/// <summary>
/// Où l'utilisateur a posé ses icônes, retenu d'une session à l'autre.
///
/// Windows garde les siennes dans un format interne qu'on ne lira pas.
/// Les nôtres sont donc à nous, et c'est tant mieux : tenir notre propre
/// fichier évite d'avoir à deviner le sien, et permet de retenir une
/// cellule plutôt qu'un pixel.
///
/// Le fichier vit dans <c>%LOCALAPPDATA%\Kurogane</c> et non dans le
/// dossier de travail : c'est un état d'utilisateur, pas une donnée du
/// projet, et il doit survivre au déplacement de l'exécutable.
///
/// Les clés sont des chemins complets. Un fichier renommé perd sa place et
/// retombe sur la première libre — Windows fait de même, et suivre un
/// renommage demanderait de surveiller le dossier en permanence pour un
/// gain mince.
/// </summary>
internal sealed class Dispositions
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Dictionary<string, Place> Positions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    private static string Fichier()
    {
        string dossier = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane");

        return Path.Combine(dossier, "bureau.json");
    }

    internal static Dispositions Charger()
    {
        try
        {
            string chemin = Fichier();
            if (!File.Exists(chemin)) return new Dispositions();

            var lues = JsonSerializer.Deserialize<Dispositions>(
                File.ReadAllText(chemin), Options);

            if (lues is null) return new Dispositions();

            // Le comparateur se perd à la désérialisation : un dictionnaire
            // reconstruit par le sérialiseur est sensible à la casse, et
            // les chemins Windows ne le sont pas.
            lues.Positions = new Dictionary<string, Place>(
                lues.Positions, StringComparer.OrdinalIgnoreCase);

            return lues;
        }
        catch (Exception)
        {
            // Fichier absent, illisible ou corrompu : on repart d'une
            // disposition vide. Perdre des places est désagréable ;
            // refuser d'afficher le bureau le serait beaucoup plus.
            return new Dispositions();
        }
    }

    internal void Enregistrer()
    {
        try
        {
            string chemin = Fichier();

            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Un disque plein ou un profil en lecture seule ne doit pas
            // faire tomber le bureau. Les places seront reconstruites au
            // prochain lancement.
        }
    }

    internal Place? Pour(string chemin) =>
        Positions.TryGetValue(chemin, out var place) ? place : null;

    internal void Poser(string chemin, int colonne, int ligne) =>
        Positions[chemin] = new Place { Colonne = colonne, Ligne = ligne };

    /// <summary>
    /// Oublie les chemins qui ne sont plus sur le bureau. Sans ce ménage,
    /// le fichier enfle d'une session à l'autre avec des places que plus
    /// rien ne réclame.
    /// </summary>
    internal void Nettoyer(IEnumerable<string> presents)
    {
        var vivants = new HashSet<string>(presents, StringComparer.OrdinalIgnoreCase);

        foreach (string mort in Positions.Keys.Where(c => !vivants.Contains(c)).ToList())
            Positions.Remove(mort);
    }
}
