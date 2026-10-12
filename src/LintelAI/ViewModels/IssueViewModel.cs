using LintelAI.Models;
using Avalonia.Media;

namespace LintelAI.ViewModels;

public sealed class SnippetLineViewModel(SnippetLine line, string? language)
{
    public string Number { get; } = line.Number.ToString();
    public string Text { get; } = line.Text.Length == 0 ? " " : line.Text;
    public string? Language { get; } = language;
    public bool IsTarget { get; } = line.IsTarget;
}

public sealed class IssueViewModel
{
    public IssueViewModel(Issue issue, string? language)
    {
        Severity = issue.Severity;
        Kind = issue.Kind;
        Type = issue.Type;
        Description = issue.Description;
        Line = issue.Line;
        Snippet = issue.Snippet?.Select(l => new SnippetLineViewModel(l, language)).ToList() ?? new();
    }

    public Severity Severity { get; }
    public IssueKind Kind { get; }
    public string Type { get; }
    public string Description { get; }
    public int? Line { get; }
    public IReadOnlyList<SnippetLineViewModel> Snippet { get; }

    public bool IsHigh => Severity == Severity.High;
    public bool IsMedium => Severity == Severity.Medium;
    public bool IsLow => Severity == Severity.Low;
    public string SeverityLabel => Severity.ToString().ToUpperInvariant();
    public bool HasLine => Line.HasValue;
    public string LineText => Line is { } l ? $"Line {l}" : "";
    public bool HasSnippet => Snippet.Count > 0;

    public bool IsVulnerability => Kind == IssueKind.Vulnerability;
    public string KindLabel => Kind switch
    {
        IssueKind.Vulnerability => "Vulnerability",
        IssueKind.Bug => "Bug",
        _ => "Code smell"
    };

    public Geometry KindIcon => Kind switch
    {
        IssueKind.Vulnerability => Icons.Shield,
        IssueKind.Bug => Icons.Bug,
        _ => Icons.Wrench
    };
}
