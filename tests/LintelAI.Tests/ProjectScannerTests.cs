using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

public sealed class ProjectScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aipa-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string relative, string content = "x = 1")
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void Finds_source_files_and_skips_build_output()
    {
        Write("src/app.js");
        Write("src/util/helper.py");
        Write("node_modules/lib/index.js");
        Write("bin/Debug/out.js");
        Write(".git/config.txt");

        var result = new ProjectScanner().Scan(_root);

        Assert.Equal(new[] { "src/app.js", "src/util/helper.py" }, result.Files);
    }

    [Fact]
    public void Skips_unknown_extensions_lockfiles_and_minified_bundles()
    {
        Write("a.js");
        Write("image.png");
        Write("package-lock.json", "{}");
        Write("vendor.min.js");
        Write("notes.txt");

        var files = new ProjectScanner().Scan(_root).Files;

        Assert.Equal(new[] { "a.js", "notes.txt" }, files);
    }

    [Fact]
    public void Honours_the_AIIgnore_file()
    {
        Write("keep.js");
        Write("legacy/old.js");
        Write("config.json", "{}");
        Write(".AIIgnore", "# skip these\nlegacy/\n*.json\n");

        var result = new ProjectScanner().Scan(_root);

        Assert.Equal(new[] { "keep.js" }, result.Files);
        Assert.Equal(2, result.IgnoredCount);
    }

    [Fact]
    public void Skips_binary_files_that_wear_a_text_extension()
    {
        Write("real.js");
        File.WriteAllBytes(Path.Combine(_root, "fake.js"), new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x01 });

        Assert.Equal(new[] { "real.js" }, new ProjectScanner().Scan(_root).Files);
    }

    [Fact]
    public void Skips_empty_files()
    {
        Write("empty.js", "");
        Write("full.js");

        Assert.Equal(new[] { "full.js" }, new ProjectScanner().Scan(_root).Files);
    }

    [Fact]
    public void Honours_cancellation()
    {
        Write("a.js");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new ProjectScanner().Scan(_root, cts.Token));
    }
}
