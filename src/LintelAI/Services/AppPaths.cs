namespace LintelAI.Services;

public static class AppPaths
{
    private const string FolderName = "LintelAI";

    /// <summary>The folder used before the app was renamed.</summary>
    private const string LegacyFolderName = "AIProjectAnalyzer";

    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>
    /// Settings, history and chats from the old name are moved over once, so a rename never costs
    /// anyone their saved runs. If the move fails the old folder is simply left where it was.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(root, FolderName);
        var legacy = Path.Combine(root, LegacyFolderName);

        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
                Directory.Move(legacy, current);
        }
        catch { /* start fresh rather than fail to launch */ }

        return current;
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public static string ChatsFile => Path.Combine(DataDirectory, "chats.json");

    /// <summary>Writes through a temp file so a crash can never leave half a JSON document behind.</summary>
    public static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
