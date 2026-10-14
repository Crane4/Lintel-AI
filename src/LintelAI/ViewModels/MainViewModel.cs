using System.Collections.ObjectModel;
using LintelAI.Models;
using LintelAI.Services;
using LintelAI.Services.Security.Advanced;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LintelAI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IChatHost, IDisposable
{
    private readonly OllamaClient _ollama = new();
    private readonly FileAnalyzer _analyzer;
    private readonly ProjectScanner _scanner = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly HistoryStore _historyStore = new();
    private readonly IDialogService _dialogs;
    private readonly ChatStore _chats = new();
    private readonly UpdateChecker _updates = new();

    private ProjectIndex? _index;
    private string? _indexFolder;

    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _toastCts;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _analyzer = new FileAnalyzer(_ollama);

        Settings = _settingsStore.Load();
        _ollama.BaseUrl = Settings.OllamaUrl;
        UpdateRemoteWarning();

        UrlInput = Settings.OllamaUrl;
        MaxCharsInput = Settings.MaxFileChars;
        TimeoutInput = Settings.TimeoutSeconds;
        CheckUpdatesInput = Settings.CheckForUpdates;
        ThemeMode = Settings.Theme;

        if (!string.IsNullOrEmpty(Settings.LastFolder) && Directory.Exists(Settings.LastFolder))
            FolderPath = Settings.LastFolder;
    }

    // ── IChatHost ───────────────────────────────────────────────────────────
    public OllamaClient Client => _ollama;
    public AppSettings Settings { get; }

    public IReadOnlyList<StoredMessage> LoadChat(string path) =>
        string.IsNullOrEmpty(FolderPath) ? Array.Empty<StoredMessage>() : _chats.Load(FolderPath, path);

    public void SaveChat(string path, IEnumerable<StoredMessage> messages)
    {
        if (!string.IsNullOrEmpty(FolderPath)) _chats.Save(FolderPath, path, messages);
    }

    // ── Ollama / model ──────────────────────────────────────────────────────
    public ObservableCollection<string> Models { get; } = new();

    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _isOllamaOnline;
    [ObservableProperty] private bool _isCheckingOllama = true;
    [ObservableProperty] private string _ollamaStatus = "Connecting…";

    /// <summary>True when the configured server is not on this machine, so file contents cross the network.</summary>
    [ObservableProperty] private bool _showRemoteWarning;

    private void UpdateRemoteWarning()
    {
        var local = Uri.TryCreate(Settings.OllamaUrl, UriKind.Absolute, out var uri) &&
                    (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        ShowRemoteWarning = !local;
    }

    public bool IsOffline => !IsOllamaOnline && !IsCheckingOllama;
    partial void OnIsOllamaOnlineChanged(bool value) => OnPropertyChanged(nameof(IsOffline));
    partial void OnIsCheckingOllamaChanged(bool value) => OnPropertyChanged(nameof(IsOffline));

    // ── Project ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string? _folderPath;

    public bool HasFolder => !string.IsNullOrEmpty(FolderPath);
    public string FolderName
    {
        get
        {
            if (!HasFolder) return "";
            var trimmed = FolderPath!.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    // ── Run state ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _currentFile = "";
    [ObservableProperty] private int _ignoredCount;

    public bool HasIgnored => IgnoredCount > 0;
    partial void OnIgnoredCountChanged(int value) => OnPropertyChanged(nameof(HasIgnored));

    public bool IsIdle => !IsAnalyzing;
    public bool ShowProgress => IsAnalyzing || (HasResults && !string.IsNullOrEmpty(ProgressText));

    // ── Results ─────────────────────────────────────────────────────────────
    public ObservableCollection<FileItemViewModel> Files { get; } = new();
    public ObservableCollection<FileItemViewModel> VisibleFiles { get; } = new();

    [ObservableProperty] private FileItemViewModel? _selectedFile;
    [ObservableProperty] private FileFilter _filter = FileFilter.All;
    [ObservableProperty] private FileSort _sort = FileSort.Path;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private DetailTab _tab = DetailTab.Issues;

    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _totalIssues;
    [ObservableProperty] private int _highIssues;
    [ObservableProperty] private int? _averageScore;
    [ObservableProperty] private int _vulnerabilityCount;
    [ObservableProperty] private int _bugCount;
    [ObservableProperty] private int _smellCount;

    public bool HasResults => Files.Count > 0;
    public bool NoResults => Files.Count == 0;
    public bool HasSelection => SelectedFile is not null;
    public bool NoSelection => SelectedFile is null;
    public bool NothingVisible => Files.Count > 0 && VisibleFiles.Count == 0;
    public string VisibleCountText => VisibleFiles.Count == Files.Count
        ? $"{Files.Count} files"
        : $"{VisibleFiles.Count} of {Files.Count}";

    public string AverageScoreText => AverageScore?.ToString() ?? "–";
    public bool AverageIsGood => ScoreBands.Of(AverageScore) == ScoreBand.Good;
    public bool AverageIsFair => ScoreBands.Of(AverageScore) == ScoreBand.Fair;
    public bool AverageIsPoor => ScoreBands.Of(AverageScore) == ScoreBand.Poor;
    public string DoneText => $"{DoneCount} / {Files.Count}";

    public bool IsFilterAll => Filter == FileFilter.All;
    public bool IsFilterHigh => Filter == FileFilter.High;
    public bool IsFilterIssues => Filter == FileFilter.WithIssues;
    public bool IsFilterClean => Filter == FileFilter.Clean;
    public bool IsFilterVulnerability => Filter == FileFilter.Vulnerability;
    public bool IsFilterBug => Filter == FileFilter.Bug;
    public bool IsFilterSmell => Filter == FileFilter.Smell;
    public bool IsSortPath => Sort == FileSort.Path;
    public bool IsSortWorst => Sort == FileSort.WorstFirst;
    public bool IsIssuesTab => Tab == DetailTab.Issues;
    public bool IsChatTab => Tab == DetailTab.Chat;

    // ── Overlays, toast, theme ──────────────────────────────────────────────
    [ObservableProperty] private bool _isHistoryOpen;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isExportOpen;

    [ObservableProperty] private string _toastText = "";
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private ToastKind _toastKind;
    public bool IsToastError => ToastKind == ToastKind.Error;
    public bool IsToastSuccess => ToastKind == ToastKind.Success;

    [ObservableProperty] private string _themeMode = "System";
    public Avalonia.Media.Geometry ThemeIcon => ThemeMode switch
    {
        "Light" => Icons.Sun,
        "Dark" => Icons.Moon,
        _ => Icons.Auto
    };
    public string ThemeTooltip => $"Theme: {(ThemeMode == "System" ? "match system" : ThemeMode.ToLowerInvariant())}";

    // ── History ─────────────────────────────────────────────────────────────
    public ObservableCollection<HistoryEntryViewModel> History { get; } = new();
    public ObservableCollection<CompareRowViewModel> Comparison { get; } = new();
    [ObservableProperty] private HistoryEntryViewModel? _selectedHistory;
    public bool HistoryEmpty => History.Count == 0;
    public bool HasComparison => Comparison.Count > 0;

    // ── Settings form ───────────────────────────────────────────────────────
    [ObservableProperty] private string _urlInput = "";
    [ObservableProperty] private decimal? _maxCharsInput;
    [ObservableProperty] private decimal? _timeoutInput;
    [ObservableProperty] private bool _checkUpdatesInput = true;

    // ═══ Lifecycle ══════════════════════════════════════════════════════════

    public async Task InitializeAsync()
    {
        ApplyTheme();
        IsAdvancedScan = Settings.AdvancedScan;
        ScanDepth = Enum.TryParse<AiDepth>(Settings.ScanDepth, out var depth) ? depth : AiDepth.Balanced;
        RestoreLastRun();
        await RefreshModelsAsync();

        // Deliberately not awaited: a slow or missing network must never hold up the window.
        _ = CheckForUpdatesAsync(manual: false);
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _toastCts?.Cancel();
        _ollama.Dispose();
        _updates.Dispose();
    }

    // ═══ Models ═════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        IsCheckingOllama = true;
        OllamaStatus = "Connecting…";

        try
        {
            var found = await _ollama.GetModelsAsync();

            Models.Clear();
            foreach (var m in found) Models.Add(m);

            IsOllamaOnline = true;
            OllamaStatus = found.Count == 1 ? "Ollama · 1 model" : $"Ollama · {found.Count} models";

            var preferred = SelectedModel ?? Settings.LastModel;
            SelectedModel = preferred is not null && found.Contains(preferred) ? preferred : found.FirstOrDefault();

            if (found.Count == 0)
                Notify("Ollama is running but has no models yet. Pull one with “ollama pull”.", ToastKind.Error);
        }
        catch (OllamaException ex)
        {
            Models.Clear();
            SelectedModel = null;
            IsOllamaOnline = false;
            OllamaStatus = "Ollama offline";
            Notify(ex.Message, ToastKind.Error);
        }
        finally
        {
            IsCheckingOllama = false;
        }
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (value is not null && value != Settings.LastModel)
        {
            Settings.LastModel = value;
            _settingsStore.Save(Settings);
        }
        StartCommand.NotifyCanExecuteChanged();
        ReanalyzeCommand.NotifyCanExecuteChanged();
    }

    // ═══ Folder ═════════════════════════════════════════════════════════════

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await _dialogs.PickFolderAsync(FolderPath);
        if (picked is null) return;

        FolderPath = picked;
        Settings.LastFolder = picked;
        _settingsStore.Save(Settings);
    }

    partial void OnFolderPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(FolderName));
        StartCommand.NotifyCanExecuteChanged();
    }

    // ═══ Analysis ═══════════════════════════════════════════════════════════

    private bool CanStart() => !IsAnalyzing && HasFolder && !string.IsNullOrEmpty(SelectedModel);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var folder = FolderPath!;
        var model = SelectedModel!;

        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        IsAnalyzing = true;
        ResetResults();
        CurrentFile = "Reading project structure…";
        ProgressText = "";
        Progress = 0;

        var completed = false;
        try
        {
            var scan = await Task.Run(() => _scanner.Scan(folder, ct), ct);
            IgnoredCount = scan.IgnoredCount;

            if (scan.Files.Count == 0)
            {
                CurrentFile = "";
                Notify("No analyzable files in that folder.", ToastKind.Error);
                return;
            }

            CurrentFile = "Indexing project…";
            _index = await Task.Run(() => ProjectIndex.Build(folder, scan.Files, ct), ct);
            _indexFolder = folder;

            foreach (var path in scan.Files) Files.Add(new FileItemViewModel(path, this));
            RefreshStats();
            RefreshView();
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(NoResults));

            var timeout = TimeSpan.FromSeconds(Settings.TimeoutSeconds);
            var failuresInARow = 0;
            var finished = 0;

            foreach (var item in Files)
            {
                ct.ThrowIfCancellationRequested();

                item.State = FileState.Running;
                CurrentFile = item.Path;
                ProgressText = $"{finished} of {Files.Count}";

                var result = await _analyzer.AnalyzeAsync(folder, item.Path, model, Settings.MaxFileChars, timeout, ct, _index);
                item.Apply(result);

                finished++;
                Progress = finished * 100.0 / Files.Count;
                RefreshStats();
                RefreshView();

                failuresInARow = result.Error is null ? 0 : failuresInARow + 1;
                if (failuresInARow >= 3)
                {
                    Notify($"Stopped after 3 failures in a row: {result.Error}", ToastKind.Error);
                    MarkRemainingSkipped();
                    return;
                }
            }

            completed = true;
            ProgressText = $"{Files.Count} of {Files.Count}";
            CurrentFile = "Analysis complete";

            var results = Files.Where(f => f.Analysis is not null).Select(f => f.Analysis!).ToList();
            _historyStore.Append(folder, model, results);
            Notify("Analysis complete.", ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            MarkRemainingSkipped();
            CurrentFile = "Stopped";
            Notify("Analysis stopped.");
        }
        finally
        {
            IsAnalyzing = false;
            _runCts?.Dispose();
            _runCts = null;
            if (!completed) RefreshStats();
            RefreshView();
        }
    }

    private void MarkRemainingSkipped()
    {
        foreach (var f in Files.Where(f => f.State is FileState.Queued or FileState.Running))
            f.State = FileState.Skipped;
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    partial void OnIsAnalyzingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ShowProgress));
        StartCommand.NotifyCanExecuteChanged();
        ReanalyzeCommand.NotifyCanExecuteChanged();
        OpenSelectedRunCommand.NotifyCanExecuteChanged();
    }

    // ── Re-run a single file ────────────────────────────────────────────────

    private bool CanReanalyze() =>
        !IsAnalyzing && SelectedFile is { State: not FileState.Running } && HasFolder && !string.IsNullOrEmpty(SelectedModel);

    [RelayCommand(CanExecute = nameof(CanReanalyze))]
    private async Task ReanalyzeAsync()
    {
        var item = SelectedFile;
        if (item is null || FolderPath is null || SelectedModel is null) return;

        item.State = FileState.Running;
        ReanalyzeCommand.NotifyCanExecuteChanged();

        try
        {
            var index = await EnsureIndexAsync(FolderPath);
            var result = await _analyzer.AnalyzeAsync(
                FolderPath, item.Path, SelectedModel, Settings.MaxFileChars,
                TimeSpan.FromSeconds(Settings.TimeoutSeconds), CancellationToken.None, index);
            item.Apply(result);
        }
        finally
        {
            if (item.State == FileState.Running) item.State = FileState.Failed;
            RefreshStats();
            RefreshView();
            ReanalyzeCommand.NotifyCanExecuteChanged();
        }
    }

    // ═══ Results view ═══════════════════════════════════════════════════════

    private void ResetResults()
    {
        SelectedFile = null;
        Files.Clear();
        VisibleFiles.Clear();
        Tab = DetailTab.Issues;
        RefreshStats();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(NothingVisible));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private void RefreshStats()
    {
        var finished = Files.Where(f => f.State is FileState.Done or FileState.Failed).ToList();
        DoneCount = finished.Count;
        TotalIssues = Files.Sum(f => f.IssueCount);
        HighIssues = Files.Sum(f => f.Issues.Count(i => i.IsHigh));
        VulnerabilityCount = Files.Sum(f => f.Issues.Count(i => i.Kind == IssueKind.Vulnerability));
        BugCount = Files.Sum(f => f.Issues.Count(i => i.Kind == IssueKind.Bug));
        SmellCount = Files.Sum(f => f.Issues.Count(i => i.Kind == IssueKind.Smell));
        AverageScore = Scoring.Average(Files.Select(f => f.Score));
        OnPropertyChanged(nameof(DoneText));
    }

    partial void OnAverageScoreChanged(int? value)
    {
        OnPropertyChanged(nameof(AverageScoreText));
        OnPropertyChanged(nameof(AverageIsGood));
        OnPropertyChanged(nameof(AverageIsFair));
        OnPropertyChanged(nameof(AverageIsPoor));
    }

    private void RefreshView()
    {
        IEnumerable<FileItemViewModel> query = Files;

        query = Filter switch
        {
            FileFilter.High => query.Where(f => f.HasHighIssue),
            FileFilter.WithIssues => query.Where(f => f.HasIssues),
            FileFilter.Clean => query.Where(f => f.IsClean),
            FileFilter.Vulnerability => query.Where(f => f.HasKind(IssueKind.Vulnerability)),
            FileFilter.Bug => query.Where(f => f.HasKind(IssueKind.Bug)),
            FileFilter.Smell => query.Where(f => f.HasKind(IssueKind.Smell)),
            _ => query
        };

        var term = SearchText.Trim();
        if (term.Length > 0)
            query = query.Where(f => f.Path.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (Sort == FileSort.WorstFirst)
            query = query.OrderBy(f => f.Score ?? 1000).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

        Sync(VisibleFiles, query.ToList());

        OnPropertyChanged(nameof(NothingVisible));
        OnPropertyChanged(nameof(VisibleCountText));
        OnPropertyChanged(nameof(ShowProgress));
    }

    /// <summary>Reconciles in place so the list box keeps its selection and scroll position.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);

        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;

            var existing = target.IndexOf(wanted[i]);
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, wanted[i]);
        }
    }

    partial void OnFilterChanged(FileFilter value)
    {
        foreach (var n in new[]
        {
            nameof(IsFilterAll), nameof(IsFilterHigh), nameof(IsFilterIssues), nameof(IsFilterClean),
            nameof(IsFilterVulnerability), nameof(IsFilterBug), nameof(IsFilterSmell)
        })
            OnPropertyChanged(n);
        RefreshView();
    }

    partial void OnSortChanged(FileSort value)
    {
        OnPropertyChanged(nameof(IsSortPath));
        OnPropertyChanged(nameof(IsSortWorst));
        OnPropertyChanged(nameof(SortLabel));
        RefreshView();
    }

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnTabChanged(DetailTab value)
    {
        OnPropertyChanged(nameof(IsIssuesTab));
        OnPropertyChanged(nameof(IsChatTab));
    }

    partial void OnSelectedFileChanged(FileItemViewModel? oldValue, FileItemViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(NoSelection));
        ReanalyzeCommand.NotifyCanExecuteChanged();

        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedFileStateChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedFileStateChanged;
    }

    private void OnSelectedFileStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileItemViewModel.State)) ReanalyzeCommand.NotifyCanExecuteChanged();
    }

    public string SortLabel => Sort == FileSort.Path ? "A – Z" : "Worst first";

    [RelayCommand] private void SetFilter(FileFilter filter) => Filter = filter;

    /// <summary>Kind chips act as toggles: pressing the active one clears the filter.</summary>
    [RelayCommand] private void ToggleKindFilter(FileFilter filter) => Filter = Filter == filter ? FileFilter.All : filter;
    [RelayCommand] private void ToggleSort() => Sort = Sort == FileSort.Path ? FileSort.WorstFirst : FileSort.Path;
    [RelayCommand] private void SetTab(DetailTab tab) => Tab = tab;

    // ═══ Export ═════════════════════════════════════════════════════════════

    [RelayCommand] private Task ExportJsonAsync() => ExportAsync("json", "JSON report");
    [RelayCommand] private Task ExportMarkdownAsync() => ExportAsync("md", "Markdown report");

    private async Task ExportAsync(string extension, string typeName)
    {
        IsExportOpen = false;

        var results = Files.Where(f => f.Analysis is not null).Select(f => f.Analysis!).ToList();
        if (results.Count == 0)
        {
            Notify("There is nothing to export yet.", ToastKind.Error);
            return;
        }

        var target = await _dialogs.PickSaveFileAsync(
            "Export report", $"analysis-{DateTime.Now:yyyyMMdd-HHmm}", extension, typeName);
        if (target is null) return;

        try
        {
            var content = extension == "json"
                ? ReportExporter.ToJson(FolderPath ?? "", SelectedModel ?? "", results)
                : ReportExporter.ToMarkdown(FolderPath ?? "", SelectedModel ?? "", results);
            await File.WriteAllTextAsync(target, content);
            Notify("Report saved.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Notify($"Couldn't save the report: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand] private void ToggleExport() => IsExportOpen = !IsExportOpen;

    // ═══ History ════════════════════════════════════════════════════════════

    [RelayCommand]
    private void OpenHistory()
    {
        History.Clear();
        foreach (var entry in _historyStore.Load()) History.Add(new HistoryEntryViewModel(entry));
        OnPropertyChanged(nameof(HistoryEmpty));

        SelectedHistory = History.FirstOrDefault();
        IsHistoryOpen = true;
    }

    [RelayCommand] private void CloseHistory() => IsHistoryOpen = false;

    partial void OnSelectedHistoryChanged(HistoryEntryViewModel? value)
    {
        Comparison.Clear();
        if (value is not null && Files.Count > 0)
        {
            var then = value.Entry.Files.ToDictionary(f => f.Path, f => f.Score, StringComparer.OrdinalIgnoreCase);
            var now = Files.ToDictionary(f => f.Path, f => f.Score, StringComparer.OrdinalIgnoreCase);

            foreach (var path in then.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                then.TryGetValue(path, out var before);
                now.TryGetValue(path, out var after);
                Comparison.Add(new CompareRowViewModel(path, before, after));
            }
        }
        OnPropertyChanged(nameof(HasComparison));
        OpenSelectedRunCommand.NotifyCanExecuteChanged();
    }

    // ═══ Settings & theme ═══════════════════════════════════════════════════

    [RelayCommand]
    private void OpenSettings()
    {
        UrlInput = Settings.OllamaUrl;
        MaxCharsInput = Settings.MaxFileChars;
        TimeoutInput = Settings.TimeoutSeconds;
        CheckUpdatesInput = Settings.CheckForUpdates;
        IsSettingsOpen = true;
    }

    [RelayCommand] private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var url = UrlInput.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Notify("That doesn't look like a valid URL.", ToastKind.Error);
            return;
        }

        var urlChanged = !string.Equals(url.TrimEnd('/'), Settings.OllamaUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        Settings.OllamaUrl = url;
        Settings.MaxFileChars = (int)Math.Clamp(MaxCharsInput ?? 12000, 1000, 100000);
        Settings.TimeoutSeconds = (int)Math.Clamp(TimeoutInput ?? 180, 15, 1800);
        Settings.CheckForUpdates = CheckUpdatesInput;
        _ollama.BaseUrl = url;
        _settingsStore.Save(Settings);
        UpdateRemoteWarning();

        IsSettingsOpen = false;
        if (urlChanged) await RefreshModelsAsync();
    }

    [RelayCommand]
    private void CycleTheme()
    {
        ThemeMode = ThemeMode switch { "System" => "Light", "Light" => "Dark", _ => "System" };
        Settings.Theme = ThemeMode;
        _settingsStore.Save(Settings);
    }

    partial void OnThemeModeChanged(string value)
    {
        OnPropertyChanged(nameof(ThemeIcon));
        OnPropertyChanged(nameof(ThemeTooltip));
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = ThemeMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    // ═══ Saved runs ═════════════════════════════════════════════════════════

    private async Task<ProjectIndex?> EnsureIndexAsync(string folder)
    {
        if (_index is not null && string.Equals(_indexFolder, folder, StringComparison.OrdinalIgnoreCase))
            return _index;

        var paths = Files.Select(f => f.Path).ToList();
        if (paths.Count == 0) return null;

        _index = await Task.Run(() => ProjectIndex.Build(folder, paths));
        _indexFolder = folder;
        return _index;
    }

    /// <summary>Brings back the newest saved run for the remembered folder so work is not lost on restart.</summary>
    private void RestoreLastRun()
    {
        if (!HasFolder) return;

        var wanted = NormalizeFolder(FolderPath!);
        var entry = _historyStore.Load()
            .FirstOrDefault(e => e.HasDetails && NormalizeFolder(e.Folder) == wanted);

        if (entry is not null) LoadEntry(entry, restored: true);
    }

    private static string NormalizeFolder(string folder) =>
        folder.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private bool CanOpenSelectedRun() => !IsAnalyzing && SelectedHistory?.HasDetails == true;

    [RelayCommand(CanExecute = nameof(CanOpenSelectedRun))]
    private void OpenSelectedRun()
    {
        if (SelectedHistory is null) return;

        LoadEntry(SelectedHistory.Entry, restored: false);
        IsHistoryOpen = false;
    }

    private void LoadEntry(HistoryEntry entry, bool restored)
    {
        // Saved runs are code reviews, so opening one always returns to that side of the app.
        Mode = AppMode.Lint;
        ScanReport = null;
        ResetResults();

        FolderPath = entry.Folder;
        if (!restored)
        {
            Settings.LastFolder = entry.Folder;
            _settingsStore.Save(Settings);
        }

        foreach (var file in entry.Files)
        {
            var item = new FileItemViewModel(file.Path, this);
            item.Apply(file.ToAnalysis());
            Files.Add(item);
        }

        _index = null;
        IgnoredCount = 0;
        Progress = 100;
        ProgressText = $"{Files.Count} of {Files.Count}";
        CurrentFile = restored
            ? "Restored from your last run"
            : $"Opened run from {entry.Timestamp.LocalDateTime:d MMM, HH:mm}";

        RefreshStats();
        RefreshView();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(NoResults));
        OnPropertyChanged(nameof(ShowProgress));

        if (!restored) Notify("Opened a saved run. Findings are from the time of that run.", ToastKind.Info);
    }

    // ═══ Files on disk ══════════════════════════════════════════════════════

    private string? SelectedFullPath() =>
        SelectedFile is { } file && HasFolder
            ? Path.GetFullPath(Path.Combine(FolderPath!, file.Path))
            : null;

    [RelayCommand]
    private void RevealFile()
    {
        var full = SelectedFullPath();
        if (full is null) return;

        try
        {
            if (!File.Exists(full)) { Notify("That file no longer exists.", ToastKind.Error); return; }

            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{full}\"");
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", new[] { "-R", full });
            else
                System.Diagnostics.Process.Start("xdg-open", Path.GetDirectoryName(full)!);
        }
        catch (Exception ex)
        {
            Notify($"Couldn't open the folder: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenFile()
    {
        var full = SelectedFullPath();
        if (full is null) return;

        try
        {
            if (!File.Exists(full)) { Notify("That file no longer exists.", ToastKind.Error); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"Couldn't open the file: {ex.Message}", ToastKind.Error);
        }
    }

    // ═══ Keyboard ═══════════════════════════════════════════════════════════

    [RelayCommand]
    private void CloseOverlays()
    {
        IsHistoryOpen = false;
        IsSettingsOpen = false;
        IsExportOpen = false;
    }

    // ═══ Updates ════════════════════════════════════════════════════════════

    public string CurrentVersionText => $"Version {AppInfo.CurrentText}";

    [ObservableProperty] private bool _isUpdateBannerVisible;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] private bool _isCheckingUpdates;

    private UpdateInfo? _availableUpdate;

    /// <summary>
    /// Looks for a newer release. It only shows a notice: the person decides whether to
    /// open the release page, and nothing is downloaded or installed by the app.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!manual && !Settings.CheckForUpdates) return;
        if (IsCheckingUpdates) return;

        IsCheckingUpdates = true;
        try
        {
            // Installed by the Velopack setup: it can replace itself, so offer that.
            if (_appUpdater.IsInstalled)
            {
                await CheckInPlaceAsync(manual);
                return;
            }

            // Anything else (the old installer, "dotnet run"): only point to the release page.
            var result = await _updates.CheckAsync(Settings.UpdateRepository, AppInfo.Current);

            switch (result.Status)
            {
                case UpdateStatus.Available when result.Update is { } update:
                    _availableUpdate = update;

                    // A skipped version stays quiet unless the person asked to check by hand.
                    var skipped = string.Equals(Settings.SkippedVersion, update.Tag, StringComparison.OrdinalIgnoreCase);
                    if (skipped && !manual) break;

                    UpdateTitle = $"Version {update.Version.ToString(3)} is available";
                    UpdateDetail = $"You are on {AppInfo.CurrentText}. Updates are not installed automatically.";
                    IsUpdateBannerVisible = true;
                    if (manual) Notify($"Version {update.Version.ToString(3)} is available.", ToastKind.Success);
                    break;

                case UpdateStatus.UpToDate:
                    if (manual) Notify($"You are on the latest version ({AppInfo.CurrentText}).", ToastKind.Success);
                    break;

                default:
                    if (manual) Notify(result.Error ?? "Couldn't check for updates.", ToastKind.Error);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand] private Task CheckForUpdatesNowAsync() => CheckForUpdatesAsync(manual: true);

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (_availableUpdate is null || !UpdateChecker.IsGitHubUrl(_availableUpdate.Url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_availableUpdate.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify($"Couldn't open the browser: {ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private void SkipUpdate()
    {
        if (_availableUpdate is not null)
        {
            Settings.SkippedVersion = _availableUpdate.Tag;
            _settingsStore.Save(Settings);
        }
        IsUpdateBannerVisible = false;
    }

    /// <summary>Turns the start-up check off for good; it can be switched back on in Settings.</summary>
    [RelayCommand]
    private void StopUpdateNotices()
    {
        Settings.CheckForUpdates = false;
        CheckUpdatesInput = false;
        _settingsStore.Save(Settings);
        IsUpdateBannerVisible = false;
        Notify("Update notices are off. You can turn them back on in Settings.");
    }

    [RelayCommand] private void DismissUpdate() => IsUpdateBannerVisible = false;
    // (updating in place lives in MainViewModel.Updates.cs)

    // ═══ Toast ══════════════════════════════════════════════════════════════

    public async void Notify(string text, ToastKind kind = ToastKind.Info)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();

        ToastText = text;
        ToastKind = kind;
        IsToastVisible = true;

        try
        {
            await Task.Delay(kind == ToastKind.Error ? 6000 : 3200, cts.Token);
            IsToastVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    partial void OnToastKindChanged(ToastKind value)
    {
        OnPropertyChanged(nameof(IsToastError));
        OnPropertyChanged(nameof(IsToastSuccess));
    }

    [RelayCommand] private void DismissToast() => IsToastVisible = false;

    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(ShowProgress));
}
