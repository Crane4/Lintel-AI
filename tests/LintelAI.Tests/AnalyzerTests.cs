using System.Net;
using System.Text;
using System.Text.Json;
using LintelAI.Models;
using LintelAI.Services;
using Xunit;

namespace LintelAI.Tests;

/// <summary>Stands in for Ollama so the analyzer and client can be exercised end to end.</summary>
internal sealed class FakeOllama(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = new();
    public List<string> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(request.RequestUri!);
        Bodies.Add(body);
        return respond(request, body);
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>Wraps an analysis object the way /api/generate does.</summary>
    public static HttpResponseMessage Generate(object analysis) =>
        Json(JsonSerializer.Serialize(new { response = JsonSerializer.Serialize(analysis), done = true }));
}

/// <summary>Hands out at most a few bytes per read, like a slow network would.</summary>
internal sealed class TrickleStream(byte[] data, int chunk) : Stream
{
    private int _position;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = Math.Min(Math.Min(count, chunk), data.Length - _position);
        Array.Copy(data, _position, buffer, offset, n);
        _position += n;
        return n;
    }
}

public sealed class FileAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aipa-an-" + Guid.NewGuid().ToString("N"));

    public FileAnalyzerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Builds_findings_with_snippets_kind_and_a_computed_score()
    {
        Write("a.js", "let a = 1;\nwhile (true) {}\nlet b = 2;\n");
        var handler = new FakeOllama((_, _) => FakeOllama.Generate(new
        {
            issues = new[] { new { kind = "bug", severity = "high", type = "loop", description = "never exits", line = 2 } },
            summary = "has a loop"
        }));

        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "a.js", "m", 12000, Timeout, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(80, result.Score);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueKind.Bug, issue.Kind);
        Assert.Equal(2, issue.Snippet!.Single(l => l.IsTarget).Number);
        Assert.Equal("has a loop", result.Summary);
    }

    [Fact]
    public async Task Sends_line_numbers_and_asks_for_json()
    {
        Write("a.js", "one\ntwo\n");
        var handler = new FakeOllama((_, _) => FakeOllama.Generate(new { issues = Array.Empty<object>(), summary = "ok" }));

        await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "a.js", "qwen", 12000, Timeout, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("qwen", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("json", body.RootElement.GetProperty("format").GetString());
        Assert.Contains("1: one", body.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Reviews_a_long_file_in_parts_and_keeps_real_line_numbers()
    {
        var lines = Enumerable.Range(1, 120).Select(i => $"statement_{i:000};");
        Write("big.js", string.Join('\n', lines));

        var call = 0;
        var handler = new FakeOllama((_, body) =>
        {
            call++;
            // Each part reports one issue on a line that only exists in that part.
            var line = call == 1 ? 5 : 110;
            return FakeOllama.Generate(new
            {
                issues = new[] { new { kind = "smell", severity = "low", type = "practice", description = $"issue {call}", line } },
                summary = $"part {call}"
            });
        });

        // ~16 chars per numbered line, budget for roughly 40 lines per part
        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "big.js", "m", 700, Timeout, CancellationToken.None);

        Assert.True(handler.Requests.Count > 1);
        Assert.Contains(result.Issues, i => i.Line == 5);
        Assert.Contains(result.Issues, i => i.Line == 110);
        Assert.Equal("part 1", result.Summary);
    }

    [Fact]
    public async Task Flags_a_file_that_is_longer_than_all_the_parts_together()
    {
        Write("huge.js", string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"line_{i};")));
        var handler = new FakeOllama((_, _) => FakeOllama.Generate(new { issues = Array.Empty<object>(), summary = "x" }));

        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "huge.js", "m", 500, Timeout, CancellationToken.None);

        Assert.Equal(FileAnalyzer.MaxParts, handler.Requests.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Drops_unused_claims_about_names_another_file_uses()
    {
        Write("lib.js", "function remember(item) { return item; }\nfunction trulyUnused() {}\nmodule.exports = { remember };\n");
        Write("main.js", "const { remember } = require('./lib');\nremember(1);\n");

        var handler = new FakeOllama((_, _) => FakeOllama.Generate(new
        {
            issues = new object[]
            {
                new { kind = "smell", severity = "medium", type = "dead-code", description = "The function remember is never called.", line = 1 },
                new { kind = "smell", severity = "low", type = "dead-code", description = "The function trulyUnused is never called.", line = 2 }
            },
            summary = "s"
        }));

        var index = ProjectIndex.Build(_root, new[] { "lib.js", "main.js" });
        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "lib.js", "m", 12000, Timeout, CancellationToken.None, index);

        var issue = Assert.Single(result.Issues);
        Assert.Contains("trulyUnused", issue.Description);
    }

    [Fact]
    public async Task Tells_the_model_what_the_rest_of_the_project_uses()
    {
        Write("lib.js", "function remember(item) { return item; }\n");
        Write("main.js", "remember(1);\n");
        var handler = new FakeOllama((_, _) => FakeOllama.Generate(new { issues = Array.Empty<object>(), summary = "" }));

        var index = ProjectIndex.Build(_root, new[] { "lib.js", "main.js" });
        await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "lib.js", "m", 12000, Timeout, CancellationToken.None, index);

        using var body = JsonDocument.Parse(handler.Bodies.Single());
        var prompt = body.RootElement.GetProperty("prompt").GetString()!;
        Assert.Contains("main.js", prompt);
        Assert.Contains("remember", prompt);
    }

    [Fact]
    public async Task Reports_a_connection_failure_instead_of_throwing()
    {
        Write("a.js", "x");
        var handler = new FakeOllama((_, _) => throw new HttpRequestException("refused"));

        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "a.js", "m", 12000, Timeout, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Contains("reach Ollama", result.Error);
        Assert.Null(result.Score);
    }

    [Fact]
    public async Task Reports_unparseable_replies_with_the_raw_text()
    {
        Write("a.js", "x");
        var handler = new FakeOllama((_, _) =>
            FakeOllama.Json(JsonSerializer.Serialize(new { response = "I am sorry, I cannot do that.", done = true })));

        var result = await new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "a.js", "m", 12000, Timeout, CancellationToken.None);

        Assert.NotNull(result.Error);
        Assert.Equal("I am sorry, I cannot do that.", result.RawResponse);
    }

    [Fact]
    public async Task Propagates_user_cancellation()
    {
        Write("a.js", "x");
        using var cts = new CancellationTokenSource();
        var handler = new FakeOllama((_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileAnalyzer(new OllamaClient(handler))
            .AnalyzeAsync(_root, "a.js", "m", 12000, Timeout, cts.Token));
    }
}

public class OllamaClientTests
{
    [Fact]
    public async Task Tries_127_0_0_1_before_the_localhost_name()
    {
        var handler = new FakeOllama((_, _) => FakeOllama.Json("""{"models":[{"name":"b"},{"name":"a"}]}"""));
        var client = new OllamaClient(handler) { BaseUrl = "http://localhost:11434" };

        var models = await client.GetModelsAsync();

        Assert.Equal(new[] { "a", "b" }, models);
        Assert.Equal("127.0.0.1", handler.Requests[0].Host);
        Assert.Equal(11434, handler.Requests[0].Port);
    }

    [Fact]
    public async Task Falls_back_to_localhost_when_the_loopback_address_refuses()
    {
        var handler = new FakeOllama((req, _) =>
        {
            if (req.RequestUri!.Host == "127.0.0.1") throw new HttpRequestException("refused");
            return FakeOllama.Json("""{"models":[{"name":"m"}]}""");
        });
        var client = new OllamaClient(handler) { BaseUrl = "http://localhost:11434" };

        Assert.Equal(new[] { "m" }, await client.GetModelsAsync());
        Assert.Equal(new[] { "127.0.0.1", "localhost" }, handler.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task Leaves_remote_hosts_alone()
    {
        var handler = new FakeOllama((_, _) => FakeOllama.Json("""{"models":[]}"""));
        var client = new OllamaClient(handler) { BaseUrl = "http://gpu-box.lan:11434/" };

        await client.GetModelsAsync();

        Assert.Equal("gpu-box.lan", Assert.Single(handler.Requests).Host);
    }

    [Fact]
    public async Task Surfaces_the_servers_own_error_message()
    {
        var handler = new FakeOllama((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"error":"model 'nope' not found"}""")
        });
        var client = new OllamaClient(handler);

        var ex = await Assert.ThrowsAsync<OllamaException>(() =>
            client.GenerateJsonAsync("nope", "hi", TimeSpan.FromSeconds(5)));

        Assert.Equal("model 'nope' not found", ex.Message);
    }

    [Fact]
    public async Task Streams_a_chat_reply_even_when_lines_arrive_in_fragments()
    {
        var ndjson =
            """{"message":{"role":"assistant","content":"Hel"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":"lo, "},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":"world"},"done":false}""" + "\n" +
            """{"message":{"role":"assistant","content":""},"done":true}""" + "\n";

        var handler = new FakeOllama((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TrickleStream(Encoding.UTF8.GetBytes(ndjson), chunk: 7))
        });
        var client = new OllamaClient(handler);

        var parts = new List<string>();
        await foreach (var chunk in client.ChatStreamAsync(
                           "m", new[] { new ChatTurn("user", "hi") }, TimeSpan.FromSeconds(5)))
            parts.Add(chunk);

        Assert.Equal("Hello, world", string.Concat(parts));
        Assert.Equal(3, parts.Count);
    }

    [Fact]
    public async Task A_stream_error_line_becomes_an_exception()
    {
        var handler = new FakeOllama((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"error":"out of memory"}""" + "\n")
        });
        var client = new OllamaClient(handler);

        await Assert.ThrowsAsync<OllamaException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("m", new[] { new ChatTurn("user", "x") }, TimeSpan.FromSeconds(5))) { }
        });
    }
}

public sealed class ProjectIndexTests
{
    [Fact]
    public void Finds_declared_names_across_languages()
    {
        const string code = """
            function fetchUser(id) {}
            class UserStore {}
            const MAX_RETRIES = 3;
            def load_config(path):
            public static async Task<int> Compute(int x) {
            exports.helper = helper;
            module.exports = { alpha, beta: 2 };
            """;

        var names = ProjectIndex.DeclaredNames(code);

        Assert.Contains("fetchUser", names);
        Assert.Contains("UserStore", names);
        Assert.Contains("MAX_RETRIES", names);
        Assert.Contains("load_config", names);
        Assert.Contains("Compute", names);
        Assert.Contains("helper", names);
        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    [Fact]
    public void Ignores_noise_words_and_very_short_names()
    {
        var names = ProjectIndex.DeclaredNames("const i = 0;\nlet value = 1;\nfunction go() {}");
        Assert.Empty(names);
    }

    [Fact]
    public void Contradiction_check_only_fires_for_unused_claims_about_used_names()
    {
        var context = new ProjectContext(Array.Empty<string>(), 0, new[] { "remember" });

        Assert.True(context.ContradictsProject(
            new Issue(Severity.Low, "dead-code", "remember is never called", 3)));
        Assert.True(context.ContradictsProject(
            new Issue(Severity.Low, "practice", "The function remember is unused.", 3)));

        // different problem with the same name: keep it
        Assert.False(context.ContradictsProject(
            new Issue(Severity.High, "memory", "remember leaks a timer", 3)));
        // unused claim about some other name: keep it
        Assert.False(context.ContradictsProject(
            new Issue(Severity.Low, "dead-code", "rememberAll is never called", 3)));
    }
}

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aipa-ps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Chat_store_round_trips_per_file_and_folder()
    {
        var path = Path.Combine(_dir, "chats.json");
        var store = new ChatStore(path);

        store.Save(@"C:\proj", "src/a.js", new[] { new StoredMessage(true, "hi"), new StoredMessage(false, "hello") });

        var again = new ChatStore(path);
        var loaded = again.Load(@"C:\proj\", "SRC/A.JS");

        Assert.Equal(2, loaded.Count);
        Assert.True(loaded[0].IsUser);
        Assert.Equal("hello", loaded[1].Text);
        Assert.Empty(again.Load(@"C:\other", "src/a.js"));
    }

    [Fact]
    public void Saving_an_empty_conversation_forgets_it()
    {
        var path = Path.Combine(_dir, "chats.json");
        var store = new ChatStore(path);

        store.Save("f", "a", new[] { new StoredMessage(true, "x") });
        store.Save("f", "a", Array.Empty<StoredMessage>());

        Assert.Empty(new ChatStore(path).Load("f", "a"));
    }

    [Fact]
    public void A_corrupt_chat_file_is_treated_as_empty()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "chats.json");
        File.WriteAllText(path, "{ not json");

        Assert.Empty(new ChatStore(path).Load("f", "a"));
    }

    [Fact]
    public void History_files_keep_their_findings_through_json()
    {
        var analysis = new FileAnalysis
        {
            Path = "src/a.js",
            Score = 70,
            Summary = "meh",
            LineCount = 40,
            Issues = new[]
            {
                new Issue(Severity.High, "security", "hard-coded key", 7, IssueKind.Vulnerability,
                    new[] { new SnippetLine(6, "a", false), new SnippetLine(7, "key = 'x'", true) })
            }
        };

        var json = JsonSerializer.Serialize(new HistoryEntry { Files = { HistoryFile.From(analysis) } });
        var back = JsonSerializer.Deserialize<HistoryEntry>(json)!;
        var restored = back.Files.Single().ToAnalysis();

        Assert.True(back.HasDetails);
        Assert.Equal(70, restored.Score);
        var issue = Assert.Single(restored.Issues);
        Assert.Equal(IssueKind.Vulnerability, issue.Kind);
        Assert.Equal(Severity.High, issue.Severity);
        Assert.Equal(7, issue.Snippet!.Single(l => l.IsTarget).Number);
    }

    [Fact]
    public void Runs_saved_by_the_first_version_are_marked_as_lacking_details()
    {
        const string legacy = """
            {"Id":"6f4b8b1e-0000-0000-0000-000000000001","Folder":"C:\\p","Model":"m","AverageScore":80,
             "TotalFiles":1,"TotalIssues":2,"Files":[{"Path":"a.js","Score":80,"IssueCount":2}]}
            """;

        var entry = JsonSerializer.Deserialize<HistoryEntry>(legacy)!;

        Assert.False(entry.HasDetails);
    }

    [Fact]
    public void Env_and_key_files_are_never_scanned()
    {
        Assert.True(ProjectScanner.IsSecretsFile(".env"));
        Assert.True(ProjectScanner.IsSecretsFile(".env.production"));
        Assert.True(ProjectScanner.IsSecretsFile("server.pem"));
        Assert.False(ProjectScanner.IsSecretsFile("environment.js"));
    }
}

public class LineSnapperTests
{
    private static readonly string[] Code =
    {
        "// header",                                 // 1
        "function fillTheVoid() {",                  // 2
        "    console.log('start');",                 // 3
        "    setInterval(() => {",                   // 4
        "        memoryVoid.push(chunk);",           // 5
        "    }, 50);",                               // 6
        "}",                                         // 7
        "",                                          // 8
        "const total = compute(1);",                 // 9
        "let x = 0;",                                // 10
    };

    [Fact]
    public void Moves_a_finding_onto_the_line_that_names_the_identifier()
    {
        var line = LineSnapper.Snap(Code, 2, "setInterval pushes data into a global array forever");
        Assert.Equal(4, line);
    }

    [Fact]
    public void Understands_backticks_and_call_syntax()
    {
        Assert.Equal(5, LineSnapper.Snap(Code, 3, "The `memoryVoid` array grows without bound"));
        Assert.Equal(9, LineSnapper.Snap(Code, 10, "compute() is called with a constant"));
    }

    [Fact]
    public void Leaves_the_line_alone_when_it_already_mentions_the_name()
    {
        Assert.Equal(4, LineSnapper.Snap(Code, 4, "setInterval is never cleared; memoryVoid keeps growing"));
    }

    [Fact]
    public void Leaves_the_line_alone_without_identifier_like_words()
    {
        Assert.Equal(3, LineSnapper.Snap(Code, 3, "This loop never exits and blocks the thread"));
    }

    [Fact]
    public void Does_not_chase_names_that_are_far_away()
    {
        // `compute` is on line 9, more than six lines from line 1
        Assert.Equal(1, LineSnapper.Snap(Code, 1, "compute() result is ignored"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(99)]
    public void Passes_through_missing_or_out_of_range_lines(int? line) =>
        Assert.Equal(line, LineSnapper.Snap(Code, line, "setInterval leaks"));
}
