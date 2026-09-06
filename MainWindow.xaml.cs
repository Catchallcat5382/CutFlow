using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CutFlow.Models;
using CutFlow.Services;
using Microsoft.Win32;

namespace CutFlow;

public partial class MainWindow : Window
{
    private readonly ProjectStorageService _storage = new();
    private readonly FFmpegService _ffmpeg = new();
    private readonly SilenceAnalyzer _silenceAnalyzer = new();
    private readonly UndoRedoService _history = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _settingsDebounce;
    private readonly DispatcherTimer _projectSettingsSaveTimer;
    private readonly DispatcherTimer _saveToastTimer;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private CutFlowProject _project = new();
    private AudioAnalysisResult? _analysis;
    private string? _manualSavePath;
    private bool _isPlaying;
    private bool _loadingUi = true;
    private bool _busy;
    private bool _dirty;
    private bool _closingInProgress;
    private bool _allowClose;
    private bool _isFullScreen;
    private string _windowMode = "Windowed";
    private Rect _windowedBounds = new(80, 60, 1400, 880);
    private GridLength _sidebarWidth = new(355);
    private bool _usingSmoothPreview;
    private string? _smoothPreviewPath;
    private string? _smoothPreviewSignature;
    private List<(double start, double end)> _smoothPreviewRanges = new();
    private CancellationTokenSource? _smoothPreviewCts;
    private bool _resumeAfterSourceSwitch;
    private double _pendingSourcePosition = -1;
    private CancellationTokenSource? _operationCts;
    private readonly Stopwatch _playbackClock = Stopwatch.StartNew();
    private double _lastUiRefreshMs;
    private double _uiRefreshIntervalMs = 1000.0 / 30.0;
    private bool _resumeAfterTimelineScrub;
    private bool _timelineScrubbing;
    private bool _updatingTimelineScroll;
    private bool _projectTitleEditing;
    private List<SilenceSegment> _appliedCuts = new();
    private int _nextCutIndex;
    private bool _syncingSegmentSelection;
    private bool _timelinePanQueued;
    private double _pendingTimelinePanFraction;
    private int _tutorialStepIndex;
    private bool _tutorialRunning;
    private bool _tutorialTemporarilyHidden;
    private bool _mediaReady;
    private bool _pendingPlayAfterMediaOpen;
    private bool _playRequestInProgress;
    private string? _loadedPreviewPath;
    private TaskCompletionSource<bool>? _cutConfirmTcs;
    private long _lastOperationUiTick;
    private bool _backgroundPreviewBuilding;
    private int _backgroundPreviewGeneration;
    private int _detectorGeneration;
    private int _previewOpenGeneration;
    private readonly DispatcherTimer _audioRecoveryTimer;
    private readonly List<(double offsetStart, double offsetEnd)> _regionClipboard = new();
    private bool _exportInProgress;

    private sealed record TutorialStep(string Title, string Body, Func<FrameworkElement?> Target, string? ActionKey);
    private List<TutorialStep>? _tutorialSteps;

    private const int CurrentProjectSchema = 54;
    private const int CurrentDetectorSchema = 53;
    private AppSettings AppSettings => ((App)Application.Current).Settings;
    private static void TraceAction(string action) => App.RecordAction(action);
    // Visible red regions are simply editable removal markers. IsCut is used only on the temporary render clone.
    private static bool IsPendingRegion(SilenceSegment s) => !s.IsIgnored && s.EndSeconds > s.StartSeconds;
    private static bool IsActiveRegion(SilenceSegment s) => !s.IsIgnored && s.EndSeconds > s.StartSeconds;
    private bool _manualRegionMode;
    private bool _forceExitRequested;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        Timeline.SeekRequested += (_, seconds) => SeekTo(seconds, markDirty: false, continuePlayback: false);
        Timeline.ScrubPreviewRequested += (_, seconds) => PreviewTimelinePosition(seconds);
        Timeline.ScrubStarted += (_, _) => BeginTimelineScrub();
        Timeline.ScrubCompleted += (_, _) => EndTimelineScrub();
        Timeline.SegmentSelectionChanged += indices =>
        {
            try { TraceAction($"Timeline selection changed ({indices.Count})"); SelectSegmentsFromTimeline(indices); }
            catch (Exception ex) { ShowError("Could not update the timeline selection", ex); }
        };
        Timeline.SegmentContextRequested += index =>
        {
            try { TraceAction($"Timeline context menu for segment {index}"); ShowTimelineSegmentMenu(index); }
            catch (Exception ex) { ShowError("Could not open the cut menu", ex); }
        };
        Timeline.RegionCreated += (start, end) => AddManualRegion(start, end);
        Timeline.SegmentBoundsChanged += (index, oldStart, oldEnd, start, end) => ResizeManualRegion(index, oldStart, oldEnd, start, end);
        Timeline.ViewChanged += (_, _) => RefreshTimelinePanSlider();

        // Cut-boundary checks stay fast, while visual refresh is throttled separately in PlaybackTimer_Tick.
        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playbackTimer.Start();

        _autosaveTimer = new DispatcherTimer();
        _autosaveTimer.Tick += AutosaveTimer_Tick;

        _settingsDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _settingsDebounce.Tick += SettingsDebounce_Tick;

        // Project-level detector/cleanup choices should survive immediately instead of waiting
        // for the normal 30s autosave interval. Debounce the disk write so dragging a slider
        // still produces one small autosave after the user pauses.
        _projectSettingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _projectSettingsSaveTimer.Tick += async (_, _) =>
        {
            _projectSettingsSaveTimer.Stop();
            if (_busy || _closingInProgress || !_dirty || !_project.HasMedia) return;
            await SaveCurrentProjectAsync(showToast: false, includeManualFile: false);
        };

        _saveToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _saveToastTimer.Tick += (_, _) => { _saveToastTimer.Stop(); SaveToast.Visibility = Visibility.Collapsed; };

        // WPF MediaElement can occasionally lose/reinitialize its audio renderer after a seek.
        // Reassert volume/mute state once after the decoder settles. IMPORTANT: do NOT call Play()
        // again while playback is already running; repeatedly re-starting MediaElement was the
        // source of random headphone crackles/dropouts and "move backwards to get audio back".
        _audioRecoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _audioRecoveryTimer.Tick += (_, _) =>
        {
            _audioRecoveryTimer.Stop();
            if (!_project.HasMedia || !_mediaReady) return;
            try { ApplyPreviewAudioState(); } catch { }
        };

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Activated += MainWindow_Activated;
        InitializeTrayIcon();
        _loadingUi = false;
    }


    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (!AppSettings.ThemeMode.Equals("System", StringComparison.OrdinalIgnoreCase)) return;
        ThemeService.Apply(AppSettings);
        Timeline.InvalidateStaticCache();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAppSettingsToUi();
        ShowHomeView();
        await RefreshRecentProjectsAsync();
        SetStatus("Ready");
        Dispatcher.BeginInvoke(new Action(() => ApplyWindowMode(AppSettings.WindowMode, persist: false)), DispatcherPriority.Loaded);
    }

    // Closing from an editor returns to the project home first, like a video editor.
    // Closing again from Home (or choosing Exit from the tray) fully exits the process.
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closingInProgress) return;

        if (_busy)
        {
            MessageBox.Show("DO NOT close CutFlow while cuts are being applied. The editor may briefly say Not Responding, but the background worker is still processing. Normal close is blocked until the job finishes. If the editor process is force-closed, the worker will cancel the render and the saved project will stay exactly as it was before the cut.", "Cuts are still being applied", MessageBoxButton.OK, MessageBoxImage.Warning);
            _forceExitRequested = false;
            return;
        }

        if (!_forceExitRequested && EditorView.Visibility == Visibility.Visible && _project.HasMedia)
        {
            // CapCut-style close behavior: the project editor visually closes, the process stays
            // alive long enough to save, then the project home reopens. Closing again from Home
            // exits CutFlow completely.
            _closingInProgress = true;
            try
            {
                Pause(markDirty: false);
                Hide();
                await Dispatcher.Yield(DispatcherPriority.Background);
                try { await SaveCurrentProjectAsync(showToast: false, includeManualFile: false, force: true); } catch { }
                ShowHomeView();
                await RefreshRecentProjectsAsync();
                SetStatus("Project saved. Close CutFlow again to exit completely.");
                Show();
                Activate();
                PlayUiSound("navigate");
            }
            finally { _closingInProgress = false; }
            return;
        }

        _closingInProgress = true;
        try
        {
            _operationCts?.Cancel();
            _smoothPreviewCts?.Cancel();
            Pause(markDirty: false);
            _playbackTimer.Stop();
            _autosaveTimer.Stop();
            _settingsDebounce.Stop();
            try { Preview.Stop(); Preview.Source = null; _mediaReady = false; _loadedPreviewPath = null; } catch { }
            if (_project.HasMedia)
            {
                try { await SaveCurrentProjectAsync(showToast: false, includeManualFile: false, force: true); } catch { }
            }
            DisposeTrayIcon();
        }
        finally
        {
            _allowClose = true;
            Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Send);
        }
    }

    private void ShowHomeView()
    {
        Pause(markDirty: false);
        HomeView.Visibility = Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        ProjectTitleBox.Text = "Projects";
        SetManualRegionMode(false);
        UpdateTutorialAvailability();
    }

    private void ShowEditorView()
    {
        HomeView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        ProjectTitleBox.Text = _project.HasMedia ? _project.Name : "Untitled";
        UpdateTutorialAvailability();
    }

    private void UpdateTutorialAvailability()
    {
        var available = _project.HasMedia && EditorView.Visibility == Visibility.Visible;
        if (StartTutorialMenuItem is not null) StartTutorialMenuItem.IsEnabled = available;
        if (TutorialButton is not null) TutorialButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshRecentProjectsAsync()
    {
        try
        {
            var recent = await _storage.GetRecentProjectsAsync();
            RecentProjectsList.ItemsSource = recent;
            HomeEmptyState.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetStatus("Could not load recent projects: " + FriendlyError(ex));
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = CreateMediaOpenDialog("Create a new CutFlow project");
        if (dialog.ShowDialog() != true) return;
        if (_project.HasMedia) await SaveCurrentProjectAsync(false, false);
        ResetProject();
        ShowEditorView();
        await ImportMediaAsync(dialog.FileName);
    }

    private static OpenFileDialog CreateMediaOpenDialog(string title) => new()
    {
        Title = title,
        Filter = "Video and audio|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.mts;*.m2ts;*.mp3;*.wav;*.m4a;*.aac;*.flac|All files|*.*",
        CheckFileExists = true,
        Multiselect = false
    };

    private async void Home_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_project.HasMedia) await SaveCurrentProjectAsync(false, false);
        ShowHomeView();
        await RefreshRecentProjectsAsync();
    }

    private void ResetProject()
    {
        Pause(markDirty: false);
        _project = new CutFlowProject
        {
            SchemaVersion = CurrentProjectSchema,
            PreviewWithoutSilence = AppSettings.PreviewWithoutSilenceByDefault,
            Silence = new SilenceSettings { NoiseProfile = AppSettings.DefaultNoiseProfile }
        };
        _analysis = null;
        _manualSavePath = null;
        _smoothPreviewCts?.Cancel();
        _smoothPreviewCts = null;
        _smoothPreviewPath = null;
        _smoothPreviewSignature = null;
        _smoothPreviewRanges = new();
        _usingSmoothPreview = false;
        Interlocked.Increment(ref _previewOpenGeneration);
        _mediaReady = false;
        _pendingPlayAfterMediaOpen = false;
        _playRequestInProgress = false;
        _loadedPreviewPath = null;
        _history.Clear();
        _dirty = false;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = CreateMediaOpenDialog("Choose media for this project");
        if (dialog.ShowDialog() != true) return;
        if (_project.HasMedia) await SaveCurrentProjectAsync(false, false);
        ShowEditorView();
        await ImportMediaAsync(dialog.FileName);
    }

    private async Task ImportMediaAsync(string path)
    {
        Interlocked.Increment(ref _detectorGeneration);
        TraceAction($"Import media: {Path.GetFileName(path)}");
        await RunBusyAsync("Importing media", "Preparing the local media engine…", async token =>
        {
            var status = new Progress<string>(s => SetOperation(s, null));
            await _ffmpeg.EnsureAvailableAsync(status, token);

            SetOperation("Reading your recording…", 3);
            var info = await _ffmpeg.ProbeAsync(path, token);
            if (!info.HasAudio) throw new InvalidOperationException("This file has no audio track, so there is no silence to analyze.");

            var progress = new Progress<double>(p => SetOperation($"Analyzing voice and pauses… {p * 100:0}%", 5 + p * 78));
            var analysis = await _ffmpeg.ExtractAudioAnalysisAsync(path, info.DurationSeconds, progress, token);

            var newProject = new CutFlowProject
            {
                SchemaVersion = CurrentProjectSchema,
                Name = Path.GetFileNameWithoutExtension(path),
                SourcePath = path,
                OriginalSourcePath = path,
                Media = info,
                WaveformPeaks = analysis.Peaks,
                AnalysisWindowDb = analysis.WindowDb,
                AnalysisWindowPeakDb = analysis.WindowPeakDb,
                AnalysisWindowZcr = analysis.WindowZcr,
                VadSpeechProbability = analysis.SpeechProbability,
                VadWindowSeconds = analysis.SpeechWindowSeconds,
                AnalysisWindowSeconds = analysis.WindowSeconds,
                SuggestedThresholdDb = analysis.SuggestedThresholdDb,
                PreviewWithoutSilence = AppSettings.PreviewWithoutSilenceByDefault,
                Silence = new SilenceSettings
                {
                    SmartThreshold = true,
                    ThresholdDb = analysis.SuggestedThresholdDb,
                    MinimumSilenceSeconds = 0.08,
                    PaddingSeconds = 0.008,
                    RemoveBlipsSeconds = 0.10,
                    NoiseProfile = AppSettings.DefaultNoiseProfile,
                    ProtectSoftVoice = true,
                    BlendCutEdges = true
                }
            };
            SetOperation("Finding speech-safe pause regions…", 84);
            var detected = await Task.Run(() => _silenceAnalyzer.AnalyzeRescan(analysis, newProject.Silence), token);
            newProject.Segments = NormalizeRegionsToFrameGrid(detected, newProject.Media);
            SetOperation($"Found {newProject.Segments.Count(IsPendingRegion)} pause region(s). Saving project…", 90);
            await _storage.AutosaveAsync(newProject, token);

            _smoothPreviewCts?.Cancel();
            _smoothPreviewCts = null;
            _smoothPreviewPath = null;
            _smoothPreviewSignature = null;
            _smoothPreviewRanges = new();
            _usingSmoothPreview = false;
            _project = newProject;
            _analysis = analysis;
            _history.Clear();
            _manualSavePath = null;
            _dirty = false;
            LoadProjectIntoUi();
            SetOperation("Ready", 100);
        });
        await RefreshRecentProjectsAsync();
    }

    private void LoadProjectIntoUi()
    {
        _loadingUi = true;
        try
        {
            ProjectTitleBox.Text = _project.HasMedia ? _project.Name : "Untitled";
            EditorEmptyState.Visibility = _project.HasMedia ? Visibility.Collapsed : Visibility.Visible;
            MediaNameLabel.Text = _project.HasMedia ? Path.GetFileName(string.IsNullOrWhiteSpace(_project.OriginalSourcePath) ? _project.SourcePath : _project.OriginalSourcePath) : "No media";
            MediaInfoLabel.Text = _project.HasMedia
                ? (_project.Media.HasVideo ? $"{_project.Media.Width}×{_project.Media.Height}  •  {_project.Media.FrameRate:0.##} fps" : "Audio")
                : string.Empty;

            if (_project.HasMedia && File.Exists(_project.SourcePath))
            {
                // Let the timeline/cards paint before asking Windows' media decoder to open a
                // potentially large video. This removes the startup/load hitch where regions
                // existed but were not visible yet because decoder initialization competed with UI.
                SchedulePreviewOpen(_project.SourcePath, _project.LastPlayheadSeconds);
                Preview.Volume = AppSettings.PlaybackVolume;
            }
            else
            {
                Interlocked.Increment(ref _previewOpenGeneration);
                try { Preview.Stop(); } catch { }
                Preview.Source = null;
                _loadedPreviewPath = null;
                _mediaReady = false;
            }

            SmartThresholdCheck.IsChecked = _project.Silence.SmartThreshold;
            ThresholdSlider.Value = _project.Silence.ThresholdDb;
            MinimumSlider.Value = _project.Silence.MinimumSilenceSeconds;
            PaddingSlider.Value = _project.Silence.PaddingSeconds;
            BlipsSlider.Value = _project.Silence.RemoveBlipsSeconds;
            NoiseProfileCombo.SelectedIndex = NoiseProfileIndex(_project.Silence.NoiseProfile);
            ProtectSoftVoiceCheck.IsChecked = _project.Silence.ProtectSoftVoice;
            BlendCutEdgesCheck.IsChecked = _project.Silence.BlendCutEdges;
            MouthClicksCheck.IsChecked = _project.Silence.ReduceMouthClicks;
            ImpulsiveNoiseCheck.IsChecked = _project.Silence.ReduceImpulsiveNoise;
            BackgroundSqueaksCheck.IsChecked = _project.Silence.ReduceBackgroundSqueaks;
            SkipCutsCheck.IsChecked = _project.PreviewWithoutSilence;
            VolumeSlider.Value = AppSettings.PlaybackVolume;

            Timeline.Duration = _project.Media.DurationSeconds;
            Timeline.Peaks = _project.WaveformPeaks;
            Timeline.Segments = _project.Segments;
            Timeline.Playhead = _project.LastPlayheadSeconds;
            Timeline.SnapEnabled = AppSettings.SnappingEnabled;
            Timeline.SnapThresholdSeconds = AppSettings.SnapThresholdSeconds;
            SnappingCheck.IsChecked = AppSettings.SnappingEnabled;
            Timeline.Fit();
            if (_project.Media.DurationSeconds > 30)
            {
                Timeline.SetZoomLevel(1.6);
                Timeline.FollowPlayhead(_project.LastPlayheadSeconds);
            }
            RebuildAppliedCutCache(_project.LastPlayheadSeconds);
            RefreshTimelinePanSlider();
            RefreshSettingsLabels();
            RefreshCutsList();
            RefreshSummary();
            RefreshHistoryButtons();
            RefreshPlaybackButtons();
            UpdateTutorialAvailability();
        }
        finally { _loadingUi = false; }
    }

    private void RestoreAnalysisFromProject()
    {
        if (_project.AnalysisWindowDb.Count == 0) { _analysis = null; return; }
        _analysis = new AudioAnalysisResult
        {
            Peaks = _project.WaveformPeaks,
            WindowDb = _project.AnalysisWindowDb,
            WindowPeakDb = _project.AnalysisWindowPeakDb,
            WindowZcr = _project.AnalysisWindowZcr,
            SpeechProbability = _project.VadSpeechProbability,
            SpeechWindowSeconds = _project.VadWindowSeconds <= 0 ? 0.032 : _project.VadWindowSeconds,
            WindowSeconds = _project.AnalysisWindowSeconds <= 0 ? 0.02 : _project.AnalysisWindowSeconds,
            SuggestedThresholdDb = _project.SuggestedThresholdDb
        };
    }

    private void Check_SettingsChanged(object sender, RoutedEventArgs e) => QueueSilenceRecalculate();
    private void Slider_SettingsChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => QueueSilenceRecalculate();
    private void NoiseProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => QueueSilenceRecalculate();

    private void CleanupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _project.Silence.BlendCutEdges = BlendCutEdgesCheck.IsChecked == true;
        _project.Silence.ReduceMouthClicks = MouthClicksCheck.IsChecked == true;
        _project.Silence.ReduceImpulsiveNoise = ImpulsiveNoiseCheck.IsChecked == true;
        _project.Silence.ReduceBackgroundSqueaks = BackgroundSqueaksCheck.IsChecked == true;
        MarkDirty();
        ScheduleProjectSettingsAutosave();
        if (CleanupRescanHint is not null)
        {
            CleanupRescanHint.Text = "Settings changed • Rescan recommended";
            CleanupRescanHint.Foreground = FindResource("AccentBrush") as Brush ?? CleanupRescanHint.Foreground;
        }
        SetStatus("Voice cleanup updated. Rescan is available now if you want detection to use the new noise-handling choices.");
    }

    private void ResetNoise_Click(object sender, RoutedEventArgs e) { NoiseProfileCombo.SelectedIndex = 0; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetSmartThreshold_Click(object sender, RoutedEventArgs e) { SmartThresholdCheck.IsChecked = true; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetThreshold_Click(object sender, RoutedEventArgs e) { ThresholdSlider.Value = -40; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetMinimum_Click(object sender, RoutedEventArgs e) { MinimumSlider.Value = 0.08; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetPadding_Click(object sender, RoutedEventArgs e) { PaddingSlider.Value = 0.008; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetBlips_Click(object sender, RoutedEventArgs e) { BlipsSlider.Value = 0.10; QueueSilenceRecalculate(); PlayUiSound("navigate"); }
    private void ResetCleanup_Click(object sender, RoutedEventArgs e)
    {
        BlendCutEdgesCheck.IsChecked = true;
        MouthClicksCheck.IsChecked = true;
        ImpulsiveNoiseCheck.IsChecked = true;
        BackgroundSqueaksCheck.IsChecked = true;
        CleanupCheck_Changed(sender, e);
        PlayUiSound("navigate");
    }

    private void QueueSilenceRecalculate()
    {
        if (_loadingUi) return;
        RefreshSettingsLabels();
        if (!_project.HasMedia) return;

        // v3.15: changing detector controls NEVER starts a second hidden detection job.
        // It only saves the exact visible settings and marks the current scan stale.
        // The next explicit Rescan reads the source once and replaces automatic regions.
        Interlocked.Increment(ref _detectorGeneration);
        _settingsDebounce.Stop();
        _project.Silence.SmartThreshold = SmartThresholdCheck.IsChecked == true;
        _project.Silence.ThresholdDb = ThresholdSlider.Value;
        _project.Silence.MinimumSilenceSeconds = MinimumSlider.Value;
        _project.Silence.PaddingSeconds = PaddingSlider.Value;
        _project.Silence.RemoveBlipsSeconds = BlipsSlider.Value;
        _project.Silence.NoiseProfile = ComboText(NoiseProfileCombo, "Balanced");
        _project.Silence.ProtectSoftVoice = ProtectSoftVoiceCheck.IsChecked == true;
        _project.Silence.BlendCutEdges = BlendCutEdgesCheck.IsChecked == true;
        _project.Silence.ReduceMouthClicks = MouthClicksCheck.IsChecked == true;
        _project.Silence.ReduceImpulsiveNoise = ImpulsiveNoiseCheck.IsChecked == true;
        _project.Silence.ReduceBackgroundSqueaks = BackgroundSqueaksCheck.IsChecked == true;
        MarkDirty();
        ScheduleProjectSettingsAutosave();
        if (CleanupRescanHint is not null)
        {
            CleanupRescanHint.Text = "Settings changed • Rescan recommended";
            CleanupRescanHint.Foreground = FindResource("AccentBrush") as Brush ?? CleanupRescanHint.Foreground;
        }
        SetStatus("Detection settings changed. Press Rescan once to replace the automatic regions using these exact settings.");
    }

    private void SettingsDebounce_Tick(object? sender, EventArgs e)
    {
        // Intentionally no detector work here. v3.18 guarantees there is exactly one path that
        // creates automatic regions: initial analysis or an explicit Rescan. Slider changes only
        // mark that scan stale through QueueSilenceRecalculate().
        _settingsDebounce.Stop();
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_project.HasMedia || string.IsNullOrWhiteSpace(_project.SourcePath) || !File.Exists(_project.SourcePath))
        {
            SetStatus("Open a project with valid source media before rescanning.");
            return;
        }

        TraceAction("Rescan silence");
        var scanGeneration = Interlocked.Increment(ref _detectorGeneration);
        PlayUiSound("scan");
        var tutorialRescan = IsTutorialWaitingFor("rescan");
        if (tutorialRescan) TutorialOverlay.Visibility = Visibility.Collapsed;
        Pause(markDirty: false);

        // Capture the current decisions, but do not touch history/project state unless the
        // scan actually succeeds. A failed scan should never create a fake Undo entry.
        var previous = GetExplicitScanOverrides(_project.Segments);
        var scanSettings = new SilenceSettings
        {
            SmartThreshold = SmartThresholdCheck.IsChecked == true,
            ThresholdDb = ThresholdSlider.Value,
            MinimumSilenceSeconds = MinimumSlider.Value,
            PaddingSeconds = PaddingSlider.Value,
            RemoveBlipsSeconds = BlipsSlider.Value,
            NoiseProfile = ComboText(NoiseProfileCombo, "Balanced"),
            ProtectSoftVoice = ProtectSoftVoiceCheck.IsChecked == true,
            BlendCutEdges = BlendCutEdgesCheck.IsChecked == true,
            ReduceMouthClicks = MouthClicksCheck.IsChecked == true,
            ReduceImpulsiveNoise = ImpulsiveNoiseCheck.IsChecked == true,
            ReduceBackgroundSqueaks = BackgroundSqueaksCheck.IsChecked == true
        };

        List<SilenceSegment>? rescanned = null;
        AudioAnalysisResult? freshAnalysis = null;
        await RunBusyAsync("Rescanning silence", "Reading the source audio from the beginning…", async token =>
        {
            await _ffmpeg.EnsureAvailableAsync(new Progress<string>(message => SetOperation(message, null)), token);
            SetOperation("Checking the exact current working media…", 3);
            var currentProbe = await _ffmpeg.ProbeAsync(_project.SourcePath, token).ConfigureAwait(false);
            if (currentProbe.DurationSeconds <= 0) throw new InvalidOperationException("CutFlow could not read the duration of the current working media.");
            if (!currentProbe.HasAudio) throw new InvalidOperationException("The current working media has no audio track to scan.");
            var progress = new Progress<double>(p => SetOperation($"Reading current edited audio… {p * 100:0}%", 5 + p * 68));
            freshAnalysis = await _ffmpeg.ExtractAudioAnalysisAsync(_project.SourcePath, currentProbe.DurationSeconds, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (scanGeneration != Volatile.Read(ref _detectorGeneration)) throw new OperationCanceledException("A newer rescan replaced this one.");
            await Dispatcher.InvokeAsync(() => _project.Media = currentProbe, DispatcherPriority.Send);
            if (freshAnalysis.WindowDb.Count == 0) throw new InvalidOperationException("The audio scan returned no samples.");

            SetOperation("Detecting quiet regions…", 78);
            rescanned = await Task.Run(() => _silenceAnalyzer.AnalyzeRescan(freshAnalysis, scanSettings, previous), token);
            token.ThrowIfCancellationRequested();
            SetOperation($"Found {rescanned.Count(IsPendingRegion)} silence region(s)", 100);
        });

        if (rescanned is null || freshAnalysis is null)
        {
            if (tutorialRescan) RestoreTutorialOverlayIfRunning();
            return;
        }
        if (scanGeneration != Volatile.Read(ref _detectorGeneration))
        {
            if (tutorialRescan) RestoreTutorialOverlayIfRunning();
            SetStatus("A newer rescan replaced this result.");
            return;
        }

        _history.Push(_project);
        _analysis = freshAnalysis;
        _project.Silence = scanSettings;
        _project.WaveformPeaks = freshAnalysis.Peaks;
        _project.AnalysisWindowDb = freshAnalysis.WindowDb;
        _project.AnalysisWindowPeakDb = freshAnalysis.WindowPeakDb;
        _project.AnalysisWindowZcr = freshAnalysis.WindowZcr;
        _project.VadSpeechProbability = freshAnalysis.SpeechProbability;
        _project.VadWindowSeconds = freshAnalysis.SpeechWindowSeconds;
        _project.AnalysisWindowSeconds = freshAnalysis.WindowSeconds;
        _project.SuggestedThresholdDb = freshAnalysis.SuggestedThresholdDb;
        _project.Segments = NormalizeRegionsToFrameGrid(rescanned, _project.Media);
        Timeline.Peaks = _project.WaveformPeaks;
        Timeline.Segments = _project.Segments;
        Timeline.ClearSelection(notify: false);
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();

        var waiting = _project.Segments.Count(IsPendingRegion);
        SetStatus(waiting == 0
            ? "Rescan finished. No silence matched the current settings. Try raising Threshold slightly or lowering Minimum pause."
            : $"Rescan replaced the automatic scan with {waiting} silence region{(waiting == 1 ? "" : "s")}.");
        PlayUiSound("complete");
        if (CleanupRescanHint is not null)
        {
            CleanupRescanHint.Text = "Scan is current • Rescan available anytime";
            CleanupRescanHint.Foreground = FindResource("MutedBrush") as Brush ?? CleanupRescanHint.Foreground;
        }
        if (tutorialRescan) { TutorialOverlay.Visibility = Visibility.Visible; AdvanceTutorialOn("rescan"); }
    }

    private void AddRegion_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia)
        {
            SetStatus("Open a project before adding a region.");
            return;
        }
        SetManualRegionMode(!_manualRegionMode);
        if (_manualRegionMode)
        {
            SetStatus("+ Region is active — drag across the waveform once. The new region will be selected so you can resize its edges.");
            AdvanceTutorialOn("add-region-mode");
        }
        else SetStatus("Add region cancelled.");
    }

    private void RemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedSegmentIndices().Length == 0)
        {
            SetStatus("Select one or more marked regions first, then press − Region.");
            return;
        }
        KeepSelectedSegments();
    }

    private void CopyRegions_Click(object sender, RoutedEventArgs e) => CopySelectedRegionsToClipboard(removeOriginals: false);

    private void CutRegionsClipboard_Click(object sender, RoutedEventArgs e) => CopySelectedRegionsToClipboard(removeOriginals: true);

    private void PasteRegions_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia || _busy) return;
        if (_regionClipboard.Count == 0)
        {
            SetStatus("Nothing is copied yet. Select one or more regions and press Ctrl+C first.");
            PlayUiSound("error");
            return;
        }

        var span = _regionClipboard.Max(x => x.offsetEnd);
        var anchor = Math.Clamp(Timeline.Playhead, 0, _project.Media.DurationSeconds);
        if (anchor + span > _project.Media.DurationSeconds) anchor = Math.Max(0, _project.Media.DurationSeconds - span);
        _history.Push(_project);

        var pastedStart = double.MaxValue;
        var pastedEnd = 0.0;
        foreach (var item in _regionClipboard)
        {
            var start = Math.Clamp(anchor + item.offsetStart, 0, _project.Media.DurationSeconds);
            var end = Math.Clamp(anchor + item.offsetEnd, 0, _project.Media.DurationSeconds);
            if (end - start < 0.003) continue;
            _project.Segments.Add(new SilenceSegment
            {
                StartSeconds = start,
                EndSeconds = end,
                IsAutomatic = false,
                IsCut = false,
                IsIgnored = false
            });
            pastedStart = Math.Min(pastedStart, start);
            pastedEnd = Math.Max(pastedEnd, end);
        }

        NormalizeAndMergeProjectRegions();
        Timeline.Segments = _project.Segments;
        var selected = _project.Segments
            .Select((segment, index) => new { segment, index })
            .Where(x => IsActiveRegion(x.segment) && x.segment.EndSeconds >= pastedStart - 0.02 && x.segment.StartSeconds <= pastedEnd + 0.02)
            .Select(x => x.index)
            .ToArray();
        Timeline.SetSelectedSegments(selected, notify: false);
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();
        SetStatus($"Pasted {_regionClipboard.Count} region{(_regionClipboard.Count == 1 ? "" : "s")} at {FormatDuration(anchor)}.");
        PlayUiSound("navigate");
    }

    private void CopySelectedRegionsToClipboard(bool removeOriginals)
    {
        if (!_project.HasMedia || _busy) return;
        var selected = GetSelectedSegmentIndices()
            .Where(i => IsActiveRegion(_project.Segments[i]))
            .OrderBy(i => _project.Segments[i].StartSeconds)
            .ToArray();
        if (selected.Length == 0)
        {
            SetStatus("Select one or more red regions first.");
            PlayUiSound("error");
            return;
        }

        var baseStart = selected.Min(i => _project.Segments[i].StartSeconds);
        _regionClipboard.Clear();
        foreach (var i in selected)
        {
            var segment = _project.Segments[i];
            _regionClipboard.Add((segment.StartSeconds - baseStart, segment.EndSeconds - baseStart));
        }

        if (removeOriginals)
        {
            _history.Push(_project);
            foreach (var i in selected.OrderByDescending(x => x)) _project.Segments.RemoveAt(i);
            NormalizeAndMergeProjectRegions();
            Timeline.Segments = _project.Segments;
            Timeline.ClearSelection(notify: false);
            RefreshCutsList();
            RefreshSummary();
            RefreshHistoryButtons();
            MarkDirty();
        }

        RefreshSelectionActionButtons();
        SetStatus(removeOriginals
            ? $"Cut {selected.Length} region{(selected.Length == 1 ? "" : "s")} to the region clipboard. Ctrl+V pastes at the playhead."
            : $"Copied {selected.Length} region{(selected.Length == 1 ? "" : "s")} to the region clipboard. Ctrl+V pastes at the playhead.");
        PlayUiSound("navigate");
    }

    private void NormalizeAndMergeProjectRegions()
    {
        if (!_project.HasMedia) return;
        _project.Segments = NormalizeRegionsToFrameGrid(_project.Segments, _project.Media);
    }

    private void SetManualRegionMode(bool enabled)
    {
        _manualRegionMode = enabled && _project.HasMedia;
        Timeline.RegionEditMode = _manualRegionMode;
        if (AddRegionButton is not null)
        {
            AddRegionButton.Content = _manualRegionMode ? "Cancel Add" : "+ Region";
            AddRegionButton.BorderBrush = _manualRegionMode ? FindResource("AccentBrush") as Brush : FindResource("StrongLineBrush") as Brush;
        }
    }

    private void AddManualRegion(double start, double end)
    {
        if (!_project.HasMedia) return;
        var regionStart = Math.Min(start, end);
        var regionEnd = Math.Max(start, end);
        start = Math.Clamp(regionStart, 0, _project.Media.DurationSeconds);
        end = Math.Clamp(regionEnd, 0, _project.Media.DurationSeconds);
        (start, end) = SnapRegionToFrameGrid(start, end, _project.Media, allowOneFrame: true);
        if (end - start < 0.02)
        {
            SetStatus("Drag across a wider part of the waveform to create a region.");
            return;
        }

        _history.Push(_project);
        var region = new SilenceSegment
        {
            StartSeconds = start,
            EndSeconds = end,
            IsAutomatic = false,
            IsCut = false,
            IsIgnored = false
        };
        _project.Segments.Add(region);
        NormalizeAndMergeProjectRegions();
        var index = _project.Segments.FindIndex(s => IsActiveRegion(s) && s.StartSeconds <= start + 0.02 && s.EndSeconds >= end - 0.02);
        Timeline.Segments = _project.Segments;
        if (index >= 0) Timeline.SetSelectedSegments(new[] { index }, notify: false);
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();
        SetManualRegionMode(false);
        SetStatus($"Manual region added: {region.RangeLabel}. It is selected — drag either edge to resize it.");
        PlayUiSound("navigate");
        AdvanceTutorialOn("manual-region-created");
    }

    private void ResizeManualRegion(int index, double oldStart, double oldEnd, double start, double end)
    {
        if (index < 0 || index >= _project.Segments.Count) return;
        var segment = _project.Segments[index];
        if (segment.IsIgnored) return;
        if (Math.Abs(oldStart - start) < 0.001 && Math.Abs(oldEnd - end) < 0.001) return;

        // TimelineView previews the drag live. Temporarily restore the old bounds so the undo
        // snapshot represents the state before the handle was moved.
        segment.StartSeconds = oldStart;
        segment.EndSeconds = oldEnd;
        _history.Push(_project);
        var snapped = SnapRegionToFrameGrid(
            Math.Clamp(start, 0, _project.Media.DurationSeconds),
            Math.Clamp(end, 0, _project.Media.DurationSeconds),
            _project.Media,
            allowOneFrame: true);
        segment.StartSeconds = snapped.start;
        segment.EndSeconds = snapped.end;
        NormalizeAndMergeProjectRegions();
        Timeline.Segments = _project.Segments;
        var newIndex = _project.Segments.FindIndex(s => IsActiveRegion(s) && s.StartSeconds <= snapped.start + 0.02 && s.EndSeconds >= snapped.end - 0.02);
        if (newIndex >= 0) Timeline.SetSelectedSegments(new[] { newIndex }, notify: false);
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();
        var resized = newIndex >= 0 && newIndex < _project.Segments.Count ? _project.Segments[newIndex] : null;
        if (resized?.IsCut == true)
        {
            InvalidateSmoothPreview();
            SetStatus($"CUT region resized to {resized.RangeLabel}. Press Play or Cut All to rebuild the continuous edited preview.");
        }
        else
        {
            SetStatus(resized is null ? "Region resized." : $"Region resized to {resized.RangeLabel}.");
        }
    }

    private static List<SilenceSegment> GetExplicitScanOverrides(IEnumerable<SilenceSegment> segments)
    {
        // Automatic detections are NEVER scan input. Only things the user explicitly drew or
        // explicitly chose to Keep survive a fresh detector pass.
        return segments
            .Where(s => !s.IsAutomatic || s.IsIgnored)
            .Select(CloneSegment)
            .ToList();
    }

    private static SilenceSegment CloneSegment(SilenceSegment s) => new()
    {
        StartSeconds = s.StartSeconds,
        EndSeconds = s.EndSeconds,
        IsCut = s.IsCut,
        IsAutomatic = s.IsAutomatic,
        IsIgnored = s.IsIgnored
    };

    private void RefreshSettingsLabels()
    {
        ThresholdSlider.IsEnabled = SmartThresholdCheck.IsChecked != true;
        var baseThreshold = SmartThresholdCheck.IsChecked == true && _analysis is not null ? _analysis.SuggestedThresholdDb : ThresholdSlider.Value;
        var profile = ComboText(NoiseProfileCombo, _project.Silence.NoiseProfile);
        var shownThreshold = Math.Clamp(baseThreshold + SilenceAnalyzer.NoiseProfileOffset(profile), -62, -18);
        ThresholdValue.Text = SmartThresholdCheck.IsChecked == true ? $"Auto ({shownThreshold:0} dB)" : $"{shownThreshold:0} dB";
        MinimumValue.Text = $"{MinimumSlider.Value:0.00}s";
        PaddingValue.Text = $"{PaddingSlider.Value:0.00}s";
        BlipsValue.Text = BlipsSlider.Value <= 0.001 ? "Off" : $"{BlipsSlider.Value:0.00}s";
        NoiseProfileHelp.Text = profile switch
        {
            "Noisy Room" => "Treats steady room noise as silence more confidently.",
            "Voice Focus" => "Raises sensitivity slightly while still protecting sustained soft speech.",
            "Keep Ambience" => "More conservative: preserves quiet room tone and soft sounds.",
            _ => "Balanced works well for clean voice recordings."
        };
    }

    private void RefreshCutsList()
    {
        var selectedProjectIndices = Timeline.SelectedSegmentIndices.ToArray();
        var visible = _project.Segments.Where(x => !x.IsIgnored).ToList();
        _syncingSegmentSelection = true;
        try
        {
            CutsList.ItemsSource = null;
            CutsList.ItemsSource = visible;
            CutsList.SelectedItems.Clear();
            foreach (var index in selectedProjectIndices)
            {
                if (index < 0 || index >= _project.Segments.Count) continue;
                var segment = _project.Segments[index];
                if (visible.Contains(segment)) CutsList.SelectedItems.Add(segment);
            }
        }
        finally { _syncingSegmentSelection = false; }

        var pending = _project.Segments.Count(IsPendingRegion);
        CutCountLabel.Text = pending > 0 ? $"{pending} marked" : "No regions";
        if (SuggestionEmptyState is not null) SuggestionEmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RescanButton is not null) RescanButton.IsEnabled = _project.HasMedia && !_busy;
        if (AddRegionButton is not null) AddRegionButton.IsEnabled = _project.HasMedia && !_busy;
        RebuildAppliedCutCache(Timeline.Playhead);
        RefreshSelectionActionButtons();
    }

    private void RefreshSummary()
    {
        if (!_project.HasMedia)
        {
            TimelineSummary.Text = "No media";
            UpdateTimeDisplay(0);
            return;
        }
        var marked = _project.Segments.Count(IsPendingRegion);
        var projectedCuts = MergeCutRanges(_project.Segments.Where(IsActiveRegion).Select(s => (s.StartSeconds, s.EndSeconds)), _project.Media.DurationSeconds);
        var projectedOutput = Math.Max(0, _project.Media.DurationSeconds - projectedCuts.Sum(x => x.end - x.start));
        TimelineSummary.Text = marked > 0
            ? $"{FormatDuration(_project.Media.DurationSeconds)} → {FormatDuration(projectedOutput)} • {marked} marked"
            : FormatDuration(_project.Media.DurationSeconds);
        UpdateTimeDisplay(Timeline.Playhead);
    }

    private void CutsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSegmentSelection) return;
        var indices = CutsList.SelectedItems.Cast<SilenceSegment>()
            .Select(segment => _project.Segments.IndexOf(segment))
            .Where(i => i >= 0)
            .ToArray();

        _syncingSegmentSelection = true;
        try { Timeline.SetSelectedSegments(indices, notify: false); }
        finally { _syncingSegmentSelection = false; }

        // Selection should be cheap and should never make the decoder seek/restart.
        // Use the timeline/playhead explicitly when you want to change playback position.
        RefreshSelectionActionButtons();
    }

    private void CutsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(CutsList, source) is not ListBoxItem item || item.DataContext is not SilenceSegment segment) return;
        var index = _project.Segments.IndexOf(segment);
        if (index < 0) return;
        if (!item.IsSelected)
        {
            _syncingSegmentSelection = true;
            try
            {
                CutsList.SelectedItems.Clear();
                item.IsSelected = true;
                Timeline.SetSelectedSegments(new[] { index }, notify: false);
            }
            finally { _syncingSegmentSelection = false; }
            RefreshSelectionActionButtons();
        }
        ShowTimelineSegmentMenu(index);
        e.Handled = true;
    }

    private void SelectSegmentsFromTimeline(IReadOnlyList<int> indices)
    {
        if (_syncingSegmentSelection) return;
        _syncingSegmentSelection = true;
        try
        {
            CutsList.SelectedItems.Clear();
            foreach (var index in indices)
            {
                if (index < 0 || index >= _project.Segments.Count) continue;
                var segment = _project.Segments[index];
                if (!segment.IsIgnored) CutsList.SelectedItems.Add(segment);
            }
        }
        finally { _syncingSegmentSelection = false; }
        RefreshSelectionActionButtons();
    }

    private int[] GetSelectedSegmentIndices() => Timeline.SelectedSegmentIndices
        .Where(i => i >= 0 && i < _project.Segments.Count && !_project.Segments[i].IsIgnored)
        .Distinct()
        .OrderBy(i => i)
        .ToArray();

    private void RefreshSelectionActionButtons()
    {
        var selected = GetSelectedSegmentIndices();
        var activeSelected = selected.Count(i => IsActiveRegion(_project.Segments[i]));
        KeepButton.IsEnabled = activeSelected > 0 && !_busy;
        if (RemoveRegionButton is not null) RemoveRegionButton.IsEnabled = activeSelected > 0 && !_busy;
        if (AddRegionButton is not null) AddRegionButton.IsEnabled = _project.HasMedia && !_busy;
        if (RescanButton is not null) RescanButton.IsEnabled = _project.HasMedia && !_busy;
        if (PasteRegionButton is not null) PasteRegionButton.IsEnabled = _project.HasMedia && !_busy && _regionClipboard.Count > 0;
        // Cut can commit/rebuild an already-red selection too. This avoids a dead-looking
        // button when the user selects a committed region and wants to apply the edit again.
        ApplyCutButton.IsEnabled = !_busy && _project.HasMedia && (activeSelected > 0 || _project.Segments.Any(IsActiveRegion));
        CutAllButton.IsEnabled = !_busy && _project.HasMedia;
        if (ExportButton is not null) ExportButton.IsEnabled = !_busy && !_exportInProgress && _project.HasMedia;
        SidebarUndoButton.IsEnabled = _history.CanUndo;
        if (ToolbarUndoButton is not null) ToolbarUndoButton.IsEnabled = _history.CanUndo;
        if (ToolbarRedoButton is not null) ToolbarRedoButton.IsEnabled = _history.CanRedo;
        if (CopyRegionsMenuItem is not null) CopyRegionsMenuItem.IsEnabled = activeSelected > 0 && !_busy;
        if (CutRegionsMenuItem is not null) CutRegionsMenuItem.IsEnabled = activeSelected > 0 && !_busy;
        if (PasteRegionsMenuItem is not null) PasteRegionsMenuItem.IsEnabled = _project.HasMedia && !_busy && _regionClipboard.Count > 0;
    }

    private async void ApplySelectedCut_Click(object sender, RoutedEventArgs e)
    {
        try { await ApplySelectedCutAsync(); }
        catch (Exception ex) { ShowError("CutFlow could not apply that cut", ex); }
    }

    private async Task ApplySelectedCutAsync()
    {
        TraceAction("Cut selected region(s)");
        if (_busy || !_project.HasMedia) return;

        var selected = GetSelectedSegmentIndices()
            .Where(i => IsActiveRegion(_project.Segments[i]))
            .ToArray();

        if (selected.Length == 0)
        {
            var index = _project.Segments.FindIndex(s => IsActiveRegion(s) && Timeline.Playhead >= s.StartSeconds && Timeline.Playhead <= s.EndSeconds);
            if (index < 0) index = _project.Segments.FindIndex(s => IsActiveRegion(s) && s.StartSeconds >= Timeline.Playhead);
            if (index >= 0) selected = new[] { index };
        }

        if (selected.Length == 0)
        {
            SetStatus("There is no marked region to cut. Rescan or + Region can add one.");
            PlayUiSound("error");
            return;
        }

        var seconds = selected.Sum(i => _project.Segments[i].DurationSeconds);
        var confirmed = await ConfirmCutAsync(
            selected.Length == 1 ? "Cut this region?" : $"Cut {selected.Length} regions?",
            selected.Length == 1
                ? $"Delete {_project.Segments[selected[0]].RangeLabel} ({seconds:0.00}s). The timeline will shrink by exactly that amount. Do not close the editor while Applying Cuts is running."
                : $"Delete {selected.Length} selected regions ({seconds:0.00}s total). The timeline will collapse around the removed space. Do not close the editor while Applying Cuts is running.",
            selected.Length == 1 ? "Confirm Cut" : $"Confirm {selected.Length} Cuts");
        if (!confirmed) { SetStatus("Cut cancelled."); return; }

        await CommitRegionsToWorkingMediaAsync(selected, cutAll: false);
    }

    private Task<bool> ConfirmCutAsync(string title, string body, string confirmLabel)
    {
        if (_cutConfirmTcs is not null) return _cutConfirmTcs.Task;
        _cutConfirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_tutorialRunning && TutorialOverlay.Visibility == Visibility.Visible)
        {
            _tutorialTemporarilyHidden = true;
            TutorialOverlay.Visibility = Visibility.Collapsed;
        }
        CutConfirmTitle.Text = title;
        CutConfirmBody.Text = body;
        CutConfirmApplyButton.Content = confirmLabel;
        CutConfirmOverlay.Opacity = 0;
        CutConfirmCardScale.ScaleX = 0.965;
        CutConfirmCardScale.ScaleY = 0.965;
        CutConfirmOverlay.Visibility = Visibility.Visible;
        CutConfirmOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var pop = new CubicEase { EasingMode = EasingMode.EaseOut };
        CutConfirmCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = pop });
        CutConfirmCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.965, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = pop });
        PlayUiSound("navigate");
        CutConfirmApplyButton.Focus();
        return _cutConfirmTcs.Task;
    }

    private void CompleteCutConfirmation(bool result)
    {
        var tcs = _cutConfirmTcs;
        if (tcs is null) return;
        _cutConfirmTcs = null;
        var fade = new DoubleAnimation(CutConfirmOverlay.Opacity, 0, TimeSpan.FromMilliseconds(100));
        fade.Completed += (_, _) => { CutConfirmOverlay.Visibility = Visibility.Collapsed; CutConfirmOverlay.Opacity = 1; };
        CutConfirmOverlay.BeginAnimation(OpacityProperty, fade);
        tcs.TrySetResult(result);
        if (!result) RestoreTutorialOverlayIfRunning();
    }

    private void CutConfirmApply_Click(object sender, RoutedEventArgs e) { PlayUiSound("confirm"); CompleteCutConfirmation(true); }
    private void CutConfirmCancel_Click(object sender, RoutedEventArgs e) => CompleteCutConfirmation(false);

    private void KeepSelected_Click(object sender, RoutedEventArgs e) => KeepSelectedSegments();

    private void KeepSelectedSegments()
    {
        TraceAction("Keep/delete selected suggestion(s)");
        var selected = GetSelectedSegmentIndices();
        if (selected.Length == 0)
        {
            SetStatus("Select one or more suggestions to keep.");
            return;
        }

        var removedCommittedCut = selected.Any(index => _project.Segments[index].IsCut);
        if (removedCommittedCut) PrepareForEditMutation();
        _history.Push(_project);
        foreach (var index in selected)
        {
            var segment = _project.Segments[index];
            segment.IsCut = false;
            segment.IsAutomatic = false;
            segment.IsIgnored = true; // explicit Keep: hidden and protected from rescans
        }
        if (removedCommittedCut) InvalidateSmoothPreview();
        Timeline.Segments = _project.Segments;
        Timeline.ClearSelection(notify: false);
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();
        SetStatus(selected.Length == 1 ? "Kept selected audio and removed its marker." : $"Kept {selected.Length} selections and removed their markers.");
        PlayUiSound("navigate");
        AdvanceTutorialOn("edit-action");
    }

    private void ShowTimelineSegmentMenu(int index)
    {
        if (index < 0 || index >= _project.Segments.Count) return;
        var menu = new ContextMenu();
        var segment = _project.Segments[index];
        if (IsActiveRegion(segment))
        {
            var cut = new MenuItem
            {
                Header = segment.IsCut
                    ? (GetSelectedSegmentIndices().Length > 1 ? "Apply selected cuts again" : "Apply this cut again")
                    : (GetSelectedSegmentIndices().Length > 1 ? "Cut selected regions" : "Cut region"),
                InputGestureText = "Enter"
            };
            cut.Click += async (_, _) => await ApplySelectedCutAsync();
            menu.Items.Add(cut);
        }
        var keep = new MenuItem { Header = GetSelectedSegmentIndices().Length > 1 ? "Delete selected suggestions (keep audio)" : "Delete suggestion (keep audio)", InputGestureText = "Backspace" };
        keep.Click += (_, _) => KeepSelectedSegments();
        menu.Items.Add(keep);
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "Copy selected regions", InputGestureText = "Ctrl+C" };
        copy.Click += (_, _) => CopySelectedRegionsToClipboard(removeOriginals: false);
        copy.IsEnabled = GetSelectedSegmentIndices().Length > 0;
        menu.Items.Add(copy);
        var cutClipboard = new MenuItem { Header = "Cut selected regions to clipboard", InputGestureText = "Ctrl+X" };
        cutClipboard.Click += (_, _) => CopySelectedRegionsToClipboard(removeOriginals: true);
        cutClipboard.IsEnabled = GetSelectedSegmentIndices().Length > 0;
        menu.Items.Add(cutClipboard);
        var paste = new MenuItem { Header = "Paste regions at playhead", InputGestureText = "Ctrl+V", IsEnabled = _regionClipboard.Count > 0 };
        paste.Click += (_, _) => PasteRegions_Click(this, new RoutedEventArgs());
        menu.Items.Add(paste);
        menu.PlacementTarget = Timeline;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private async void CutAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_project.HasMedia) return;
        TraceAction("Cut all marked regions");

        var activeIndices = _project.Segments
            .Select((segment, index) => new { segment, index })
            .Where(x => IsActiveRegion(x.segment))
            .Select(x => x.index)
            .ToArray();

        if (activeIndices.Length == 0)
        {
            SetStatus("There are no marked regions to cut. Press Rescan or + Region first.");
            PlayUiSound("error");
            return;
        }

        // v3.22: Cut All gets a hidden three-pass VAD safety plan before any encoding starts. The raw
        // audio map is already in memory, so this is fast math rather than three FFmpeg renders.
        // Passes 2/3 only relax pause length/edge padding while keeping soft-speech protection,
        // then tiny click/object-noise islands are merged so one natural pause becomes one range.
        List<RenderCutRange> finalPlan;
        if (_analysis is not null && _analysis.WindowDb.Count > 0)
        {
            var refined = await Task.Run(() => _silenceAnalyzer.BuildThreePassCutAllPlan(_analysis, _project.Silence, _project.Segments));
            var snapped = NormalizeRegionsToFrameGrid(refined, _project.Media);
            finalPlan = MergeCutRanges(snapped.Where(IsActiveRegion).Select(x => (x.StartSeconds, x.EndSeconds)), _project.Media.DurationSeconds)
                .Select(r => new RenderCutRange { StartSeconds = r.start, EndSeconds = r.end })
                .ToList();
        }
        else
        {
            finalPlan = MergeCutRanges(activeIndices.Select(i => (_project.Segments[i].StartSeconds, _project.Segments[i].EndSeconds)), _project.Media.DurationSeconds)
                .Select(r => new RenderCutRange { StartSeconds = r.start, EndSeconds = r.end })
                .ToList();
        }

        if (finalPlan.Count == 0)
        {
            SetStatus("There are no marked regions to cut.");
            return;
        }

        var totalSeconds = finalPlan.Sum(r => Math.Max(0, r.EndSeconds - r.StartSeconds));
        var confirmed = await ConfirmCutAsync(
            $"Cut all {finalPlan.Count} final regions?",
            $"CutFlow has finished its three-pass safety check. It will physically delete {totalSeconds:0.00}s across {finalPlan.Count} merged region(s), including tiny leftover slivers between pause fragments. Video and audio use the same exact ranges. Do not close the editor while Applying Cuts is running; a forced close safely rolls back the project.",
            "Confirm Cut All");
        if (!confirmed) { SetStatus("Cut All cancelled."); return; }

        await CommitRegionsToWorkingMediaAsync(activeIndices, cutAll: true, explicitCutPlan: finalPlan);
    }

    private async Task<bool> CommitRegionsToWorkingMediaAsync(
        IReadOnlyList<int> selectedIndices,
        bool cutAll,
        IReadOnlyList<RenderCutRange>? explicitCutPlan = null)
    {
        if (_busy || !_project.HasMedia || selectedIndices.Count == 0) return false;

        var validIndices = selectedIndices
            .Where(i => i >= 0 && i < _project.Segments.Count && IsActiveRegion(_project.Segments[i]))
            .Distinct()
            .OrderBy(i => i)
            .ToArray();
        if (validIndices.Length == 0) return false;

        Pause(markDirty: false);
        var oldPlayhead = Math.Clamp(Timeline.Playhead, 0, _project.Media.DurationSeconds);
        var oldSegments = _project.Segments.Select(CloneSegment).ToList();
        var undoSnapshot = _history.Capture(_project);

        // Freeze the exact plan before the worker starts. Single Cut uses the selected visible
        // ranges. Cut All may supply the three-pass safety plan, which is already merged/snapped
        // and includes any tiny missed pause fragments discovered during the hidden rechecks.
        var requestedCutRanges = (explicitCutPlan is not null && explicitCutPlan.Count > 0
                ? explicitCutPlan.Select(r => new RenderCutRange { StartSeconds = r.StartSeconds, EndSeconds = r.EndSeconds })
                : validIndices.Select(i => oldSegments[i])
                    .Select(s => new RenderCutRange { StartSeconds = s.StartSeconds, EndSeconds = s.EndSeconds }))
            .OrderBy(r => r.StartSeconds)
            .ThenBy(r => r.EndSeconds)
            .ToList();
        if (requestedCutRanges.Count == 0) return false;
        var requestedPlanHash = BuildCutPlanHash(requestedCutRanges);

        // This is only an estimate for status text. After rendering, the editor uses the worker's
        // returned AppliedRanges as the sole source of truth for collapsing analysis/timeline.
        var estimatedMergedRanges = MergeCutRanges(requestedCutRanges.Select(r => (r.StartSeconds, r.EndSeconds)), _project.Media.DurationSeconds);
        if (estimatedMergedRanges.Count == 0) return false;
        var removedSeconds = estimatedMergedRanges.Sum(x => x.end - x.start);
        var sourceDuration = _project.Media.DurationSeconds;
        // Keep processor internals out of the user's human-readable project folder. The project
        // itself contains the .cutflow file; temporary jobs and shortened working revisions live
        // under CutFlow\Cache where users do not have to wonder what Jobs/Working means.
        var projectCacheKey = _project.Id.ToString("N");
        var workingDir = Path.Combine(_storage.WorkingDirectory, projectCacheKey);
        var jobDir = Path.Combine(_storage.ProcessingDirectory, "Cuts", projectCacheKey, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        Directory.CreateDirectory(workingDir);

        // Transaction checkpoint: save the exact pre-cut project and copy it into this job.
        // The worker can restore this file if the editor is force-closed or fails before ack.
        await _storage.AutosaveAsync(_project);
        var projectPath = _storage.GetAutosavePath(_project);
        var projectBackupPath = Path.Combine(jobDir, "project-before-cut.cutflow");
        if (File.Exists(projectPath)) File.Copy(projectPath, projectBackupPath, true);

        var extension = _project.Media.HasVideo ? ".mp4" : ".m4a";
        var renderedPath = Path.Combine(workingDir, $"edit-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{extension}");
        var jobPath = Path.Combine(jobDir, "job.json");
        var resultPath = Path.Combine(jobDir, "result.json");
        var acknowledgePath = Path.Combine(jobDir, "editor-ack.txt");
        var progressPath = Path.Combine(jobDir, "progress.json");
        var cancelPath = Path.Combine(jobDir, "cancel.request");

        var parentHandle = new WindowInteropHelper(this).Handle;
        var job = new RenderWorkerJob
        {
            ProjectName = _project.Name,
            SourcePath = _project.SourcePath,
            DestinationPath = renderedPath,
            ResultPath = resultPath,
            AcknowledgePath = acknowledgePath,
            ProgressPath = progressPath,
            CancelPath = cancelPath,
            ProjectPath = projectPath,
            ProjectBackupPath = projectBackupPath,
            Media = new MediaInfo
            {
                DurationSeconds = _project.Media.DurationSeconds,
                Width = _project.Media.Width,
                Height = _project.Media.Height,
                FrameRate = _project.Media.FrameRate,
                VideoDurationSeconds = _project.Media.VideoDurationSeconds,
                AudioDurationSeconds = _project.Media.AudioDurationSeconds,
                FormatStartSeconds = _project.Media.FormatStartSeconds,
                VideoStartSeconds = _project.Media.VideoStartSeconds,
                AudioStartSeconds = _project.Media.AudioStartSeconds,
                HasVideo = _project.Media.HasVideo,
                HasAudio = _project.Media.HasAudio,
                VideoCodec = _project.Media.VideoCodec,
                AudioCodec = _project.Media.AudioCodec
            },
            Silence = CloneSilenceSettingsForWorkingCut(_project.Silence),
            CutRanges = requestedCutRanges,
            InputPlanHash = requestedPlanHash,
            ParentWindowHandle = parentHandle.ToInt64(),
            ParentProcessId = Environment.ProcessId
        };

        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));

        _busy = true;
        _operationCts = new CancellationTokenSource();
        RefreshSelectionActionButtons();
        RefreshPlaybackButtons();
        SetStatus($"{(cutAll ? "Cut All" : "Cut")} is running in the background CutFlow worker. The separate progress notice will stay responsive even if the editor pauses.");

        Process? worker = null;
        Process? monitor = null;
        var committed = false;
        var uiApplied = false;
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                executable = Path.Combine(AppContext.BaseDirectory, "CutFlow.exe");
            if (!File.Exists(executable)) throw new InvalidOperationException("CutFlow could not locate its processor executable.");

            // Heavy encoder process: hidden, represented by its own system-tray icon.
            var workerPsi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
                CreateNoWindow = true
            };
            workerPsi.ArgumentList.Add("--cut-worker");
            workerPsi.ArgumentList.Add(jobPath);
            worker = Process.Start(workerPsi) ?? throw new InvalidOperationException("CutFlow could not start the background cut worker.");

            // Progress monitor: completely separate process. It does no FFmpeg/media work at all;
            // it only reads progress.json and performs tiny ETA math, so it stays responsive even
            // when the hidden worker is CPU-bound.
            var monitorPsi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            monitorPsi.ArgumentList.Add("--cut-monitor");
            monitorPsi.ArgumentList.Add(jobPath);
            try { monitor = Process.Start(monitorPsi); } catch { monitor = null; }

            // The editor also only polls a tiny result JSON. All actual media work happens in the
            // hidden worker process. If WPF pauses, worker + monitor continue independently.
            RenderWorkerResult? result = null;
            while (result is null)
            {
                _operationCts.Token.ThrowIfCancellationRequested();
                if (File.Exists(resultPath)) result = await ReadRenderWorkerResultAsync(resultPath, _operationCts.Token);
                if (result is not null) break;
                if (worker.HasExited)
                    throw new InvalidOperationException($"The CutFlow Processor closed before returning a result (exit code {worker.ExitCode}).");
                await Task.Delay(140, _operationCts.Token);
            }

            if (!result.Success)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "The CutFlow Processor could not finish the cut." : result.Error);
            if (!File.Exists(result.DestinationPath))
                throw new InvalidOperationException("The processor reported success, but the shortened working file is missing.");
            if (result.InputRangeCount != requestedCutRanges.Count)
                throw new InvalidOperationException($"The processor received {result.InputRangeCount} regions, but the editor snapshot contained {requestedCutRanges.Count}. The result was rejected.");
            if (!string.Equals(result.InputPlanHash, requestedPlanHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The processor's input cut-plan hash did not match the exact red-region snapshot. The result was rejected instead of applying the wrong intervals.");
            if (result.AppliedRanges.Count == 0 || result.AppliedRanges.Count != result.MergedRangeCount)
                throw new InvalidOperationException("The processor did not return a complete applied-range manifest. The result was rejected.");
            if (!string.Equals(result.AppliedPlanHash, BuildCutPlanHash(result.AppliedRanges), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The processor's applied cut manifest failed integrity verification.");

            var appliedCutRanges = result.AppliedRanges
                .Select(r => (start: r.StartSeconds, end: r.EndSeconds))
                .OrderBy(r => r.start)
                .ToList();

            // Collapse the editor using the EXACT merged ranges that the worker says it rendered.
            // This prevents the UI from showing one set of deletions while media used another.
            var renderedAnalysis = await Task.Run(
                () => CollapseAnalysisAfterCuts(_analysis, appliedCutRanges, result.Media.DurationSeconds),
                _operationCts.Token);
            _operationCts.Token.ThrowIfCancellationRequested();

            var shiftedSegments = await Task.Run(() =>
                cutAll
                    ? ShiftOnlyExplicitKeepDecisionsAfterCuts(oldSegments, appliedCutRanges, result.Media.DurationSeconds)
                    : ShiftRemainingSegmentsAfterCuts(oldSegments, validIndices.ToHashSet(), appliedCutRanges, result.Media.DurationSeconds),
                _operationCts.Token);

            // ONE Cut All must consume ONE complete visible cut plan. If any active/red region
            // survived this calculation, reject the UI mutation instead of making the user cut
            // the same plan a second or third time.
            if (cutAll && shiftedSegments.Any(IsActiveRegion))
                throw new InvalidOperationException("Cut All did not consume the complete visible region plan. The project was left unchanged instead of requiring another pass.");

            await Dispatcher.InvokeAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(_project.OriginalSourcePath)) _project.OriginalSourcePath = _project.SourcePath;
                _project.SourcePath = result.DestinationPath;
                _project.Media = result.Media;
                _project.WaveformPeaks = renderedAnalysis.Peaks;
                _project.AnalysisWindowDb = renderedAnalysis.WindowDb;
                _project.AnalysisWindowPeakDb = renderedAnalysis.WindowPeakDb;
                _project.AnalysisWindowZcr = renderedAnalysis.WindowZcr;
                _project.VadSpeechProbability = renderedAnalysis.SpeechProbability;
                _project.VadWindowSeconds = renderedAnalysis.SpeechWindowSeconds;
                _project.AnalysisWindowSeconds = renderedAnalysis.WindowSeconds;
                _project.SuggestedThresholdDb = renderedAnalysis.SuggestedThresholdDb;
                _analysis = renderedAnalysis;
                _project.Segments = shiftedSegments;
                _project.LastPlayheadSeconds = Math.Clamp(MapTimeAfterCuts(oldPlayhead, appliedCutRanges), 0, result.Media.DurationSeconds);
                if (cutAll && _project.Segments.Any(IsActiveRegion))
                    throw new InvalidOperationException("Cut All completed media rendering but active red markers still remained. CutFlow refused to commit that inconsistent state.");
                _project.SchemaVersion = CurrentProjectSchema;

                InvalidateSmoothPreview();
                _usingSmoothPreview = false;
                _smoothPreviewPath = null;
                _smoothPreviewRanges = new();
                _loadedPreviewPath = null;
                _mediaReady = false;
                RebuildAppliedCutCache(_project.LastPlayheadSeconds);
                // The verified shortened file is now the ONLY playback/rescan source. Force any
                // decoder still holding the pre-cut file to release it before the UI reloads.
                Interlocked.Increment(ref _previewOpenGeneration);
                try { Preview.Stop(); Preview.Source = null; } catch { }
                _loadedPreviewPath = null;
                _mediaReady = false;
                LoadProjectIntoUi();
                Timeline.ClearSelection(notify: false);
                Timeline.InvalidateStaticCache();
                RefreshCutsList();
                RefreshSummary();
                RefreshHistoryButtons();
                MarkDirty();
                uiApplied = true;
            }, DispatcherPriority.Send);

            await _storage.AutosaveAsync(_project, _operationCts.Token);
            await WriteEditorAcknowledgementAsync(acknowledgePath, "editor-applied", _operationCts.Token);
            _history.PushSnapshot(undoSnapshot);
            committed = true;
            RefreshHistoryButtons();

            // Give the processor a moment to show its 100% success state and close itself.
            try { await worker.WaitForExitAsync(_operationCts.Token).WaitAsync(TimeSpan.FromSeconds(4), _operationCts.Token); } catch { }

            _dirty = false;
            SetStatus(cutAll
                ? $"Cut All finished in one pass. All {result.InputRangeCount} marked regions were consumed; {result.RemovedSeconds:0.00}s physically removed ({sourceDuration:0.00}s → {result.Media.DurationSeconds:0.00}s). No new regions appear unless you press Rescan or + Region."
                : $"Cut finished. {result.RemovedSeconds:0.00}s physically removed; timeline is {result.Media.DurationSeconds:0.00}s. Playback + Rescan now use the new edited file.");
            PlayUiSound("success");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cut operation cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            ShowError("CutFlow could not finish that cut", ex);
            return false;
        }
        finally
        {
            _busy = false;
            _operationCts?.Dispose();
            _operationCts = null;
            RefreshSelectionActionButtons();
            RefreshPlaybackButtons();
            if (!committed)
            {
                if (uiApplied)
                {
                    try
                    {
                        _history.Restore(_project, undoSnapshot);
                        RestoreAnalysisFromProject();
                        InvalidateSmoothPreview();
                        _usingSmoothPreview = false;
                        _loadedPreviewPath = null;
                        _mediaReady = false;
                        LoadProjectIntoUi();
                        RefreshCutsList();
                        RefreshSummary();
                        RefreshHistoryButtons();
                    }
                    catch { }
                }
                try { if (File.Exists(renderedPath)) File.Delete(renderedPath); } catch { }
                try { File.WriteAllText(acknowledgePath, "editor-failed"); } catch { }
            }
            try { worker?.Dispose(); } catch { }
            try { monitor?.Dispose(); } catch { }
        }
    }

    private static async Task WriteEditorAcknowledgementAsync(string path, string value, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await File.WriteAllTextAsync(path, value, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(80 + attempt * 40, cancellationToken);
            }
        }
        throw new IOException("CutFlow could not finalize the cut transaction acknowledgement.", last);
    }

    private static async Task<RenderWorkerResult?> ReadRenderWorkerResultAsync(string path, CancellationToken cancellationToken)
    {
        // The worker writes temp + atomic rename, but antivirus/indexers can still briefly hold the
        // file. Retry a few times instead of turning a successful cut into a random JSON error.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return await JsonSerializer.DeserializeAsync<RenderWorkerResult>(stream, cancellationToken: cancellationToken);
            }
            catch (IOException) when (attempt < 7) { await Task.Delay(70, cancellationToken); }
            catch (JsonException) when (attempt < 7) { await Task.Delay(70, cancellationToken); }
        }
        return null;
    }

    private CutFlowProject CloneProjectForWorkingRender(CutFlowProject source, IReadOnlyList<(double start, double end)> cutRanges)
    {
        return new CutFlowProject
        {
            SchemaVersion = CurrentProjectSchema,
            Id = source.Id,
            Name = source.Name,
            SourcePath = source.SourcePath,
            OriginalSourcePath = source.OriginalSourcePath,
            Media = new MediaInfo
            {
                DurationSeconds = source.Media.DurationSeconds,
                Width = source.Media.Width,
                Height = source.Media.Height,
                FrameRate = source.Media.FrameRate,
                VideoDurationSeconds = source.Media.VideoDurationSeconds,
                AudioDurationSeconds = source.Media.AudioDurationSeconds,
                FormatStartSeconds = source.Media.FormatStartSeconds,
                VideoStartSeconds = source.Media.VideoStartSeconds,
                AudioStartSeconds = source.Media.AudioStartSeconds,
                HasVideo = source.Media.HasVideo,
                HasAudio = source.Media.HasAudio,
                VideoCodec = source.Media.VideoCodec,
                AudioCodec = source.Media.AudioCodec
            },
            Silence = CloneSilenceSettingsForWorkingCut(source.Silence),
            Segments = cutRanges.Select(r => new SilenceSegment
            {
                StartSeconds = r.start,
                EndSeconds = r.end,
                IsCut = true,
                IsAutomatic = false,
                IsIgnored = false
            }).ToList()
        };
    }


    private static SilenceSettings CloneSilenceSettingsForWorkingCut(SilenceSettings s)
    {
        var clone = CloneSilenceSettings(s);
        // Noise cleanup is applied once at final export so repeated manual Cut operations do
        // not stack the same restoration filter over the voice again and again. Edge blending
        // remains active here because it belongs to the newly-created join itself.
        clone.ReduceMouthClicks = false;
        clone.ReduceImpulsiveNoise = false;
        clone.ReduceBackgroundSqueaks = false;
        return clone;
    }
    private static SilenceSettings CloneSilenceSettings(SilenceSettings s) => new()
    {
        SmartThreshold = s.SmartThreshold,
        ThresholdDb = s.ThresholdDb,
        MinimumSilenceSeconds = s.MinimumSilenceSeconds,
        PaddingSeconds = s.PaddingSeconds,
        RemoveBlipsSeconds = s.RemoveBlipsSeconds,
        NoiseProfile = s.NoiseProfile,
        ProtectSoftVoice = s.ProtectSoftVoice,
        ReduceMouthClicks = s.ReduceMouthClicks,
        ReduceImpulsiveNoise = s.ReduceImpulsiveNoise,
        ReduceBackgroundSqueaks = s.ReduceBackgroundSqueaks,
        BlendCutEdges = s.BlendCutEdges
    };

    private static List<SilenceSegment> NormalizeRegionsToFrameGrid(IEnumerable<SilenceSegment> regions, MediaInfo media)
    {
        // A video can only remove whole frames. v3.18 snaps OUTWARD (never inward) and stores the
        // snapped values back into the visible red box. That means the box on screen is the exact
        // frame-accurate interval used by BOTH video and audio during Cut/Cut All.
        var result = new List<SilenceSegment>();
        foreach (var source in regions.OrderBy(x => x.StartSeconds).ThenBy(x => x.EndSeconds))
        {
            var snapped = SnapRegionToFrameGrid(source.StartSeconds, source.EndSeconds, media, allowOneFrame: true);
            if (snapped.end - snapped.start < 0.003) continue;
            result.Add(new SilenceSegment
            {
                StartSeconds = snapped.start,
                EndSeconds = snapped.end,
                IsCut = source.IsCut,
                IsAutomatic = source.IsAutomatic,
                IsIgnored = source.IsIgnored
            });
        }

        // Outward frame snapping can make two fragments touch/overlap by a frame. Never draw
        // those as duplicate skinny boxes. Collapse them into one region so the timeline exactly
        // communicates one continuous deletion. Explicit Keep records stay separate/hidden.
        // Treat microscopic gaps as one continuous region. Two detector/manual boxes that are
        // separated by less than about one frame (or 18 ms for audio-only media) are visually and
        // audibly one edit, and leaving that sliver behind is exactly what caused "double boxes".
        var mergeGap = media.HasVideo && media.FrameRate > 1 && media.FrameRate <= 240
            ? Math.Max(0.018, (1.0 / media.FrameRate) * 1.15)
            : 0.018;
        var merged = new List<SilenceSegment>();
        foreach (var region in result.OrderBy(x => x.StartSeconds).ThenBy(x => x.EndSeconds))
        {
            if (region.IsIgnored)
            {
                merged.Add(region);
                continue;
            }
            var previous = merged.Count > 0 ? merged[^1] : null;
            if (previous is not null && !previous.IsIgnored && region.StartSeconds <= previous.EndSeconds + mergeGap)
            {
                previous.EndSeconds = Math.Max(previous.EndSeconds, region.EndSeconds);
                previous.IsAutomatic = previous.IsAutomatic && region.IsAutomatic;
                previous.IsCut = previous.IsCut || region.IsCut;
            }
            else merged.Add(region);
        }
        return merged.OrderBy(x => x.StartSeconds).ThenBy(x => x.EndSeconds).ToList();
    }

    private static (double start, double end) SnapRegionToFrameGrid(double start, double end, MediaInfo media, bool allowOneFrame)
    {
        var rawStart = Math.Clamp(Math.Min(start, end), 0, media.DurationSeconds);
        var rawEnd = Math.Clamp(Math.Max(start, end), 0, media.DurationSeconds);

        if (!media.HasVideo || media.FrameRate <= 1 || media.FrameRate > 240)
        {
            if (rawEnd > rawStart + 0.003) return (rawStart, rawEnd);
            return allowOneFrame ? (rawStart, Math.Min(media.DurationSeconds, rawStart + 0.010)) : (rawStart, rawStart);
        }

        var frame = 1.0 / media.FrameRate;
        // OUTWARD snapping is deliberate: CutFlow must never show a red interval and then remove
        // LESS than that interval because a boundary landed between video frames.
        var snappedStart = Math.Floor((rawStart + 1e-9) / frame) * frame;
        var snappedEnd = Math.Ceiling((rawEnd - 1e-9) / frame) * frame;
        snappedStart = Math.Clamp(snappedStart, 0, media.DurationSeconds);
        snappedEnd = Math.Clamp(snappedEnd, 0, media.DurationSeconds);

        if (snappedEnd <= snappedStart + 0.0005 && allowOneFrame)
            snappedEnd = Math.Min(media.DurationSeconds, snappedStart + frame);
        if (snappedEnd <= snappedStart + 0.0005) return (snappedStart, snappedStart);
        return (snappedStart, snappedEnd);
    }

    private static string BuildCutPlanHash(IEnumerable<RenderCutRange> ranges)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var payload = string.Join("|", ranges
            .OrderBy(r => r.StartSeconds)
            .ThenBy(r => r.EndSeconds)
            .Select(r => $"{r.StartSeconds.ToString("0.######", inv)}:{r.EndSeconds.ToString("0.######", inv)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static List<(double start, double end)> MergeCutRanges(IEnumerable<(double start, double end)> ranges, double duration)
    {
        var sorted = ranges
            .Select(r => (start: Math.Clamp(Math.Min(r.start, r.end), 0, duration), end: Math.Clamp(Math.Max(r.start, r.end), 0, duration)))
            .Where(r => r.end - r.start >= 0.003)
            .OrderBy(r => r.start)
            .ToList();
        var merged = new List<(double start, double end)>();
        foreach (var r in sorted)
        {
            if (merged.Count == 0 || r.start > merged[^1].end + 0.018) merged.Add(r);
            else merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, r.end));
        }
        return merged;
    }

    private static AudioAnalysisResult CollapseAnalysisAfterCuts(AudioAnalysisResult? source, IReadOnlyList<(double start, double end)> cuts, double newDuration)
    {
        if (source is null || source.WindowDb.Count == 0 || source.WindowSeconds <= 0)
            return new AudioAnalysisResult { WindowSeconds = 0.02 };

        var db = new List<double>(source.WindowDb.Count);
        var peakDb = new List<double>(source.WindowDb.Count);
        var zcr = new List<double>(source.WindowDb.Count);
        var peakSource = source.WindowPeakDb.Count == source.WindowDb.Count ? source.WindowPeakDb : source.WindowDb;
        var zcrSource = source.WindowZcr.Count == source.WindowDb.Count ? source.WindowZcr : null;
        var ws = source.WindowSeconds;
        var cutIndex = 0;

        for (var i = 0; i < source.WindowDb.Count; i++)
        {
            var center = (i + 0.5) * ws;
            while (cutIndex < cuts.Count && center >= cuts[cutIndex].end) cutIndex++;
            if (cutIndex < cuts.Count && center >= cuts[cutIndex].start && center < cuts[cutIndex].end) continue;
            db.Add(source.WindowDb[i]);
            peakDb.Add(peakSource[i]);
            zcr.Add(zcrSource is null ? 0.08 : zcrSource[i]);
        }

        // Bound the analysis to the new duration in case container/frame rounding differs by a
        // few milliseconds from the mathematical cut duration.
        var expectedWindows = Math.Max(0, (int)Math.Ceiling(newDuration / ws) + 1);
        if (expectedWindows > 0 && db.Count > expectedWindows)
        {
            db.RemoveRange(expectedWindows, db.Count - expectedWindows);
            peakDb.RemoveRange(expectedWindows, peakDb.Count - expectedWindows);
            zcr.RemoveRange(expectedWindows, zcr.Count - expectedWindows);
        }

        var vadProbability = new List<double>();
        var vadWs = source.SpeechWindowSeconds > 0 ? source.SpeechWindowSeconds : 0.032;
        if (source.SpeechProbability.Count > 0)
        {
            var vadCutIndex = 0;
            for (var i = 0; i < source.SpeechProbability.Count; i++)
            {
                var center = (i + 0.5) * vadWs;
                while (vadCutIndex < cuts.Count && center >= cuts[vadCutIndex].end) vadCutIndex++;
                if (vadCutIndex < cuts.Count && center >= cuts[vadCutIndex].start && center < cuts[vadCutIndex].end) continue;
                vadProbability.Add(source.SpeechProbability[i]);
            }
            var expectedVadWindows = Math.Max(0, (int)Math.Ceiling(newDuration / vadWs) + 1);
            if (expectedVadWindows > 0 && vadProbability.Count > expectedVadWindows)
                vadProbability.RemoveRange(expectedVadWindows, vadProbability.Count - expectedVadWindows);
        }

        var peaks = BuildWaveformPeaksFromWindowDb(peakDb, 4800);
        return new AudioAnalysisResult
        {
            Peaks = peaks,
            WindowDb = db,
            WindowPeakDb = peakDb,
            WindowZcr = zcr,
            SpeechProbability = vadProbability,
            SpeechWindowSeconds = vadWs,
            WindowSeconds = ws,
            SuggestedThresholdDb = source.SuggestedThresholdDb
        };
    }

    private static List<double> BuildWaveformPeaksFromWindowDb(IReadOnlyList<double> peakDb, int targetCount)
    {
        if (peakDb.Count == 0) return new List<double>();
        var bucket = Math.Max(1, (int)Math.Ceiling(peakDb.Count / (double)Math.Max(1, targetCount)));
        var result = new List<double>(Math.Min(targetCount + 1, peakDb.Count));
        for (var i = 0; i < peakDb.Count; i += bucket)
        {
            var max = 0.0;
            var end = Math.Min(peakDb.Count, i + bucket);
            for (var j = i; j < end; j++)
                max = Math.Max(max, Math.Pow(10.0, Math.Clamp(peakDb[j], -90.0, 0.0) / 20.0));
            result.Add(Math.Clamp(max, 0, 1));
        }
        return result;
    }

    private static double MapTimeAfterCuts(double time, IReadOnlyList<(double start, double end)> cuts)
    {
        var removed = 0.0;
        foreach (var cut in cuts)
        {
            if (time <= cut.start) break;
            removed += Math.Max(0, Math.Min(time, cut.end) - cut.start);
            if (time < cut.end) return Math.Max(0, cut.start - removed + (Math.Min(time, cut.end) - cut.start));
        }
        return Math.Max(0, time - removed);
    }

    private static List<SilenceSegment> ShiftOnlyExplicitKeepDecisionsAfterCuts(
        IReadOnlyList<SilenceSegment> oldSegments,
        IReadOnlyList<(double start, double end)> cuts,
        double newDuration)
    {
        // Cut All consumes EVERY active/red region. The only region records allowed to survive
        // are explicit Keep decisions (IsIgnored), which remain hidden and are shifted so a later
        // Rescan can continue honoring that user choice on the shortened timeline.
        var result = new List<SilenceSegment>();
        foreach (var old in oldSegments.Where(s => s.IsIgnored))
        {
            var overlap = cuts.Sum(c => Math.Max(0, Math.Min(old.EndSeconds, c.end) - Math.Max(old.StartSeconds, c.start)));
            if (overlap >= old.DurationSeconds - 0.005) continue;

            var start = Math.Clamp(MapTimeAfterCuts(old.StartSeconds, cuts), 0, newDuration);
            var end = Math.Clamp(MapTimeAfterCuts(old.EndSeconds, cuts), 0, newDuration);
            if (end - start < 0.01) continue;
            result.Add(new SilenceSegment
            {
                StartSeconds = start,
                EndSeconds = end,
                IsCut = false,
                IsAutomatic = old.IsAutomatic,
                IsIgnored = true
            });
        }
        return result.OrderBy(x => x.StartSeconds).ThenBy(x => x.EndSeconds).ToList();
    }

    private static List<SilenceSegment> ShiftRemainingSegmentsAfterCuts(
        IReadOnlyList<SilenceSegment> oldSegments,
        HashSet<int> selectedIndices,
        IReadOnlyList<(double start, double end)> cuts,
        double newDuration)
    {
        var result = new List<SilenceSegment>();
        for (var i = 0; i < oldSegments.Count; i++)
        {
            if (selectedIndices.Contains(i)) continue;
            var old = oldSegments[i];
            var overlap = cuts.Sum(c => Math.Max(0, Math.Min(old.EndSeconds, c.end) - Math.Max(old.StartSeconds, c.start)));
            if (overlap >= old.DurationSeconds - 0.01) continue;

            var start = MapTimeAfterCuts(old.StartSeconds, cuts);
            var end = MapTimeAfterCuts(old.EndSeconds, cuts);
            start = Math.Clamp(start, 0, newDuration);
            end = Math.Clamp(end, 0, newDuration);
            if (end - start < 0.02) continue;
            result.Add(new SilenceSegment
            {
                StartSeconds = start,
                EndSeconds = end,
                IsCut = false,
                IsAutomatic = old.IsAutomatic,
                IsIgnored = old.IsIgnored
            });
        }
        return result.OrderBy(x => x.StartSeconds).ThenBy(x => x.EndSeconds).ToList();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia || _busy || _playRequestInProgress) return;
        if (_isPlaying)
        {
            Pause();
            return;
        }

        _playRequestInProgress = true;
        try
        {
            await PlayAsync();
            AdvanceTutorialOn("play");
        }
        catch (Exception ex)
        {
            _isPlaying = false;
            _pendingPlayAfterMediaOpen = false;
            RefreshPlaybackButtons();
            ShowError("CutFlow could not start playback", ex);
        }
        finally
        {
            _playRequestInProgress = false;
        }
    }

    private void SchedulePreviewOpen(string path, double sourceSeconds)
    {
        var generation = Interlocked.Increment(ref _previewOpenGeneration);
        _mediaReady = false;
        _pendingSourcePosition = sourceSeconds;
        try { Preview.Pause(); Preview.Source = null; } catch { }
        _loadedPreviewPath = null;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (generation != Volatile.Read(ref _previewOpenGeneration)) return;
            if (!_project.HasMedia || !string.Equals(_project.SourcePath, path, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return;
            SwitchPreviewSource(path, useSmoothPreview: false, sourceSeconds: sourceSeconds, playWhenOpened: false);
        }), DispatcherPriority.ContextIdle);
    }

    private async Task PlayAsync()
    {
        if (!_project.HasMedia || !File.Exists(_project.SourcePath)) return;
        var seconds = Math.Clamp(Timeline.Playhead, 0, _project.Media.DurationSeconds);

        // v3.11 uses a physically shortened working media file after every confirmed cut.
        // Playback therefore has ONE source and never swaps to hidden cut proxies or seeks over
        // pending markers. This removes an entire class of decoder races/freezes.
        if (_usingSmoothPreview || !IsLoadedPreviewPath(_project.SourcePath))
        {
            Interlocked.Increment(ref _previewOpenGeneration);
            SwitchPreviewSource(_project.SourcePath, useSmoothPreview: false, sourceSeconds: seconds, playWhenOpened: true);
            return;
        }

        if (!_mediaReady)
        {
            _pendingPlayAfterMediaOpen = true;
            _pendingSourcePosition = seconds;
            SetStatus("Opening the current edited timeline…");
            try
            {
                Preview.IsMuted = false;
                Preview.Volume = Math.Clamp(VolumeSlider.Value, 0, 1);
                Preview.Play(); // forces MediaElement to open/initialize if it has not yet done so
            }
            catch (Exception ex)
            {
                _pendingPlayAfterMediaOpen = false;
                throw new InvalidOperationException("The Windows media decoder could not start this video.", ex);
            }
            return;
        }

        StartMediaPlayback();
        await Task.CompletedTask;
    }

    private void ApplyPreviewAudioState()
    {
        if (Preview is null) return;
        Preview.IsMuted = false;
        Preview.Balance = 0;
        Preview.Volume = Math.Clamp(VolumeSlider?.Value ?? AppSettings.PlaybackVolume, 0, 1);
    }

    private void ScheduleAudioRecovery(bool resumePlayback)
    {
        // The caller still tells us whether playback is active for readability, but recovery now
        // only reapplies device state; it never restarts MediaElement mid-stream.
        _ = resumePlayback;
        _audioRecoveryTimer.Stop();
        _audioRecoveryTimer.Start();
    }

    private void StartMediaPlayback()
    {
        if (!_mediaReady || !_project.HasMedia) return;
        ApplyPreviewAudioState();
        Preview.ScrubbingEnabled = false;
        SyncNextCutIndex(CurrentSourcePositionSafe());
        Preview.Play();
        _isPlaying = true;
        RefreshPlaybackButtons();
        SetStatus(_project.PreviewWithoutSilence && _appliedCuts.Count > 0
            ? $"TEST preview • skipping {_appliedCuts.Count} marked region{(_appliedCuts.Count == 1 ? "" : "s")} live"
            : "Playing current timeline");
    }

    private void Pause(bool markDirty = false)
    {
        if (!_project.HasMedia) { _isPlaying = false; RefreshPlaybackButtons(); return; }
        _pendingPlayAfterMediaOpen = false;
        try { if (_mediaReady) Preview.Pause(); Preview.ScrubbingEnabled = true; } catch { }
        _isPlaying = false;
        RefreshPlaybackButtons();
        if (markDirty) MarkDirty();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia) return;
        Pause();
        SeekTo(0, markDirty: false, continuePlayback: false);
    }

    private void Rewind_Click(object sender, RoutedEventArgs e) => SeekTo(Timeline.Playhead - AppSettings.SkipSeconds, false, continuePlayback: _isPlaying);
    private void Forward_Click(object sender, RoutedEventArgs e) => SeekTo(Timeline.Playhead + AppSettings.SkipSeconds, false, continuePlayback: _isPlaying);

    private void RefreshPlaybackButtons()
    {
        if (PlayGlyph is null || PauseGlyph is null) return;
        PlayGlyph.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_project.HasMedia || !_isPlaying || _timelineScrubbing || !_mediaReady) return;
        try
        {
            var now = _playbackClock.Elapsed.TotalMilliseconds;
            var needsCutBoundaryPolling = !_usingSmoothPreview && _project.PreviewWithoutSilence && _appliedCuts.Count > 0;
            // Reading MediaElement.Position is a cross-component call into the Windows media
            // pipeline. Poll it quickly only while TEST cuts need boundary timing. Normal playback
            // uses at most ~24 UI polls/sec; the video/audio decoder itself keeps playing at full
            // frame/sample rate, but the editor stops wasting CPU asking for the clock 30-60x/sec.
            if (!needsCutBoundaryPolling && now - _lastUiRefreshMs < Math.Max(_uiRefreshIntervalMs, 1000.0 / 24.0)) return;

            var mediaSeconds = Preview.Position.TotalSeconds;
            var seconds = _usingSmoothPreview ? PreviewToSourceTime(mediaSeconds) : mediaSeconds;

            // TEST CUTS is intentionally a live seek preview, not a render. A very small decoder
            // hiccup at each marker is acceptable here because the point is to audition the edit
            // before committing it. Cut/Cut All later produces the physically shortened file.
            if (!_usingSmoothPreview && _project.PreviewWithoutSilence && _appliedCuts.Count > 0)
            {
                while (_nextCutIndex < _appliedCuts.Count && seconds >= _appliedCuts[_nextCutIndex].EndSeconds - 0.002)
                    _nextCutIndex++;
                if (_nextCutIndex < _appliedCuts.Count)
                {
                    var region = _appliedCuts[_nextCutIndex];
                    // TEST mode waits until playback actually reaches the visible red start,
                    // then seeks directly to that region's visible end. The tiny MediaElement
                    // seek hiccup is intentional in TEST mode so the user can feel where the
                    // future edit is; a committed Cut uses the continuous shortened file instead.
                    if (seconds >= region.StartSeconds && seconds < region.EndSeconds - 0.001)
                    {
                        var jumpTo = Math.Clamp(region.EndSeconds, 0, _project.Media.DurationSeconds);
                        Preview.Position = TimeSpan.FromSeconds(jumpTo);
                        ScheduleAudioRecovery(resumePlayback: true);
                        _nextCutIndex++;
                        _project.LastPlayheadSeconds = jumpTo;
                        Timeline.Playhead = jumpTo;
                        UpdateTimeDisplay(jumpTo);
                        return;
                    }
                }
            }

            _project.LastPlayheadSeconds = seconds;
            // Do not poke MediaElement's audio device on a timer. Volume/mute are applied on media
            // open, explicit volume changes and shortly after a seek. Periodically reassigning them
            // during playback can cause audible dropouts on some Windows audio drivers.
            if (now - _lastUiRefreshMs < _uiRefreshIntervalMs) return;
            _lastUiRefreshMs = now;
            Timeline.Playhead = seconds;
            if (AppSettings.FollowPlayhead) Timeline.FollowPlayhead(seconds);
            UpdateTimeDisplay(seconds);
        }
        catch (Exception ex)
        {
            // MediaElement can briefly reject Position access while a source is opening/closing.
            // Never let that transient decoder state crash the WPF dispatcher.
            _isPlaying = false;
            _pendingPlayAfterMediaOpen = false;
            RefreshPlaybackButtons();
            SetStatus("Playback paused safely: " + FriendlyError(ex));
        }
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            _mediaReady = true;
            ApplyPreviewAudioState();
            var sourceSeconds = Math.Clamp(_pendingSourcePosition >= 0 ? _pendingSourcePosition : _project.LastPlayheadSeconds, 0, _project.Media.DurationSeconds);
            var mediaSeconds = _usingSmoothPreview ? SourceToPreviewTime(sourceSeconds) : sourceSeconds;
            Preview.Position = TimeSpan.FromSeconds(Math.Max(0, mediaSeconds));
            _pendingSourcePosition = -1;
            _project.LastPlayheadSeconds = sourceSeconds;
            Timeline.Playhead = sourceSeconds;
            UpdateTimeDisplay(sourceSeconds);

            if (_pendingPlayAfterMediaOpen)
            {
                _pendingPlayAfterMediaOpen = false;
                StartMediaPlayback();
            }
            else
            {
                try { Preview.Pause(); } catch { }
                _isPlaying = false;
                RefreshPlaybackButtons();
            }
        }
        catch (Exception ex)
        {
            _mediaReady = false;
            _isPlaying = false;
            _pendingPlayAfterMediaOpen = false;
            RefreshPlaybackButtons();
            SetStatus("Preview could not finish loading: " + FriendlyError(ex));
        }
    }

    private void Preview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        _isPlaying = false;
        _pendingPlayAfterMediaOpen = false;
        RefreshPlaybackButtons();
        var message = FriendlyError(e.ErrorException ?? new InvalidOperationException("The preview decoder could not open this media."));
        SetStatus("Preview failed: " + message);

        if (_usingSmoothPreview)
        {
            var sourceSeconds = _project.LastPlayheadSeconds;
            try { if (!string.IsNullOrWhiteSpace(_smoothPreviewPath) && File.Exists(_smoothPreviewPath)) File.Delete(_smoothPreviewPath); } catch { }
            InvalidateSmoothPreview();
            SwitchPreviewSource(_project.SourcePath, useSmoothPreview: false, sourceSeconds: sourceSeconds, playWhenOpened: false);
        }
    }

    private void Preview_MediaEnded(object sender, RoutedEventArgs e)
    {
        Pause();
        SeekTo(_project.Media.DurationSeconds, false, continuePlayback: false);
    }

    private void SeekTo(double seconds, bool markDirty, bool continuePlayback = false)
    {
        if (!_project.HasMedia) return;
        seconds = Math.Clamp(seconds, 0, _project.Media.DurationSeconds);
        var shouldContinue = continuePlayback && _isPlaying;
        if (_isPlaying && !shouldContinue) Pause(markDirty: false);

        var mediaSeconds = _usingSmoothPreview ? SourceToPreviewTime(seconds) : seconds;
        try
        {
            if (_mediaReady) Preview.Position = TimeSpan.FromSeconds(Math.Max(0, mediaSeconds));
            else _pendingSourcePosition = seconds;
        }
        catch
        {
            _pendingSourcePosition = seconds;
            _mediaReady = false;
        }
        if (_mediaReady) ScheduleAudioRecovery(resumePlayback: shouldContinue);

        _project.LastPlayheadSeconds = seconds;
        Timeline.Playhead = seconds;
        SyncNextCutIndex(seconds);
        UpdateTimeDisplay(seconds);
        if (markDirty) MarkDirty();
    }

    private void BeginTimelineScrub()
    {
        if (!_project.HasMedia) return;
        _timelineScrubbing = true;
        _resumeAfterTimelineScrub = _isPlaying;
        if (_isPlaying) Pause(markDirty: false);
    }

    private void PreviewTimelinePosition(double seconds)
    {
        if (!_project.HasMedia) return;
        seconds = Math.Clamp(seconds, 0, _project.Media.DurationSeconds);
        _project.LastPlayheadSeconds = seconds;
        UpdateTimeDisplay(seconds);
    }

    private void EndTimelineScrub()
    {
        _timelineScrubbing = false;
        if (_resumeAfterTimelineScrub)
        {
            _resumeAfterTimelineScrub = false;
            _ = PlayAsyncAfterScrub();
        }
    }

    private async Task PlayAsyncAfterScrub()
    {
        if (_busy || _playRequestInProgress) return;
        _playRequestInProgress = true;
        try { await PlayAsync(); }
        catch (Exception ex) { ShowError("CutFlow could not resume playback", ex); }
        finally { _playRequestInProgress = false; }
    }

    private void RebuildAppliedCutCache(double playhead)
    {
        // TEST preview uses every visible red marker, not some hidden committed-only state.
        // Physical Cut/Cut All removes those ranges from the working media; until then this cache
        // lets MediaElement do the intentionally quick live jump so the user can hear the plan.
        _appliedCuts = _project.Segments.Where(IsActiveRegion).OrderBy(s => s.StartSeconds).ToList();
        SyncNextCutIndex(playhead);
    }

    private void SyncNextCutIndex(double seconds)
    {
        var lo = 0;
        var hi = _appliedCuts.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_appliedCuts[mid].EndSeconds <= seconds + 0.001) lo = mid + 1; else hi = mid;
        }
        _nextCutIndex = lo;
    }

    private void PrepareForEditMutation(bool switchToSource = true)
    {
        if (!_project.HasMedia) return;
        var sourceSeconds = CurrentSourcePositionSafe();
        Pause(markDirty: false);
        _project.LastPlayheadSeconds = sourceSeconds;
        Timeline.Playhead = sourceSeconds;
        UpdateTimeDisplay(sourceSeconds);

        _smoothPreviewCts?.Cancel();
        _smoothPreviewCts?.Dispose();
        _smoothPreviewCts = null;

        if (switchToSource && _usingSmoothPreview)
            SwitchPreviewSource(_project.SourcePath, useSmoothPreview: false, sourceSeconds: sourceSeconds, playWhenOpened: false);
    }

    private void InvalidateSmoothPreview()
    {
        _smoothPreviewCts?.Cancel();
        _smoothPreviewCts = null;
        Interlocked.Increment(ref _backgroundPreviewGeneration);
        _backgroundPreviewBuilding = false;
        _smoothPreviewPath = null;
        _smoothPreviewSignature = null;
        _smoothPreviewRanges = new();
    }

    private bool HasValidSmoothPreview()
    {
        if (!_project.HasMedia || !_project.Segments.Any(s => s.IsCut)) return false;
        if (string.IsNullOrWhiteSpace(_smoothPreviewPath) || !File.Exists(_smoothPreviewPath)) return false;
        if (_smoothPreviewSignature != BuildSmoothPreviewSignature()) return false;
        try { return new FileInfo(_smoothPreviewPath).Length > 1024; } catch { return false; }
    }

    private void StartBackgroundSmoothPreviewBuild()
    {
        if (!_project.HasMedia || !_project.Segments.Any(s => s.IsCut) || HasValidSmoothPreview()) return;

        // Every edit supersedes the previous proxy request. Do not let a still-cancelling
        // FFmpeg task swallow the newest request (that race made edited preview generation
        // randomly stop after quick Cut/Undo/resize actions).
        _smoothPreviewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _smoothPreviewCts = cts;
        var generation = Interlocked.Increment(ref _backgroundPreviewGeneration);
        _backgroundPreviewBuilding = true;
        _ = BuildSmoothPreviewInBackgroundAsync(generation, cts);
    }

    private async Task BuildSmoothPreviewInBackgroundAsync(int generation, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var signature = BuildSmoothPreviewSignature();
        try
        {
            // Debounce quick edits. If the user makes another change this task is cancelled
            // before FFmpeg ever starts, preventing a pile of encoders from fighting the UI.
            await Task.Delay(700, token);
            await _ffmpeg.EnsureAvailableAsync(null, token);
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "PreviewCache");
            Directory.CreateDirectory(cacheRoot);
            var output = Path.Combine(cacheRoot, $"{_project.Id:N}-{signature[..16]}.mp4");
            if (!File.Exists(output) || new FileInfo(output).Length < 1024)
            {
                var (w, h) = PreviewProxySize(_project.Media.Width, _project.Media.Height);
                var tempOutput = output + $".{Guid.NewGuid():N}.tmp.mp4";
                var options = new ExportOptions
                {
                    DestinationPath = tempOutput,
                    Format = "MP4",
                    Width = w,
                    Height = h,
                    FrameRate = _project.Media.FrameRate > 0 ? Math.Min(_project.Media.FrameRate, 30) : 30,
                    VideoCrf = 28,
                    AudioBitrateKbps = 192,
                    VideoPreset = "ultrafast",
                    VideoThreads = 1,
                    LowPriority = true
                };
                try
                {
                    var last = 0L;
                    var progress = new Progress<double>(value =>
                    {
                        if (generation != _backgroundPreviewGeneration) return;
                        var now = Environment.TickCount64;
                        if (value < 0.999 && now - last < 550) return;
                        last = now;
                        if (!_isPlaying) SetStatus($"Preparing seamless preview in background… {value * 100:0}%");
                    });
                    await _ffmpeg.ExportAsync(_project, options, progress, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(tempOutput, output, true);
                }
                finally
                {
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                }
            }

            token.ThrowIfCancellationRequested();
            if (generation != _backgroundPreviewGeneration || signature != BuildSmoothPreviewSignature()) return;
            _smoothPreviewSignature = signature;
            _smoothPreviewPath = output;
            _smoothPreviewRanges = FFmpegService.BuildKeptRanges(_project.Media.DurationSeconds, _project.Segments);
            if (!_isPlaying) SetStatus("Seamless edited preview ready. Press Play to use it.");
            PlayUiSound("complete");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.RecordAction("Background preview failed: " + FriendlyError(ex));
            if (generation == _backgroundPreviewGeneration && !_isPlaying)
                SetStatus("Cuts are saved. Seamless preview could not be prepared; live cut preview will be used.");
        }
        finally
        {
            if (generation == _backgroundPreviewGeneration)
            {
                _backgroundPreviewBuilding = false;
                if (ReferenceEquals(_smoothPreviewCts, cts)) _smoothPreviewCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task<bool> PrepareEditedPreviewWithOverlayAsync(string title, string message)
    {
        if (!_project.HasMedia || !_project.Segments.Any(s => s.IsCut)) return false;
        if (HasValidSmoothPreview()) return true;

        var succeeded = false;
        await RunBusyAsync(title, message, async token =>
        {
            var signature = BuildSmoothPreviewSignature();
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "PreviewCache");
            Directory.CreateDirectory(cacheRoot);
            var output = Path.Combine(cacheRoot, $"{_project.Id:N}-{signature[..16]}.mp4");

            if (!File.Exists(output) || new FileInfo(output).Length < 1024)
            {
                await _ffmpeg.EnsureAvailableAsync(new Progress<string>(s => SetOperation(s, null)), token);
                var (w, h) = PreviewProxySize(_project.Media.Width, _project.Media.Height);
                var tempOutput = output + $".{Guid.NewGuid():N}.tmp.mp4";
                var options = new ExportOptions
                {
                    DestinationPath = tempOutput,
                    Format = "MP4",
                    Width = w,
                    Height = h,
                    FrameRate = _project.Media.FrameRate > 0 ? Math.Min(_project.Media.FrameRate, 30) : 30,
                    VideoCrf = 27,
                    AudioBitrateKbps = 256,
                    VideoPreset = "ultrafast",
                    VideoThreads = 1,
                    LowPriority = true
                };

                try
                {
                    var progress = new Progress<double>(p => SetOperation($"Building continuous edited preview… {p * 100:0}%", Math.Max(1, p * 100)));
                    await _ffmpeg.ExportAsync(_project, options, progress, token);
                    token.ThrowIfCancellationRequested();
                    File.Move(tempOutput, output, true);
                }
                finally
                {
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                }
            }

            token.ThrowIfCancellationRequested();
            if (signature != BuildSmoothPreviewSignature()) throw new OperationCanceledException("The edit changed while the preview was being prepared.");
            _smoothPreviewSignature = signature;
            _smoothPreviewPath = output;
            _smoothPreviewRanges = FFmpegService.BuildKeptRanges(_project.Media.DurationSeconds, _project.Segments);
            succeeded = true;
            SetOperation("Edited preview ready", 100);
        });
        return succeeded && HasValidSmoothPreview();
    }

    private void SwitchToSmoothPreviewAtCurrentPosition(bool playWhenOpened)
    {
        if (!HasValidSmoothPreview()) return;
        // _project.LastPlayheadSeconds is captured before edit mutation. Using it avoids
        // mapping an OLD preview's decoder position through the NEW cut-range map.
        var sourceSeconds = Math.Clamp(_project.LastPlayheadSeconds, 0, _project.Media.DurationSeconds);
        SwitchPreviewSource(_smoothPreviewPath!, useSmoothPreview: true, sourceSeconds: sourceSeconds, playWhenOpened: playWhenOpened);
    }

    private void SwitchToOriginalSource()
    {
        if (!_project.HasMedia || !File.Exists(_project.SourcePath)) return;
        if (!_usingSmoothPreview && IsLoadedPreviewPath(_project.SourcePath)) return;
        SwitchPreviewSource(_project.SourcePath, useSmoothPreview: false, sourceSeconds: CurrentSourcePositionSafe(), playWhenOpened: false);
    }

    private void SwitchPreviewSource(string path, bool useSmoothPreview, double sourceSeconds, bool playWhenOpened)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        sourceSeconds = Math.Clamp(sourceSeconds, 0, _project.Media.DurationSeconds);
        try { if (_mediaReady) Preview.Pause(); } catch { }
        _isPlaying = false;
        _pendingPlayAfterMediaOpen = playWhenOpened;
        _pendingSourcePosition = sourceSeconds;
        _usingSmoothPreview = useSmoothPreview;
        _mediaReady = false;
        _loadedPreviewPath = path;
        Preview.Source = new Uri(path, UriKind.Absolute);
        RefreshPlaybackButtons();
        if (playWhenOpened)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { Preview.Play(); } catch { }
            }), DispatcherPriority.Background);
        }
    }

    private bool IsLoadedPreviewPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(_loadedPreviewPath)) return false;
        try { return Path.GetFullPath(path).Equals(Path.GetFullPath(_loadedPreviewPath), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(path, _loadedPreviewPath, StringComparison.OrdinalIgnoreCase); }
    }

    private double CurrentSourcePositionSafe()
    {
        var fallback = Math.Clamp(_project.LastPlayheadSeconds, 0, _project.Media.DurationSeconds);
        if (!_mediaReady) return fallback;
        try
        {
            var mediaSeconds = Math.Max(0, Preview.Position.TotalSeconds);
            var sourceSeconds = _usingSmoothPreview ? PreviewToSourceTime(mediaSeconds) : mediaSeconds;
            return Math.Clamp(sourceSeconds, 0, _project.Media.DurationSeconds);
        }
        catch { return fallback; }
    }

    private string BuildSmoothPreviewSignature()
    {
        var sb = new StringBuilder("preview-v310|");
        sb.Append(_project.SourcePath).Append('|');
        try { sb.Append(File.GetLastWriteTimeUtc(_project.SourcePath).Ticks); } catch { }
        foreach (var s in _project.Segments.Where(x => x.IsCut).OrderBy(x => x.StartSeconds))
            sb.Append('|').Append(s.StartSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append('-').Append(s.EndSeconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static (int width, int height) PreviewProxySize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);
        var scale = Math.Min(1.0, Math.Min(960.0 / sourceWidth, 540.0 / sourceHeight));
        if (scale >= 0.999) return (0, 0); // keep original when already modest
        var w = Math.Max(2, (int)Math.Round(sourceWidth * scale / 2) * 2);
        var h = Math.Max(2, (int)Math.Round(sourceHeight * scale / 2) * 2);
        return (w, h);
    }

    private double SourceToPreviewTime(double sourceSeconds)
    {
        if (!_usingSmoothPreview || _smoothPreviewRanges.Count == 0) return sourceSeconds;
        sourceSeconds = Math.Clamp(sourceSeconds, 0, _project.Media.DurationSeconds);
        var output = 0.0;
        foreach (var (start, end) in _smoothPreviewRanges)
        {
            if (sourceSeconds < start) return output;
            if (sourceSeconds <= end) return output + Math.Max(0, sourceSeconds - start);
            output += end - start;
        }
        return output;
    }

    private double PreviewToSourceTime(double previewSeconds)
    {
        if (!_usingSmoothPreview || _smoothPreviewRanges.Count == 0) return previewSeconds;
        previewSeconds = Math.Max(0, previewSeconds);
        var cursor = 0.0;
        foreach (var (start, end) in _smoothPreviewRanges)
        {
            var length = end - start;
            if (previewSeconds <= cursor + length) return start + Math.Max(0, previewSeconds - cursor);
            cursor += length;
        }
        return _project.Media.DurationSeconds;
    }

    private void StepBackOneFrame() => SeekTo(Timeline.Playhead - 1.0 / Math.Max(1, _project.Media.FrameRate), false, true);
    private void StepForwardOneFrame() => SeekTo(Timeline.Playhead + 1.0 / Math.Max(1, _project.Media.FrameRate), false, true);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Timeline.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Timeline.ZoomOut();
    private void Fit_Click(object sender, RoutedEventArgs e) => Timeline.Fit();

    private void TimelinePanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimelineScroll || Timeline is null) return;
        _pendingTimelinePanFraction = e.NewValue / 1000.0;
        if (_timelinePanQueued) return;
        _timelinePanQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _timelinePanQueued = false;
            if (!_updatingTimelineScroll) Timeline.SetScrollFraction(_pendingTimelinePanFraction);
        }), DispatcherPriority.Render);
    }

    private void RefreshTimelinePanSlider()
    {
        if (TimelinePanSlider is null) return;
        _updatingTimelineScroll = true;
        try
        {
            TimelinePanSlider.IsEnabled = Timeline.CanScroll;
            TimelinePanSlider.Opacity = Timeline.CanScroll ? 1 : 0.42;
            TimelinePanSlider.Value = Timeline.ScrollFraction * 1000.0;
            TimelinePanSlider.ToolTip = Timeline.CanScroll
                ? "Drag left or right through the timeline. Mouse wheel pans too."
                : "The entire project currently fits. Zoom in (+ or Ctrl+wheel) to pan left/right.";
        }
        finally { _updatingTimelineScroll = false; }
    }

    private void SnappingCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi || Timeline is null) return;
        Timeline.SnapEnabled = SnappingCheck.IsChecked == true;
    }

    private void SkipCutsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingUi) return;
        _project.PreviewWithoutSilence = SkipCutsCheck.IsChecked == true;
        RebuildAppliedCutCache(Timeline.Playhead);
        MarkDirty();
        SetStatus(_project.PreviewWithoutSilence
            ? "TEST cuts enabled. Playback will quickly jump over marked red regions without permanently changing the media."
            : "TEST cuts disabled. Playback will include marked regions until you Cut/Cut All.");
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeLabel is null || Preview is null) return;
        var value = Math.Clamp(e.NewValue, 0, 1);
        VolumeLabel.Text = $"{value * 100:0}%";
        Preview.IsMuted = false;
        Preview.Balance = 0;
        Preview.Volume = value;
        ScheduleAudioRecovery(resumePlayback: _isPlaying);
    }

    private async void ExportMedia_Click(object sender, RoutedEventArgs e)
    {
        TraceAction("Open export flow");
        if (_busy || !_project.HasMedia) return;
        if (_exportInProgress)
        {
            SetStatus("An export is already running in the background. Double-click the CutFlow Export tray icon to see progress.");
            return;
        }

        var pendingIndices = _project.Segments
            .Select((segment, index) => new { segment, index })
            .Where(x => IsActiveRegion(x.segment))
            .Select(x => x.index)
            .ToArray();
        if (pendingIndices.Length > 0)
        {
            var decision = MessageBox.Show(
                $"There are {pendingIndices.Length} red marked region{(pendingIndices.Length == 1 ? "" : "s")} still on the timeline.\n\nYes = physically cut those ranges first, then export\nNo = export the current shortened media without applying those markers\nCancel = go back",
                "Marked regions still on timeline",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (decision == MessageBoxResult.Cancel) return;
            if (decision == MessageBoxResult.Yes)
            {
                var applied = await CommitRegionsToWorkingMediaAsync(pendingIndices, cutAll: true);
                if (!applied) return;
            }
        }

        AdvanceTutorialOn("export-opened");
        var dialog = new ExportWindow(_project, AppSettings) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not ExportOptions options) return;
        try { await ((App)Application.Current).SettingsStore.SaveAsync(AppSettings); } catch { }
        await StartBackgroundExportAsync(options);
    }

    private async Task StartBackgroundExportAsync(ExportOptions options)
    {
        if (_exportInProgress) return;
        // Export already runs in a completely separate CutFlow worker, so crippling FFmpeg to
        // Idle priority / two CPU threads only makes a 4-minute project take tens of minutes.
        // Keep the worker isolated, but let the encoder use normal priority and enough cores.
        options.LowPriority = false;
        if (options.VideoThreads <= 0) options.VideoThreads = Math.Clamp(Environment.ProcessorCount - 1, 2, 12);
        if (string.IsNullOrWhiteSpace(options.SpeedMode)) options.SpeedMode = "Fast";
        options.VideoPreset = options.SpeedMode.Equals("Quality", StringComparison.OrdinalIgnoreCase)
            ? "medium"
            : options.SpeedMode.Equals("Balanced", StringComparison.OrdinalIgnoreCase) ? "veryfast" : "ultrafast";
        var destinationDirectory = Path.GetDirectoryName(options.DestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

        var jobDir = Path.Combine(_storage.ProcessingDirectory, "Exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        var jobPath = Path.Combine(jobDir, "job.json");
        var resultPath = Path.Combine(jobDir, "result.json");
        var progressPath = Path.Combine(jobDir, "progress.json");
        var cancelPath = Path.Combine(jobDir, "cancel.request");
        var parentHandle = new WindowInteropHelper(this).Handle;

        var job = new RenderWorkerJob
        {
            JobKind = "Export",
            ProjectName = _project.Name,
            SourcePath = _project.SourcePath,
            DestinationPath = options.DestinationPath,
            ResultPath = resultPath,
            ProgressPath = progressPath,
            CancelPath = cancelPath,
            Media = new MediaInfo
            {
                DurationSeconds = _project.Media.DurationSeconds,
                Width = _project.Media.Width,
                Height = _project.Media.Height,
                FrameRate = _project.Media.FrameRate,
                VideoDurationSeconds = _project.Media.VideoDurationSeconds,
                AudioDurationSeconds = _project.Media.AudioDurationSeconds,
                FormatStartSeconds = _project.Media.FormatStartSeconds,
                VideoStartSeconds = _project.Media.VideoStartSeconds,
                AudioStartSeconds = _project.Media.AudioStartSeconds,
                HasVideo = _project.Media.HasVideo,
                HasAudio = _project.Media.HasAudio,
                VideoCodec = _project.Media.VideoCodec,
                AudioCodec = _project.Media.AudioCodec
            },
            Silence = CloneSilenceSettings(_project.Silence),
            ExportOptions = options,
            ParentWindowHandle = parentHandle.ToInt64(),
            ParentProcessId = Environment.ProcessId
        };
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "CutFlow.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("CutFlow could not locate its background export worker.");

        var workerPsi = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory, CreateNoWindow = true };
        workerPsi.ArgumentList.Add("--export-worker");
        workerPsi.ArgumentList.Add(jobPath);
        var worker = Process.Start(workerPsi) ?? throw new InvalidOperationException("CutFlow could not start the background export worker.");

        var monitorPsi = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        monitorPsi.ArgumentList.Add("--export-monitor");
        monitorPsi.ArgumentList.Add(jobPath);
        try { Process.Start(monitorPsi); } catch { }

        _exportInProgress = true;
        RefreshSelectionActionButtons();
        SetStatus($"Exporting in the background → {options.DestinationPath}");
        PlayUiSound("navigate");

        try
        {
            RenderWorkerResult? result = null;
            while (result is null && IsLoaded)
            {
                if (File.Exists(resultPath)) result = await ReadRenderWorkerResultAsync(resultPath, CancellationToken.None);
                if (result is not null) break;
                if (worker.HasExited && !File.Exists(resultPath))
                    throw new InvalidOperationException($"The CutFlow Export worker closed unexpectedly (exit code {worker.ExitCode}).");
                await Task.Delay(180);
            }
            if (result is null) return;
            if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? "Export failed." : result.Error);
            SetStatus($"Export complete → {result.DestinationPath}");
            PlayUiSound("success");
        }
        catch (Exception ex)
        {
            ShowError("CutFlow could not finish the export", ex);
        }
        finally
        {
            _exportInProgress = false;
            try { worker.Dispose(); } catch { }
            RefreshSelectionActionButtons();
        }
    }

    private async void ExportProjectPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_project.HasMedia) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export portable CutFlow project",
            FileName = _project.Name + ".cutflowproj",
            DefaultExt = ".cutflowproj",
            Filter = "CutFlow portable project|*.cutflowproj"
        };
        if (dialog.ShowDialog() != true) return;

        await SaveCurrentProjectAsync(false, false);
        var packageCompleted = false;
        await RunBusyAsync("Exporting project", "Packing the project and source media into one CutFlow file…", async token =>
        {
            var progress = new Progress<double>(p => SetOperation($"Packing project… {p * 100:0}%", p * 100));
            await _storage.ExportPackageAsync(dialog.FileName, _project, progress, token);
            packageCompleted = true;
            SetOperation("Project package complete", 100);
        });
        if (!_closingInProgress && packageCompleted)
            MessageBox.Show("Project package exported. This .cutflowproj file contains the project data and its source media, so you can import it back into CutFlow later.", "CutFlow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia) return;
        await SaveCurrentProjectAsync(showToast: true, includeManualFile: true, force: true);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (!_project.HasMedia) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save CutFlow project file",
            InitialDirectory = _storage.ProjectsDirectory,
            FileName = _project.Name + ".cutflow",
            DefaultExt = ".cutflow",
            Filter = "CutFlow project|*.cutflow"
        };
        if (dialog.ShowDialog() != true) return;
        _manualSavePath = dialog.FileName;
        await SaveCurrentProjectAsync(showToast: true, includeManualFile: true, force: true);
    }

    private void SyncProjectSettingsFromUiForSave()
    {
        if (_loadingUi || !_project.HasMedia) return;
        _project.Silence.SmartThreshold = SmartThresholdCheck.IsChecked == true;
        _project.Silence.ThresholdDb = ThresholdSlider.Value;
        _project.Silence.MinimumSilenceSeconds = MinimumSlider.Value;
        _project.Silence.PaddingSeconds = PaddingSlider.Value;
        _project.Silence.RemoveBlipsSeconds = BlipsSlider.Value;
        _project.Silence.NoiseProfile = ComboText(NoiseProfileCombo, _project.Silence.NoiseProfile);
        _project.Silence.ProtectSoftVoice = ProtectSoftVoiceCheck.IsChecked == true;
        _project.Silence.BlendCutEdges = BlendCutEdgesCheck.IsChecked == true;
        _project.Silence.ReduceMouthClicks = MouthClicksCheck.IsChecked == true;
        _project.Silence.ReduceImpulsiveNoise = ImpulsiveNoiseCheck.IsChecked == true;
        _project.Silence.ReduceBackgroundSqueaks = BackgroundSqueaksCheck.IsChecked == true;
    }

    private async Task SaveCurrentProjectAsync(bool showToast, bool includeManualFile, bool force = false)
    {
        if (!_project.HasMedia || (!force && !_dirty)) return;
        SyncProjectSettingsFromUiForSave();
        await _saveLock.WaitAsync();
        try
        {
            if (showToast) ShowSavingToast();
            if (includeManualFile && !string.IsNullOrWhiteSpace(_manualSavePath))
                await _storage.SaveManualAsync(_manualSavePath, _project);
            else
                await _storage.AutosaveAsync(_project);
            _dirty = false;
            AutosaveStatus.Text = $"Saved {DateTime.Now:h:mm:ss tt}";
            if (showToast) ShowSavedToast();
        }
        catch (Exception ex)
        {
            if (showToast)
            {
                SaveSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                SaveToast.Visibility = Visibility.Collapsed;
            }
            SetStatus("Save failed: " + FriendlyError(ex));
            if (showToast) MessageBox.Show(FriendlyError(ex), "Could not save project", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _saveLock.Release(); }
    }

    private void ShowSavingToast()
    {
        _saveToastTimer.Stop();
        SaveCheck.Visibility = Visibility.Collapsed;
        SaveSpinner.Visibility = Visibility.Visible;
        SaveToastText.Text = "Saving project…";
        SaveToast.Visibility = Visibility.Visible;
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(720)) { RepeatBehavior = RepeatBehavior.Forever };
        SaveSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    private void ShowSavedToast()
    {
        SaveSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        SaveSpinner.Visibility = Visibility.Collapsed;
        SaveCheck.Visibility = Visibility.Visible;
        SaveToastText.Text = "Saved";
        SaveToast.Visibility = Visibility.Visible;
        _saveToastTimer.Stop();
        _saveToastTimer.Start();
        PlayUiSound("navigate");
    }

    private async void AutosaveTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy || _closingInProgress || !_dirty || !_project.HasMedia) return;
        await SaveCurrentProjectAsync(false, false);
        AutosaveStatus.Text = $"Autosaved {DateTime.Now:h:mm:ss tt}";
    }

    private void MarkDirty()
    {
        if (!_project.HasMedia) return;
        _dirty = true;
        AutosaveStatus.Text = "Changes pending autosave";
    }

    private void ScheduleProjectSettingsAutosave()
    {
        if (!_project.HasMedia) return;
        _projectSettingsSaveTimer.Stop();
        _projectSettingsSaveTimer.Start();
    }

    private void OpenProjectsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_storage.ProjectsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_storage.ProjectsDirectory}\"") { UseShellExecute = true });
            SetStatus("Opened the CutFlow Projects folder.");
        }
        catch (Exception ex) { ShowError("Could not open the Projects folder", ex); }
    }

    private void OpenExportsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_storage.ExportsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_storage.ExportsDirectory}\"") { UseShellExecute = true });
            SetStatus("Opened the CutFlow Exports folder.");
        }
        catch (Exception ex) { ShowError("Could not open the Exports folder", ex); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Directory.CreateDirectory(_storage.ProjectsDirectory);
        var dialog = new OpenFileDialog
        {
            Title = "Open CutFlow project",
            InitialDirectory = _storage.ProjectsDirectory,
            Filter = "CutFlow projects|*.cutflow;*.cutflowproj|Linked CutFlow project|*.cutflow|Portable CutFlow project|*.cutflowproj|All files|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        await OpenProjectPathAsync(dialog.FileName);
    }

    private async Task OpenProjectPathAsync(string path)
    {
        if (_project.HasMedia) await SaveCurrentProjectAsync(false, false);
        if (path.EndsWith(".cutflowproj", StringComparison.OrdinalIgnoreCase))
        {
            CutFlowProject? imported = null;
            await RunBusyAsync("Importing project", "Unpacking the CutFlow project and its media…", async token =>
            {
                var progress = new Progress<double>(p => SetOperation($"Unpacking project… {p * 100:0}%", p * 100));
                imported = await _storage.ImportPackageAsync(path, progress, token);
            });
            if (imported is not null) await ActivateProjectAsync(imported, null);
            return;
        }

        try
        {
            var loaded = await _storage.LoadProjectAsync(path);
            if (loaded is null) throw new InvalidOperationException("The project file could not be read.");
            await ActivateProjectAsync(loaded, path);
        }
        catch (Exception ex) { ShowError("Could not open project", ex); }
    }

    private async Task ActivateProjectAsync(CutFlowProject loaded, string? manualPath)
    {
        if (!File.Exists(loaded.SourcePath))
        {
            var missing = loaded.SourcePath;
            var answer = MessageBox.Show($"The source media for this project has moved or been deleted.\n\n{missing}\n\nWould you like to locate it now?", "Missing source media", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            var locate = new OpenFileDialog { Title = "Locate source media", FileName = Path.GetFileName(missing), Filter = "Media files|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.mp3;*.wav;*.m4a;*.aac;*.flac|All files|*.*" };
            if (locate.ShowDialog() != true) return;
            loaded.SourcePath = locate.FileName;
        }

        Pause(markDirty: false);
        Interlocked.Increment(ref _detectorGeneration);
        InvalidateSmoothPreview();
        _usingSmoothPreview = false;
        _project = loaded;
        var loadedSchema = _project.SchemaVersion;
        // Only the genuinely broken legacy cut engines are reset to the immutable import.
        // v3.16/schema 47 already stores the verified shortened working file correctly, so a
        // normal app update must NEVER make previously-applied cuts come back.
        var requiresLegacyMediaReset = loadedSchema <= 46;
        var detectorUpgrade = loadedSchema < CurrentDetectorSchema;

        if (loadedSchema < 46)
        {
            _project.Silence.ReduceMouthClicks = true;
            _project.Silence.ReduceImpulsiveNoise = true;
            _project.Silence.ReduceBackgroundSqueaks = true;
        }
        // v3.20 detector uses full non-speech spans and therefore needs far less hidden edge
        // safety. Migrate ONLY known historical defaults; explicit custom values stay untouched.
        if (loadedSchema < 51)
        {
            if (Math.Abs(_project.Silence.MinimumSilenceSeconds - 0.12) < 0.0001 ||
                Math.Abs(_project.Silence.MinimumSilenceSeconds - 0.10) < 0.0001)
                _project.Silence.MinimumSilenceSeconds = 0.08;
            if (Math.Abs(_project.Silence.PaddingSeconds - 0.03) < 0.0001 ||
                Math.Abs(_project.Silence.PaddingSeconds - 0.02) < 0.0001)
                _project.Silence.PaddingSeconds = 0.008;
        }

        // A detector-engine upgrade always performs one fresh read of the CURRENT working media.
        // Never seed a new detector version from the mathematically-collapsed cache left by a
        // previous cut engine, because then "open project" and "Rescan" could disagree.
        var needsVoiceMap = requiresLegacyMediaReset || detectorUpgrade ||
            _project.AnalysisWindowDb.Count == 0 ||
            _project.AnalysisWindowPeakDb.Count != _project.AnalysisWindowDb.Count ||
            _project.AnalysisWindowZcr.Count != _project.AnalysisWindowDb.Count ||
            _project.VadSpeechProbability.Count == 0;

        if (string.IsNullOrWhiteSpace(_project.OriginalSourcePath)) _project.OriginalSourcePath = _project.SourcePath;
        _manualSavePath = manualPath;
        _history.Clear();
        _dirty = false;
        ShowEditorView();

        if (requiresLegacyMediaReset)
        {
            if (!string.IsNullOrWhiteSpace(_project.OriginalSourcePath) && File.Exists(_project.OriginalSourcePath))
                _project.SourcePath = _project.OriginalSourcePath;
            _project.Segments.Clear();
            _project.WaveformPeaks.Clear();
            _project.AnalysisWindowDb.Clear();
            _project.AnalysisWindowPeakDb.Clear();
            _project.AnalysisWindowZcr.Clear();
            _project.VadSpeechProbability.Clear();
            _project.LastPlayheadSeconds = 0;
        }

        if (needsVoiceMap)
        {
            AudioAnalysisResult? refreshed = null;
            await RunBusyAsync("Refreshing project analysis", "Building a clean voice map for this version…", async token =>
            {
                await _ffmpeg.EnsureAvailableAsync(new Progress<string>(message => SetOperation(message, null)), token);
                var probe = await _ffmpeg.ProbeAsync(_project.SourcePath, token).ConfigureAwait(false);
                if (!probe.HasAudio) throw new InvalidOperationException("This project media has no audio track.");
                var progress = new Progress<double>(p => SetOperation($"Listening for speech and pauses… {p * 100:0}%", 4 + p * 90));
                refreshed = await _ffmpeg.ExtractAudioAnalysisAsync(_project.SourcePath, probe.DurationSeconds, progress, token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => _project.Media = probe, DispatcherPriority.Send);
                SetOperation("Rebuilding regions…", 96);
            });

            if (refreshed is not null)
            {
                _analysis = refreshed;
                _project.WaveformPeaks = refreshed.Peaks;
                _project.AnalysisWindowDb = refreshed.WindowDb;
                _project.AnalysisWindowPeakDb = refreshed.WindowPeakDb;
                _project.AnalysisWindowZcr = refreshed.WindowZcr;
                _project.VadSpeechProbability = refreshed.SpeechProbability;
                _project.VadWindowSeconds = refreshed.SpeechWindowSeconds;
                _project.AnalysisWindowSeconds = refreshed.WindowSeconds;
                _project.SuggestedThresholdDb = refreshed.SuggestedThresholdDb;
                var previous = requiresLegacyMediaReset ? null : GetExplicitScanOverrides(_project.Segments);
                _project.Segments = NormalizeRegionsToFrameGrid(await Task.Run(() => _silenceAnalyzer.AnalyzeRescan(refreshed, _project.Silence, previous)), _project.Media);
            }
            else
            {
                RestoreAnalysisFromProject();
                if (_analysis is not null)
                    _project.Segments = NormalizeRegionsToFrameGrid(await Task.Run(() => _silenceAnalyzer.AnalyzeRescan(_analysis, _project.Silence, requiresLegacyMediaReset ? null : GetExplicitScanOverrides(_project.Segments))), _project.Media);
            }
        }
        else
        {
            RestoreAnalysisFromProject();
            // A detector upgrade recomputes the complete automatic set ONCE from the current
            // working media analysis. It does not reset SourcePath and does not append regions.
            if (detectorUpgrade && _analysis is not null)
            {
                var previous = GetExplicitScanOverrides(_project.Segments);
                _project.Segments = NormalizeRegionsToFrameGrid(await Task.Run(() => _silenceAnalyzer.AnalyzeRescan(_analysis, _project.Silence, previous)), _project.Media);
            }
            // IMPORTANT: an empty region list is a valid, persistent editor state.
            // In particular, Cut All intentionally consumes every visible marker. Never recreate
            // automatic regions merely because the list is empty; only an explicit Rescan may do that.
        }

        // Normalize saved/manual regions on every open without re-running detection. This folds
        // microscopic gaps and overlapping boxes into the exact same region model the current
        // editor uses, so old projects cannot retain near-touching slivers.
        _project.Segments = NormalizeRegionsToFrameGrid(_project.Segments, _project.Media);
        _project.SchemaVersion = CurrentProjectSchema;
        LoadProjectIntoUi();
        await _storage.AutosaveAsync(_project);
        SetStatus(_project.Segments.Any(IsActiveRegion)
            ? $"Project opened with {_project.Segments.Count(IsActiveRegion)} marked pause region(s)."
            : "Project opened. No automatic pause regions matched; use + Region for a manual range or adjust detection and Rescan.");
        await RefreshRecentProjectsAsync();
    }

    private async void RecentOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentProjectEntry entry) await OpenRecentProjectAsync(entry);
    }

    private async void RecentProjectsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (RecentProjectsList.SelectedItem is RecentProjectEntry entry) await OpenRecentProjectAsync(entry);
    }

    private async Task OpenRecentProjectAsync(RecentProjectEntry entry)
    {
        try
        {
            var loaded = await _storage.LoadRecentProjectAsync(entry);
            if (loaded is null) throw new FileNotFoundException("The autosave for this project could not be found.");
            await ActivateProjectAsync(loaded, null);
        }
        catch (Exception ex) { ShowError("Could not open project", ex); }
    }

    private async void RecentRename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentProjectEntry entry) return;
        var dialog = new RenameProjectWindow(entry.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _storage.RenameProjectAsync(entry.Id, dialog.ProjectName);
            if (_project.Id == entry.Id) { _project.Name = dialog.ProjectName; ProjectTitleBox.Text = _project.Name; }
            await RefreshRecentProjectsAsync();
        }
        catch (Exception ex) { ShowError("Could not rename project", ex); }
    }

    private async void RecentRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentProjectEntry entry) return;
        var answer = MessageBox.Show($"Remove “{entry.Name}” from CutFlow projects?\n\nThis deletes CutFlow's autosaved project data, but it does not delete your original video/audio file.", "Remove project", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        await _storage.RemoveProjectAsync(entry.Id);
        if (_project.Id == entry.Id) ResetProject();
        await RefreshRecentProjectsAsync();
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_history.CanUndo) { SetStatus("Nothing to undo yet."); return; }
        var target = _history.PeekUndo();
        if (target is not null && !string.IsNullOrWhiteSpace(target.SourcePath) && !File.Exists(target.SourcePath))
        {
            SetStatus("That older media revision is no longer available, so CutFlow safely left the project unchanged instead of breaking playback.");
            PlayUiSound("error");
            return;
        }
        PrepareForEditMutation();
        if (!_history.Undo(_project)) return;
        ApplyHistoryState();
        SetStatus("Undo applied.");
        PlayUiSound("undo");
        AdvanceTutorialOn("edit-action");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_history.CanRedo) { SetStatus("Nothing to redo yet."); return; }
        var target = _history.PeekRedo();
        if (target is not null && !string.IsNullOrWhiteSpace(target.SourcePath) && !File.Exists(target.SourcePath))
        {
            SetStatus("That media revision is no longer available, so Redo was cancelled safely.");
            PlayUiSound("error");
            return;
        }
        PrepareForEditMutation();
        if (!_history.Redo(_project)) return;
        ApplyHistoryState();
        SetStatus("Redo applied.");
        PlayUiSound("navigate");
    }

    private void ApplyHistoryState()
    {
        RestoreAnalysisFromProject();
        InvalidateSmoothPreview();
        _usingSmoothPreview = false;
        _loadedPreviewPath = null;
        _mediaReady = false;
        LoadProjectIntoUi();
        Timeline.ClearSelection(notify: false);
        RefreshSettingsLabels();
        RefreshCutsList();
        RefreshSummary();
        RefreshHistoryButtons();
        MarkDirty();
    }

    private void RefreshHistoryButtons()
    {
        UndoMenuItem.IsEnabled = _history.CanUndo;
        RedoMenuItem.IsEnabled = _history.CanRedo;
        if (SidebarUndoButton is not null) SidebarUndoButton.IsEnabled = _history.CanUndo;
        if (ToolbarUndoButton is not null) ToolbarUndoButton.IsEnabled = _history.CanUndo;
        if (ToolbarRedoButton is not null) ToolbarRedoButton.IsEnabled = _history.CanRedo;
        if (ApplyCutButton is not null || CutAllButton is not null || KeepButton is not null) RefreshSelectionActionButtons();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        TraceAction("Open settings");
        if (_busy) return;
        var dialog = new SettingsWindow(AppSettings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await ((App)Application.Current).ApplySettingsAsync(dialog.Result);
            ApplyAppSettingsToUi();
            Timeline.InvalidateStaticCache();
            SetStatus("Settings saved.");
        }
        catch (Exception ex) { ShowError("Could not save settings", ex); }
    }

    private void ApplyAppSettingsToUi()
    {
        _loadingUi = true;
        try
        {
            VolumeSlider.Value = Math.Clamp(AppSettings.PlaybackVolume, 0, 1);
            Preview.IsMuted = false;
            Preview.Balance = 0;
            Preview.Volume = VolumeSlider.Value;
            VolumeLabel.Text = $"{VolumeSlider.Value * 100:0}%";
            _uiRefreshIntervalMs = 1000.0 / Math.Clamp(AppSettings.UiRefreshHz, 20, 60);
            if (SidebarColumn is not null) SidebarColumn.Width = new GridLength(Math.Clamp(AppSettings.EditorSidebarWidth, 260, 650));
            if (TimelineRow is not null) TimelineRow.Height = new GridLength(Math.Clamp(AppSettings.EditorTimelineHeight, 150, 520));
            _autosaveTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(AppSettings.AutosaveSeconds, 15, 120));
            _autosaveTimer.Stop();
            _autosaveTimer.Start();
            RewindButton.ToolTip = $"Rewind {AppSettings.SkipSeconds:0} seconds ({KeyBindingService.Get(AppSettings, "Rewind")})";
            ForwardButton.ToolTip = $"Fast-forward {AppSettings.SkipSeconds:0} seconds ({KeyBindingService.Get(AppSettings, "FastForward")})";
            PlayButton.ToolTip = $"Play / Pause ({KeyBindingService.Get(AppSettings, "PlayPause")})";
            ApplyCutButton.ToolTip = $"Apply selected marked region(s) to edited playback ({KeyBindingService.Get(AppSettings, "Cut")})";
            KeepButton.ToolTip = $"Keep selected region(s) ({KeyBindingService.Get(AppSettings, "Keep")})";
            CutAllButton.ToolTip = $"Cut every active marked region ({KeyBindingService.Get(AppSettings, "CutAll")})";
            SidebarUndoButton.ToolTip = $"Undo last edit ({KeyBindingService.Get(AppSettings, "Undo")})";
            Timeline.SnapEnabled = AppSettings.SnappingEnabled;
            Timeline.SnapThresholdSeconds = Math.Clamp(AppSettings.SnapThresholdSeconds, 0.01, 0.5);
            SnappingCheck.IsChecked = AppSettings.SnappingEnabled;
            if (IsLoaded && !_windowMode.Equals(AppSettings.WindowMode, StringComparison.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(new Action(() => ApplyWindowMode(AppSettings.WindowMode, persist: false)), DispatcherPriority.Background);
        }
        finally { _loadingUi = false; }
    }

    private void SidebarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (SidebarMenuItem.IsChecked)
        {
            SidebarColumn.Width = _sidebarWidth.Value > 0 ? _sidebarWidth : new GridLength(350);
            SplitterColumn.Width = new GridLength(5);
            SidebarPanel.Visibility = Visibility.Visible;
            EditorSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (SidebarColumn.ActualWidth > 0) _sidebarWidth = new GridLength(SidebarColumn.ActualWidth);
            SidebarColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
            EditorSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();
    private void FullScreenMode_Click(object sender, RoutedEventArgs e) => ApplyWindowMode("FullScreen", persist: true);
    private void BorderlessMode_Click(object sender, RoutedEventArgs e) => ApplyWindowMode("Borderless", persist: true);
    private void WindowedMode_Click(object sender, RoutedEventArgs e) => ApplyWindowMode("Windowed", persist: true);

    private void ToggleFullScreen()
    {
        ApplyWindowMode(_windowMode.Equals("FullScreen", StringComparison.OrdinalIgnoreCase) ? "Windowed" : "FullScreen", persist: true);
    }

    private void ApplyWindowMode(string? requestedMode, bool persist)
    {
        if (!IsLoaded) return;
        var mode = requestedMode switch
        {
            "Windowed" => "Windowed",
            "Borderless" => "Borderless",
            _ => "FullScreen"
        };

        if (_windowMode == "Windowed" && WindowState == WindowState.Normal && ActualWidth > 500 && ActualHeight > 400)
            _windowedBounds = new Rect(Left, Top, ActualWidth, ActualHeight);

        _windowMode = mode;
        _isFullScreen = mode == "FullScreen";
        TopBar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;

        if (mode == "Windowed")
        {
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            AppFrame.BorderThickness = new Thickness(1);
            AppFrame.CornerRadius = new CornerRadius(10);
            var bounds = _windowedBounds;
            if (bounds.Width < 980 || bounds.Height < 650)
                bounds = new Rect(80, 60, 1400, 880);
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }
        else
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            AppFrame.BorderThickness = new Thickness(0);
            AppFrame.CornerRadius = new CornerRadius(0);
            FillCurrentMonitor(mode == "FullScreen");
        }

        FullScreenModeMenuItem.IsChecked = mode == "FullScreen";
        BorderlessModeMenuItem.IsChecked = mode == "Borderless";
        WindowedModeMenuItem.IsChecked = mode == "Windowed";

        if (persist)
        {
            AppSettings.WindowMode = mode;
            _ = ((App)Application.Current).SettingsStore.SaveAsync(AppSettings);
        }
    }

    private void FillCurrentMonitor(bool includeTaskbar)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var rect = includeTaskbar ? info.rcMonitor : info.rcWork;
        SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }


    private void BuildTutorialSteps()
    {
        _tutorialSteps = new List<TutorialStep>
        {
            new("Project title & menus", "Your project name is centered at the top. File handles save/open/export, Edit contains undo/redo, View controls the workspace, and Help can replay this tour. The tutorial is view-only — nothing underneath can be clicked while it is open.", () => TopBar, null),
            new("Preview controls", "This is the playback area. Rewind and fast-forward jump by your configured amount, Play/Pause controls preview, Stop returns to the beginning, and the timestamp can be typed for an exact position.", () => PlayButton, null),
            new("Timeline navigation", "The waveform is your source timeline. Zooming increases the amount of horizontal space per second, so every region grows visually as you zoom in. The bar underneath pans left and right through a zoomed project.", () => Timeline, null),
            new("Silence detection", "The Cut silence panel controls automatic detection. Smart threshold estimates the noise floor; Minimum pause, Padding, soft-voice protection and short-noise bridging tune what counts as a removable gap. Red regions are the exact ranges CutFlow will remove if you choose Cut or Cut All.", () => SidebarPanel, null),
            new("Review regions", "Select regions on the waveform or in the Regions list. Ctrl-click selects individual regions and Shift-click selects a range. Selected region edges can be resized directly on the timeline.", () => CutsList, null),
            new("Edit actions", "Keep preserves selected audio and removes its marker. Cut removes the selected red range after confirmation. Cut All removes every currently marked red range in one operation. Undo and Redo move through your edit history.", () => ActionBar, null),
            new("Export", "Export opens the render settings where you choose format, resolution, FPS, quality and destination. Only edits you actually confirmed with Cut or Cut All are removed from the finished export.", () => ExportButton, null)
        };
    }

    private void StartTutorial(bool markAsReplay)
    {
        TraceAction("Start project tutorial");
        if (!IsLoaded || !_project.HasMedia || EditorView.Visibility != Visibility.Visible)
        {
            SetStatus("Open or create a project first. The tutorial only runs inside the editor.");
            return;
        }
        BuildTutorialSteps();
        _tutorialStepIndex = 0;
        _tutorialRunning = true;
        TutorialOverlay.Opacity = 0;
        TutorialOverlay.Visibility = Visibility.Visible;
        TutorialOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { UpdateTutorialStep(); }
            catch (Exception ex) { TutorialOverlay.Visibility = Visibility.Collapsed; _tutorialRunning = false; ShowError("The tutorial could not be displayed", ex); }
        }), DispatcherPriority.Loaded);
    }

    private async void TutorialStop_Click(object sender, RoutedEventArgs e) => await StopTutorialAsync(completed: false);

    private async void TutorialNext_Click(object sender, RoutedEventArgs e)
    {
        if (!_tutorialRunning || _tutorialSteps is null) return;
        if (_tutorialStepIndex >= _tutorialSteps.Count - 1) { await StopTutorialAsync(completed: true); return; }
        _tutorialStepIndex++;
        UpdateTutorialStep();
        PlayUiSound("navigate");
    }

    private void TutorialBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_tutorialRunning || _tutorialSteps is null || _tutorialStepIndex <= 0) return;
        _tutorialStepIndex--;
        UpdateTutorialStep();
        PlayUiSound("navigate");
    }

    private bool IsTutorialWaitingFor(string actionKey) => false;
    private void AdvanceTutorialOn(string actionKey) { }

    private void RestoreTutorialOverlayIfRunning()
    {
        if (!_tutorialRunning) return;
        TutorialOverlay.Visibility = Visibility.Visible;
        UpdateTutorialStep();
    }

    private void UpdateTutorialStep()
    {
        if (!_tutorialRunning || _tutorialSteps is null || _tutorialSteps.Count == 0) return;
        _tutorialStepIndex = Math.Clamp(_tutorialStepIndex, 0, _tutorialSteps.Count - 1);
        var step = _tutorialSteps[_tutorialStepIndex];
        TutorialStepLabel.Text = $"GUIDED TOUR  •  {_tutorialStepIndex + 1} OF {_tutorialSteps.Count}";
        TutorialTitle.Text = step.Title;
        TutorialBody.Text = step.Body;
        TutorialBackButton.IsEnabled = _tutorialStepIndex > 0;
        TutorialNextButton.Content = _tutorialStepIndex == _tutorialSteps.Count - 1 ? "Finish" : "Next";

        TutorialCard.BeginAnimation(OpacityProperty, null);
        TutorialCard.Opacity = 0;
        TutorialCardTranslate.Y = 10;
        TutorialCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        TutorialCardTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(210)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Dispatcher.BeginInvoke(new Action(() => PositionTutorialHighlight(step.Target())), DispatcherPriority.Render);
    }

    private void PositionTutorialHighlight(FrameworkElement? target)
    {
        if (!_tutorialRunning || target is null || !target.IsVisible || TutorialShadeCanvas.ActualWidth <= 1 || TutorialShadeCanvas.ActualHeight <= 1)
        {
            SetTutorialShade(new Rect(24, 64, Math.Max(80, ActualWidth - 48), 80));
            return;
        }
        try
        {
            var point = target.TransformToAncestor(AppFrame).Transform(new Point(0, 0));
            var pad = 10.0;
            var rect = new Rect(point.X - pad, point.Y - pad, Math.Max(28, target.ActualWidth + pad * 2), Math.Max(28, target.ActualHeight + pad * 2));
            SetTutorialShade(rect);

            if (rect.Left + rect.Width / 2 > ActualWidth / 2)
            {
                TutorialCard.HorizontalAlignment = HorizontalAlignment.Left;
                TutorialCard.Margin = new Thickness(24, Math.Max(72, Math.Min(ActualHeight - 330, rect.Top)), 0, 0);
            }
            else
            {
                TutorialCard.HorizontalAlignment = HorizontalAlignment.Right;
                TutorialCard.Margin = new Thickness(0, Math.Max(72, Math.Min(ActualHeight - 330, rect.Top)), 24, 0);
            }
        }
        catch { SetTutorialShade(new Rect(24, 64, Math.Max(80, ActualWidth - 48), 80)); }
    }

    private void SetTutorialShade(Rect hole)
    {
        var width = Math.Max(0, TutorialShadeCanvas.ActualWidth);
        var height = Math.Max(0, TutorialShadeCanvas.ActualHeight);
        hole.Intersect(new Rect(0, 0, width, height));
        SetCanvasRect(TutorialShadeTop, 0, 0, width, Math.Max(0, hole.Top));
        SetCanvasRect(TutorialShadeBottom, 0, hole.Bottom, width, Math.Max(0, height - hole.Bottom));
        SetCanvasRect(TutorialShadeLeft, 0, hole.Top, Math.Max(0, hole.Left), Math.Max(0, hole.Height));
        SetCanvasRect(TutorialShadeRight, hole.Right, hole.Top, Math.Max(0, width - hole.Right), Math.Max(0, hole.Height));
        SetCanvasRect(TutorialHighlight, hole.Left, hole.Top, Math.Max(0, hole.Width), Math.Max(0, hole.Height));
    }

    private static void SetCanvasRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }

    private async Task StopTutorialAsync(bool completed)
    {
        _tutorialRunning = false;
        AppSettings.TutorialCompleted = true;
        try { await ((App)Application.Current).SettingsStore.SaveAsync(AppSettings); } catch { }
        var anim = new DoubleAnimation(TutorialOverlay.Opacity, 0, TimeSpan.FromMilliseconds(130));
        anim.Completed += (_, _) => { TutorialOverlay.Visibility = Visibility.Collapsed; TutorialOverlay.Opacity = 1; };
        TutorialOverlay.BeginAnimation(OpacityProperty, anim);
        SetStatus(completed ? "Tutorial complete." : "Tutorial stopped. Replay it from Help → Start Tutorial or press F1 while a project is open.");
    }

    private void StartTutorial_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TraceAction("Help → Start Tutorial");
            StartTutorial(markAsReplay: true);
        }
        catch (Exception ex) { ShowError("Could not start the tutorial", ex); }
    }

    private void OpenCrashLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TraceAction("Open crash logs folder");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "Logs");
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError("Could not open the crash-log folder", ex); }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("CutFlow\n\nLocal silence-cutting editor with reviewable cuts, autosaved projects, portable project packages, and media export.", "About CutFlow", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RunBusyAsync(string title, string initialMessage, Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _operationCts = new CancellationTokenSource();
        OperationTitle.Text = title;
        OperationMessage.Text = initialMessage;
        OperationProgress.Value = 0;
        OperationPercent.Text = "0%";
        CancelOperationButton.IsEnabled = true;
        OperationOverlay.Opacity = 0;
        OperationCardScale.ScaleX = 0.972;
        OperationCardScale.ScaleY = 0.972;
        OperationOverlay.Visibility = Visibility.Visible;
        OperationOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        var cardEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        OperationCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.972, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = cardEase });
        OperationCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.972, 1, TimeSpan.FromMilliseconds(210)) { EasingFunction = cardEase });
        OperationSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(780))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        });
        // Let WPF paint the blocking loading overlay BEFORE any FFmpeg/process work begins.
        // Without this yield, a heavy operation can start in the same dispatcher turn and the
        // window looks frozen before the loading UI ever becomes visible.
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Task.Delay(20, _operationCts.Token);
        try { await action(_operationCts.Token); }
        catch (OperationCanceledException) { SetStatus("Operation cancelled."); }
        catch (Exception ex) { ShowError("CutFlow could not finish that operation", ex); }
        finally
        {
            _busy = false;
            OperationSpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _operationCts.Dispose();
            _operationCts = null;
            if (!_closingInProgress && IsLoaded)
            {
                var fade = new DoubleAnimation(OperationOverlay.Opacity, 0, TimeSpan.FromMilliseconds(130));
                fade.Completed += (_, _) => { OperationOverlay.Visibility = Visibility.Collapsed; OperationOverlay.Opacity = 1; };
                OperationOverlay.BeginAnimation(OpacityProperty, fade);
                RefreshSelectionActionButtons();
                if (RescanButton is not null) RescanButton.IsEnabled = _project.HasMedia;
                if (AddRegionButton is not null) AddRegionButton.IsEnabled = _project.HasMedia;
                RefreshPlaybackButtons();
            }
        }
    }

    private void SetOperation(string text, double? progress)
    {
        var now = Environment.TickCount64;
        var force = progress is >= 99.9 or <= 0.1;
        if (!force && now - Interlocked.Read(ref _lastOperationUiTick) < 90) return;
        Interlocked.Exchange(ref _lastOperationUiTick, now);
        void Apply()
        {
            OperationMessage.Text = text;
            SidebarStatus.Text = text;
            StatusText.Text = text;
            if (progress is double value)
            {
                var p = Math.Clamp(value, 0, 100);
                OperationProgress.IsIndeterminate = false;
                OperationProgress.Value = p;
                OperationPercent.Text = $"{p:0}%";
            }
            else
            {
                OperationProgress.IsIndeterminate = true;
                OperationPercent.Text = string.Empty;
            }
        }
        if (Dispatcher.CheckAccess()) Apply(); else Dispatcher.BeginInvoke((Action)Apply);
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        CancelOperationButton.IsEnabled = false;
        OperationMessage.Text = "Cancelling…";
        _operationCts?.Cancel();
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        SidebarStatus.Text = text;
    }

    private void ShowError(string title, Exception ex)
    {
        SetStatus(FriendlyError(ex));
        PlayUiSound("error");
        MessageBox.Show(FriendlyError(ex), title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FriendlyError(Exception ex)
    {
        var message = ex.GetBaseException().Message.Trim();
        if (message.Length > 900) message = message[..900] + "…";
        return message;
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }

    private static string ComboText(ComboBox combo, string fallback) => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    private static int NoiseProfileIndex(string? profile) => profile switch { "Noisy Room" => 1, "Voice Focus" => 2, "Keep Ambience" => 3, _ => 0 };

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (CutConfirmOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { CompleteCutConfirmation(false); e.Handled = true; }
            return;
        }
        if (_tutorialRunning)
        {
            if (e.Key == Key.Escape) { _ = StopTutorialAsync(completed: false); e.Handled = true; }
            else e.Handled = true;
            return;
        }
        if (e.Key == Key.F1) { StartTutorial_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBoxBase) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { CopyRegions_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X) { CutRegionsClipboard_Click(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V) { PasteRegions_Click(sender, e); e.Handled = true; return; }

        if (e.Key == Key.Escape && _manualRegionMode) { SetManualRegionMode(false); SetStatus("Manual region mode off."); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "FullScreen", e)) { ToggleFullScreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && _isFullScreen) { ApplyWindowMode("Windowed", persist: true); e.Handled = true; return; }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { SaveAs_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Save", e)) { Save_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { OpenProject_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { NewProject_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Export", e)) { ExportMedia_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Undo", e)) { Undo_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Redo", e)) { Redo_Click(sender, e); e.Handled = true; return; }

        if (EditorView.Visibility != Visibility.Visible) return;
        if (KeyBindingService.Matches(AppSettings, "PlayPause", e)) { Play_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "CutAll", e)) { CutAll_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Cut", e)) { _ = ApplySelectedCutAsync(); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Keep", e)) { KeepSelectedSegments(); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "Rewind", e)) { Rewind_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "FastForward", e)) { Forward_Click(sender, e); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "StepBack", e)) { StepBackOneFrame(); e.Handled = true; return; }
        if (KeyBindingService.Matches(AppSettings, "StepForward", e)) { StepForwardOneFrame(); e.Handled = true; }
    }

    private void TopMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.HorizontalOffset = 0;
        button.ContextMenu.VerticalOffset = 2;
        button.ContextMenu.IsOpen = true;
    }

    private void UpdateTimeDisplay(double seconds)
    {
        if (TimeLabel is null || TotalTimeLabel is null) return;
        if (!TimeLabel.IsKeyboardFocusWithin) TimeLabel.Text = FormatEditableTimestamp(seconds);
        TotalTimeLabel.Text = FormatEditableTimestamp(_project.Media.DurationSeconds);
    }

    private void TimeLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTimeEntry();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateTimeDisplay(Timeline.Playhead);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TimeLabel_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTimeEntry();

    private void CommitTimeEntry()
    {
        if (!_project.HasMedia) { UpdateTimeDisplay(0); return; }
        if (!TryParseTimestamp(TimeLabel.Text, out var seconds))
        {
            UpdateTimeDisplay(Timeline.Playhead);
            SetStatus("Timestamp format: hh:mm:ss.xx, mm:ss.xx, or seconds.");
            return;
        }
        SeekTo(Math.Clamp(seconds, 0, _project.Media.DurationSeconds), markDirty: false, continuePlayback: false);
    }

    private static bool TryParseTimestamp(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            seconds = Math.Max(0, raw);
            return true;
        }
        var parts = text.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3) return false;
        if (!double.TryParse(parts[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)) return false;
        if (!int.TryParse(parts[^2], out var min)) return false;
        var hour = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], out hour)) return false;
        seconds = Math.Max(0, hour * 3600 + min * 60 + sec);
        return true;
    }

    private static string FormatEditableTimestamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }

    private void Info_Click(object sender, RoutedEventArgs e)
    {
        var topic = (sender as FrameworkElement)?.Tag?.ToString() ?? "Setting";
        var text = topic switch
        {
            "Background noise" => "Changes the detector's sensitivity to steady room noise. Balanced is best for most recordings; Voice Focus cuts more aggressively; Keep Ambience preserves more quiet room tone.",
            "Smart threshold" => "CutFlow estimates the recording's noise floor automatically instead of forcing one fixed dB threshold for every microphone and room.",
            "Threshold" => "Audio below this loudness can be considered silence. A value closer to 0 dB is more aggressive; a more negative value is more conservative.",
            "Minimum pause" => "A quiet section must last at least this long before CutFlow suggests it as a cut. Raise it to keep short natural pauses.",
            "Padding" => "Keeps a little audio before and after speech so words do not feel clipped at the cut boundary.",
            "Voice blips" => "Bridges extremely short non-speech noises inside an otherwise quiet section, such as mouse clicks, mouth pops or tiny bumps.",
            _ => "This option changes how CutFlow analyzes or previews your recording."
        };
        InfoTitle.Text = topic;
        InfoBody.Text = text;
        InfoOverlay.Visibility = Visibility.Visible;
    }

    private void InfoClose_Click(object sender, RoutedEventArgs e) => InfoOverlay.Visibility = Visibility.Collapsed;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (MediaInfoLabel is null) return;
        var width = e.NewSize.Width;
        MediaInfoLabel.Visibility = width >= 1160 ? Visibility.Visible : Visibility.Collapsed;
        TimelineHint.Visibility = width >= 1220 ? Visibility.Visible : Visibility.Collapsed;
        VolumePanel.Visibility = width >= 1080 ? Visibility.Visible : Visibility.Collapsed;
        if (width < 1120 && SidebarPanel.Visibility == Visibility.Visible && SidebarColumn.Width.Value > 315)
            SidebarColumn.Width = new GridLength(300);
        if (_tutorialRunning) Dispatcher.BeginInvoke(new Action(UpdateTutorialStep), DispatcherPriority.Render);
    }

    private void ProjectTitleBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EditorView.Visibility != Visibility.Visible || !_project.HasMedia) return;
        BeginProjectRename();
        e.Handled = true;
    }

    private void RenameCurrentProject_Click(object sender, RoutedEventArgs e) => BeginProjectRename();

    private void BeginProjectRename()
    {
        if (!_project.HasMedia || _projectTitleEditing) return;
        _projectTitleEditing = true;
        ProjectTitleBox.IsReadOnly = false;
        ProjectTitleBox.BorderBrush = FindResource("AccentBrush") as Brush;
        ProjectTitleBox.Text = _project.Name;
        ProjectTitleBox.Focus();
        ProjectTitleBox.SelectAll();
    }

    private async void ProjectTitleBox_LostFocus(object sender, RoutedEventArgs e) => await CommitProjectRenameAsync(false);

    private async void ProjectTitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_projectTitleEditing) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitProjectRenameAsync(false);
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await CommitProjectRenameAsync(true);
            Keyboard.ClearFocus();
        }
    }

    private async Task CommitProjectRenameAsync(bool cancel)
    {
        if (!_projectTitleEditing) return;
        _projectTitleEditing = false;
        var next = cancel ? _project.Name : ProjectTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(next)) next = _project.Name;
        ProjectTitleBox.IsReadOnly = true;
        ProjectTitleBox.BorderBrush = Brushes.Transparent;
        ProjectTitleBox.Text = next;
        if (cancel || next.Equals(_project.Name, StringComparison.Ordinal)) return;

        _project.Name = next;
        MarkDirty();
        try
        {
            await _storage.RenameProjectAsync(_project.Id, next);
            await RefreshRecentProjectsAsync();
            SetStatus("Project renamed.");
        }
        catch (Exception ex) { SetStatus("Rename saved in project, but the recent-project index could not update: " + FriendlyError(ex)); }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        var path = files[0];
        var ext = Path.GetExtension(path);
        if (ext.Equals(".cutflow", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cutflowproj", StringComparison.OrdinalIgnoreCase))
        {
            await OpenProjectPathAsync(path);
            return;
        }
        if (_project.HasMedia) await SaveCurrentProjectAsync(false, false);
        ShowEditorView();
        await ImportMediaAsync(path);
    }

    private async void EditorSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn is null) return;
        AppSettings.EditorSidebarWidth = Math.Clamp(SidebarColumn.ActualWidth, 260, 650);
        try { await ((App)Application.Current).SettingsStore.SaveAsync(AppSettings); } catch { }
    }

    private async void PreviewTimelineSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (TimelineRow is null) return;
        AppSettings.EditorTimelineHeight = Math.Clamp(TimelineRow.ActualHeight, 150, 520);
        try { await ((App)Application.Current).SettingsStore.SaveAsync(AppSettings); } catch { }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            if (_windowMode == "Windowed") ToggleMaximize(); else ApplyWindowMode("Windowed", persist: true);
            return;
        }
        if (_windowMode == "Windowed" && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or MenuItem or Menu or TextBoxBase or ComboBox or Slider or ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize()
    {
        if (_windowMode != "Windowed") { ApplyWindowMode("Windowed", persist: true); return; }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Exit_Click(object sender, RoutedEventArgs e) { _forceExitRequested = true; Close(); }

    private void PlayUiSound(string name) { if (AppSettings.UiSoundsEnabled) UiFeedbackService.Play(name); }

    private void InitializeTrayIcon()
    {
        try
        {
            var icon = !string.IsNullOrWhiteSpace(Environment.ProcessPath) ? System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath) : null;
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "CutFlow",
                Icon = icon ?? System.Drawing.SystemIcons.Application,
                Visible = true
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var open = new System.Windows.Forms.ToolStripMenuItem("Open CutFlow");
            open.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }));
            var exit = new System.Windows.Forms.ToolStripMenuItem("Exit CutFlow");
            exit.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _forceExitRequested = true; Close(); }));
            menu.Items.Add(open);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(exit);
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); }));
        }
        catch { _trayIcon = null; }
    }

    private void DisposeTrayIcon()
    {
        try
        {
            if (_trayIcon is null) return;
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        catch { }
    }

    private void Window_StateChanged(object sender, EventArgs e) => UpdateWindowFrame();

    private void UpdateWindowFrame()
    {
        if (AppFrame is null) return;
        if (_windowMode != "Windowed" || WindowState == WindowState.Maximized)
        {
            AppFrame.BorderThickness = new Thickness(0);
            AppFrame.CornerRadius = new CornerRadius(0);
        }
        else
        {
            AppFrame.BorderThickness = new Thickness(1);
            AppFrame.CornerRadius = new CornerRadius(10);
        }
    }

    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
