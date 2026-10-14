using System.Text.Json;
using System.Text.RegularExpressions;
using LintelAI.Models;

namespace LintelAI.Services;

public sealed record ParsedAnalysis(string Summary, IReadOnlyList<Issue> Issues);

public static partial class JsonExtractor
{
    [GeneratedRegex(@"<think>[\s\S]*?(</think>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();

    public static string StripThinking(string text) => ThinkBlock().Replace(text, "").TrimStart();

    /// <summary>Models love to wrap JSON in prose or fences, so try the likely shapes in order.</summary>
    public static bool TryParseAnalysis(string? raw, out ParsedAnalysis result)
    {
        result = new ParsedAnalysis("", Array.Empty<Issue>());
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = StripThinking(raw).Trim();

        foreach (var candidate in Candidates(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (TryRead(doc.RootElement, out result)) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    /// <summary>Finds a single JSON object in a model reply, whatever prose or fences surround it.</summary>
    public static bool TryParseObject(string? raw, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        foreach (var candidate in Candidates(StripThinking(raw).Trim()))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                root = doc.RootElement.Clone();
                return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static IEnumerable<string> Candidates(string text)
    {
        yield return text;

        var fence = CodeFence().Match(text);
        if (fence.Success) yield return fence.Groups[1].Value.Trim();

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start) yield return text[start..(end + 1)];

        start = text.IndexOf('[');
        end = text.LastIndexOf(']');
        if (start >= 0 && end > start) yield return text[start..(end + 1)];
    }

    private static bool TryRead(JsonElement root, out ParsedAnalysis result)
    {
        result = new ParsedAnalysis("", Array.Empty<Issue>());
        var summary = "";
        JsonElement issuesElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                issuesElement = root;
                break;

            case JsonValueKind.Object:
                if (root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                    summary = s.GetString() ?? "";
                if (!root.TryGetProperty("issues", out issuesElement))
                {
                    // an object with a summary but no list still counts as "nothing found"
                    if (summary.Length == 0) return false;
                    result = new ParsedAnalysis(summary, Array.Empty<Issue>());
                    return true;
                }
                break;

            default:
                return false;
        }

        if (issuesElement.ValueKind != JsonValueKind.Array) return false;

        var issues = new List<Issue>();
        foreach (var item in issuesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var description = ReadString(item, "description") ?? ReadString(item, "message") ?? ReadString(item, "issue");
            if (string.IsNullOrWhiteSpace(description)) continue;

            var type = (ReadString(item, "type") ?? "general").Trim().ToLowerInvariant();

            issues.Add(new Issue(
                ParseSeverity(ReadString(item, "severity")),
                type,
                description.Trim(),
                ReadLine(item),
                ParseKind(ReadString(item, "kind"), type)));
        }

        result = new ParsedAnalysis(summary, issues);
        return true;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadLine(JsonElement obj)
    {
        if (!obj.TryGetProperty("line", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) && n > 0 => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) && n > 0 => n,
            _ => null
        };
    }

    /// <summary>
    /// Trusts the model's own "kind" when it gave one. Smaller models often forget the field,
    /// so fall back to what the finding's type implies.
    /// </summary>
    public static IssueKind ParseKind(string? kind, string? type)
    {
        var k = kind?.Trim().ToLowerInvariant() ?? "";
        if (k.Contains("vuln") || k.Contains("secur")) return IssueKind.Vulnerability;
        if (k.Contains("bug") || k.Contains("defect") || k.Contains("error")) return IssueKind.Bug;
        if (k.Contains("smell") || k.Contains("quality") || k.Contains("style") ||
            k.Contains("maint") || k.Contains("problem")) return IssueKind.Smell;

        var t = type?.Trim().ToLowerInvariant() ?? "";
        if (t.Contains("secur") || t.Contains("vuln") || t.Contains("inject") ||
            t.Contains("secret") || t.Contains("xss") || t.Contains("crypto")) return IssueKind.Vulnerability;
        if (t.Contains("memory") || t.Contains("leak") || t.Contains("loop") ||
            t.Contains("logic") || t.Contains("crash") || t.Contains("null") ||
            t.Contains("race") || t.Contains("bug")) return IssueKind.Bug;

        return IssueKind.Smell;
    }

    public static Severity ParseSeverity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" or "critical" or "severe" or "error" => Severity.High,
        "medium" or "moderate" or "warning" or "warn" => Severity.Medium,
        _ => Severity.Low
    };
}
