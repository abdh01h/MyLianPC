using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyLianPC.Core.Settings;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public static class AtomicFile
{
    /// <summary>Writes to a temp file in the same directory, flushes it, then replaces the target in one rename.</summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs))
        {
            w.Write(contents);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
