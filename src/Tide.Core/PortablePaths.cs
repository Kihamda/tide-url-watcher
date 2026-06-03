namespace Tide.Core;

public static class PortablePaths
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "Data");
    public static string StoragePath => Path.Combine(DataDirectory, "watcher-data.json");
    public static string BackupStoragePath => Path.Combine(DataDirectory, "watcher-data.json.bak");
    public static string TempStoragePath => Path.Combine(DataDirectory, "watcher-data.json.tmp");
    public static string AppLogPath => Path.Combine(DataDirectory, "app.log");
    public static string StartupLogPath => Path.Combine(DataDirectory, "startup.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    public static void MigrateLegacyData()
    {
        var legacyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tide");
        if (!Directory.Exists(legacyDirectory))
        {
            return;
        }

        try
        {
            EnsureDataDirectory();
            MoveIfPresent(Path.Combine(legacyDirectory, "watcher-data.json"), StoragePath);
            MoveIfPresent(Path.Combine(legacyDirectory, "watcher-data.json.bak"), BackupStoragePath);
            MoveIfPresent(Path.Combine(legacyDirectory, "app.log"), AppLogPath);
            MoveIfPresent(Path.Combine(legacyDirectory, "startup.log"), StartupLogPath);
            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            {
                Directory.Delete(legacyDirectory);
            }
        }
        catch
        {
            // Keep legacy files intact if the extracted app folder is read-only.
        }
    }

    private static void MoveIfPresent(string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination))
        {
            File.Move(source, destination);
        }
    }
}
