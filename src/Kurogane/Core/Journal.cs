using System.IO;
using System.Globalization;
using System.Text;

namespace Kurogane.Core;

/// <summary>
/// Journal double : console (lisible tout de suite) et fichier (collable
/// intégralement dans une conversation pour analyse après coup).
/// C'est la seule fenêtre dont on dispose sur ce qui s'est passé dans la VM.
/// </summary>
public sealed class Journal : IDisposable
{
    private readonly StringBuilder _buffer = new();
    private readonly string _path;

    public string Path => _path;

    public Journal(string action)
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kurogane", "logs");
        Directory.CreateDirectory(dir);

        _path = System.IO.Path.Combine(
            dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{action}.log");

        Raw($"=== kurogane {action} ===");
        Raw($"machine : {Environment.MachineName}   utilisateur : {Environment.UserName}");
        Raw($"os      : {Environment.OSVersion.VersionString}   64 bits : {Environment.Is64BitOperatingSystem}");
        Raw($"début   : {DateTimeOffset.Now:O}");
        Raw("");
    }

    public void Info(string msg)  => Line("    ", msg);
    public void Step(string msg)  => Line("==> ", msg);
    public void Ok(string msg)    => Line("  + ", msg);
    public void Warn(string msg)  => Line("  ! ", msg);
    public void Error(string msg) => Line("  X ", msg);

    public void Blank() => Raw("");

    private void Line(string prefix, string msg)
    {
        foreach (var l in msg.Split('\n'))
            Raw(prefix + l);
    }

    private void Raw(string line)
    {
        Console.WriteLine(line);
        _buffer.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
               .Append("  ")
               .AppendLine(line);
    }

    public void Dispose()
    {
        Raw("");
        Raw($"fin     : {DateTimeOffset.Now:O}");
        try { File.WriteAllText(_path, _buffer.ToString()); }
        catch (Exception ex) { Console.WriteLine($"  ! Journal non écrit : {ex.Message}"); }
    }
}
