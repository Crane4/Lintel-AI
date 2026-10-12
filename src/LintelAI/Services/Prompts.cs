using System.Text;
using LintelAI.Models;

namespace LintelAI.Services;

public static class Prompts
{
    /// <param name="part">1-based index when a long file is reviewed in several pieces.</param>
    public static string CodeReview(
        string path,
        string numberedContent,
        ProjectContext? context = null,
        int part = 1,
        int parts = 1,
        int fromLine = 1,
        int toLine = 0)
    {
        var scope = parts > 1
            ? $"This is part {part} of {parts} of the file (lines {fromLine}-{toLine}). Report only problems inside these lines.\n"
            : "";

        return $$"""
            You are a strict code review tool. Review the file below and report every real problem you find.

            File: {{path}}
            {{scope}}Every line starts with its line number and a colon. Use those numbers for "line".
            {{ProjectBlock(context)}}
            Look for:
            - Memory leaks: timers or listeners that are never cleared or removed, collections that grow forever, globals that accumulate data, resources that are never disposed
            - Infinite loops: loops with no reachable exit, or an exit condition that can never be true
            - Dead code: unreachable statements, unused variables, functions that are never called
            - Useless logic: conditions that always evaluate the same way, redundant nested checks
            - Bad practices: blocking the main thread, leftover debug logging, swallowed errors, mutated globals
            - Security: hard-coded secrets or credentials, eval or dynamic code execution, SQL/command/HTML injection, path traversal, unsafe deserialization, weak cryptography, unvalidated input

            Give every finding a "kind":
            - "bug": the code misbehaves at runtime (leaks, hangs, wrong results, crashes)
            - "vulnerability": an attacker could exploit it
            - "smell": it works, but it is dead, noisy, redundant or hard to maintain

            Only report problems that are really in the code. Do not invent issues. A clean file has an empty list.

            --- BEGIN FILE ---
            {{numberedContent}}
            --- END FILE ---

            Reply with one JSON object and nothing else, in exactly this shape:
            {"issues":[{"kind":"bug|vulnerability|smell","severity":"high|medium|low","type":"memory|loop|dead-code|logic|practice|security","description":"what is wrong and why it matters","line":12}],"summary":"one sentence about the file"}
            """;
    }

    /// <summary>
    /// Other files and cross-file usage, so the model stops calling exported things "never used".
    /// </summary>
    private static string ProjectBlock(ProjectContext? context)
    {
        if (context is null || context.IsEmpty) return "";

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Project context (this file is only one part of a larger project):");

        if (context.OtherFiles.Count > 0)
        {
            var more = context.OtherFileCount - context.OtherFiles.Count;
            sb.Append("- Other files: ").Append(string.Join(", ", context.OtherFiles));
            if (more > 0) sb.Append($" (+{more} more)");
            sb.AppendLine();
        }

        if (context.UsedElsewhere.Count > 0)
        {
            sb.Append("- Declared in this file and used by other files, so NOT dead code: ")
              .AppendLine(string.Join(", ", context.UsedElsewhere));
        }

        sb.AppendLine("- Do not call something unused just because it is not used inside this file; it may be part of the module's public API.");
        return sb.ToString();
    }

    public static string Readme(string content) => $$"""
        You are a technical documentation reviewer. Review this README.md.

        Check for:
        1. Missing sections (installation, usage, API docs, license, contributing)
        2. Incorrect or outdated information
        3. Unclear instructions
        4. Broken markdown

        --- BEGIN README ---
        {{content}}
        --- END README ---

        Reply with one JSON object and nothing else, in exactly this shape:
        {"issues":[{"severity":"high|medium|low","type":"missing|incorrect|unclear|broken","description":"..."}],"summary":"one sentence about the document"}
        """;

    public static string ChatSystem(FileAnalysis analysis, string numberedContent)
    {
        var issues = analysis.Issues.Count == 0
            ? "None"
            : string.Join('\n', analysis.Issues.Select(i =>
                $"- [{i.Severity.ToString().ToUpperInvariant()}] ({i.Kind.ToString().ToLowerInvariant()}){(i.Line is { } l ? $" line {l}:" : "")} {i.Description}"));

        var score = analysis.Score is { } s ? $"{s}/100" : "n/a";
        var summary = string.IsNullOrWhiteSpace(analysis.Summary) ? "n/a" : analysis.Summary;

        return
            "You are a code review assistant helping with a single file.\n\n" +
            $"File: {analysis.Path}\n" +
            $"Quality score: {score}\n" +
            $"Review summary: {summary}\n" +
            $"Issues found:\n{issues}\n\n" +
            "File content (line numbers added):\n```\n" + numberedContent + "\n```\n\n" +
            "Answer helpfully and concisely. When you suggest a fix, show the corrected code in a fenced code block.";
    }

    /// <summary>Prefixes each line with its number and stops once the character budget is used up.</summary>
    public static (string Text, bool Truncated) NumberLines(IReadOnlyList<string> lines, int maxChars)
    {
        var (text, end) = NumberRange(lines, 0, maxChars);
        var truncated = end < lines.Count;
        if (truncated) text += $"\n[... file truncated after line {end} ...]";
        return (text, truncated);
    }

    /// <summary>
    /// Numbers lines from <paramref name="start"/> (0-based) until the budget runs out. Numbers stay
    /// global, so a finding in the third part of a file still reports the real line.
    /// Always takes at least one line so a single huge line cannot stall the loop.
    /// </summary>
    public static (string Text, int EndExclusive) NumberRange(IReadOnlyList<string> lines, int start, int maxChars)
    {
        var sb = new StringBuilder();
        var width = lines.Count.ToString().Length;
        var i = start;

        for (; i < lines.Count; i++)
        {
            var row = $"{(i + 1).ToString().PadLeft(width)}: {lines[i]}";
            if (sb.Length + row.Length > maxChars && i > start) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(row);
        }

        return (sb.ToString(), i);
    }

    public static (string Text, bool Truncated) Clip(string text, int maxChars) =>
        text.Length <= maxChars
            ? (text, false)
            : (text[..maxChars] + "\n\n[... truncated ...]", true);
}
