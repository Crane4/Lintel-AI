using LintelAI.Models;
using LintelAI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LintelAI.ViewModels;

public sealed partial class FileItemViewModel : ObservableObject
{
    public FileItemViewModel(string path, IChatHost host)
    {
        Path = path;
        var slash = path.LastIndexOf('/');
        Name = slash >= 0 ? path[(slash + 1)..] : path;
        Directory = slash >= 0 ? path[..slash] : "";
        Language = SyntaxHighlighter.LanguageFromPath(path);
        Chat = new ChatViewModel(this, host);
    }

    /// <summary>File extension used to pick a syntax highlighter, e.g. "cs" or "js".</summary>
    public string? Language { get; }

    public string Path { get; }
    public string Name { get; }
    public string Directory { get; }
    public bool HasDirectory => Directory.Length > 0;
    public ChatViewModel Chat { get; }

    public FileAnalysis? Analysis { get; private set; }

    [ObservableProperty] private FileState _state = FileState.Queued;
    [ObservableProperty] private int? _score;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _rawResponse;
    [ObservableProperty] private bool _truncated;
    [ObservableProperty] private int _lineCount;
    [ObservableProperty] private IReadOnlyList<IssueViewModel> _issues = Array.Empty<IssueViewModel>();

    public void Apply(FileAnalysis analysis)
    {
        Analysis = analysis;
        Score = analysis.Score;
        Summary = analysis.Summary;
        Error = analysis.Error;
        RawResponse = analysis.Error is null ? null : analysis.RawResponse;
        Truncated = analysis.Truncated;
        LineCount = analysis.LineCount;
        Issues = analysis.Issues.Select(i => new IssueViewModel(i, Language)).ToList();
        State = analysis.Error is null ? FileState.Done : FileState.Failed;
    }

    public ScoreBand Band => ScoreBands.Of(Score);
    public bool IsGood => Band == ScoreBand.Good;
    public bool IsFair => Band == ScoreBand.Fair;
    public bool IsPoor => Band == ScoreBand.Poor;

    public bool IsQueued => State == FileState.Queued;
    public bool IsRunning => State == FileState.Running;
    public bool IsDone => State == FileState.Done;
    public bool IsFailed => State == FileState.Failed;
    public bool IsSkipped => State == FileState.Skipped;

    public bool HasScore => Score.HasValue;
    public string ScoreText => Score?.ToString() ?? "–";
    public int IssueCount => Issues.Count;
    public bool HasIssues => Issues.Count > 0;
    public bool IsClean => State == FileState.Done && Issues.Count == 0;
    public bool HasHighIssue => Issues.Any(i => i.IsHigh);
    public bool HasKind(IssueKind kind) => Issues.Any(i => i.Kind == kind);
    public bool HasError => Error is not null;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public string IssueCountText => Issues.Count == 1 ? "1 issue" : $"{Issues.Count} issues";

    /// <summary>"1 vulnerability · 2 bugs · 1 code smell", skipping kinds that did not turn up.</summary>
    public string KindSummary
    {
        get
        {
            var parts = new List<string>();
            var vulnerabilities = Issues.Count(i => i.Kind == IssueKind.Vulnerability);
            var bugs = Issues.Count(i => i.Kind == IssueKind.Bug);
            var smells = Issues.Count(i => i.Kind == IssueKind.Smell);

            if (vulnerabilities > 0) parts.Add(vulnerabilities == 1 ? "1 vulnerability" : $"{vulnerabilities} vulnerabilities");
            if (bugs > 0) parts.Add(bugs == 1 ? "1 bug" : $"{bugs} bugs");
            if (smells > 0) parts.Add(smells == 1 ? "1 code smell" : $"{smells} code smells");
            return string.Join("  ·  ", parts);
        }
    }
    public string LineCountText => LineCount == 1 ? "1 line" : $"{LineCount:N0} lines";

    /// <summary>Sweep of the score ring in degrees; kept just under 360 so the arc never collapses.</summary>
    public double ScoreSweep => Math.Min(359.9, (Score ?? 0) * 3.6);

    partial void OnStateChanged(FileState value) => RaiseDerived();
    partial void OnScoreChanged(int? value) => RaiseDerived();
    partial void OnIssuesChanged(IReadOnlyList<IssueViewModel> value) => RaiseDerived();
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));
    partial void OnLineCountChanged(int value) => OnPropertyChanged(nameof(LineCountText));

    private void RaiseDerived()
    {
        foreach (var name in new[]
        {
            nameof(Band), nameof(IsGood), nameof(IsFair), nameof(IsPoor),
            nameof(IsQueued), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsSkipped),
            nameof(HasScore), nameof(ScoreText), nameof(ScoreSweep),
            nameof(IssueCount), nameof(HasIssues), nameof(IsClean), nameof(HasHighIssue), nameof(IssueCountText),
            nameof(KindSummary)
        })
        {
            OnPropertyChanged(name);
        }
    }
}
