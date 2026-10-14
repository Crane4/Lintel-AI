using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LintelAI.Services;

public sealed record ChatTurn(string Role, string Content);

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;

    public OllamaClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request limits are applied with cancellation tokens
    }

    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama's default window is small enough to silently cut source files off.</summary>
    public int ContextLength { get; set; } = 8192;

    /// <summary>
    /// "localhost" resolves to IPv6 first on Windows, which costs seconds when Ollama only listens
    /// on IPv4. Try 127.0.0.1 first and fall back to the name the user actually typed.
    /// </summary>
    private IEnumerable<Uri> Candidates(string path)
    {
        var primary = new Uri($"{BaseUrl.TrimEnd('/')}/{path}");
        if (string.Equals(primary.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            yield return new UriBuilder(primary) { Host = "127.0.0.1" }.Uri;
        yield return primary;
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path,
        Func<Uri, HttpRequestMessage> build,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        HttpRequestException? last = null;
        foreach (var uri in Candidates(path))
        {
            using var request = build(uri);
            try
            {
                return await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("No address to try.");
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(6));

            using var response = await SendAsync(
                "api/tags", uri => new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            await EnsureSuccess(response, linked.Token).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token).ConfigureAwait(false);

            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var model in models.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
                        names.Add(value);
                }
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex) when (!IsUserCancel(ex, ct))
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// One-shot completion that asks Ollama to constrain the output to valid JSON. Pass a JSON
    /// schema to constrain it further (needs a recent Ollama); without one it is plain "json" mode.
    /// </summary>
    public async Task<string> GenerateJsonAsync(
        string model,
        string prompt,
        TimeSpan timeout,
        CancellationToken ct = default,
        object? schema = null,
        double temperature = 0.1)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            var body = new
            {
                model,
                prompt,
                stream = false,
                format = schema ?? "json",
                options = new { temperature, num_ctx = ContextLength }
            };

            using var response = await SendAsync(
                "api/generate",
                uri => new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) },
                HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
            await EnsureSuccess(response, linked.Token).ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: linked.Token).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("error", out var error))
                throw new OllamaException(error.GetString() ?? "Ollama returned an error.");

            return doc.RootElement.TryGetProperty("response", out var text) ? text.GetString() ?? "" : "";
        }
        catch (Exception ex) when (!IsUserCancel(ex, ct))
        {
            throw Translate(ex);
        }
    }

    /// <summary>Streams an assistant reply token by token. <paramref name="idleTimeout"/> resets on every chunk.</summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(
        string model,
        IReadOnlyList<ChatTurn> messages,
        TimeSpan idleTimeout,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(idleTimeout);

        HttpResponseMessage response;
        StreamReader reader;

        try
        {
            var body = new
            {
                model,
                stream = true,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                options = new { num_ctx = ContextLength }
            };

            response = await SendAsync(
                "api/chat",
                uri => new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) },
                HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            await EnsureSuccess(response, linked.Token).ConfigureAwait(false);

            var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            reader = new StreamReader(stream, Encoding.UTF8);
        }
        catch (Exception ex) when (!IsUserCancel(ex, ct))
        {
            throw Translate(ex);
        }

        using (response)
        using (reader)
        {
            while (true)
            {
                string? line;
                try
                {
                    linked.CancelAfter(idleTimeout);
                    line = await reader.ReadLineAsync(linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsUserCancel(ex, ct))
                {
                    throw Translate(ex);
                }

                if (line is null) yield break;
                if (line.Length == 0) continue;

                string? chunk = null, error = null;
                var done = false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var e)) error = e.GetString();
                    if (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c))
                        chunk = c.GetString();
                    if (root.TryGetProperty("done", out var d)) done = d.ValueKind == JsonValueKind.True;
                }
                catch (JsonException)
                {
                    continue; // ignore a malformed line rather than abort the whole reply
                }

                if (error is not null) throw new OllamaException(error);
                if (!string.IsNullOrEmpty(chunk)) yield return chunk;
                if (done) yield break;
            }
        }
    }

    private static bool IsUserCancel(Exception ex, CancellationToken userToken) =>
        ex is OperationCanceledException && userToken.IsCancellationRequested;

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        string? detail = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error)) detail = error.GetString();
        }
        catch { }

        throw new OllamaException(detail ?? $"Ollama answered with HTTP {(int)response.StatusCode}.");
    }

    private OllamaException Translate(Exception ex) => ex switch
    {
        OllamaException o => o,
        HttpRequestException => new OllamaException($"Can't reach Ollama at {BaseUrl}. Is it running?", ex),
        OperationCanceledException => new OllamaException("Ollama took too long to answer.", ex),
        JsonException => new OllamaException("Ollama sent a reply that couldn't be read.", ex),
        _ => new OllamaException(ex.Message, ex)
    };

    public void Dispose() => _http.Dispose();
}
