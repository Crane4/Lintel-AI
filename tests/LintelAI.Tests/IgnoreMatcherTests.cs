using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

public class IgnoreMatcherTests
{
    private static IgnoreMatcher Build(params string[] lines) => IgnoreMatcher.Parse(lines);

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var m = Build("# a comment", "", "   ");
        Assert.Equal(0, m.RuleCount);
        Assert.False(m.IsIgnored("anything.js", false));
    }

    [Fact]
    public void Exact_relative_path_matches()
    {
        var m = Build("src/legacy/old.js");
        Assert.True(m.IsIgnored("src/legacy/old.js", false));
        Assert.False(m.IsIgnored("src/legacy/new.js", false));
        Assert.False(m.IsIgnored("other/src/legacy/old.js", false));
    }

    [Fact]
    public void Bare_name_matches_at_any_depth()
    {
        var m = Build("secrets.json");
        Assert.True(m.IsIgnored("secrets.json", false));
        Assert.True(m.IsIgnored("config/deep/secrets.json", false));
    }

    [Fact]
    public void Star_extension_matches_any_folder()
    {
        var m = Build("*.log");
        Assert.True(m.IsIgnored("debug.log", false));
        Assert.True(m.IsIgnored("logs/app/debug.log", false));
        Assert.False(m.IsIgnored("debug.logger.js", false));
    }

    [Fact]
    public void Trailing_slash_only_matches_directories()
    {
        var m = Build("legacy/");
        Assert.True(m.IsIgnored("legacy", true));
        Assert.True(m.IsIgnored("src/legacy", true));
        Assert.False(m.IsIgnored("legacy", false));
    }

    [Fact]
    public void Leading_slash_anchors_to_root()
    {
        var m = Build("/build");
        Assert.True(m.IsIgnored("build", true));
        Assert.False(m.IsIgnored("src/build", true));
    }

    [Fact]
    public void Double_star_spans_directories()
    {
        var m = Build("docs/**/*.md");
        Assert.True(m.IsIgnored("docs/a.md", false));
        Assert.True(m.IsIgnored("docs/guide/deep/b.md", false));
        Assert.False(m.IsIgnored("src/a.md", false));
    }

    [Fact]
    public void Single_star_does_not_cross_directories()
    {
        var m = Build("src/*.js");
        Assert.True(m.IsIgnored("src/a.js", false));
        Assert.False(m.IsIgnored("src/nested/a.js", false));
    }

    [Fact]
    public void Negation_re_includes_a_file()
    {
        var m = Build("*.json", "!keep.json");
        Assert.True(m.IsIgnored("data.json", false));
        Assert.False(m.IsIgnored("keep.json", false));
    }

    [Fact]
    public void Matching_is_case_insensitive_and_accepts_backslashes()
    {
        var m = Build("Src\\Legacy");
        Assert.True(m.IsIgnored("src/legacy", true));
    }
}
