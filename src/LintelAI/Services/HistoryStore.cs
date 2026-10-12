using System.Text.Json;
using LintelAI.Models;

namespace LintelAI.Services;

public sealed class HistoryStore
{
    private const int MaxEntries = 30;

    public IReadOnlyList<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(AppPaths.HistoryFile))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(AppPaths.HistoryFile)) ?? new();
        }
        catch { }
        return new List<HistoryEntry>();
    }

    public HistoryEntry Append(string folder, string model, IReadOnlyList<FileAnalysis> results)
    {
        var entry = new HistoryEntry
        {
            Folder = folder,
            Model = model,
            AverageScore = Scoring.Average(results.Select(r => r.Score)),
            TotalFiles = results.Count,
            TotalIssues = results.Sum(r => r.Issues.Count),
            Files = results.Select(HistoryFile.From).ToList()
        };

        var all = Load().ToList();
        all.Insert(0, entry);
        if (all.Count > MaxEntries) all.RemoveRange(MaxEntries, all.Count - MaxEntries);

        try { AppPaths.WriteAtomic(AppPaths.HistoryFile, JsonSerializer.Serialize(all)); } catch { }
        return entry;
    }
}
