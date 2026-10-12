using LintelAI.Models;
using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

public class IssueKindTests
{
    [Theory]
    [InlineData("vulnerability", "logic", IssueKind.Vulnerability)]
    [InlineData("Security", null, IssueKind.Vulnerability)]
    [InlineData("bug", "practice", IssueKind.Bug)]
    [InlineData("code smell", "memory", IssueKind.Smell)]
    [InlineData("quality", null, IssueKind.Smell)]
    public void An_explicit_kind_wins_over_the_type(string kind, string? type, IssueKind expected) =>
        Assert.Equal(expected, JsonExtractor.ParseKind(kind, type));

    [Theory]
    [InlineData("security", IssueKind.Vulnerability)]
    [InlineData("sql-injection", IssueKind.Vulnerability)]
    [InlineData("memory", IssueKind.Bug)]
    [InlineData("loop", IssueKind.Bug)]
    [InlineData("logic", IssueKind.Bug)]
    [InlineData("dead-code", IssueKind.Smell)]
    [InlineData("practice", IssueKind.Smell)]
    [InlineData("missing", IssueKind.Smell)]
    [InlineData("", IssueKind.Smell)]
    public void Falls_back_to_the_type_when_the_model_forgot_the_kind(string type, IssueKind expected) =>
        Assert.Equal(expected, JsonExtractor.ParseKind(null, type));

    [Fact]
    public void The_kind_field_is_read_from_the_reply()
    {
        const string raw = """{"issues":[{"kind":"vulnerability","severity":"high","type":"security","description":"eval of user input","line":3}]}""";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal(IssueKind.Vulnerability, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Replies_without_a_kind_still_get_one()
    {
        const string raw = """{"issues":[{"severity":"high","type":"memory","description":"never cleared"}]}""";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal(IssueKind.Bug, Assert.Single(result.Issues).Kind);
    }
}

public class SyntaxHighlighterTests
{
    private static List<Token> Tokens(string code, string language) =>
        SyntaxHighlighter.Tokenize(code, language).ToList();

    private static void Has(IEnumerable<Token> tokens, string text, TokenKind kind) =>
        Assert.Contains(tokens, t => t.Text == text && t.Kind == kind);

    [Fact]
    public void Tokens_always_reassemble_into_the_original_text()
    {
        const string code = "function add(a, b) {\n  // sum\n  return a + b; /* done */\n}\nconst s = `x${1}`;";
        var rebuilt = string.Concat(Tokens(code, "js").Select(t => t.Text));
        Assert.Equal(code, rebuilt);
    }

    [Fact]
    public void Highlights_javascript()
    {
        var tokens = Tokens("const total = compute(42, \"x\"); // note", "js");

        Has(tokens, "const", TokenKind.Keyword);
        Has(tokens, "compute", TokenKind.Function);
        Has(tokens, "42", TokenKind.Number);
        Has(tokens, "\"x\"", TokenKind.String);
        Has(tokens, "// note", TokenKind.Comment);
    }

    [Fact]
    public void Control_flow_is_a_different_colour_from_other_keywords()
    {
        var tokens = Tokens("if (ok) { return null; }", "ts");

        Has(tokens, "if", TokenKind.Control);
        Has(tokens, "return", TokenKind.Control);
        Has(tokens, "null", TokenKind.Keyword);
    }

    [Fact]
    public void Highlights_python_with_hash_comments_and_triple_quotes()
    {
        var tokens = Tokens("def run(x):  # go\n    \"\"\"doc\"\"\"\n    return x", "py");

        Has(tokens, "def", TokenKind.Keyword);
        Has(tokens, "run", TokenKind.Function);
        Has(tokens, "# go", TokenKind.Comment);
        Has(tokens, "\"\"\"doc\"\"\"", TokenKind.String);
        Has(tokens, "return", TokenKind.Control);
    }

    [Fact]
    public void Class_names_are_types()
    {
        var tokens = Tokens("var cache = new Dictionary<string, int>();", "cs");

        Has(tokens, "var", TokenKind.Keyword);
        Has(tokens, "new", TokenKind.Keyword);
        Has(tokens, "Dictionary", TokenKind.Type);
        Has(tokens, "string", TokenKind.Keyword);
    }

    [Fact]
    public void Comment_markers_inside_strings_do_not_start_comments()
    {
        var tokens = Tokens("const url = \"http://example.com\";", "js");

        Has(tokens, "\"http://example.com\"", TokenKind.String);
        Assert.DoesNotContain(tokens, t => t.Kind == TokenKind.Comment);
    }

    [Fact]
    public void An_unterminated_block_comment_runs_to_the_end()
    {
        var tokens = Tokens("let a; /* still open", "js");
        Has(tokens, "/* still open", TokenKind.Comment);
    }

    [Fact]
    public void Json_keys_differ_from_string_values()
    {
        var tokens = Tokens("{\"name\": \"sample\", \"count\": 3, \"ok\": true}", "json");

        Has(tokens, "\"name\"", TokenKind.Property);
        Has(tokens, "\"sample\"", TokenKind.String);
        Has(tokens, "3", TokenKind.Number);
        Has(tokens, "true", TokenKind.Keyword);
    }

    [Fact]
    public void Sql_keywords_are_case_insensitive()
    {
        var tokens = Tokens("SELECT id FROM users WHERE id = 1 -- first", "sql");

        Has(tokens, "SELECT", TokenKind.Keyword);
        Has(tokens, "FROM", TokenKind.Keyword);
        Has(tokens, "-- first", TokenKind.Comment);
    }

    [Fact]
    public void Markup_tags_and_attributes()
    {
        var tokens = Tokens("<Button Content=\"Hi\" />", "axaml");

        Has(tokens, "<Button", TokenKind.Tag);
        Has(tokens, "Content", TokenKind.Attribute);
        Has(tokens, "\"Hi\"", TokenKind.String);
    }

    [Theory]
    [InlineData("md")]
    [InlineData("txt")]
    [InlineData("whatever")]
    [InlineData(null)]
    public void Unknown_languages_stay_plain(string? language)
    {
        var token = Assert.Single(SyntaxHighlighter.Tokenize("const x = 1;", language));
        Assert.Equal(TokenKind.Plain, token.Kind);
    }

    [Theory]
    [InlineData("src/app/Main.cs", "cs")]
    [InlineData("README.MD", "md")]
    [InlineData("Makefile", null)]
    public void Derives_the_language_from_a_path(string path, string? expected) =>
        Assert.Equal(expected, SyntaxHighlighter.LanguageFromPath(path));
}
