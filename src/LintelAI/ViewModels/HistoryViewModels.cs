using LintelAI.Models;

namespace LintelAI.ViewModels;

public sealed class HistoryEntryViewModel(HistoryEntry entry)
{
    public HistoryEntry Entry { get; } = entry;

    public string DateText => Entry.Timestamp.LocalDateTime.ToString("d MMM yyyy · HH:mm");
    public string Folder => Entry.Folder;
    public string FolderName
    {
        get
        {
            var trimmed = Entry.Folder.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public string Model => Entry.Model;
    public string ScoreText => Entry.AverageScore?.ToString() ?? "–";
    public ScoreBand Band => ScoreBands.Of(Entry.AverageScore);
    public bool IsGood => Band == ScoreBand.Good;
    public bool IsFair => Band == ScoreBand.Fair;
    public bool IsPoor => Band == ScoreBand.Poor;
    public string MetaText => $"{Entry.TotalFiles} files · {Entry.TotalIssues} issues · {Entry.Model}";
    public bool HasDetails => Entry.HasDetails;
    public bool LacksDetails => !Entry.HasDetails;
}

public sealed class CompareRowViewModel(string path, int? then, int? now)
{
    public string Path { get; } = path;
    public string ThenText { get; } = then?.ToString() ?? "–";
    public string NowText { get; } = now?.ToString() ?? "–";

    private int? Delta => then.HasValue && now.HasValue ? now - then : null;
    public bool IsUp => Delta > 0;
    public bool IsDown => Delta < 0;
    public string DeltaText => Delta switch
    {
        null => "–",
        0 => "0",
        > 0 => $"+{Delta}",
        _ => $"−{Math.Abs(Delta.Value)}"
    };
}
