using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

public sealed class AppUpdaterTests
{
    [Fact]
    public void A_copy_that_was_not_put_there_by_the_setup_is_not_installed()
    {
        // Tests (like "dotnet run") are not a Velopack install, so the app must fall back to the
        // notify-only update check instead of trying to replace itself.
        Assert.False(new AppUpdater().IsInstalled);
    }

    [Fact]
    public async Task Refuses_a_repository_that_is_not_on_github()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AppUpdater().CheckAsync("evil.example.com/x/../../y"));

        Assert.Contains("owner/name", ex.Message);
    }
}
