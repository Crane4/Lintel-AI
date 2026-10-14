namespace LintelAI.Services.Security.Advanced;

/// <summary>The one thing the advanced scan needs from a model: answer a prompt with JSON.</summary>
public interface ISecurityModel
{
    /// <exception cref="OllamaException">The model could not be reached or answered with an error.</exception>
    Task<string> AskAsync(string prompt, object schema, CancellationToken ct);
}

public sealed class OllamaSecurityModel(OllamaClient client, string model, TimeSpan timeout) : ISecurityModel
{
    public async Task<string> AskAsync(string prompt, object schema, CancellationToken ct)
    {
        try
        {
            // Temperature 0: the same alert should get the same answer every time.
            return await client.GenerateJsonAsync(model, prompt, timeout, ct, schema, temperature: 0).ConfigureAwait(false);
        }
        catch (OllamaException ex) when (!ct.IsCancellationRequested &&
                                         ex.Message.Contains("format", StringComparison.OrdinalIgnoreCase))
        {
            // Older Ollama builds only understand format: "json"; the schema is then rejected.
            return await client.GenerateJsonAsync(model, prompt, timeout, ct, null, temperature: 0).ConfigureAwait(false);
        }
    }
}
