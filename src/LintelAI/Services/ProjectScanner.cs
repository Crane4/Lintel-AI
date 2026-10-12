namespace LintelAI.Services;

public sealed record ScanResult(IReadOnlyList<string> Files, int IgnoredCount);

public sealed class ProjectScanner
{
    public const string IgnoreFileName = ".AIIgnore";

    private const long MaxFileBytes = 1_000_000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx", ".py", ".java", ".go", ".rs",
        ".c", ".cpp", ".cc", ".h", ".hpp", ".cs", ".vb", ".fs", ".php", ".rb", ".swift", ".kt",
        ".dart", ".lua", ".scala", ".sql", ".ps1", ".bat",
        ".vue", ".svelte", ".html", ".css", ".scss", ".sass", ".less",
        ".xaml", ".axaml", ".razor", ".cshtml",
        ".json", ".yaml", ".yml", ".toml", ".xml", ".ini", ".cfg",
        ".md", ".txt", ".sh", ".bash"
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "dist", "build", "out", "bin", "obj",
        ".vs", ".idea", ".vscode", "__pycache__", ".venv", "venv", "target", ".next", ".nuxt", "coverage"
    };

    private static readonly HashSet<string> SkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "composer.lock", "Cargo.lock"
    };

    public ScanResult Scan(string root, CancellationToken ct = default)
    {
        var matcher = IgnoreMatcher.FromFile(Path.Combine(root, IgnoreFileName));
        var files = new List<string>();
        var ignored = 0;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch
            {
                continue; // unreadable folder, move on
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // avoid link cycles
                    if (SkipDirectories.Contains(entry.Name)) continue;
                    if (matcher.IsIgnored(relative, isDirectory: true)) { ignored++; continue; }
                    pending.Push(entry.FullName);
                    continue;
                }

                if (!TextExtensions.Contains(entry.Extension)) continue;
                if (SkipFiles.Contains(entry.Name)) continue;
                if (IsSecretsFile(entry.Name)) continue;
                if (entry.Name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
                    entry.Name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase)) continue;
                if (matcher.IsIgnored(relative, isDirectory: false)) { ignored++; continue; }

                var info = (FileInfo)entry;
                if (info.Length == 0 || info.Length > MaxFileBytes) continue;
                if (LooksBinary(info.FullName)) continue;

                files.Add(relative);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return new ScanResult(files, ignored);
    }

    /// <summary>
    /// Environment files hold credentials. They are never worth sending to a model, least of all
    /// one on another machine, so they are skipped no matter what .AIIgnore says.
    /// </summary>
    public static bool IsSecretsFile(string name) =>
        name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);

    private static bool LooksBinary(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        }
        catch
        {
            return true;
        }
    }
}
