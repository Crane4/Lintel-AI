using System.Net;
using System.Text;
using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

public class UpdateCheckerTests
{
    private static readonly Version Running = new(1, 0, 0);

    private static UpdateChecker WithRelease(string tag, string url = "https://github.com/Crane4/lintel-ai/releases/tag/v1.0.1",
        string body = "Notes", HttpStatusCode status = HttpStatusCode.OK, List<Uri>? seen = null)
    {
        var json = $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","body":"{{body}}","prerelease":false}""";
        return new UpdateChecker(new FakeOllama((req, _) =>
        {
            seen?.Add(req.RequestUri!);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }));
    }

    [Fact]
    public async Task Reports_a_newer_release_with_its_page_and_notes()
    {
        var result = await WithRelease("v1.0.1", body: "Fixes a crash").CheckAsync("Crane4/lintel-ai", Running);

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(new Version(1, 0, 1), result.Update!.Version);
        Assert.Equal("v1.0.1", result.Update.Tag);
        Assert.Equal("Fixes a crash", result.Update.Notes);
        Assert.StartsWith("https://github.com/", result.Update.Url);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0")]
    [InlineData("v0.9.9")]
    public async Task Same_or_older_releases_are_not_updates(string tag)
    {
        var result = await WithRelease(tag).CheckAsync("Crane4/lintel-ai", Running);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Asks_the_github_api_for_the_configured_repository()
    {
        var seen = new List<Uri>();
        await WithRelease("v1.0.1", seen: seen).CheckAsync("someone/fork", Running);

        Assert.Equal("https://api.github.com/repos/someone/fork/releases/latest", Assert.Single(seen).ToString());
    }

    [Fact]
    public async Task A_repository_without_releases_counts_as_up_to_date()
    {
        var result = await WithRelease("v9.9.9", status: HttpStatusCode.NotFound).CheckAsync("a/b", Running);
        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Network_failures_are_reported_not_thrown()
    {
        var checker = new UpdateChecker(new FakeOllama((_, _) => throw new HttpRequestException("offline")));

        var result = await checker.CheckAsync("a/b", Running);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Rate_limiting_is_a_failure_with_a_reason()
    {
        var result = await WithRelease("v2.0.0", status: HttpStatusCode.Forbidden).CheckAsync("a/b", Running);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task Refuses_a_release_page_that_is_not_on_github()
    {
        var result = await WithRelease("v2.0.0", url: "https://evil.example/download.exe").CheckAsync("a/b", Running);

        Assert.Equal(UpdateStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Rejects_a_malformed_repository_name_without_calling_out()
    {
        var seen = new List<Uri>();
        var result = await WithRelease("v2.0.0", seen: seen).CheckAsync("../../etc/passwd", Running);

        Assert.Equal(UpdateStatus.Failed, result.Status);
        Assert.Empty(seen);
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("V2", 2, 0, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    public void Parses_the_tag_styles_people_actually_use(string tag, int major, int minor, int patch)
    {
        Assert.True(UpdateChecker.TryParseVersion(tag, out var version));
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v.1")]
    [InlineData("1.x.3")]
    public void Rejects_tags_that_are_not_versions(string tag) =>
        Assert.False(UpdateChecker.TryParseVersion(tag, out _));

    [Theory]
    [InlineData("https://github.com/Crane4/lintel-ai/releases/tag/v1", true)]
    [InlineData("http://github.com/x/y", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(null, false)]
    public void Only_github_https_pages_are_opened(string? url, bool expected) =>
        Assert.Equal(expected, UpdateChecker.IsGitHubUrl(url));

    [Fact]
    public void The_running_version_comes_from_the_build()
    {
        Assert.Equal(new Version(1, 0, 0), AppInfo.Current);
        Assert.Equal("1.0.0", AppInfo.CurrentText);
    }
}
