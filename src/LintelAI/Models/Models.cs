using System.Text.Json.Serialization;

namespace LintelAI.Models;

public enum Severity { Low, Medium, High }

/// <summary>What sort of finding this is, using the vocabulary code-analysis tools share.</summary>
public enum IssueKind
{
    /// <summary>Code that misbehaves at runtime: leaks, hangs, wrong results.</summary>
    Bug,

    /// <summary>Something an attacker could exploit: injection, hard-coded secrets, unsafe eval.</summary>
    Vulnerability,

    /// <summary>Works, but is harder to maintain than it should be: dead code, noise, bad habits.</summary>
    Smell
}

public sealed record SnippetLine(int Number, string Text, bool IsTarget);

public sealed record Issue(
    Severity Severity,
    string Type,
    string Description,
    int? Line,
    IssueKind Kind = IssueKind.Smell,
    IReadOnlyList<SnippetLine>? Snippet = null);

public sealed class FileAnalysis
{
    public required string Path { get; init; }
    public IReadOnlyList<Issue> Issues { get; init; } = Array.Empty<Issue>();
    public string Summary { get; init; } = "";
    public int? Score { get; init; }
    public string? Error { get; init; }
    public bool Truncated { get; init; }
    public int LineCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawResponse { get; init; }
}

public sealed class HistoryIssue
{
    public IssueKind Kind { get; set; }
    public Severity Severity { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public int? Line { get; set; }
    public List<SnippetLine>? Snippet { get; set; }
}

/// <summary>One file of a saved run: enough to show its findings again without re-analyzing.</summary>
public sealed class HistoryFile
{
    public string Path { get; set; } = "";
    public int? Score { get; set; }
    public int IssueCount { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public bool Truncated { get; set; }
    public int LineCount { get; set; }
    public List<HistoryIssue> Issues { get; set; } = new();

    public static HistoryFile From(FileAnalysis analysis) => new()
    {
        Path = analysis.Path,
        Score = analysis.Score,
        IssueCount = analysis.Issues.Count,
        Summary = analysis.Summary,
        Error = analysis.Error,
        Truncated = analysis.Truncated,
        LineCount = analysis.LineCount,
        Issues = analysis.Issues.Select(i => new HistoryIssue
        {
            Kind = i.Kind,
            Severity = i.Severity,
            Type = i.Type,
            Description = i.Description,
            Line = i.Line,
            Snippet = i.Snippet?.ToList()
        }).ToList()
    };

    public FileAnalysis ToAnalysis() => new()
    {
        Path = Path,
        Score = Score,
        Summary = Summary ?? "",
        Error = Error,
        Truncated = Truncated,
        LineCount = LineCount,
        Issues = Issues.Select(i => new Issue(i.Severity, i.Type, i.Description, i.Line, i.Kind, i.Snippet)).ToList()
    };
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Folder { get; set; } = "";
    public string Model { get; set; } = "";
    public int? AverageScore { get; set; }
    public int TotalFiles { get; set; }
    public int TotalIssues { get; set; }
    public List<HistoryFile> Files { get; set; } = new();

    /// <summary>Runs saved by the first version only kept scores, so their findings cannot be reopened.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasDetails => Files.All(f => f.IssueCount == 0 || f.Issues.Count > 0);
}

public sealed class AppSettings
{
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string? LastModel { get; set; }
    public string? LastFolder { get; set; }
    public string Theme { get; set; } = "System";
    public int MaxFileChars { get; set; } = 12000;
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Scan level: false is the rules only, true also has the model judge what they found.</summary>
    public bool AdvancedScan { get; set; }

    /// <summary>How much the advanced scan reads: Quick, Balanced or Thorough.</summary>
    public string ScanDepth { get; set; } = "Balanced";

    /// <summary>Ask GitHub for the newest release number when the app starts. Nothing is downloaded.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>owner/name of the GitHub repository whose releases are checked.</summary>
    public string UpdateRepository { get; set; } = "Crane4/Lintel-AI";

    /// <summary>A version the user chose not to hear about again.</summary>
    public string? SkippedVersion { get; set; }
}

public static class Scoring
{
    public static int Compute(IEnumerable<Issue> issues) =>
        Math.Max(0, 100 - issues.Sum(i => i.Severity switch
        {
            Severity.High => 20,
            Severity.Medium => 10,
            _ => 5
        }));

    public static int? Average(IEnumerable<int?> scores)
    {
        var list = scores.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        return list.Count == 0 ? null : (int)Math.Round(list.Average(), MidpointRounding.AwayFromZero);
    }
}
