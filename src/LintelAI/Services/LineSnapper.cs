using System.Text.RegularExpressions;

namespace LintelAI.Services;

/// <summary>
/// Small models know roughly where a problem is but often miss the number by a few lines.
/// When the description names something from the code (<c>setInterval</c>, <c>fillTheVoid</c>),
/// look for it near the reported line and move the finding onto the line that really has it.
/// </summary>
public static partial class LineSnapper
{
    private const int Radius = 6;

    [GeneratedRegex(@"`([^`\n]{3,60})`|([A-Za-z_$][\w$]*)\(\)|\b([A-Za-z_$][\w$]*)\b")]
    private static partial Regex Candidate();

    public static int? Snap(IReadOnlyList<string> lines, int? reported, string description)
    {
        if (reported is not { } line || line < 1 || line > lines.Count) return reported;

        var names = NamesIn(description);
        if (names.Count == 0) return line;

        // already pointing at a line that mentions one of them: leave it alone
        if (Score(lines[line - 1], names) > 0) return line;

        var best = line;
        var bestScore = 0;
        var from = Math.Max(1, line - Radius);
        var to = Math.Min(lines.Count, line + Radius);

        for (var i = from; i <= to; i++)
        {
            var score = Score(lines[i - 1], names);
            if (score == 0) continue;

            if (score > bestScore || (score == bestScore && Math.Abs(i - line) < Math.Abs(best - line)))
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Identifier-like words only: plain English ("array", "never") would match anywhere.</summary>
    private static HashSet<string> NamesIn(string description)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in Candidate().Matches(description))
        {
            var quoted = m.Groups[1].Value.Trim().TrimEnd('(', ')');
            if (quoted.Length >= 3) { names.Add(quoted); continue; }

            if (m.Groups[2].Success && m.Groups[2].Length >= 3) { names.Add(m.Groups[2].Value); continue; }

            var word = m.Groups[3].Value;
            if (word.Length >= 4 && LooksLikeIdentifier(word)) names.Add(word);
        }

        return names;
    }

    private static bool LooksLikeIdentifier(string word)
    {
        if (word.Contains('_') || word.Contains('$')) return true;
        // camelCase or PascalCase with a capital in the middle
        for (var i = 1; i < word.Length; i++)
            if (char.IsUpper(word[i]) && char.IsLower(word[i - 1])) return true;
        return false;
    }

    private static int Score(string line, HashSet<string> names) =>
        names.Count(name => line.Contains(name, StringComparison.Ordinal));
}
