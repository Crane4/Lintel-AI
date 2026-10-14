using System.ComponentModel;
using LintelAI.Models;
using LintelAI.Services.Security;
using LintelAI.Services.Security.Advanced;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LintelAI.ViewModels;

public enum AppMode { Lint, Scan }

/// <summary>
/// The "Scan" side of the app: checking an unfamiliar project for malicious code. It reuses the
/// results list and detail views, so findings from a scan look like findings from a review.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SecurityScanner _securityScanner = new();

    [ObservableProperty] private AppMode _mode = AppMode.Lint;
    [ObservableProperty] private SecurityReport? _scanReport;

    public bool IsLintMode => Mode == AppMode.Lint;
    public bool IsScanMode => Mode == AppMode.Scan;

    partial void OnModeChanged(AppMode value)
    {
        OnPropertyChanged(nameof(IsLintMode));
        OnPropertyChanged(nameof(IsScanMode));
        OnPropertyChanged(nameof(ShowModelPicker));
    }

    // ── Scan level ──────────────────────────────────────────────────────────

    /// <summary>False: rules only. True: the rules, then a model judges what they found.</summary>
    [ObservableProperty] private bool _isAdvancedScan;

    public bool IsNormalScan => !IsAdvancedScan;

    /// <summary>A model is needed for code review and for the advanced scan, not for the normal one.</summary>
    public bool ShowModelPicker => IsLintMode || IsAdvancedScan;

    partial void OnIsAdvancedScanChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNormalScan));
        OnPropertyChanged(nameof(ShowModelPicker));

        if (Settings.AdvancedScan == value) return;
        Settings.AdvancedScan = value;
        _settingsStore.Save(Settings);
    }

    [RelayCommand]
    private void SetScanLevel(bool advanced)
    {
        if (!IsAnalyzing) IsAdvancedScan = advanced;
    }

    // ── How much the advanced scan reads ────────────────────────────────────

    [ObservableProperty] private AiDepth _scanDepth = AiDepth.Balanced;

    public bool IsDepthQuick => ScanDepth == AiDepth.Quick;
    public bool IsDepthBalanced => ScanDepth == AiDepth.Balanced;
    public bool IsDepthThorough => ScanDepth == AiDepth.Thorough;

    public string DepthHelp => ScanDepth switch
    {
        AiDepth.Quick => "Only judges the medium alerts the rules raised. Fastest.",
        AiDepth.Thorough => "Also reads up to 25 risky files in full, looking for what the rules missed. Can take a long time.",
        _ => "Judges the alerts, then reads the start of up to 8 risky files looking for what the rules missed."
    };

    partial void OnScanDepthChanged(AiDepth value)
    {
        OnPropertyChanged(nameof(IsDepthQuick));
        OnPropertyChanged(nameof(IsDepthBalanced));
        OnPropertyChanged(nameof(IsDepthThorough));
        OnPropertyChanged(nameof(DepthHelp));

        if (Settings.ScanDepth == value.ToString()) return;
        Settings.ScanDepth = value.ToString();
        _settingsStore.Save(Settings);
    }

    [RelayCommand]
    private void SetScanDepth(AiDepth depth)
    {
        if (!IsAnalyzing) ScanDepth = depth;
    }

    // ── What the window shows ───────────────────────────────────────────────

    /// <summary>A clean scan has no files to list, but it still has a verdict to show.</summary>
    public bool ShowResults => HasResults || ScanReport is not null;
    public bool ShowWelcome => !ShowResults;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName is nameof(HasResults) or nameof(NoResults) or nameof(ScanReport))
        {
            OnPropertyChanged(nameof(ShowResults));
            OnPropertyChanged(nameof(ShowWelcome));
        }
    }

    // ── Verdict ─────────────────────────────────────────────────────────────

    public bool HasScanReport => ScanReport is not null;
    public bool ShowScanClean => ScanReport is not null && Files.Count == 0;

    public bool IsVerdictClean => ScanReport?.Verdict == RiskVerdict.Clean;
    public bool IsVerdictSuspicious => ScanReport?.Verdict == RiskVerdict.Suspicious;
    public bool IsVerdictDangerous => ScanReport?.Verdict == RiskVerdict.Dangerous;

    public string VerdictText => ScanReport?.Verdict switch
    {
        RiskVerdict.Clean => "No findings",
        RiskVerdict.Suspicious => "Suspicious",
        RiskVerdict.Dangerous => "Dangerous",
        _ => "–"
    };

    public string VerdictDetail => ScanReport?.Verdict switch
    {
        RiskVerdict.Clean => "No known malicious patterns matched. That is not a guarantee the project is safe.",
        RiskVerdict.Suspicious => "Some files do things that deserve a closer look before you run or build anything.",
        RiskVerdict.Dangerous => "Patterns commonly used by malware were found. Do not run, build or open this project in an editor.",
        _ => ""
    };

    public int ScanHigh => ScanReport?.Count(Severity.High) ?? 0;
    public int ScanMedium => ScanReport?.Count(Severity.Medium) ?? 0;
    public int ScanLow => ScanReport?.Count(Severity.Low) ?? 0;
    public string ScanFilesText => ScanReport is null ? "" : $"{ScanReport.FilesScanned:N0} files checked";

    /// <summary>What the scan could not look at, so a quiet result is not mistaken for full coverage.</summary>
    public string ScanNotice
    {
        get
        {
            if (ScanReport is not { } report) return "";

            var parts = new List<string>();

            if (!string.IsNullOrEmpty(report.AiSummary)) parts.Add(report.AiSummary);

            if (report.Skipped.Count > 0)
            {
                var shown = string.Join(", ", report.Skipped.Take(3));
                var more = report.Skipped.Count > 3 ? $" and {report.Skipped.Count - 3} more" : "";
                parts.Add($"Not inspected: {shown}{more}");
            }

            if (report.BinaryFiles > 0)
                parts.Add($"{report.BinaryFiles:N0} binary file(s) cannot be read as code");

            if (report.Truncated)
                parts.Add($"Stopped at {SecurityFileCollector.MaxFiles:N0} files");

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasScanNotice => ScanNotice.Length > 0;

    partial void OnScanReportChanged(SecurityReport? value)
    {
        foreach (var name in new[]
        {
            nameof(HasScanReport), nameof(ShowScanClean),
            nameof(IsVerdictClean), nameof(IsVerdictSuspicious), nameof(IsVerdictDangerous),
            nameof(VerdictText), nameof(VerdictDetail),
            nameof(ScanHigh), nameof(ScanMedium), nameof(ScanLow), nameof(ScanFilesText),
            nameof(ScanNotice), nameof(HasScanNotice)
        })
        {
            OnPropertyChanged(name);
        }
    }

    // ── Commands ────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SetMode(AppMode mode)
    {
        if (IsAnalyzing || mode == Mode) return;

        ResetResults();
        ScanReport = null;
        IgnoredCount = 0;
        Progress = 0;
        ProgressText = "";
        CurrentFile = "";
        Mode = mode;

        // Coming back to Lint: bring back the last review of this folder, as at start-up.
        if (mode == AppMode.Lint) RestoreLastRun();
    }

    /// <summary>F5: does whatever the current mode's main button does.</summary>
    [RelayCommand]
    private void Run()
    {
        if (IsScanMode) ScanCommand.Execute(null);
        else if (StartCommand.CanExecute(null)) StartCommand.Execute(null);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsAnalyzing) return;
        if (!HasFolder)
        {
            Notify("Choose a project folder first.", ToastKind.Error);
            return;
        }

        var advanced = IsAdvancedScan;
        if (advanced && (string.IsNullOrEmpty(SelectedModel) || !IsOllamaOnline))
        {
            Notify("The advanced scan needs an Ollama model. Start Ollama and pick a model, or use the normal scan.", ToastKind.Error);
            return;
        }

        var folder = FolderPath!;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        IsAnalyzing = true;
        ResetResults();
        ScanReport = null;
        IgnoredCount = 0;
        CurrentFile = "Collecting files…";
        ProgressText = "";
        Progress = 0;

        try
        {
            SecurityReport report;

            if (advanced)
            {
                var model = new OllamaSecurityModel(_ollama, SelectedModel!, TimeSpan.FromSeconds(Settings.TimeoutSeconds));
                var aiProgress = new Progress<SecurityProgress>(p =>
                {
                    Progress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                    ProgressText = p.Total == 0 ? "" : $"{p.Done} of {p.Total}";
                    CurrentFile = p.Message;
                });

                report = await new AdvancedSecurityScanner().ScanAsync(
                    folder, model, Settings.MaxFileChars, aiProgress, ct, ScanDepth);
            }
            else
            {
                var progress = new Progress<(int Done, int Total)>(p =>
                {
                    Progress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                    ProgressText = $"{p.Done} of {p.Total}";
                    CurrentFile = "Scanning…";
                });

                report = await _securityScanner.ScanAsync(folder, progress, ct);
            }

            // Worst first: the files most worth reading are at the top of the list.
            var ordered = report.Files
                .OrderByDescending(f => f.Issues.Max(i => i.Severity))
                .ThenByDescending(f => f.Issues.Count)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var analysis in ordered)
            {
                var item = new FileItemViewModel(analysis.Path, this);
                item.Apply(analysis);
                Files.Add(item);
            }

            ScanReport = report;
            RefreshStats();
            RefreshView();
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(NoResults));

            ProgressText = $"{report.FilesScanned} of {report.FilesScanned}";
            CurrentFile = "Scan complete";

            switch (report.Verdict)
            {
                case RiskVerdict.Dangerous:
                    Notify("Dangerous patterns found. Do not run this project.", ToastKind.Error);
                    break;
                case RiskVerdict.Suspicious:
                    Notify("Suspicious patterns found. Review them before running anything.");
                    break;
                default:
                    Notify("No known malicious patterns found.", ToastKind.Success);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            CurrentFile = "Stopped";
            Notify("Scan stopped.");
        }
        catch (Exception ex)
        {
            CurrentFile = "";
            Notify($"The scan failed: {ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsAnalyzing = false;
            _runCts?.Dispose();
            _runCts = null;
            RefreshView();
        }
    }
}
