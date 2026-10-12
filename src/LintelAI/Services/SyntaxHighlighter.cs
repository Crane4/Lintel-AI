using System.Text.RegularExpressions;

namespace LintelAI.Services;

public enum TokenKind { Plain, Keyword, Control, Type, Function, String, Number, Comment, Property, Tag, Attribute }

public readonly record struct Token(string Text, TokenKind Kind);

/// <summary>
/// A small lexical highlighter. It is not a parser: it only needs to make a handful of lines
/// of code readable, so comments, strings, numbers, keywords, types and calls are enough.
/// </summary>
public static class SyntaxHighlighter
{
    private enum Family { Plain, CLike, Python, Hash, Sql, Json, Markup, Css }

    private sealed record Language(Family Family, HashSet<string> Keywords, bool CaseInsensitive = false, bool KeyValue = false);

    // ── Keyword tables ──────────────────────────────────────────────────────

    private static HashSet<string> Words(string list, bool ignoreCase = false) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> ControlWords = Words(
        "if else elif elsif elseif for foreach while do switch case break continue return try catch finally throw throws " +
        "yield await goto default match when except raise unless until then fi done esac");

    private static readonly Dictionary<string, Language> Languages = BuildLanguages();

    private static Dictionary<string, Language> BuildLanguages()
    {
        var js = new Language(Family.CLike, Words(
            "break case catch class const continue debugger default delete do else export extends finally for function if import " +
            "in instanceof let new of return static super switch this throw try typeof var void while with yield async await " +
            "null undefined true false get set from as interface type enum implements public private protected readonly abstract declare namespace"));

        var py = new Language(Family.Python, Words(
            "and as assert async await break class continue def del elif else except finally for from global if import in is " +
            "lambda nonlocal not or pass raise return try while with yield None True False self"));

        var cs = new Language(Family.CLike, Words(
            "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do " +
            "double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal " +
            "is lock long namespace new null object operator out override params private protected public readonly ref return sbyte " +
            "sealed short sizeof static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var " +
            "virtual void volatile while record init get set partial yield"));

        var java = new Language(Family.CLike, Words(
            "abstract assert boolean break byte case catch char class const continue default do double else enum extends final " +
            "finally float for goto if implements import instanceof int interface long native new null package private protected " +
            "public return short static strictfp super switch synchronized this throw throws transient true false try void volatile while var record"));

        var go = new Language(Family.CLike, Words(
            "break case chan const continue default defer else fallthrough for func go goto if import interface map package range " +
            "return select struct switch type var nil true false"));

        var rust = new Language(Family.CLike, Words(
            "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub " +
            "ref return self Self static struct super trait true type unsafe use where while"));

        var c = new Language(Family.CLike, Words(
            "auto break case char class const constexpr continue default delete do double else enum extern float for goto if inline " +
            "int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch " +
            "template this throw try typedef typename union unsigned using virtual void volatile while true false NULL bool"));

        var php = new Language(Family.CLike, Words(
            "abstract and array as break case catch class clone const continue default do echo else elseif empty extends final " +
            "finally fn for foreach function global if implements include instanceof interface isset namespace new null or print " +
            "private protected public require return static switch throw trait true false try unset use var while yield"));

        var generic = new Language(Family.CLike, Words(
            "abstract as break by case catch class companion const continue data default do else enum extends extension false final " +
            "finally for fun func guard if implements import in init inline interface internal is lateinit let null object open " +
            "operator override package private protected public return sealed self static struct super switch this throw throws true " +
            "try typealias typeof val var when where while with yield async await"));

        var ruby = new Language(Family.Hash, Words(
            "alias and begin break case class def defined do else elsif end ensure false for if in module next nil not or redo " +
            "rescue retry return self super then true undef unless until when while yield"));

        var shell = new Language(Family.Hash, Words(
            "if then else elif fi for while do done case esac in function return exit local export echo cd set unset readonly " +
            "source true false"));

        var powershell = new Language(Family.Hash, Words(
            "function param if else elseif foreach for while do switch break continue return try catch finally throw begin " +
            "process end in $true $false $null"));

        var sql = new Language(Family.Sql, Words(
            "select from where and or not insert into values update set delete create table drop alter add join inner left right " +
            "outer on group by order having limit offset as distinct union all null is in like between exists case when then else " +
            "end primary key foreign references index unique default count sum avg min max", ignoreCase: true), CaseInsensitive: true);

        var json = new Language(Family.Json, Words("true false null"));
        var keyValue = new Language(Family.Hash, Words("true false null yes no on off"), KeyValue: true);
        var markup = new Language(Family.Markup, Words(""));
        var css = new Language(Family.Css, Words(""));

        var map = new Dictionary<string, Language>(StringComparer.OrdinalIgnoreCase);
        void Add(Language language, params string[] names)
        {
            foreach (var name in names) map[name] = language;
        }

        Add(js, "js", "javascript", "mjs", "cjs", "jsx", "node", "ts", "typescript", "tsx");
        Add(py, "py", "python");
        Add(cs, "cs", "csharp", "c#");
        Add(java, "java");
        Add(go, "go", "golang");
        Add(rust, "rs", "rust");
        Add(c, "c", "h", "cpp", "cc", "hpp", "c++", "cxx");
        Add(php, "php");
        Add(generic, "kt", "kotlin", "swift", "scala", "dart");
        Add(ruby, "rb", "ruby");
        Add(shell, "sh", "bash", "shell", "zsh");
        Add(powershell, "ps1", "powershell", "pwsh");
        Add(sql, "sql");
        Add(json, "json", "jsonc");
        Add(keyValue, "yaml", "yml", "toml", "ini", "cfg", "env");
        Add(markup, "html", "htm", "xml", "xaml", "axaml", "vue", "svelte", "razor", "cshtml", "svg", "csproj", "props");
        Add(css, "css", "scss", "less", "sass");
        return map;
    }

    // ── Lexers (one compiled regex per family) ──────────────────────────────

    private const string DoubleQuoted = "\"(?:\\\\.|[^\"\\\\\\n])*\"?";
    private const string SingleQuoted = "'(?:\\\\.|[^'\\\\\\n])*'?";
    private const string Backticked = "`(?:\\\\.|[^`\\\\])*`?";
    private const string NumberLiteral =
        @"\b(?:0[xX][0-9a-fA-F_]+|\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?)[fFdDlLuUmMnN]*\b";
    private const string Word = @"[A-Za-z_$][\w$]*";

    private static readonly Regex CLikeLexer = Make(
        $@"(?<comment>//[^\n]*|/\*[\s\S]*?(?:\*/|$))|(?<string>{DoubleQuoted}|{SingleQuoted}|{Backticked})|(?<number>{NumberLiteral})|(?<word>{Word})");

    private static readonly Regex PythonLexer = Make(
        $@"(?<comment>#[^\n]*)|(?<string>""""""[\s\S]*?(?:""""""|$)|'''[\s\S]*?(?:'''|$)|{DoubleQuoted}|{SingleQuoted})|(?<number>{NumberLiteral})|(?<word>{Word})");

    private static readonly Regex HashLexer = Make(
        $@"(?<comment>#[^\n]*)|(?<string>{DoubleQuoted}|{SingleQuoted})|(?<number>{NumberLiteral})|(?<word>{Word})");

    private static readonly Regex SqlLexer = Make(
        $@"(?<comment>--[^\n]*|/\*[\s\S]*?(?:\*/|$))|(?<string>{SingleQuoted}|{DoubleQuoted})|(?<number>{NumberLiteral})|(?<word>{Word})");

    private static readonly Regex JsonLexer = Make(
        $@"(?<property>{DoubleQuoted}(?=\s*:))|(?<string>{DoubleQuoted})|(?<number>-?\d[\d.eE+-]*)|(?<word>[A-Za-z_]+)");

    private static readonly Regex MarkupLexer = Make(
        @"(?<comment><!--[\s\S]*?(?:-->|$))|(?<tag></?[A-Za-z][\w:.-]*)|(?<string>""[^""\n]*""?|'[^'\n]*'?)|(?<attribute>[A-Za-z_:@][\w:.-]*(?=\s*=))");

    private static readonly Regex CssLexer = Make(
        @"(?<comment>/\*[\s\S]*?(?:\*/|$))|(?<string>""[^""\n]*""?|'[^'\n]*'?)|(?<number>#[0-9a-fA-F]{3,8}\b|-?\d*\.?\d+(?:px|em|rem|%|vh|vw|s|ms|deg|fr)?)|(?<keyword>@[\w-]+)|(?<property>[A-Za-z-]+(?=\s*:))|(?<type>[.#][\w-]+)");

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Turns "src/app/Main.cs" into "cs", or null when there is nothing to highlight.</summary>
    public static string? LanguageFromPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : null;
    }

    public static IReadOnlyList<Token> Tokenize(string? code, string? language)
    {
        if (string.IsNullOrEmpty(code)) return Array.Empty<Token>();

        var key = language?.Trim().TrimStart('.') ?? "";
        if (!Languages.TryGetValue(key, out var lang))
            return new[] { new Token(code, TokenKind.Plain) };

        var lexer = lang.Family switch
        {
            Family.CLike => CLikeLexer,
            Family.Python => PythonLexer,
            Family.Hash => HashLexer,
            Family.Sql => SqlLexer,
            Family.Json => JsonLexer,
            Family.Markup => MarkupLexer,
            Family.Css => CssLexer,
            _ => null
        };
        if (lexer is null) return new[] { new Token(code, TokenKind.Plain) };

        var tokens = new List<Token>();
        var position = 0;

        void Emit(string text, TokenKind kind)
        {
            if (text.Length == 0) return;
            if (kind == TokenKind.Plain && tokens.Count > 0 && tokens[^1].Kind == TokenKind.Plain)
                tokens[^1] = new Token(tokens[^1].Text + text, TokenKind.Plain);
            else
                tokens.Add(new Token(text, kind));
        }

        foreach (Match match in lexer.Matches(code))
        {
            if (match.Index > position) Emit(code[position..match.Index], TokenKind.Plain);

            var kind = Classify(match, lang, code);
            Emit(match.Value, kind);
            position = match.Index + match.Length;
        }

        if (position < code.Length) Emit(code[position..], TokenKind.Plain);
        return tokens;
    }

    private static TokenKind Classify(Match match, Language lang, string code)
    {
        if (match.Groups["comment"].Success) return TokenKind.Comment;
        if (match.Groups["string"].Success) return TokenKind.String;
        if (match.Groups["number"].Success) return TokenKind.Number;
        if (match.Groups["property"].Success) return TokenKind.Property;
        if (match.Groups["tag"].Success) return TokenKind.Tag;
        if (match.Groups["attribute"].Success) return TokenKind.Attribute;
        if (match.Groups["type"].Success) return TokenKind.Type;
        if (match.Groups["keyword"].Success) return TokenKind.Keyword;

        var word = match.Value;

        if (lang.Keywords.Contains(word))
        {
            if (ControlWords.Contains(lang.CaseInsensitive ? word.ToLowerInvariant() : word)) return TokenKind.Control;
            return TokenKind.Keyword;
        }

        // key: value / key = value in YAML, TOML, INI and .env files
        var next = NextNonSpace(code, match.Index + match.Length);
        if (lang.KeyValue && (next == ':' || next == '=')) return TokenKind.Property;

        if (lang.Family == Family.Json) return TokenKind.Plain;
        if (next == '(') return TokenKind.Function;
        if (char.IsUpper(word[0]) && word.Any(char.IsLower)) return TokenKind.Type;
        return TokenKind.Plain;
    }

    private static char NextNonSpace(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is ' ' or '\t') continue;
            return ch;
        }
        return '\0';
    }
}
