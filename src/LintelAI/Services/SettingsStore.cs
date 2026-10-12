using System.Text.Json;
using LintelAI.Models;

namespace LintelAI.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { /* corrupt settings fall back to defaults */ }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try { AppPaths.WriteAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, Options)); }
        catch { /* not worth interrupting the user over */ }
    }
}
