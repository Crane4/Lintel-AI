namespace LintelAI.Services.Security;

/// <summary>One file the security scan will look at, described without reading all of it.</summary>
public sealed record SecurityFile(
    string Path,
    string FullPath,
    long Length,
    bool IsBinary,
    bool HasExecutableHeader,
    bool TooLarge = false)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path);
}

public sealed record SecurityCollection(
    IReadOnlyList<SecurityFile> Files,
    IReadOnlyList<string> Skipped,
    bool Truncated);

/// <summary>
/// Gathers what the security scan should inspect. Unlike <see cref="ProjectScanner"/> this keeps
/// binaries, hidden files and unusual extensions, because that is where malware hides, and it does
/// not honour .AIIgnore: the project being checked must not get to decide what is left unchecked.
/// </summary>
public sealed class SecurityFileCollector
{
    /// <summary>Single-header libraries such as miniaudio.h run to several MB of ordinary source.</summary>
    public const long MaxTextBytes = 10_000_000;
    public const int MaxFiles = 5000;

    private const int HeaderBytes = 4096;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "__pycache__", ".venv", "venv"
    };

    public SecurityCollection Collect(string root, CancellationToken ct = default)
    {
        var files = new List<SecurityFile>();
        var skipped = new List<string>();
        var truncated = false;

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && !truncated)
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
                continue;
            }

            foreach (var entry in entries)
            {
                var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');

                // A link in a downloaded project can point anywhere on this machine: never follow one.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(relative + " (link, not followed)");
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    if (SkipDirectories.Contains(entry.Name)) skipped.Add(relative + "/");
                    else pending.Push(entry.FullName);
                    continue;
                }

                var info = (FileInfo)entry;
                if (info.Length == 0) continue;

                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }

                var file = Describe(relative, info);
                if (file is not null) files.Add(file);
            }
        }

        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        skipped.Sort(StringComparer.OrdinalIgnoreCase);
        return new SecurityCollection(files, skipped, truncated);
    }

    private static SecurityFile? Describe(string relative, FileInfo info)
    {
        var buffer = new byte[HeaderBytes];
        int read;
        try
        {
            using var stream = info.OpenRead();
            read = stream.Read(buffer, 0, buffer.Length);
        }
        catch
        {
            return null;
        }

        var isBinary = Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
        var executable = isBinary && HasExecutableMagic(buffer, read);
        var tooLarge = !isBinary && info.Length > MaxTextBytes;

        return new SecurityFile(relative, info.FullName, info.Length, isBinary, executable, tooLarge);
    }

    /// <summary>PE (Windows), ELF (Linux) and Mach-O (macOS) executables.</summary>
    private static bool HasExecutableMagic(byte[] b, int length)
    {
        if (length < 4) return false;
        if (b[0] == 0x4D && b[1] == 0x5A) return true;
        if (b[0] == 0x7F && b[1] == 0x45 && b[2] == 0x4C && b[3] == 0x46) return true;

        var magic = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE;
    }
}
