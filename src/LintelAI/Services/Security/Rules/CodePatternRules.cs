using System.Text.RegularExpressions;
using LintelAI.Models;
using static LintelAI.Services.Security.SecurityRuleSupport;

namespace LintelAI.Services.Security.Rules;

/// <summary>Known-bad and known-suspicious code patterns, matched line by line. No AI involved.</summary>
public sealed class CodePatternRules : ISecurityRule
{
    private const int MaxHitsPerPattern = 5;

    private sealed record Pattern(string Type, Severity Severity, string Description, Regex Regex);

    private static Pattern P(string type, Severity severity, string description, string regex, bool ignoreCase = true) =>
        new(type, severity, description, Compile(regex, ignoreCase));

    private static readonly Pattern[] Patterns =
    [
        P("Download and execute", Severity.High,
            "Fetches code from the internet and runs it straight away.",
            """(curl|wget)\b[^|\n]*\|\s*(sudo\s+)?(ba|z|da)?sh\b|(iex|invoke-expression)\b.{0,80}(downloadstring|invoke-webrequest|\biwr\b|\birm\b|webclient)|(downloadstring|invoke-webrequest|\biwr\b|\birm\b)\b.{0,120}\|\s*(iex|invoke-expression)"""),

        P("Living-off-the-land download", Severity.High,
            "Uses a built-in Windows tool to download or run remote code, a common way to avoid detection.",
            """certutil(\.exe)?\s.{0,40}-urlcache|bitsadmin\s.{0,20}/transfer|mshta(\.exe)?\s+(https?:|vbscript:|javascript:)|regsvr32\s.{0,30}/i:https?:|rundll32.{0,40}javascript:"""),

        P("Encoded PowerShell command", Severity.High,
            "Runs a PowerShell command hidden inside an encoded string.",
            """powershell(\.exe)?\b.{0,80}\s-(e|enc|encodedcommand)\b"""),

        P("Hidden PowerShell window", Severity.Medium,
            "Starts PowerShell with its window hidden, so the user never sees it run.",
            """(powershell|pwsh)(\.exe)?\b[^\n]{0,80}-w(indowstyle)?\s+hidden"""),

        P("Decode then execute", Severity.High,
            "Decodes a hidden payload and executes it. Legitimate code rarely needs to do this.",
            """(?<![\w.])(eval|exec|invoke-expression)\s*\(.{0,40}(atob|b64decode|base64_decode|frombase64string|zlib\.decompress|gzinflate|marshal\.loads|codecs\.decode|unescape)"""),

        P("Discord / Telegram exfiltration", Severity.High,
            "Sends data to a Discord webhook or Telegram bot, the usual way stolen data leaves the machine.",
            """discord(app)?\.com/api/webhooks/|api\.telegram\.org/bot"""),

        P("Anonymous drop or tunnel", Severity.Medium,
            "Talks to a throwaway endpoint, file drop or tunnel service.",
            """webhook\.site|requestbin|pipedream\.net|transfer\.sh|anonfiles|gofile\.io|pastebin\.com/raw|ngrok(-free)?\.(io|app|dev)|serveo\.net|burpcollaborator|oast\.(fun|live|me|pro|site|online)|interact\.sh"""),

        // Case-sensitive on purpose: these are exact file names, and "local state" in a sentence is not one.
        // They must also look like a path or a quoted name, never bare words.
        P("Browser data theft", Severity.High,
            "Reaches into browser profile files where saved passwords and cookies are kept.",
            """User Data[\\/]+(Default|Local State)|[\\/](Local State|Login Data|Web Data)\b|["'](Local State|Login Data)["']|\bkey4\.db\b|\blogins\.json\b|\bcookies\.sqlite\b|Mozilla[\\/]+Firefox[\\/]+Profiles""",
            ignoreCase: false),

        P("Wallet or session theft", Severity.High,
            "Looks for crypto wallet files or Discord session data.",
            """wallet\.dat|nkbihfbeogaeaoehlefnkodbefgpgknn|exodus\.wallet|Electrum[\\/]+wallets|discord[\w\\/ ]*[\\/]+Local Storage[\\/]+leveldb|\bmfa\.[\w-]{84}\b"""),

        P("Discord-token-like string", Severity.Medium,
            "A string shaped like a Discord account token.",
            """\b[\w-]{24}\.[\w-]{6}\.[\w-]{27,38}\b""",
            ignoreCase: false),

        P("Credential file access", Severity.Medium,
            "Reads files where SSH keys, cloud credentials or tokens are stored.",
            """[\\/]\.ssh[\\/]|\bid_rsa\b|\.aws[\\/]credentials|\.kube[\\/]config|\.git-credentials|\.docker[\\/]config\.json"""),

        // Reading a specific key (GetAsyncKeyState(VK_SHIFT)) is ordinary game input. What a keylogger
        // does is hook the whole keyboard or poll every key, which is what these look for.
        P("Keylogging or screen capture", Severity.Medium,
            "Uses APIs that record keystrokes or the screen.",
            """SetWindowsHookEx\w*\(\s*WH_KEYBOARD|\bpynput\b|keyboard\.(on_press|hook|add_hotkey)|ImageGrab\.grab|pyautogui\.screenshot|CopyFromScreen"""),

        P("Key state polling", Severity.Low,
            "Reads the state of keys by variable, as a loop over every key would. Normal for games and input code.",
            """GetAsyncKeyState\(\s*(?!VK_)[a-z_]\w*\s*\)|\bGetKeyboardState\b"""),

        P("Persistence", Severity.Medium,
            "Arranges to run again after reboot or logon.",
            """CurrentVersion[\\/]+Run|schtasks(\.exe)?\s+/create|shell:startup|Start Menu[\\/]+Programs[\\/]+Startup|launchctl\s+load|/etc/rc\.local"""),

        P("Process injection", Severity.High,
            "Writes code into another running process.",
            """VirtualAllocEx|WriteProcessMemory|CreateRemoteThread|NtUnmapViewOfSection|QueueUserAPC|SetThreadContext|NtWriteVirtualMemory"""),

        P("Security software tampering", Severity.High,
            "Turns off antivirus, backups, logs or the firewall.",
            """(Add|Set)-MpPreference\s.{0,60}(Exclusion|Disable)|DisableRealtimeMonitoring|DisableAntiSpyware|vssadmin(\.exe)?\s+delete\s+shadows|wbadmin\s+delete|wevtutil\s+cl\b|sc\s+(stop|config)\s+WinDefend|taskkill.{0,40}(MsMpEng|MsSense)|netsh\s+advfirewall\s+set"""),

        P("Reverse shell", Severity.High,
            "Gives a remote machine an interactive shell on this one.",
            """/dev/tcp/\d|\bnc(at)?\s[^\n]*-e\s+/bin/(ba)?sh|bash\s+-i\s+>&|mkfifo\s+\S+;?\s*(nc|ncat)\b|pty\.spawn\(\s*["']/bin/(ba)?sh"""),

        P("Crypto miner", Severity.High,
            "References mining pools or well-known miner software.",
            """stratum\+(tcp|ssl)://|\bxmrig\b|cryptonight|minexmr|nanopool\.org|moneroocean|supportxmr|hashvault\.pro"""),

        P("Destructive command", Severity.High,
            "Wipes disks or deletes everything under a root folder.",
            """\bcipher\s+/w:|\brm\s+-rf\s+(/|~|\$HOME)(\s|$|\*)|\bformat\s+[a-z]:|\bdd\s+if=/dev/(zero|urandom)\s+of=/dev/(sd|nvme|hd)"""),

        P("In-memory assembly loading", Severity.Medium,
            "Loads a .NET assembly from decoded or downloaded bytes, so no file ever hits the disk.",
            """Assembly\.Load(From)?\(\s*(Convert\.FromBase64String|[^)]*DownloadData|[^)]*Decrypt)"""),

        P("Hard-coded IP address", Severity.Low,
            "Contacts a raw IP address instead of a domain name.",
            """https?://(?!(127\.|10\.|192\.168\.|0\.0\.0\.0|172\.(1[6-9]|2\d|3[01])\.|169\.254\.))\d{1,3}(\.\d{1,3}){3}\b"""),

        P("Dynamic code evaluation", Severity.Low,
            "Evaluates a string as code. Often harmless, but it is how hidden payloads get run.",
            """(?<![\w.])eval\s*\(|\bnew\s+Function\s*\("""),
    ];

    public IEnumerable<Issue> Evaluate(SecurityFile file, IReadOnlyList<string>? lines)
    {
        var issues = new List<Issue>();
        if (lines is null || IsDocument(file.Path)) return issues;

        var hits = new int[Patterns.Length];

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length < 4 || IsCommentLine(line)) continue;

            for (var p = 0; p < Patterns.Length; p++)
            {
                if (hits[p] >= MaxHitsPerPattern) continue;

                var pattern = Patterns[p];
                var match = TryMatch(pattern.Regex, line);
                if (match is null) continue;

                hits[p]++;
                issues.Add(Finding(
                    pattern.Severity,
                    pattern.Type,
                    $"{pattern.Description} Matched: {Excerpt(match.Value)}",
                    i + 1));
            }
        }

        return issues;
    }
}
