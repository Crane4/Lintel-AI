using LintelAI.Models;

namespace LintelAI.Services;

public sealed class FileAnalyzer(OllamaClient ollama)
{
    /// <summary>Longer files are reviewed in pieces; beyond this the rest is left unread (and flagged).</summary>
    public const int MaxParts = 4;

    /// <summary>Lines repeated at the start of the next piece so a construct cut in half is still seen.</summary>
    private const int Overlap = 3;

    public async Task<FileAnalysis> AnalyzeAsync(
        string root,
        string relativePath,
        string model,
        int maxChars,
        TimeSpan timeout,
        CancellationToken ct,
        ProjectIndex? index = null)
    {
        var lines = await FileText.ReadLinesAsync(Path.Combine(root, relativePath), ct).ConfigureAwait(false);
        if (lines is null)
            return new FileAnalysis { Path = relativePath, Error = "Could not read this file." };

        var isReadme = string.Equals(Path.GetFileName(relativePath), "README.md", StringComparison.OrdinalIgnoreCase);

        var context = ProjectContext.Empty;
        var prompts = new List<string>();
        var truncated = false;

        if (isReadme)
        {
            var (clipped, wasClipped) = Prompts.Clip(string.Join('\n', lines), maxChars);
            prompts.Add(Prompts.Readme(clipped));
            truncated = wasClipped;
        }
        else
        {
            if (index is not null) context = index.ContextFor(relativePath, string.Join('\n', lines));

            var plan = Plan(lines, maxChars);
            truncated = plan.Truncated;
            for (var i = 0; i < plan.Parts.Count; i++)
            {
                var part = plan.Parts[i];
                prompts.Add(Prompts.CodeReview(
                    relativePath, part.Text, context, i + 1, plan.Parts.Count, part.Start + 1, part.End));
            }
        }

        var parsedParts = new List<ParsedAnalysis>();
        string? lastError = null;
        string? lastRaw = null;
        var failedParts = 0;

        foreach (var prompt in prompts)
        {
            ct.ThrowIfCancellationRequested();

            string raw;
            try
            {
                raw = await ollama.GenerateJsonAsync(model, prompt, timeout, ct).ConfigureAwait(false);
            }
            catch (OllamaException ex)
            {
                failedParts++;
                lastError = ex.Message;
                // No point hammering a server that just failed: report what we have.
                break;
            }

            if (JsonExtractor.TryParseAnalysis(raw, out var parsed))
            {
                parsedParts.Add(parsed);
            }
            else
            {
                failedParts++;
                lastRaw = raw;
                lastError = "The model's reply wasn't valid JSON. Re-running this file or using a larger model usually fixes it.";
            }
        }

        if (parsedParts.Count == 0)
        {
            return new FileAnalysis
            {
                Path = relativePath,
                LineCount = lines.Count,
                Error = lastError ?? "The model returned nothing.",
                RawResponse = lastRaw
            };
        }

        var issues = Merge(parsedParts)
            .Where(i => !context.ContradictsProject(i))
            .Select(i => i with { Line = LineSnapper.Snap(lines, i.Line, i.Description) })
            .Select(i => i with { Snippet = BuildSnippet(lines, i.Line) })
            .ToList();

        var summary = parsedParts.Select(p => p.Summary).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        if (string.IsNullOrEmpty(summary))
            summary = issues.Count == 0 ? "No issues found." : $"{issues.Count} issue(s) found.";

        var unreviewed = prompts.Count - parsedParts.Count;
        if (unreviewed > 0)
            summary += $" ({unreviewed} of {prompts.Count} parts could not be reviewed.)";

        return new FileAnalysis
        {
            Path = relativePath,
            Issues = issues,
            Summary = summary,
            // The score is derived from the issues rather than trusted from the model, which is bad at arithmetic.
            Score = Scoring.Compute(issues),
            Truncated = truncated || unreviewed > 0,
            LineCount = lines.Count
        };
    }

    private sealed record Part(int Start, int End, string Text);
    private sealed record PartPlan(IReadOnlyList<Part> Parts, bool Truncated);

    /// <summary>Splits a file into pieces that each fit the character budget.</summary>
    private static PartPlan Plan(IReadOnlyList<string> lines, int maxChars)
    {
        var parts = new List<Part>();
        var start = 0;

        while (start < lines.Count && parts.Count < MaxParts)
        {
            var (text, end) = Prompts.NumberRange(lines, start, maxChars);
            parts.Add(new Part(start, end, text));
            if (end >= lines.Count) break;

            // step back a little so code split across the boundary is seen whole at least once
            start = Math.Max(end - Overlap, start + 1);
        }

        var covered = parts.Count > 0 ? parts[^1].End : 0;
        return new PartPlan(parts, covered < lines.Count);
    }

    /// <summary>Joins findings from several parts, dropping the repeats the overlap produces.</summary>
    private static IEnumerable<Issue> Merge(IEnumerable<ParsedAnalysis> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var issue in parts.SelectMany(p => p.Issues))
        {
            var key = $"{issue.Line}|{issue.Type}|{issue.Description}";
            if (seen.Add(key)) yield return issue;
        }
    }

    private static IReadOnlyList<SnippetLine>? BuildSnippet(IReadOnlyList<string> lines, int? line)
    {
        if (line is not { } n || n < 1 || n > lines.Count) return null;

        var from = Math.Max(1, n - 2);
        var to = Math.Min(lines.Count, n + 2);
        var snippet = new List<SnippetLine>();

        for (var i = from; i <= to; i++)
        {
            var text = lines[i - 1].Replace("\t", "    ");
            if (text.Length > 180) text = text[..180] + "…";
            snippet.Add(new SnippetLine(i, text, i == n));
        }
        return snippet;
    }
}

public static class FileText
{
    public static async Task<IReadOnlyList<string>?> ReadLinesAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            var text = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
