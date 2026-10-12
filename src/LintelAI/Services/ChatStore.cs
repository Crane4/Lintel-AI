using System.Text.Json;

namespace LintelAI.Services;

public sealed record StoredMessage(bool IsUser, string Text);

/// <summary>Remembers each file's conversation between sessions, keyed by project folder and file.</summary>
public sealed class ChatStore
{
    private const int MaxConversations = 120;
    private const int MaxMessages = 60;

    private sealed class Conversation
    {
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
        public List<StoredMessage> Messages { get; set; } = new();
    }

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Conversation>? _data;

    public ChatStore(string? path = null) => _path = path ?? AppPaths.ChatsFile;

    private static string Key(string folder, string file) =>
        folder.TrimEnd('\\', '/').ToLowerInvariant() + "|" + file.ToLowerInvariant();

    private Dictionary<string, Conversation> Data()
    {
        if (_data is not null) return _data;

        try
        {
            if (File.Exists(_path))
            {
                _data = JsonSerializer.Deserialize<Dictionary<string, Conversation>>(File.ReadAllText(_path));
            }
        }
        catch { /* a damaged file just means no remembered chats */ }

        return _data ??= new Dictionary<string, Conversation>();
    }

    public IReadOnlyList<StoredMessage> Load(string folder, string file)
    {
        lock (_gate)
        {
            return Data().TryGetValue(Key(folder, file), out var conversation)
                ? conversation.Messages.ToList()
                : Array.Empty<StoredMessage>();
        }
    }

    public void Save(string folder, string file, IEnumerable<StoredMessage> messages)
    {
        lock (_gate)
        {
            var data = Data();
            var key = Key(folder, file);
            var list = messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).TakeLast(MaxMessages).ToList();

            if (list.Count == 0) data.Remove(key);
            else data[key] = new Conversation { Messages = list };

            if (data.Count > MaxConversations)
            {
                foreach (var stale in data.OrderBy(kv => kv.Value.UpdatedAt).Take(data.Count - MaxConversations).Select(kv => kv.Key).ToList())
                    data.Remove(stale);
            }

            try { AppPaths.WriteAtomic(_path, JsonSerializer.Serialize(data)); } catch { }
        }
    }
}
