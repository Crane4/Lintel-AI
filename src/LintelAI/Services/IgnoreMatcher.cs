using System.Text;
using System.Text.RegularExpressions;

namespace LintelAI.Services;

/// <summary>
/// Reads <c>.AIIgnore</c> files. The syntax follows .gitignore closely: comments, negation with
/// <c>!</c>, trailing <c>/</c> for directories, <c>*</c>, <c>?</c>, <c>**</c> and a leading or inner
/// <c>/</c> to anchor a pattern at the project root.
/// </summary>
public sealed class IgnoreMatcher
{
    private sealed record Rule(Regex Pattern, bool Negated, bool DirectoryOnly);

    private readonly List<Rule> _rules = new();

    public static IgnoreMatcher Empty { get; } = new();

    public int RuleCount => _rules.Count;

    public static IgnoreMatcher FromFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : Empty;
        }
        catch
        {
            return Empty;
        }
    }

    public static IgnoreMatcher Parse(IEnumerable<string> lines)
    {
        var matcher = new IgnoreMatcher();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var negated = line.StartsWith('!');
            if (negated) line = line[1..].Trim();

            line = line.Replace('\\', '/');
            if (line.StartsWith("./")) line = line[2..];

            var dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0) continue;

            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0) continue;

            matcher._rules.Add(new Rule(Translate(line, anchored), negated, dirOnly));
        }
        return matcher;
    }

    /// <param name="relativePath">Path relative to the project root, using forward slashes.</param>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.DirectoryOnly && !isDirectory) continue;
            if (rule.Pattern.IsMatch(relativePath)) ignored = !rule.Negated;
        }
        return ignored;
    }

    private static Regex Translate(string glob, bool anchored)
    {
        var sb = new StringBuilder(anchored ? "^" : "^(?:.*/)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i++;
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                    break;

                case '?':
                    sb.Append("[^/]");
                    break;

                case '[':
                    var close = glob.IndexOf(']', i + 1);
                    if (close > i + 1)
                    {
                        var body = glob.Substring(i + 1, close - i - 1);
                        if (body.StartsWith('!')) body = "^" + body[1..];
                        sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                        i = close;
                    }
                    else sb.Append("\\[");
                    break;

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
