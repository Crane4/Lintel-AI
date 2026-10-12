using System.Collections.ObjectModel;
using LintelAI.Models;
using LintelAI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LintelAI.ViewModels;

/// <summary>What a per-file chat needs from the rest of the app.</summary>
public interface IChatHost
{
    OllamaClient Client { get; }
    string? SelectedModel { get; }
    string? FolderPath { get; }
    AppSettings Settings { get; }
    void Notify(string text, ToastKind kind = ToastKind.Info);
    IReadOnlyList<StoredMessage> LoadChat(string path);
    void SaveChat(string path, IEnumerable<StoredMessage> messages);
}

public abstract partial class SegmentViewModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
}

public sealed class ProseSegmentViewModel : SegmentViewModel { }

public sealed partial class CodeSegmentViewModel : SegmentViewModel
{
    [ObservableProperty] private string? _language;
    [ObservableProperty] private bool _justCopied;

    /// <summary>The fence's language, or the file's own language when the model did not name one.</summary>
    [ObservableProperty] private string? _highlightLanguage;

    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);

    partial void OnLanguageChanged(string? value) => OnPropertyChanged(nameof(HasLanguage));

    [RelayCommand]
    private async Task CopyAsync()
    {
        await Clipboard.SetTextAsync(Text);
        JustCopied = true;
        await Task.Delay(1400);
        JustCopied = false;
    }
}

public sealed partial class ChatMessageViewModel : ObservableObject
{
    private readonly string? _defaultLanguage;

    public ChatMessageViewModel(bool isUser, string text = "", string? defaultLanguage = null)
    {
        IsUser = isUser;
        _defaultLanguage = defaultLanguage;
        SetRaw(text);
    }

    private string? LanguageFor(string? fenceLanguage) =>
        string.IsNullOrWhiteSpace(fenceLanguage) ? _defaultLanguage : fenceLanguage;

    public bool IsUser { get; }
    public bool IsAssistant => !IsUser;
    public string Raw { get; private set; } = "";

    /// <summary>The reply with any hidden reasoning stripped out.</summary>
    public string Display => IsUser ? Raw : JsonExtractor.StripThinking(Raw);

    public ObservableCollection<SegmentViewModel> Segments { get; } = new();

    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isError;

    public void Append(string chunk) => SetRaw(Raw + chunk);

    public void SetRaw(string raw)
    {
        Raw = raw;
        var blocks = MessageParser.Split(Display);

        // Update segments in place so controls aren't rebuilt for every streamed token.
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var existing = i < Segments.Count ? Segments[i] : null;

            if (existing is CodeSegmentViewModel code && block.IsCode)
            {
                code.Text = block.Text;
                code.Language = block.Language;
                code.HighlightLanguage = LanguageFor(block.Language);
            }
            else if (existing is ProseSegmentViewModel prose && !block.IsCode)
            {
                prose.Text = block.Text;
            }
            else
            {
                SegmentViewModel created = block.IsCode
                    ? new CodeSegmentViewModel
                    {
                        Text = block.Text,
                        Language = block.Language,
                        HighlightLanguage = LanguageFor(block.Language)
                    }
                    : new ProseSegmentViewModel { Text = block.Text };

                if (existing is null) Segments.Add(created);
                else Segments[i] = created;
            }
        }

        while (Segments.Count > blocks.Count) Segments.RemoveAt(Segments.Count - 1);
    }
}

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly FileItemViewModel _file;
    private readonly IChatHost _host;
    private CancellationTokenSource? _cts;

    public ChatViewModel(FileItemViewModel file, IChatHost host)
    {
        _file = file;
        _host = host;

        foreach (var saved in host.LoadChat(file.Path))
            Messages.Add(new ChatMessageViewModel(saved.IsUser, saved.Text, file.Language));

        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    private void Persist() =>
        _host.SaveChat(_file.Path, Messages.Where(m => !m.IsError).Select(m => new StoredMessage(m.IsUser, m.Display)));

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public bool IsEmpty => Messages.Count == 0;

    public IReadOnlyList<string> Suggestions { get; } = new[]
    {
        "What is the most serious problem in this file?",
        "Show me a corrected version.",
        "Explain these issues in plain language."
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isStreaming;

    private bool CanSend() => !IsStreaming && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var question = Input.Trim();
        if (question.Length == 0 || IsStreaming) return;

        var model = _host.SelectedModel;
        if (string.IsNullOrEmpty(model))
        {
            _host.Notify("Pick a model first.", ToastKind.Error);
            return;
        }

        Input = "";
        Messages.Add(new ChatMessageViewModel(isUser: true, question));
        var reply = new ChatMessageViewModel(isUser: false, defaultLanguage: _file.Language) { IsWaiting = true };
        Messages.Add(reply);

        IsStreaming = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var turns = await BuildTurnsAsync(reply, ct);
            var idle = TimeSpan.FromSeconds(Math.Max(30, _host.Settings.TimeoutSeconds));

            await foreach (var chunk in _host.Client.ChatStreamAsync(model, turns, idle, ct))
            {
                reply.IsWaiting = false;
                reply.Append(chunk);
            }

            if (reply.Display.Trim().Length == 0)
            {
                reply.IsError = true;
                reply.SetRaw("The model sent an empty reply.");
            }
        }
        catch (OperationCanceledException)
        {
            if (reply.Display.Trim().Length == 0) Messages.Remove(reply);
        }
        catch (OllamaException ex)
        {
            reply.IsError = true;
            reply.SetRaw(ex.Message);
        }
        finally
        {
            reply.IsWaiting = false;
            IsStreaming = false;
            _cts?.Dispose();
            _cts = null;
            Persist();
        }
    }

    private async Task<IReadOnlyList<ChatTurn>> BuildTurnsAsync(ChatMessageViewModel pendingReply, CancellationToken ct)
    {
        IReadOnlyList<string> lines = new[] { "[The file could not be read]" };
        if (_host.FolderPath is { } root &&
            await FileText.ReadLinesAsync(Path.Combine(root, _file.Path), ct) is { } read)
        {
            lines = read;
        }

        var (numbered, _) = Prompts.NumberLines(lines, 8000);
        var analysis = _file.Analysis ?? new FileAnalysis { Path = _file.Path };

        var turns = new List<ChatTurn> { new("system", Prompts.ChatSystem(analysis, numbered)) };
        foreach (var message in Messages)
        {
            if (message == pendingReply || message.IsError) continue;
            turns.Add(new ChatTurn(message.IsUser ? "user" : "assistant", message.Display));
        }
        return turns;
    }

    [RelayCommand]
    private async Task AskAsync(string question)
    {
        if (IsStreaming) return;
        Input = question;
        await SendAsync();
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        _cts?.Cancel();
        Messages.Clear();
        Persist();
    }
}
