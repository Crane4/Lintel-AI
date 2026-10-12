using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LintelAI.Models;

namespace LintelAI.Services;

public static class ReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string ToJson(string folder, string model, IReadOnlyList<FileAnalysis> results)
    {
        var report = new
        {
            generatedAt = DateTimeOffset.Now,
            folder,
            model,
            summary = new
            {
                totalFiles = results.Count,
                totalIssues = results.Sum(r => r.Issues.Count),
                averageScore = Scoring.Average(results.Select(r => r.Score))
            },
            files = results.Select(r => new
            {
                file = r.Path,
                score = r.Score,
                summary = r.Summary,
                error = r.Error,
                issues = r.Issues.Select(i => new
                {
                    kind = i.Kind,
                    severity = i.Severity,
                    type = i.Type,
                    description = i.Description,
                    line = i.Line
                })
            })
        };
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToMarkdown(string folder, string model, IReadOnlyList<FileAnalysis> results)
    {
        var sb = new StringBuilder();
        var average = Scoring.Average(results.Select(r => r.Score));

        sb.AppendLine("# Project analysis");
        sb.AppendLine();
        sb.AppendLine($"- **Folder:** `{folder}`");
        sb.AppendLine($"- **Model:** `{model}`");
        sb.AppendLine($"- **Generated:** {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Files:** {results.Count} · **Issues:** {results.Sum(r => r.Issues.Count)} · **Average score:** {average?.ToString() ?? "n/a"}");
        sb.AppendLine();
        sb.AppendLine("| File | Score | Issues |");
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var r in results.OrderBy(r => r.Score ?? 101))
            sb.AppendLine($"| `{r.Path}` | {r.Score?.ToString() ?? "–"} | {r.Issues.Count} |");

        foreach (var r in results.Where(r => r.Issues.Count > 0 || r.Error is not null).OrderBy(r => r.Score ?? 101))
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Path}");
            if (r.Error is not null) { sb.AppendLine($"> {r.Error}"); continue; }
            if (!string.IsNullOrWhiteSpace(r.Summary)) sb.AppendLine($"_{r.Summary}_");
            sb.AppendLine();
            foreach (var issue in r.Issues.OrderByDescending(i => i.Severity))
            {
                var where = issue.Line is { } l ? $" (line {l})" : "";
                var kind = issue.Kind == IssueKind.Smell ? "code smell" : issue.Kind.ToString().ToLowerInvariant();
                sb.AppendLine($"- **{issue.Severity.ToString().ToUpperInvariant()}** · {kind} · {issue.Type}{where}: {issue.Description}");
            }
        }
        return sb.ToString();
    }
}
