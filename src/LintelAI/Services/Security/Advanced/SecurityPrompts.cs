using System.Text;
using System.Text.RegularExpressions;

namespace LintelAI.Services.Security.Advanced;

/// <summary>
/// What the model is asked, and the shape it must answer in. The questions are deliberately
/// narrow: a small model is reliable at "is this line harmful given this code?" and unreliable
/// at "find all the problems in this file".
/// </summary>
public static class SecurityPrompts
{
    public static readonly string[] Categories =
    [
        "download_execute", "data_exfiltration", "credential_theft", "persistence",
        "hidden_payload", "process_injection", "destructive", "remote_shell", "disables_security"
    ];

    public static string CategoryLabel(string category) => category switch
    {
        "download_execute" => "Downloads and runs code",
        "data_exfiltration" => "Sends data out",
        "credential_theft" => "Reads credentials",
        "persistence" => "Persistence",
        "hidden_payload" => "Hidden payload",
        "process_injection" => "Process injection",
        "destructive" => "Destructive action",
        "remote_shell" => "Remote shell",
        "disables_security" => "Disables security",
        _ => category
    };

    // ── Output schemas. "evidence" comes first so the model commits to a quote before it decides. ──

    public static object JudgeSchema => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["evidence"] = new { type = "string" },
            ["reason"] = new { type = "string" },
            ["verdict"] = new { type = "string", @enum = new[] { "benign", "malicious", "unclear" } }
        },
        ["required"] = new[] { "evidence", "reason", "verdict" }
    };

    public static object DiscoverSchema => new Dictionary<string, object>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object>
        {
            ["findings"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["evidence"] = new { type = "string" },
                        ["line"] = new { type = "integer" },
                        ["category"] = new { type = "string", @enum = Categories },
                        ["reason"] = new { type = "string" }
                    },
                    ["required"] = new[] { "evidence", "line", "category", "reason" }
                }
            }
        },
        ["required"] = new[] { "findings" }
    };

    // ── Rule-specific rubrics: what harmless and harmful look like for each kind of alert. ──

    private const string GenericRubric = """
        Harmless: ordinary application behaviour that the project's own purpose explains.
        Harmful: it acts on the user's machine or data in a way the project has no reason to, runs content it fetched or decoded, or hides what it does.
        """;

    private static readonly Dictionary<string, string> Rubrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Keylogging or screen capture"] = """
            Harmless: input handling for the program's own window (a game reading keys or the mouse), or a screenshot feature the user triggers.
            Harmful: a global hook or polling of every key while other programs are in focus, with the keys stored or sent somewhere.
            """,
        ["Key state polling"] = """
            Harmless: a game or app reading key state for its own controls.
            Harmful: looping over every key and writing the result to a file or the network.
            """,
        ["Download and execute"] = """
            Harmless: an install or setup command a user is meant to type, pointing at the project's own official site.
            Harmful: fetching code from an unrelated or hidden host and running it automatically, without the user choosing to.
            """,
        ["Decode then execute"] = """
            Harmless: decoding and running a fixed, visible snippet the project ships itself.
            Harmful: decoding or decompressing a blob and then running it, so the real code is hidden.
            """,
        ["Dynamic code evaluation"] = """
            Harmless: evaluating a fixed expression, a calculator or template feature, or test code.
            Harmful: evaluating text that was decoded, downloaded, or taken from a remote source.
            """,
        ["Hard-coded IP address"] = """
            Harmless: localhost, a private network address, a public DNS server, or an example in a comment or test.
            Harmful: a bare public IP used to download code or to receive stolen data.
            """,
        ["Install script"] = """
            Harmless: a build step such as compiling a native module, git hooks setup, or generating files from local sources.
            Harmful: it downloads or runs remote code, runs an obfuscated script, or touches files outside the project.
            """,
        ["Suspicious install script"] = """
            Harmless: a documented build or setup step that only works on the project's own files.
            Harmful: it downloads and runs something, or runs obfuscated code, the moment the project is installed.
            """,
        ["Build step"] = """
            Harmless: copying outputs, generating code, running the project's own tools.
            Harmful: downloading files or running scripts from the internet during the build.
            """,
        ["Suspicious build step"] = """
            Harmless: a documented step that only works on the project's own files.
            Harmful: downloading and running something, or launching a hidden shell, during the build.
            """,
        ["Large encoded blob"] = """
            Harmless: an embedded font, image, certificate or test data, or a compressed asset that is only displayed or parsed.
            Harmful: a blob that is decoded and then executed or loaded as a program.
            """,
        ["Hex-escaped string"] = """
            Harmless: binary data, test vectors, or a lookup table.
            Harmful: escaped text that spells out a command, URL or code that is then executed.
            """,
        ["Character-code string building"] = """
            Harmless: building a text string for display or a simple encoding.
            Harmful: building a command, URL or code string that is then executed.
            """,
        ["Obfuscated JavaScript"] = """
            Harmless: a minified or bundled build of a known library.
            Harmful: hand-obfuscated project code that hides what it does.
            """,
        ["Extremely long line"] = """
            Harmless: minified code, embedded data, or a generated file.
            Harmful: a packed payload on one line that is decoded and run.
            """,
        ["Credential file access"] = """
            Harmless: a tool that is meant to read its own configuration, or a git/ssh/cloud client feature the user invoked.
            Harmful: reading keys or tokens and sending them elsewhere, or reading them without any reason in the program's purpose.
            """,
        ["Persistence"] = """
            Harmless: an installer or documented feature that registers the program to start with the system, because the user asked for it.
            Harmful: a program quietly registering itself to survive reboots without saying so.
            """,
        ["Process injection"] = """
            Harmless: a debugger, profiler or a program modifying its own memory.
            Harmful: writing code into another program's memory and starting it there.
            """,
        ["Anonymous drop or tunnel"] = """
            Harmless: development or test tooling the author uses to expose a local server.
            Harmful: a shipped program sending user data or files to an anonymous endpoint.
            """
    };

    public static string RubricFor(string type, string description)
    {
        var rubric = Rubrics.TryGetValue(type, out var specific) ? specific : GenericRubric;
        return $"What the alert means: {description}\n{rubric}";
    }

    // ── Prompts ──

    /// <summary>
    /// Asks whether one flagged line is harmful. The "adversarial" variant frames it the other way
    /// round so a model that tends to agree with the framing cannot clear a real threat twice.
    /// </summary>
    public static string Judge(
        string path, string type, string rubric, string window, string projectHint, bool adversarial)
    {
        var task = adversarial
            ? """
              Your job is to find out whether the flagged line could hurt the person who runs this project.
              Check: where does the data come from and where does it go? Is anything executed that was downloaded, decoded or decrypted? Is anything hidden from the user?
              If nothing in the code below shows harm, the answer is "benign".
              """
            : """
              Your job is to decide whether the flagged line is really harmful in this context, or an ordinary false alarm.
              """;

        return $$"""
            You are a careful malware analyst. An automated scanner raised ONE alert in a downloaded software project.
            {{task}}

            Project: {{projectHint}}
            File: {{path}}
            Alert: {{type}}
            {{rubric}}

            Code (line numbers on the left; ">>>" marks the flagged line):
            --- BEGIN CODE ---
            {{window}}
            --- END CODE ---

            Rules for your answer:
            - "evidence" must be copied word for word from the code above: the one line or fragment your answer depends on. Never paraphrase it and never quote text that is not above.
            - "benign": the code clearly does something ordinary and harmless here. Say what it does.
            - "malicious": the code does what the alert says, in a way that harms the user or hides what it is doing.
            - "unclear": you cannot tell from this code alone. Use "unclear" whenever you are not sure.
            - Judge only what is written. Do not assume code outside the window and do not invent behaviour.

            Reply with one JSON object and nothing else:
            {"evidence":"<exact text from the code>","reason":"<one or two sentences>","verdict":"benign|malicious|unclear"}
            """;
    }

    public static string Discover(string path, string numberedContent, string projectHint)
    {
        var categories = new StringBuilder();
        foreach (var c in Categories)
        {
            categories.Append("- ").Append(c).Append(": ").AppendLine(CategoryHelp(c));
        }

        return $$"""
            You are a careful malware analyst. Read the file below and report code that does something harmful to the person who runs it.

            Project: {{projectHint}}
            File: {{path}}

            Report a line ONLY if it clearly does one of these:
            {{categories}}
            Do NOT report: bugs, style, missing input validation, normal file or network use, logging, build steps that only compile code, test code, or anything you are not sure about.
            Most files are harmless. The correct answer for a harmless file is an empty list.

            Every finding needs "evidence" (the exact line copied word for word from the file) and its "line" number. Every line below starts with its number and a colon.

            --- BEGIN FILE ---
            {{numberedContent}}
            --- END FILE ---

            Reply with one JSON object and nothing else:
            {"findings":[{"evidence":"<exact line>","line":12,"category":"<one of the categories>","reason":"<one sentence>"}]}
            """;
    }

    private static string CategoryHelp(string category) => category switch
    {
        "download_execute" => "downloads code or a program and runs it",
        "data_exfiltration" => "sends files, passwords, tokens, clipboard or system information to a remote server",
        "credential_theft" => "reads browser data, saved passwords, wallets, SSH keys or tokens this program has no reason to read",
        "persistence" => "installs itself to run again at startup or on a schedule",
        "hidden_payload" => "decodes, decrypts or unpacks hidden code and then runs it",
        "process_injection" => "writes code into another running process",
        "destructive" => "wipes or encrypts files, disks or backups",
        "remote_shell" => "gives a remote person a command shell",
        "disables_security" => "turns off antivirus, firewall, logging or updates",
        _ => category
    };

    /// <summary>A short description of what kind of project this is, so "a game reading keys" is recognisable.</summary>
    public static string ProjectHint(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "unknown";

        var extensions = paths
            .Select(p => Path.GetExtension(p).ToLowerInvariant())
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => g.Key);

        var top = paths
            .Select(p => p.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(14);

        var hint = $"{paths.Count} files, mostly {string.Join(" ", extensions)}; top level: {string.Join(", ", top)}";
        return Regex.Replace(hint, @"[\r\n]+", " ");
    }
}
