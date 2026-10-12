using System.Text.RegularExpressions;
using LintelAI.Models;

namespace LintelAI.Services;

/// <summary>What the model should know about the rest of the project while it reads one file.</summary>
public sealed record ProjectContext(IReadOnlyList<string> OtherFiles, int OtherFileCount, IReadOnlyList<string> UsedElsewhere)
{
    public static ProjectContext Empty { get; } = new(Array.Empty<string>(), 0, Array.Empty<string>());

    public bool IsEmpty => OtherFiles.Count == 0 && UsedElsewhere.Count == 0;

    private static readonly string[] UnusedPhrases =
    {
        "unused", "never used", "never called", "not used", "not called", "dead code", "no references",
        "is not referenced", "never referenced", "never invoked"
    };

    /// <summary>
    /// A "dead code" claim about something another file demonstrably uses is a false positive,
    /// whatever the model believed. Those are dropped instead of shown.
    /// </summary>
    public bool ContradictsProject(Issue issue)
    {
        if (UsedElsewhere.Count == 0) return false;

        var claimsUnused = issue.Type.Contains("dead", StringComparison.OrdinalIgnoreCase) ||
                           UnusedPhrases.Any(p => issue.Description.Contains(p, StringComparison.OrdinalIgnoreCase));
        if (!claimsUnused) return false;

        return UsedElsewhere.Any(name => Regex.IsMatch(
            issue.Description, $@"(?<![\w$]){Regex.Escape(name)}(?![\w$])"));
    }
}

/// <summary>
/// A cheap cross-file index: which identifiers appear in which file. It lets a file be reviewed
/// knowing that, say, <c>remember</c> is called from <c>main.js</c>, without sending the whole project.
/// </summary>
public sealed class ProjectIndex
{
    private const int MaxIndexedFiles = 3000;
    private const int MaxListedFiles = 60;
    private const int MaxListedNames = 40;

    private static readonly Regex WordPattern = new(@"[A-Za-z_$][\w$]{2,}", RegexOptions.Compiled);

    private static readonly Regex[] Declarations =
    {
        // function foo / class Foo / def foo / fn foo / const foo / type Foo …
        new(@"\b(?:function\*?|class|interface|enum|struct|trait|type|def|fn|func|const|let|var|val|record|namespace|module|object)\s+([A-Za-z_$][\w$]*)",
            RegexOptions.Compiled),

        // public static async Task<int> Foo(  /  export default foo =
        new(@"\b(?:public|private|protected|internal|static|async|override|virtual|export)\s+(?:[\w<>\[\],.?*&]+\s+)*?([A-Za-z_$][\w$]*)\s*[(={;]",
            RegexOptions.Compiled),

        // exports.foo = …
        new(@"\bexports\.([A-Za-z_$][\w$]*)\s*=", RegexOptions.Compiled)
    };

    private static readonly Regex ModuleExports =
        new(@"module\.exports\s*=\s*\{([^}]*)\}", RegexOptions.Compiled);

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "new", "get", "set", "var", "let", "const", "int", "string", "bool", "void", "null", "true", "false",
        "this", "self", "return", "function", "class", "static", "public", "private", "async", "await",
        "the", "and", "for", "not", "main", "init", "args", "value", "key", "data", "item", "index", "result"
    };

    private readonly Dictionary<string, HashSet<string>> _words;
    private readonly IReadOnlyList<string> _files;

    private ProjectIndex(IReadOnlyList<string> files, Dictionary<string, HashSet<string>> words)
    {
        _files = files;
        _words = words;
    }

    public int FileCount => _files.Count;

    public static ProjectIndex Build(string root, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var words = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in files.Take(MaxIndexedFiles))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var text = File.ReadAllText(Path.Combine(root, path));
                words[path] = WordPattern.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                // an unreadable file simply contributes nothing
            }
        }

        return new ProjectIndex(files, words);
    }

    /// <summary>Names a file declares: functions, classes, constants, exports.</summary>
    public static IReadOnlyList<string> DeclaredNames(string content)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pattern in Declarations)
            foreach (Match m in pattern.Matches(content))
                Add(m.Groups[1].Value);

        foreach (Match m in ModuleExports.Matches(content))
        {
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var name = part.Split(':')[0].Trim();
                if (name.Length > 0) Add(name);
            }
        }

        return names.ToList();

        void Add(string name)
        {
            if (name.Length >= 3 && !Noise.Contains(name)) names.Add(name);
        }
    }

    public ProjectContext ContextFor(string path, string content)
    {
        var others = _files.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)).ToList();

        var used = DeclaredNames(content)
            .Where(name => _words.Any(kv =>
                !string.Equals(kv.Key, path, StringComparison.OrdinalIgnoreCase) && kv.Value.Contains(name)))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(MaxListedNames)
            .ToList();

        return new ProjectContext(others.Take(MaxListedFiles).ToList(), others.Count, used);
    }
}
