using System.Text.Json;
using System.Text.RegularExpressions;
using LintelAI.Models;
using static LintelAI.Services.Security.SecurityRuleSupport;

namespace LintelAI.Services.Security.Rules;

/// <summary>
/// Files that make code run without anyone choosing to run it: install scripts, build events and
/// editor tasks. These are the usual way a downloaded project infects whoever merely opens it.
/// </summary>
public sealed class ManifestRules : ISecurityRule
{
    private static readonly string[] LifecycleScripts = ["preinstall", "install", "postinstall", "prepare"];

    private static readonly Regex NetworkOrShell = Compile(
        @"\b(curl|wget|powershell|pwsh|certutil|bitsadmin|mshta|ncat?)\b|(ba)?sh\s+-c|node\s+-e|python\d?\s+-c|https?://|base64|\beval\b");

    private static readonly Regex Harmless = Compile(
        @"^\s*(husky(\s+install)?|tsc(\s.*)?|npm run build|patch-package|node-gyp(-build)?(\s.*)?|is-ci.*)\s*$");

    private static readonly Regex FolderOpen = Compile("\"runOn\"\\s*:\\s*\"folderOpen\"");
    private static readonly Regex CmdClass = Compile(@"\bcmdclass\b");
    private static readonly Regex PythonExec = Compile(@"os\.system|subprocess|urlopen|urllib|requests\.|\bexec\(|\beval\(");
    private static readonly Regex MsBuildCommand = Compile(@"<Exec\b|\bCommand\s*=|<(Pre|Post)BuildEvent>");

    private static readonly HashSet<string> MsBuildExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".targets", ".props"
    };

    public IEnumerable<Issue> Evaluate(SecurityFile file, IReadOnlyList<string>? lines)
    {
        if (lines is null) return [];

        var name = file.Name;

        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase)) return PackageJson(lines);
        if (name.Equals("setup.py", StringComparison.OrdinalIgnoreCase)) return SetupPy(lines);
        if (name.Equals("tasks.json", StringComparison.OrdinalIgnoreCase) &&
            file.Path.Contains(".vscode/", StringComparison.OrdinalIgnoreCase)) return VsCodeTasks(lines);
        if (MsBuildExtensions.Contains(file.Extension)) return MsBuild(lines);

        return [];
    }

    private static List<Issue> PackageJson(IReadOnlyList<string> lines)
    {
        var issues = new List<Issue>();

        try
        {
            using var doc = JsonDocument.Parse(string.Join('\n', lines),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
                return issues;

            foreach (var name in LifecycleScripts)
            {
                if (!scripts.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) continue;

                var command = value.GetString() ?? "";
                if (Harmless.IsMatch(command)) continue;

                var line = LineContaining(lines, $"\"{name}\"");
                if (NetworkOrShell.IsMatch(command))
                {
                    issues.Add(Finding(Severity.High, "Suspicious install script",
                        $"\"{name}\" runs automatically on npm install and calls out to the network or a shell: {Excerpt(command)}",
                        line));
                }
                else
                {
                    issues.Add(Finding(Severity.Medium, "Install script",
                        $"\"{name}\" runs automatically on npm install: {Excerpt(command)}", line));
                }
            }
        }
        catch (JsonException)
        {
            // not valid JSON; nothing to say about its scripts
        }

        return issues;
    }

    private static List<Issue> SetupPy(IReadOnlyList<string> lines)
    {
        var issues = new List<Issue>();
        var cmdClassLine = IndexOf(lines, CmdClass);
        if (cmdClassLine is null) return issues;

        if (IndexOf(lines, PythonExec) is not null)
        {
            issues.Add(Finding(Severity.High, "Custom install command",
                "setup.py overrides the install step and also runs system commands or network calls, so code executes on pip install.",
                cmdClassLine + 1));
        }

        return issues;
    }

    private static List<Issue> VsCodeTasks(IReadOnlyList<string> lines)
    {
        var issues = new List<Issue>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!FolderOpen.IsMatch(lines[i])) continue;
            issues.Add(Finding(Severity.High, "Task runs when the folder is opened",
                "VS Code will run this task as soon as the folder is opened, without being asked.", i + 1));
        }
        return issues;
    }

    private static List<Issue> MsBuild(IReadOnlyList<string> lines)
    {
        var issues = new List<Issue>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!MsBuildCommand.IsMatch(line)) continue;

            if (NetworkOrShell.IsMatch(line) || line.Contains("cmd /c", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Finding(Severity.High, "Suspicious build step",
                    $"A build event runs a shell or network command during the build: {Excerpt(line)}", i + 1));
            }
            else
            {
                issues.Add(Finding(Severity.Medium, "Build step",
                    $"A build event runs a command whenever the project is built: {Excerpt(line)}", i + 1));
            }
        }
        return issues;
    }

    private static int? LineContaining(IReadOnlyList<string> lines, string text)
    {
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Contains(text, StringComparison.Ordinal)) return i + 1;
        return null;
    }

    private static int? IndexOf(IReadOnlyList<string> lines, Regex regex)
    {
        for (var i = 0; i < lines.Count; i++)
            if (regex.IsMatch(lines[i])) return i;
        return null;
    }
}
