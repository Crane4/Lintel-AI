using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LintelAI.Models;

namespace LintelAI.Services.Security.Advanced;

public enum AiVerdict { Benign, Malicious, Unclear }

/// <param name="EvidenceFound">Whether the quoted evidence really appears in the code that was shown.</param>
public sealed record AiVote(AiVerdict Verdict, string Evidence, string Reason, bool EvidenceFound);

public enum JudgeDecision
{
    /// <summary>Leave the finding exactly as the rules reported it.</summary>
    Keep,

    /// <summary>The model gave verified reasons that this is a false alarm.</summary>
    Clear,

    /// <summary>The model agrees, with verified evidence, that this is harmful.</summary>
    Confirm
}

public sealed record JudgeOutcome(JudgeDecision Decision, string Reason, string Evidence, string? Note = null);

public sealed record JudgeCandidate(
    string Path,
    string Type,
    string Description,
    Severity Severity,
    int Line,
    IReadOnlyList<string> Lines,
    string ProjectHint);

/// <summary>
/// Decides whether a rule-based alert is real. Everything the model says is checked before it can
/// change anything:
/// <list type="bullet">
/// <item>its quoted evidence must appear, word for word, in the code it was shown;</item>
/// <item>it can lower a finding or confirm one, but it can never raise severity;</item>
/// <item>clearing a high-severity finding needs two independently framed answers that agree;</item>
/// <item>"unclear", a bad quote or a malformed reply all mean "leave the rule's verdict alone".</item>
/// </list>
/// </summary>
public sealed class AiJudge(ISecurityModel model)
{
    /// <summary>
    /// Alerts that have almost no innocent explanation, or that are about a file's name or bytes
    /// rather than its code. The model never gets to clear these.
    /// </summary>
    private static readonly HashSet<string> NeverCleared = new(StringComparer.OrdinalIgnoreCase)
    {
        "Hidden Unicode control characters",
        "Right-to-left override in file name",
        "Double file extension",
        "Compiled binary in the project",
        "Windows script or shortcut file",
        "Reverse shell",
        "Crypto miner",
        "Destructive command",
        "Security software tampering"
    };

    public static bool CanBeCleared(string type) =>
        !NeverCleared.Contains(type) && !type.StartsWith("Executable disguised", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Judges one alert. Callers must not pass high-severity alerts: a model that wrongly calls
    /// real malware harmless is the worst failure a security scan can have, and a small model
    /// does exactly that (measured), so a high alert is never put in the model's hands.
    /// </summary>
    public async Task<JudgeOutcome> JudgeAsync(JudgeCandidate candidate, CancellationToken ct)
    {
        var first = await VoteAsync(candidate, adversarial: false, ct).ConfigureAwait(false);

        if (first.Verdict == AiVerdict.Malicious && first.EvidenceFound)
            return new JudgeOutcome(JudgeDecision.Confirm, first.Reason, first.Evidence);

        if (first.Verdict != AiVerdict.Benign || !first.EvidenceFound)
            return new JudgeOutcome(JudgeDecision.Keep, "", "");

        // The model says harmless. One opinion is not enough to lower an alert: ask again with the
        // question turned around, and only clear it if both agree.
        var second = await VoteAsync(candidate, adversarial: true, ct).ConfigureAwait(false);
        if (second.Verdict != AiVerdict.Benign || !second.EvidenceFound)
        {
            return new JudgeOutcome(JudgeDecision.Keep, "", "",
                "A first check judged this harmless, a second did not agree, so it was left as found.");
        }

        return new JudgeOutcome(JudgeDecision.Clear, first.Reason, first.Evidence);
    }

    /// <summary>Used for findings the model proposed itself: both framings must call it harmful.</summary>
    public async Task<JudgeOutcome> ConfirmHarmAsync(JudgeCandidate candidate, CancellationToken ct)
    {
        var first = await VoteAsync(candidate, adversarial: false, ct).ConfigureAwait(false);
        if (first.Verdict != AiVerdict.Malicious || !first.EvidenceFound)
            return new JudgeOutcome(JudgeDecision.Keep, "", "");

        var second = await VoteAsync(candidate, adversarial: true, ct).ConfigureAwait(false);
        if (second.Verdict != AiVerdict.Malicious || !second.EvidenceFound)
            return new JudgeOutcome(JudgeDecision.Keep, "", "");

        return new JudgeOutcome(JudgeDecision.Confirm, first.Reason, first.Evidence);
    }

    private async Task<AiVote> VoteAsync(JudgeCandidate c, bool adversarial, CancellationToken ct)
    {
        var window = EvidenceWindow.Build(c.Lines, c.Line);
        var prompt = SecurityPrompts.Judge(
            c.Path, c.Type, SecurityPrompts.RubricFor(c.Type, c.Description),
            window.Text, c.ProjectHint, adversarial);

        var raw = await model.AskAsync(prompt, SecurityPrompts.JudgeSchema, ct).ConfigureAwait(false);

        var unclear = new AiVote(AiVerdict.Unclear, "", "", false);
        if (!JsonExtractor.TryParseObject(raw, out var root)) return unclear;

        var verdict = ReadString(root, "verdict")?.Trim().ToLowerInvariant() switch
        {
            "benign" => AiVerdict.Benign,
            "malicious" => AiVerdict.Malicious,
            _ => AiVerdict.Unclear
        };

        var evidence = EvidenceWindow.CleanEvidence(ReadString(root, "evidence") ?? "");
        var reason = (ReadString(root, "reason") ?? "").Trim();
        if (reason.Length > 400) reason = reason[..400] + "…";

        // A verdict nobody can check is worth nothing: no real quote, or no real reason, means "unclear".
        var found = reason.Length >= 8 && window.Contains(evidence);
        return new AiVote(found ? verdict : AiVerdict.Unclear, evidence, reason, found);
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>The few lines around a finding as the model sees them, and the means to check its quotes.</summary>
public sealed partial class EvidenceWindow
{
    private const int MaxLineChars = 300;
    private const int MaxChars = 3500;
    private const int MinEvidenceChars = 5;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?m)^\s*(>>>)?\s*\d+:\s?")]
    private static partial Regex LineNumberPrefix();

    private readonly string _searchable;

    private EvidenceWindow(string text, string searchable)
    {
        Text = text;
        _searchable = searchable;
    }

    public string Text { get; }

    /// <param name="line">1-based line of the finding.</param>
    public static EvidenceWindow Build(IReadOnlyList<string> lines, int line, int radius = 12)
    {
        var target = Math.Clamp(line, 1, Math.Max(1, lines.Count)) - 1;

        while (true)
        {
            var from = Math.Max(0, target - radius);
            var to = Math.Min(lines.Count - 1, target + radius);
            var width = (to + 1).ToString().Length;

            var shown = new StringBuilder();
            var plain = new StringBuilder();

            for (var i = from; i <= to; i++)
            {
                var text = Clip(lines[i]);
                if (shown.Length > 0) shown.Append('\n');
                shown.Append(i == target ? ">>> " : "    ")
                     .Append((i + 1).ToString().PadLeft(width)).Append(": ").Append(text);
                plain.Append(text).Append('\n');
            }

            if (shown.Length <= MaxChars || radius <= 1)
                return new EvidenceWindow(shown.ToString(), Normalize(plain.ToString()));

            radius = Math.Max(1, radius / 2);
        }
    }

    /// <summary>Builds a window over the whole of a file's lines, used to check a quote against it.</summary>
    public static EvidenceWindow Whole(IReadOnlyList<string> lines) =>
        new("", Normalize(string.Join('\n', lines.Select(Clip))));

    public bool Contains(string evidence)
    {
        var needle = Normalize(evidence);
        return needle.Length >= MinEvidenceChars && _searchable.Contains(needle, StringComparison.Ordinal);
    }

    /// <summary>Models like to quote with line numbers, ">>>" markers, backticks or quotes around the text.</summary>
    public static string CleanEvidence(string evidence)
    {
        var text = LineNumberPrefix().Replace(evidence, "").Trim();
        return text.Trim('`', '"', '\'').Trim();
    }

    private static string Clip(string line) => line.Length <= MaxLineChars ? line : line[..MaxLineChars];

    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim().ToLowerInvariant();
}
