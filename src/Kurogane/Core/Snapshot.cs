using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kurogane.Core;

/// <summary>
/// Point de restauration. Écrit sur disque AVANT la première écriture système
/// (write-ahead) : si le processus meurt au milieu de l'application, le fichier
/// couvre déjà la totalité des modifications prévues. Mieux vaut un point de
/// restauration qui en dit trop qu'un qui en dit trop peu.
/// </summary>
public sealed class Snapshot
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public string ThemeName { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string ToolVersion { get; set; } = "0.1.0";
    public List<ChangeRecord> Changes { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string SnapshotDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kurogane", "snapshots");

    public static Snapshot Create(string themeName, IEnumerable<ChangeRecord> changes) => new()
    {
        Id          = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),
        CreatedUtc  = DateTimeOffset.UtcNow,
        ThemeName   = themeName,
        MachineName = Environment.MachineName,
        UserName    = Environment.UserName,
        Changes     = changes.ToList(),
    };

    public string Save()
    {
        Directory.CreateDirectory(SnapshotDirectory);
        string path = Path.Combine(SnapshotDirectory, $"{Id}.json");

        // Écriture atomique : fichier temporaire puis remplacement, pour qu'un
        // point de restauration ne puisse jamais être tronqué.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, overwrite: true);

        return path;
    }

    public static Snapshot Load(string path)
    {
        var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidDataException($"Point de restauration illisible : {path}");
        return snap;
    }

    public static List<(string Path, Snapshot Snap)> ListAll()
    {
        if (!Directory.Exists(SnapshotDirectory)) return new();

        var result = new List<(string, Snapshot)>();
        foreach (var f in Directory.GetFiles(SnapshotDirectory, "*.json").OrderBy(f => f))
        {
            try { result.Add((f, Load(f))); }
            catch { /* un fichier corrompu ne doit pas empêcher de lister les autres */ }
        }
        return result;
    }

    public static (string Path, Snapshot Snap)? Latest() =>
        ListAll() is { Count: > 0 } all ? all[^1] : null;
}
