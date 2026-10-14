using System.Text.Json;
using LintelAI.Models;

namespace LintelAI.Services.Security.Advanced;

public sealed record SecurityProgress(int Done, int Total, string Message);

/// <summary>
/// The "Advanced" scan: the normal rules first, then a model that judges what they found.
///
/// The model is a reviewer of the rules' alerts, not a free-ranging detective, because small
/// models invent problems when asked to "find issues". So:
/// <list type="number">
/// <item>Every alert that could change the verdict (medium or high) is judged with a narrow,
///   rule-specific question, and the answer is checked against the code (see <see cref="AiJudge"/>).</item>
/// <item>The model may clear a false alarm or confirm a real one. It cannot make anything worse.</item>
/// <item>It may also propose findings the rules missed, in a few risky files, but each one needs
///   a quote that exists in the file and two independent "harmful" verdicts, and it is capped at
///   medium, so the model alone can never turn a project "Dangerous".</item>
/// <item>If the model fails or goes away, the rule results stand.</item>
/// </list>
/// </summary>
public sealed class AdvancedSecurityScanner(SecurityScanner baseScanner)
{
    public const int MaxJudged = 60;
    private const int MaxConsecutiveFailures = 3;

    private static readonly HashSet<string> ScriptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "setup.py"
    };

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".bat", ".cmd", ".vbs", ".sh"
    };

    public AdvancedSecurityScanner() : this(new SecurityScanner())
    {
    }

    public async Task<SecurityReport> ScanAsync(
        string root,
        ISecurityModel model,
        int maxChars = 12000,
        IProgress<SecurityProgress>? progress = null,
        CancellationToken ct = default,
        AiDepth depth = AiDepth.Balanced)
    {
        var plan = DepthPlan.For(depth);

        progress?.Report(new SecurityProgress(0, 0, "Running the rule scan…"));
        var report = await baseScanner.ScanAsync(root, null, ct).ConfigureAwait(false);

        var collected = await Task.Run(() => new SecurityFileCollector().Collect(root, ct), ct).ConfigureAwait(false);
        var hint = SecurityPrompts.ProjectHint(collected.Files.Select(f => f.Path).ToList());

        var run = new Run(root, model, new AiJudge(model), hint, maxChars, plan, depth, progress);
        foreach (var file in report.Files)
            run.Issues[file.Path] = file.Issues.ToList();

        await run.JudgeAlertsAsync(report, ct).ConfigureAwait(false);

        if (run.StoppedBecause is null && plan.Discover)
            await run.DiscoverAsync(report, collected, ct).ConfigureAwait(false);

        // Rebuild the per-file results from what the AI stages left behind.
        var files = new List<FileAnalysis>();
        foreach (var (path, issues) in run.Issues)
        {
            if (issues.Count == 0) continue;

            var ordered = issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Line ?? int.MaxValue).ToList();
            var original = report.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            var lines = await run.LinesAsync(path, ct).ConfigureAwait(false);

            files.Add(new FileAnalysis
            {
                Path = path,
                Issues = ordered
                    .Select(i => i.Snippet is null && lines is not null
                        ? i with { Snippet = FileAnalyzer.BuildSnippet(lines, i.Line) }
                        : i)
                    .ToList(),
                Summary = $"{ordered.Count} finding(s).",
                LineCount = original?.LineCount ?? lines?.Count ?? 0
            });
        }

        var verdict = SecurityReport.Assess(files.SelectMany(f => f.Issues));
        return report with { Files = files, Verdict = verdict, AiSummary = run.Summary() };
    }

    /// <summary>The mutable state of one advanced scan.</summary>
    private sealed class Run(
        string root,
        ISecurityModel model,
        AiJudge judge,
        string hint,
        int maxChars,
        DepthPlan plan,
        AiDepth depth,
        IProgress<SecurityProgress>? progress)
    {
        private readonly Dictionary<string, IReadOnlyList<string>?> _lines = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, JudgeOutcome> _verdicts = new();
        private int _failures;

        public Dictionary<string, List<Issue>> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? StoppedBecause { get; private set; }

        private int _judged, _cleared, _confirmed, _found;

        /// <summary>High alerts, which the model is not allowed to touch.</summary>
        public int KeptHigh { get; private set; }

        public async Task<IReadOnlyList<string>?> LinesAsync(string path, CancellationToken ct)
        {
            if (_lines.TryGetValue(path, out var cached)) return cached;

            var lines = await FileText.ReadLinesAsync(Path.Combine(root, path), ct).ConfigureAwait(false);
            _lines[path] = lines;
            return lines;
        }

        public string Summary()
        {
            var text = $"AI review ({depth}): {_judged} medium alert(s) judged · {_cleared} cleared as harmless · {_confirmed} confirmed · {_found} new";
            if (KeptHigh > 0) text += $" · {KeptHigh} high alert(s) kept as found (the model can't clear those)";
            return StoppedBecause is null ? text : $"{text} · stopped early: {StoppedBecause}";
        }

        /// <summary>Records a model failure; returns true when the AI stages should give up.</summary>
        private bool Failed(OllamaException ex)
        {
            if (++_failures < MaxConsecutiveFailures) return false;

            StoppedBecause = ex.Message;
            return true;
        }

        // ── Stage 1: judge the rules' alerts ──

        public async Task JudgeAlertsAsync(SecurityReport report, CancellationToken ct)
        {
            // The model only ever sees medium alerts, and only in files with no high alert.
            // A high alert is kept as the rules found it, and anything else in the same file is
            // not worth arguing about: a file that steals browser data does not get the benefit
            // of the doubt on its other behaviour.
            var alerts = report.Files
                .Where(f => f.Issues.All(i => i.Severity < Severity.High))
                .SelectMany(f => f.Issues.Select(i => (f.Path, Issue: i)))
                .Where(x => x.Issue.Severity == Severity.Medium && x.Issue.Line is not null && AiJudge.CanBeCleared(x.Issue.Type))
                .Take(MaxJudged)
                .ToList();

            KeptHigh = report.Files.SelectMany(f => f.Issues).Count(i => i.Severity == Severity.High);

            for (var n = 0; n < alerts.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var (path, issue) = alerts[n];

                progress?.Report(new SecurityProgress(n, alerts.Count, $"AI is reviewing alert {n + 1} of {alerts.Count}: {path}"));

                var lines = await LinesAsync(path, ct).ConfigureAwait(false);
                if (lines is null || issue.Line is not { } line || line > lines.Count) continue;

                // The same alert on the same code gets the same answer, so ask once.
                var key = $"{issue.Type}|{issue.Severity}|{lines[line - 1].Trim()}";

                if (!_verdicts.TryGetValue(key, out var outcome))
                {
                    try
                    {
                        outcome = await judge.JudgeAsync(
                            new JudgeCandidate(path, issue.Type, DescriptionOf(issue), issue.Severity, line, lines, hint), ct)
                            .ConfigureAwait(false);
                        _failures = 0;
                    }
                    catch (OllamaException ex)
                    {
                        if (Failed(ex)) return;
                        continue;
                    }

                    _verdicts[key] = outcome;
                }

                _judged++;
                var list = Issues[path];
                var at = list.IndexOf(issue);
                if (at < 0) continue;

                switch (outcome.Decision)
                {
                    case JudgeDecision.Clear:
                        _cleared++;
                        list[at] = issue with
                        {
                            Severity = Severity.Low,
                            Description = $"{issue.Description}\nAI review: judged harmless (was {issue.Severity}). {outcome.Reason} Evidence: \"{outcome.Evidence}\""
                        };
                        break;

                    case JudgeDecision.Confirm:
                        _confirmed++;
                        list[at] = issue with
                        {
                            Description = $"{issue.Description}\nAI opinion (it can be wrong): this looks harmful. {outcome.Reason} Evidence: \"{outcome.Evidence}\""
                        };
                        break;

                    default:
                        if (outcome.Note is not null)
                            list[at] = issue with { Description = $"{issue.Description}\nAI review: {outcome.Note}" };
                        break;
                }
            }
        }

        // The rule's own sentence, without anything a previous stage appended.
        private static string DescriptionOf(Issue issue)
        {
            var cut = issue.Description.IndexOf(" Matched:", StringComparison.Ordinal);
            return cut > 0 ? issue.Description[..cut] : issue.Description;
        }

        // ── Stage 2: look for what the rules missed, in the files most worth a second look ──

        public async Task DiscoverAsync(SecurityReport report, SecurityCollection collected, CancellationToken ct)
        {
            // A file with a high alert is already as bad as it gets; the model has nothing to add
            // there, and reading it costs a minute on a CPU. Look at the rest.
            var flagged = report.Files
                .Where(f => f.Issues.All(i => i.Severity < Severity.High))
                .OrderByDescending(f => f.Issues.Max(i => i.Severity))
                .Select(f => f.Path);

            var highFiles = report.Files
                .Where(f => f.Issues.Any(i => i.Severity == Severity.High))
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var scripts = collected.Files
                .Where(f => !f.IsBinary && !f.TooLarge && !highFiles.Contains(f.Path) &&
                            (ScriptNames.Contains(f.Name) || ScriptExtensions.Contains(f.Extension)))
                .Select(f => f.Path);

            var targets = flagged.Concat(scripts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !IsDocument(p))
                .Take(plan.Files)
                .ToList();

            for (var n = 0; n < targets.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var path = targets[n];

                progress?.Report(new SecurityProgress(n, targets.Count, $"AI is reading {path} ({n + 1} of {targets.Count})"));

                var lines = await LinesAsync(path, ct).ConfigureAwait(false);
                if (lines is null) continue;

                var proposals = await ProposeAsync(path, lines, ct).ConfigureAwait(false);
                if (StoppedBecause is not null) return;

                foreach (var p in proposals)
                {
                    if (!await VerifyProposalAsync(path, lines, p, ct).ConfigureAwait(false)) continue;
                    if (StoppedBecause is not null) return;
                }
            }
        }

        private sealed record Proposal(int Line, string Category, string Evidence, string Reason);

        private async Task<List<Proposal>> ProposeAsync(string path, IReadOnlyList<string> lines, CancellationToken ct)
        {
            var result = new List<Proposal>();
            var start = 0;

            for (var part = 0; part < plan.Parts && start < lines.Count; part++)
            {
                var (text, end) = Prompts.NumberRange(lines, start, maxChars);

                try
                {
                    var raw = await model.AskAsync(
                        SecurityPrompts.Discover(path, text, hint), SecurityPrompts.DiscoverSchema, ct).ConfigureAwait(false);
                    _failures = 0;

                    if (JsonExtractor.TryParseObject(raw, out var root) &&
                        root.TryGetProperty("findings", out var findings) &&
                        findings.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in findings.EnumerateArray().Take(5))
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;

                            var category = Str(item, "category")?.Trim().ToLowerInvariant() ?? "";
                            var evidence = EvidenceWindow.CleanEvidence(Str(item, "evidence") ?? "");
                            var reason = (Str(item, "reason") ?? "").Trim();
                            var claimed = item.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : 0;

                            if (!SecurityPrompts.Categories.Contains(category) || evidence.Length == 0) continue;
                            result.Add(new Proposal(claimed, category, evidence, reason));
                        }
                    }
                }
                catch (OllamaException ex)
                {
                    if (Failed(ex)) return result;
                }

                start = Math.Max(end - 3, start + 1);
                if (end >= lines.Count) break;
            }

            return result;
        }

        /// <summary>Returns true when the proposal was real, verified and added.</summary>
        private async Task<bool> VerifyProposalAsync(string path, IReadOnlyList<string> lines, Proposal p, CancellationToken ct)
        {
            // 1. The quote has to exist in the file, and we find the true line ourselves.
            var line = LocateEvidence(lines, p.Evidence, p.Line);
            if (line is null) return false;

            // 2. Not a comment, not something the rules already reported on that line.
            if (SecurityRuleSupport.IsCommentLine(lines[line.Value - 1])) return false;

            // A file the rules had nothing to say about has no list yet.
            if (!Issues.TryGetValue(path, out var existing)) existing = Issues[path] = [];
            if (existing.Any(i => i.Line == line)) return false;

            // 3. Two differently framed checks must both call it harmful, with quotes that exist.
            var label = SecurityPrompts.CategoryLabel(p.Category);
            try
            {
                var outcome = await judge.ConfirmHarmAsync(
                    new JudgeCandidate(path, "AI-suggested: " + label, p.Reason, Severity.Medium, line.Value, lines, hint), ct)
                    .ConfigureAwait(false);
                _failures = 0;

                if (outcome.Decision != JudgeDecision.Confirm) return false;

                existing.Add(new Issue(
                    Severity.Medium,
                    "AI-suggested: " + label,
                    $"{outcome.Reason}\nAI review: found by the model, not by the rules. It was checked twice and the quoted line exists in the file. Evidence: \"{outcome.Evidence}\"",
                    line,
                    IssueKind.Vulnerability));
                _found++;
                return true;
            }
            catch (OllamaException ex)
            {
                Failed(ex);
                return false;
            }
        }

        /// <summary>Finds where a quote really is: near the claimed line first, then anywhere in the file.</summary>
        private static int? LocateEvidence(IReadOnlyList<string> lines, string evidence, int claimed)
        {
            var needle = Squash(evidence);
            if (needle.Length < 5) return null;

            if (claimed > 0)
            {
                for (var offset = 0; offset <= 5; offset++)
                {
                    foreach (var candidate in new[] { claimed - offset, claimed + offset })
                    {
                        if (candidate >= 1 && candidate <= lines.Count &&
                            Squash(lines[candidate - 1]).Contains(needle, StringComparison.Ordinal))
                            return candidate;
                    }
                }
            }

            int? only = null;
            for (var i = 0; i < lines.Count; i++)
            {
                if (!Squash(lines[i]).Contains(needle, StringComparison.Ordinal)) continue;
                if (only is not null) return null; // ambiguous: refuse to guess
                only = i + 1;
            }
            return only;
        }

        private static string Squash(string text) =>
            string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        private static bool IsDocument(string path) =>
            Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".txt" or ".rst" or ".adoc";

        private static string? Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
