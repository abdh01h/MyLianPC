namespace MyLianPC.Core.Settings;

/// <summary>
/// Copies state written by the app's previous name (MyPcRgb) into the MyLianPC folders the first time it runs,
/// so profiles, device settings and preferences survive the rename. Best effort: failures leave the new folder empty.
/// </summary>
public static class LegacyData
{
    private const string OldFolder = "MyPcRgb", NewFolder = "MyLianPC";

    /// <summary>Copies missing files from &lt;parent&gt;\MyPcRgb into &lt;parent&gt;\MyLianPC. Never overwrites.</summary>
    public static void MigrateFolder(string parent)
    {
        try
        {
            var from = Path.Combine(parent, OldFolder);
            var to = Path.Combine(parent, NewFolder);
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (NotSupportedException) { }
    }

    /// <summary>Migration for the machine-wide folder (%ProgramData%).</summary>
    public static void MigrateMachineData() =>
        MigrateFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>Migration for the per-account folder (%LOCALAPPDATA%).</summary>
    public static void MigrateUserData() =>
        MigrateFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
}
