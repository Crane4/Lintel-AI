using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace LintelAI.Services;

public static class AppInfo
{
    /// <summary>Where releases are published. Forks can point the app elsewhere in settings.json.</summary>
    public const string DefaultRepository = "Crane4/Lintel-AI";

    /// <summary>The version baked into this build (the csproj &lt;Version&gt;), without any commit suffix.</summary>
    public static Version Current { get; } = ReadCurrent();

    public static string CurrentText => Current.ToString(3);

    private static Version ReadCurrent()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (informational is not null && UpdateChecker.TryParseVersion(informational, out var parsed))
            return parsed;

        return assembly.GetName().Version ?? new Version(0, 0, 0);
    }
}

public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateInfo(Version Version, string Tag, string Url, string Notes);

public sealed record UpdateResult(UpdateStatus Status, UpdateInfo? Update = null, string? Error = null);

/// <summary>
/// Asks GitHub for the newest published release and compares it with the running version.
/// It only ever reads a version number: nothing is downloaded or installed.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private readonly HttpClient _http;

    public UpdateChecker(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ProjectAnalyzer", AppInfo.CurrentText));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateResult> CheckAsync(string repository, Version current, CancellationToken ct = default)
    {
        if (!IsValidRepository(repository))
            return new UpdateResult(UpdateStatus.Failed, Error: "The update repository should look like owner/name.");

        try
        {
            var url = $"https://api.github.com/repos/{repository}/releases/latest";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            // A repository without any release yet answers 404; that simply means nothing is newer.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateResult(UpdateStatus.UpToDate);

            if (!response.IsSuccessStatusCode)
                return new UpdateResult(UpdateStatus.Failed, Error: $"GitHub answered with HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return Evaluate(doc.RootElement, current);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return new UpdateResult(UpdateStatus.Failed, Error: "Couldn't reach GitHub to check for updates.");
        }
    }

    internal static UpdateResult Evaluate(JsonElement release, Version current)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || !TryParseVersion(tag, out var latest))
            return new UpdateResult(UpdateStatus.Failed, Error: "The latest release has a tag the app can't read.");

        if (latest <= current) return new UpdateResult(UpdateStatus.UpToDate);

        var page = release.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        if (!IsGitHubUrl(page))
            return new UpdateResult(UpdateStatus.Failed, Error: "The latest release has no usable page.");

        var notes = release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        if (notes.Length > 600) notes = notes[..600].TrimEnd() + "…";

        return new UpdateResult(UpdateStatus.Available, new UpdateInfo(latest, tag, page!, notes));
    }

    /// <summary>Reads "v1.2.3", "1.2", "1.2.3-beta.1" or "1.2.3+abc123" as a plain Version.</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var s = text.Trim().TrimStart('v', 'V');

        var cut = s.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) s = s[..cut];

        var parts = s.Split('.');
        if (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsDigit))) return false;

        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return Version.TryParse(string.Join('.', parts), out version!);
    }

    /// <summary>Only ever hand a github.com page to the browser, whatever the response said.</summary>
    public static bool IsGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "github.com" || uri.Host.EndsWith(".github.com", StringComparison.Ordinal));

    public static bool IsValidRepository(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
    }

    public void Dispose() => _http.Dispose();
}
