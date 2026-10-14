using System.Text.RegularExpressions;
using LintelAI.Models;
using static LintelAI.Services.Security.SecurityRuleSupport;

namespace LintelAI.Services.Security.Rules;

/// <summary>What a file is called, and what it really is, without reading its content.</summary>
public sealed class FileNameRules : ISecurityRule
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".msi", ".com", ".pif", ".sys", ".ocx",
        ".jar", ".node", ".so", ".dylib", ".apk"
    };

    private static readonly HashSet<string> ScriptHostExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vbs", ".vbe", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".cpl", ".reg"
    };

    private static readonly Regex DoubleExtension = Compile(
        @"\.(pdf|docx?|xlsx?|pptx?|txt|jpe?g|png|gif|mp[34]|zip|rar)\.(exe|scr|com|bat|cmd|ps1|vbs|js|jar|lnk|hta)$");

    public IEnumerable<Issue> Evaluate(SecurityFile file, IReadOnlyList<string>? lines)
    {
        var issues = new List<Issue>();
        var name = file.Name;
        var extension = file.Extension;

        if (name.Contains('‮'))
        {
            issues.Add(Finding(Severity.High, "Right-to-left override in file name",
                "The name contains a character that flips how it is displayed, used to make an .exe look like a document.",
                null));
        }

        if (DoubleExtension.IsMatch(name))
        {
            issues.Add(Finding(Severity.High, "Double file extension",
                $"\"{name}\" looks like a document or image but ends in a runnable extension.", null));
        }

        if (file.HasExecutableHeader)
        {
            if (extension.Length == 0 || ExecutableExtensions.Contains(extension))
            {
                issues.Add(Finding(Severity.Medium, "Compiled binary in the project",
                    "A compiled executable shipped with the source. It cannot be reviewed as code.", null));
            }
            else
            {
                issues.Add(Finding(Severity.High, "Executable disguised as " + extension,
                    $"The file is named \"{extension}\" but its header says it is a program.", null));
            }
        }
        else if (file.IsBinary && ExecutableExtensions.Contains(extension))
        {
            issues.Add(Finding(Severity.Medium, "Compiled binary in the project",
                "A compiled library or package shipped with the source. It cannot be reviewed as code.", null));
        }

        if (ScriptHostExtensions.Contains(extension))
        {
            issues.Add(Finding(Severity.Medium, "Windows script or shortcut file",
                $"\"{extension}\" files run through Windows Script Host or as shortcuts and are a common malware carrier.",
                null));
        }

        return issues;
    }
}
