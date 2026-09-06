using System.IO;
using System.Text.Json.Serialization;

namespace CutFlow.Models;

public sealed class CutFlowProject
{
    public int SchemaVersion { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled";
    public string SourcePath { get; set; } = string.Empty;
    // Original import is kept for reference. SourcePath is the CURRENT working media after committed cuts.
    public string OriginalSourcePath { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public MediaInfo Media { get; set; } = new();
    public SilenceSettings Silence { get; set; } = new();
    public List<SilenceSegment> Segments { get; set; } = new();
    public List<double> WaveformPeaks { get; set; } = new();
    public List<double> AnalysisWindowDb { get; set; } = new();
    public List<double> AnalysisWindowPeakDb { get; set; } = new();
    public List<double> AnalysisWindowZcr { get; set; } = new();
    // Silero VAD speech probability for each 32 ms chunk of the CURRENT working media.
    public List<double> VadSpeechProbability { get; set; } = new();
    public double VadWindowSeconds { get; set; } = 0.032;
    public double AnalysisWindowSeconds { get; set; } = 0.02;
    public double SuggestedThresholdDb { get; set; } = -40;
    public double LastPlayheadSeconds { get; set; }
    public bool PreviewWithoutSilence { get; set; } = true;

    [JsonIgnore]
    public bool HasMedia => !string.IsNullOrWhiteSpace(SourcePath);
}

public sealed class MediaInfo
{
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; } = 30;
    // Per-stream durations are used by the cut verifier so audio and video are proven to stay aligned.
    public double VideoDurationSeconds { get; set; }
    public double AudioDurationSeconds { get; set; }
    // Start timestamps matter for stream-copy exports. Some MP4/MOV files use edit lists or
    // non-zero stream starts; blindly remuxing those can make the first second look/sound corrupt.
    public double FormatStartSeconds { get; set; }
    public double VideoStartSeconds { get; set; }
    public double AudioStartSeconds { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
}

public sealed class SilenceSettings
{
    public bool SmartThreshold { get; set; } = true;
    public double ThresholdDb { get; set; } = -40.0;
    public double MinimumSilenceSeconds { get; set; } = 0.08;
    public double PaddingSeconds { get; set; } = 0.008;
    public double RemoveBlipsSeconds { get; set; } = 0.10;
    public string NoiseProfile { get; set; } = "Balanced";
    // Protect low-level sustained speech (whispers/slurred speech) from being mistaken for silence.
    public bool ProtectSoftVoice { get; set; } = true;
    // Optional subtle cleanup applied to working renders/exports; these never change detection itself.
    public bool ReduceMouthClicks { get; set; } = true;
    public bool ReduceImpulsiveNoise { get; set; } = true;
    public bool ReduceBackgroundSqueaks { get; set; } = true;
    public bool BlendCutEdges { get; set; } = true;
}

public sealed class SilenceSegment
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    // IsCut means this section has actually been committed for removal.
    // Automatic + !IsCut means it is only a detected suggestion waiting for the editor to commit it.
    public bool IsCut { get; set; } = false;
    public bool IsAutomatic { get; set; } = true;
    // True when the user explicitly chose to keep this region. Ignored regions stay out of rescans.
    public bool IsIgnored { get; set; } = false;

    [JsonIgnore]
    public double DurationSeconds => Math.Max(0, EndSeconds - StartSeconds);

    [JsonIgnore]
    public string RangeLabel => $"{FormatTime(StartSeconds)} – {FormatTime(EndSeconds)}";

    [JsonIgnore]
    public string DurationLabel => $"{DurationSeconds:0.00}s";

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }
}

public sealed class AudioAnalysisResult
{
    public List<double> Peaks { get; set; } = new();
    public List<double> WindowDb { get; set; } = new();
    public List<double> WindowPeakDb { get; set; } = new();
    public List<double> WindowZcr { get; set; } = new();
    public List<double> SpeechProbability { get; set; } = new();
    public double SpeechWindowSeconds { get; set; } = 0.032;
    public double SuggestedThresholdDb { get; set; } = -40;
    public double WindowSeconds { get; set; } = 0.02;
}

public sealed class ProjectSnapshot
{
    public string SourcePath { get; set; } = string.Empty;
    public string OriginalSourcePath { get; set; } = string.Empty;
    public MediaInfo Media { get; set; } = new();
    public SilenceSettings Silence { get; set; } = new();
    public List<SilenceSegment> Segments { get; set; } = new();
    public List<double> WaveformPeaks { get; set; } = new();
    public List<double> AnalysisWindowDb { get; set; } = new();
    public List<double> AnalysisWindowPeakDb { get; set; } = new();
    public List<double> AnalysisWindowZcr { get; set; } = new();
    // Silero VAD speech probability for each 32 ms chunk of the CURRENT working media.
    public List<double> VadSpeechProbability { get; set; } = new();
    public double VadWindowSeconds { get; set; } = 0.032;
    public double AnalysisWindowSeconds { get; set; } = 0.02;
    public double SuggestedThresholdDb { get; set; } = -40;
    public double LastPlayheadSeconds { get; set; }
}

public sealed class AppState
{
    public Guid? LastProjectId { get; set; }
    public List<RecentProjectEntry> RecentProjects { get; set; } = new();
}

public sealed class RecentProjectEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    // Original import is kept for reference. SourcePath is the CURRENT working media after committed cuts.
    public string OriginalSourcePath { get; set; } = string.Empty;
    public string AutosavePath { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasVideo { get; set; }

    [JsonIgnore]
    public string UpdatedLabel => UpdatedUtc == default ? string.Empty : UpdatedUtc.ToLocalTime().ToString("MMM d, h:mm tt");

    [JsonIgnore]
    public string DetailsLabel
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            var durationText = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}";
            if (HasVideo && Width > 0 && Height > 0) return $"{Width}×{Height}  •  {durationText}";
            return durationText;
        }
    }

    [JsonIgnore]
    public string SourceFileLabel => string.IsNullOrWhiteSpace(SourcePath) ? "Media unavailable" : Path.GetFileName(SourcePath);
}

public sealed class AppSettings
{
    public string ThemeMode { get; set; } = "System";
    public byte CustomBackgroundR { get; set; } = 34;
    public byte CustomBackgroundG { get; set; } = 36;
    public byte CustomBackgroundB { get; set; } = 38;
    // In Custom theme mode the app canvas and the editor surfaces can be tuned independently.
    public byte CustomSurfaceR { get; set; } = 31;
    public byte CustomSurfaceG { get; set; } = 33;
    public byte CustomSurfaceB { get; set; } = 36;
    public double PlaybackVolume { get; set; } = 1.0;
    public double SkipSeconds { get; set; } = 5.0;
    public int AutosaveSeconds { get; set; } = 30;
    public string DefaultNoiseProfile { get; set; } = "Balanced";
    public bool PreviewWithoutSilenceByDefault { get; set; } = true;
    public bool FollowPlayhead { get; set; } = true;
    public int UiRefreshHz { get; set; } = 30;
    public string DefaultExportFps { get; set; } = "Original";
    public string DefaultExportResolution { get; set; } = "Original";
    public string DefaultExportSpeed { get; set; } = "Fast";
    public bool ApplyCleanupOnExport { get; set; } = true;
    // Empty means CutFlow uses %LOCALAPPDATA%\CutFlow\Exports. Once the user browses elsewhere,
    // the chosen directory is remembered for future exports.
    public string LastExportDirectory { get; set; } = string.Empty;
    public double EditorSidebarWidth { get; set; } = 355;
    public double EditorTimelineHeight { get; set; } = 245;
    public string WindowMode { get; set; } = "FullScreen";
    public bool SnappingEnabled { get; set; } = true;
    public double SnapThresholdSeconds { get; set; } = 0.08;
    public bool TutorialCompleted { get; set; } = false;
    public bool ShowFirstRunTutorial { get; set; } = true;
    public bool UiSoundsEnabled { get; set; } = true;
    public Dictionary<string, string> KeyBindings { get; set; } = new()
    {
        ["PlayPause"] = "Space",
        ["StepBack"] = "Left",
        ["StepForward"] = "Right",
        ["Rewind"] = "Shift+Left",
        ["FastForward"] = "Shift+Right",
        ["Cut"] = "Enter",
        ["Keep"] = "Delete",
        ["CutAll"] = "Ctrl+Delete",
        ["Undo"] = "Ctrl+Z",
        ["Redo"] = "Ctrl+Y",
        ["Save"] = "Ctrl+S",
        ["Export"] = "Ctrl+E",
        ["FullScreen"] = "F11"
    };
}



public sealed class RenderWorkerJob
{
    // "Cut" or "Export". The lightweight monitor uses this to present the right wording.
    public string JobKind { get; set; } = "Cut";
    public string ProjectName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string AcknowledgePath { get; set; } = string.Empty;
    public string ProgressPath { get; set; } = string.Empty;
    public string CancelPath { get; set; } = string.Empty;
    public string ProjectPath { get; set; } = string.Empty;
    public string ProjectBackupPath { get; set; } = string.Empty;
    public MediaInfo Media { get; set; } = new();
    public SilenceSettings Silence { get; set; } = new();
    public List<RenderCutRange> CutRanges { get; set; } = new();
    // Hash of the exact visible region snapshot sent by the editor. The worker echoes this
    // back so no scan/filter step can silently change which ranges are cut mid-operation.
    public string InputPlanHash { get; set; } = string.Empty;
    public long ParentWindowHandle { get; set; }
    public int ParentProcessId { get; set; }
    public ExportOptions? ExportOptions { get; set; }
}

public sealed class RenderCutRange
{
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}

public sealed class RenderWorkerProgress
{
    public double Progress { get; set; }
    // Negative means the worker does not have a useful ETA yet. Export workers populate this
    // after roughly the first second of real media progress so the monitor does not sit on
    // "Estimating…" for minutes on long files.
    public double EstimatedSecondsRemaining { get; set; } = -1;
    public string Stage { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int WorkerProcessId { get; set; }
    public int InputRangeCount { get; set; }
    public int MergedRangeCount { get; set; }
    public bool IsFinished { get; set; }
    public bool HasError { get; set; }
}

public sealed class RenderWorkerResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public MediaInfo Media { get; set; } = new();
    public double SourceDurationSeconds { get; set; }
    public double RemovedSeconds { get; set; }
    public double ExpectedDurationSeconds { get; set; }
    public double ActualDurationSeconds { get; set; }
    public int InputRangeCount { get; set; }
    public int MergedRangeCount { get; set; }
    public string InputPlanHash { get; set; } = string.Empty;
    public string AppliedPlanHash { get; set; } = string.Empty;
    public List<RenderCutRange> AppliedRanges { get; set; } = new();
}

public sealed class ExportOptions
{
    public string DestinationPath { get; set; } = string.Empty;
    public string Format { get; set; } = "MP4";
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public int VideoCrf { get; set; } = 18;
    public int AudioBitrateKbps { get; set; } = 256;
    public string VideoPreset { get; set; } = "fast";
    public int VideoThreads { get; set; }
    public bool LowPriority { get; set; }
    // Fast is the default: when resolution/FPS stay Original, CutFlow copies the already-cut
    // video stream instead of re-encoding it. Balanced/Quality remain available when the user
    // explicitly wants a heavier render.
    public string SpeedMode { get; set; } = "Fast";
    // Voice cleanup is intentionally independent from video smart-copy. When enabled CutFlow can
    // still copy the video bit-for-bit and only process/re-encode the audio stream.
    public bool ApplyAudioCleanup { get; set; } = true;

    [JsonIgnore]
    public bool AudioOnly => Format.Equals("MP3", StringComparison.OrdinalIgnoreCase) ||
                             Format.Equals("WAV", StringComparison.OrdinalIgnoreCase) ||
                             Format.Equals("M4A", StringComparison.OrdinalIgnoreCase);
}

