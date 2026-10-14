using System.Text.RegularExpressions;
using LintelAI.Models;

namespace LintelAI.Services.Security;

public interface ISecurityRule
{
    /// <param name="lines">The file's lines, or null for binary or unreadable files.</param>
    IEnumerable<Issue> Evaluate(SecurityFile file, IReadOnlyList<string>? lines);
}

internal static class SecurityRuleSupport
{
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".adoc"
    };

    /// <summary>Documentation legitimately shows commands like "curl ... | sh", so code rules skip it.</summary>
    public static bool IsDocument(string path) => DocumentExtensions.Contains(Path.GetExtension(path));

    public static Regex Compile(string pattern, bool ignoreCase = true) =>
        new(
            pattern,
            (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            RegexTimeout);

    private static readonly string[] PreprocessorDirectives =
    [
        "#include", "#define", "#undef", "#if", "#else", "#elif", "#endif", "#pragma",
        "#region", "#endregion", "#error", "#line", "#import", "#nullable", "#!"
    ];

    /// <summary>
    /// True for a line that is only a comment. Prose in comments ("capture local state", a sample
    /// "curl | sh") is the main source of false alarms, and a comment never runs.
    /// </summary>
    public static bool IsCommentLine(string line)
    {
        var text = line.AsSpan().TrimStart();
        if (text.Length == 0) return false;

        if (text.StartsWith("//") || text.StartsWith("/*") || text.StartsWith("<!--") ||
            text.StartsWith("--") || text.StartsWith("::") || text.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
            return true;

        // " * text" and "*/" continue a block comment; "*ptr" is code.
        if (text[0] == '*') return text.Length == 1 || text[1] is ' ' or '/' or '\t';

        if (text[0] == '#')
        {
            foreach (var directive in PreprocessorDirectives)
                if (text.StartsWith(directive, StringComparison.Ordinal)) return false;
            return true;
        }

        return false;
    }

    public static Match? TryMatch(Regex regex, string input)
    {
        try
        {
            var match = regex.Match(input);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    public static string Excerpt(string text, int max = 80)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    public static Issue Finding(Severity severity, string type, string description, int? line) =>
        new(severity, type, description, line, IssueKind.Vulnerability);
}
