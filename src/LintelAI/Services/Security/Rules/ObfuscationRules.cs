using System.Text.RegularExpressions;
using LintelAI.Models;
using static LintelAI.Services.Security.SecurityRuleSupport;

namespace LintelAI.Services.Security.Rules;

/// <summary>Signs that code was deliberately made hard to read, which honest projects rarely do.</summary>
public sealed class ObfuscationRules : ISecurityRule
{
    private const int LongLine = 4000;
    private const int ObfuscatorNamesNeeded = 10;

    private static readonly Regex Base64Blob = Compile(@"[A-Za-z0-9+/]{300,}={0,2}");
    private static readonly Regex HexEscapes = Compile(@"(\\x[0-9a-f]{2}){24,}");
    private static readonly Regex CharCodes = Compile(@"fromCharCode\((\s*\d+\s*,){12,}|(chr\(\d+\)\s*\+\s*){8,}");
    private static readonly Regex ObfuscatorName = Compile(@"\b_0x[0-9a-f]{4,6}\b");
    private static readonly Regex DataUri = Compile(@"data:(image|font|application/(font|octet-stream))");

    /// <summary>Formats whose lines are long and encoded by nature.</summary>
    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".svg", ".map", ".csv", ".lock", ".css", ".xml", ".resx"
    };

    public IEnumerable<Issue> Evaluate(SecurityFile file, IReadOnlyList<string>? lines)
    {
        var issues = new List<Issue>();
        if (lines is null) return issues;

        var isData = DataExtensions.Contains(file.Extension) ||
                     file.Name.Contains(".min.", StringComparison.OrdinalIgnoreCase);
        var skipCode = IsDocument(file.Path);

        var hidden = false;
        var longLine = false;
        var obfuscatorLines = 0;
        int? firstObfuscator = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (!hidden && HasBidiControl(line))
            {
                hidden = true;
                issues.Add(Finding(Severity.High, "Hidden Unicode control characters",
                    "Contains invisible characters that reorder text, so the code can read differently than it runs (Trojan Source).",
                    i + 1));
            }

            if (skipCode || line.Length < 24) continue;

            if (!isData && !longLine && line.Length > LongLine)
            {
                longLine = true;
                issues.Add(Finding(Severity.Low, "Extremely long line",
                    $"A single line of {line.Length:N0} characters. Packed code on one line is hard to review.",
                    i + 1));
            }

            if (!DataUri.IsMatch(line) && TryMatch(Base64Blob, line) is { } blob)
            {
                issues.Add(Finding(Severity.Medium, "Large encoded blob",
                    $"A {blob.Length:N0}-character base64-looking string. Payloads are often hidden this way.",
                    i + 1));
            }

            if (TryMatch(HexEscapes, line) is not null)
                issues.Add(Finding(Severity.Medium, "Hex-escaped string",
                    "A long run of \\x escapes spelling out text, a common way to hide strings.", i + 1));

            if (TryMatch(CharCodes, line) is not null)
                issues.Add(Finding(Severity.Medium, "Character-code string building",
                    "Builds a string from numeric character codes to hide what it says.", i + 1));

            if (TryMatch(ObfuscatorName, line) is not null)
            {
                obfuscatorLines++;
                firstObfuscator ??= i + 1;
            }
        }

        if (obfuscatorLines >= ObfuscatorNamesNeeded)
        {
            issues.Add(Finding(Severity.Medium, "Obfuscated JavaScript",
                $"{obfuscatorLines} lines use _0x-style names, the signature of javascript-obfuscator.",
                firstObfuscator));
        }

        return issues;
    }

    private static bool HasBidiControl(string line)
    {
        foreach (var c in line)
        {
            if (c is >= '‪' and <= '‮' or >= '⁦' and <= '⁩') return true;
        }
        return false;
    }
}
