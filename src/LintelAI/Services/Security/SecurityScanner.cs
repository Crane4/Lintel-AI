using LintelAI.Models;
using LintelAI.Services.Security.Rules;

namespace LintelAI.Services.Security;

public enum RiskVerdict
{
    /// <summary>Nothing worth flagging was found. That is not a guarantee the project is safe.</summary>
    Clean,
    Suspicious,
    Dangerous
}

public sealed record SecurityReport(
    RiskVerdict Verdict,
    IReadOnlyList<FileAnalysis> Files,
    int FilesScanned,
    int BinaryFiles,
    IReadOnlyList<string> Skipped,
    bool Truncated,
    string? AiSummary = null)
{
    public int Count(Severity severity) => Files.Sum(f => f.Issues.Count(i => i.Severity == severity));

    public static RiskVerdict Assess(IEnumerable<Issue> issues)
    {
        var verdict = RiskVerdict.Clean;
        foreach (var issue in issues)
        {
            if (issue.Severity == Severity.High) return RiskVerdict.Dangerous;
            if (issue.Severity == Severity.Medium) verdict = RiskVerdict.Suspicious;
        }
        return verdict;
    }
}

/// <summary>The "Normal" scan: rules and patterns only, nothing leaves the machine.</summary>
public sealed class SecurityScanner
{
    private readonly IReadOnlyList<ISecurityRule> _rules;

    public SecurityScanner() : this(DefaultRules())
    {
    }

    public SecurityScanner(IEnumerable<ISecurityRule> rules) => _rules = rules.ToList();

    public static IReadOnlyList<ISecurityRule> DefaultRules() =>
    [
        new FileNameRules(),
        new ManifestRules(),
        new CodePatternRules(),
        new ObfuscationRules()
    ];

    public async Task<SecurityReport> ScanAsync(
        string root,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var collected = await Task.Run(() => new SecurityFileCollector().Collect(root, ct), ct).ConfigureAwait(false);

        var results = new List<FileAnalysis>();
        var done = 0;

        foreach (var file in collected.Files)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<string>? lines = null;
            if (!file.IsBinary && !file.TooLarge)
                lines = await FileText.ReadLinesAsync(file.FullPath, ct).ConfigureAwait(false);

            var issues = new List<Issue>();

            if (file.TooLarge)
            {
                issues.Add(new Issue(Severity.Low, "File not scanned",
                    $"Larger than {SecurityFileCollector.MaxTextBytes / 1_000_000} MB, so its content was not checked.",
                    null, IssueKind.Vulnerability));
            }

            foreach (var rule in _rules)
                issues.AddRange(rule.Evaluate(file, lines));

            if (issues.Count > 0)
            {
                var ordered = issues
                    .OrderByDescending(i => i.Severity)
                    .ThenBy(i => i.Line ?? int.MaxValue)
                    .Select(i => lines is null ? i : i with { Snippet = FileAnalyzer.BuildSnippet(lines, i.Line) })
                    .ToList();

                results.Add(new FileAnalysis
                {
                    Path = file.Path,
                    Issues = ordered,
                    Summary = $"{ordered.Count} finding(s).",
                    // No score: a code-quality number would read as "fine" for a file that is not.
                    LineCount = lines?.Count ?? 0
                });
            }

            progress?.Report((++done, collected.Files.Count));
        }

        var verdict = SecurityReport.Assess(results.SelectMany(f => f.Issues));

        return new SecurityReport(
            verdict,
            results,
            collected.Files.Count,
            collected.Files.Count(f => f.IsBinary),
            collected.Skipped,
            collected.Truncated);
    }
}
