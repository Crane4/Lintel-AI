using LintelAI.Models;
using LintelAI.Services;
using LintelAI.ViewModels;
using Xunit;

namespace LintelAI.Tests;

public class JsonExtractorTests
{
    [Fact]
    public void Parses_plain_json()
    {
        var ok = JsonExtractor.TryParseAnalysis(
            """{"issues":[{"severity":"high","type":"memory","description":"leak","line":12}],"summary":"bad"}""",
            out var result);

        Assert.True(ok);
        Assert.Equal("bad", result.Summary);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(Severity.High, issue.Severity);
        Assert.Equal("memory", issue.Type);
        Assert.Equal(12, issue.Line);
    }

    [Fact]
    public void Parses_json_inside_a_markdown_fence_with_chatter()
    {
        const string raw = "Sure! Here is the review:\n```json\n{\"issues\":[],\"summary\":\"clean\"}\n```\nHope that helps.";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Empty(result.Issues);
        Assert.Equal("clean", result.Summary);
    }

    [Fact]
    public void Finds_a_json_object_surrounded_by_prose()
    {
        const string raw = "Result -> {\"issues\":[{\"severity\":\"low\",\"description\":\"x\"}],\"summary\":\"s\"} (end)";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Single(result.Issues);
    }

    [Fact]
    public void Accepts_a_bare_array_of_issues()
    {
        const string raw = """[{"severity":"medium","type":"logic","description":"always true"}]""";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal(Severity.Medium, Assert.Single(result.Issues).Severity);
    }

    [Fact]
    public void Strips_reasoning_blocks_before_parsing()
    {
        const string raw = "<think>I should output {\"issues\": nope}</think>{\"issues\":[],\"summary\":\"ok\"}";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal("ok", result.Summary);
    }

    [Fact]
    public void Reads_line_numbers_given_as_strings_and_ignores_nonsense()
    {
        const string raw = """{"issues":[{"severity":"low","description":"a","line":"7"},{"severity":"low","description":"b","line":"n/a"},{"severity":"low","description":"c","line":-3}]}""";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal(7, result.Issues[0].Line);
        Assert.Null(result.Issues[1].Line);
        Assert.Null(result.Issues[2].Line);
    }

    [Fact]
    public void Skips_issues_without_a_description()
    {
        const string raw = """{"issues":[{"severity":"high"},{"severity":"low","description":"real"}]}""";
        Assert.True(JsonExtractor.TryParseAnalysis(raw, out var result));
        Assert.Equal("real", Assert.Single(result.Issues).Description);
    }

    [Theory]
    [InlineData("HIGH", Severity.High)]
    [InlineData("critical", Severity.High)]
    [InlineData("Moderate", Severity.Medium)]
    [InlineData("warning", Severity.Medium)]
    [InlineData("info", Severity.Low)]
    [InlineData(null, Severity.Low)]
    public void Maps_severity_words(string? input, Severity expected) =>
        Assert.Equal(expected, JsonExtractor.ParseSeverity(input));

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"foo\":1}")]
    [InlineData("\"just a string\"")]
    public void Rejects_unusable_replies(string raw) =>
        Assert.False(JsonExtractor.TryParseAnalysis(raw, out _));
}

public class ScoringTests
{
    private static Issue Make(Severity s) => new(s, "t", "d", null);

    [Fact]
    public void A_clean_file_scores_100() => Assert.Equal(100, Scoring.Compute(Array.Empty<Issue>()));

    [Fact]
    public void Penalties_follow_severity() =>
        Assert.Equal(100 - 20 - 10 - 5, Scoring.Compute(new[] { Make(Severity.High), Make(Severity.Medium), Make(Severity.Low) }));

    [Fact]
    public void Score_never_drops_below_zero() =>
        Assert.Equal(0, Scoring.Compute(Enumerable.Repeat(Make(Severity.High), 9)));

    [Fact]
    public void Average_skips_missing_scores_and_rounds()
    {
        Assert.Equal(75, Scoring.Average(new int?[] { 100, null, 50 }));
        Assert.Null(Scoring.Average(new int?[] { null, null }));
    }
}

public class MessageParserTests
{
    [Fact]
    public void Splits_prose_and_code()
    {
        var blocks = MessageParser.Split("Intro\n\n```js\nlet a = 1;\n```\n\nOutro");
        Assert.Equal(3, blocks.Count);
        Assert.False(blocks[0].IsCode);
        Assert.True(blocks[1].IsCode);
        Assert.Equal("js", blocks[1].Language);
        Assert.Equal("let a = 1;", blocks[1].Text);
        Assert.Equal("Outro", blocks[2].Text);
    }

    [Fact]
    public void An_unterminated_fence_is_treated_as_code_while_streaming()
    {
        var blocks = MessageParser.Split("Here:\n```py\nprint(1)");
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[1].IsCode);
        Assert.Equal("print(1)", blocks[1].Text);
    }

    [Fact]
    public void Preserves_indentation_inside_code()
    {
        var blocks = MessageParser.Split("```\nif x:\n    y()\n```");
        Assert.Equal("if x:\n    y()", Assert.Single(blocks).Text);
    }

    [Fact]
    public void Empty_text_yields_no_blocks() => Assert.Empty(MessageParser.Split(""));
}

public class PromptsTests
{
    [Fact]
    public void Numbers_lines_with_aligned_width()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"row{i}").ToList();
        var (text, truncated) = Prompts.NumberLines(lines, 10_000);

        Assert.False(truncated);
        Assert.StartsWith(" 1: row1", text);
        Assert.Contains("12: row12", text);
    }

    [Fact]
    public void Truncates_on_a_line_boundary_and_says_so()
    {
        var lines = Enumerable.Range(1, 100).Select(i => new string('x', 20)).ToList();
        var (text, truncated) = Prompts.NumberLines(lines, 200);

        Assert.True(truncated);
        Assert.Contains("file truncated after line", text);
    }

    [Fact]
    public void Always_keeps_at_least_the_first_line()
    {
        var (text, truncated) = Prompts.NumberLines(new[] { new string('y', 500) }, 50);
        Assert.False(truncated);
        Assert.Contains("yyyy", text);
    }
}

public class ChatMessageViewModelTests
{
    [Fact]
    public void Hides_reasoning_blocks_from_the_displayed_reply()
    {
        var message = new ChatMessageViewModel(isUser: false);
        message.Append("<think>let me work this out</think>The answer is 42.");

        Assert.Equal("The answer is 42.", message.Display);
        var segment = Assert.Single(message.Segments);
        Assert.IsType<ProseSegmentViewModel>(segment);
    }

    [Fact]
    public void Streaming_updates_segments_in_place()
    {
        var message = new ChatMessageViewModel(isUser: false);
        message.Append("Use this:\n```js\nlet a");
        var code = message.Segments[1];

        message.Append(" = 1;\n```");

        Assert.Same(code, message.Segments[1]);
        Assert.Equal("let a = 1;", code.Text);
    }
}
