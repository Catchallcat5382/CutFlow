using System.Diagnostics;
using System.Text.Json;
using CutFlow.Models;

namespace CutFlow.Services;

/// <summary>
/// Runs media export completely outside the WPF editor process. The editor only writes a small
/// job file and polls result.json; FFmpeg, progress parsing and file output live here.
/// </summary>
public sealed class ExportWorkerHost : IDisposable
{
    private readonly string _jobPath;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _disposed;

    public ExportWorkerHost(string jobPath) => _jobPath = jobPath;

    public void Start()
    {
        InitializeTrayIcon();
        _ = Task.Run(RunCoreAsync);
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CutFlow.exe");
            var icon = File.Exists(executable) ? System.Drawing.Icon.ExtractAssociatedIcon(executable) : System.Drawing.SystemIcons.Application;
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var show = new System.Windows.Forms.ToolStripMenuItem("Show export progress");
            show.Click += (_, _) => LaunchMonitor();
            menu.Items.Add(show);
            var folderItem = new System.Windows.Forms.ToolStripMenuItem("Open export folder");
            folderItem.Click += (_, _) =>
            {
                try
                {
                    var job = ReadJobAsync(_jobPath).GetAwaiter().GetResult();
                    var folder = Path.GetDirectoryName(job.DestinationPath);
                    if (!string.IsNullOrWhiteSpace(folder))
                        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                }
                catch { }
            };
            menu.Items.Add(folderItem);

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "CutFlow Export • starting",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => LaunchMonitor();
        }
        catch { }
    }

    private void LaunchMonitor()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "CutFlow.exe");
            var psi = new ProcessStartInfo(executable!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add("--export-monitor");
            psi.ArgumentList.Add(_jobPath);
            Process.Start(psi);
        }
        catch { }
    }

    private async Task RunCoreAsync()
    {
        var exitCode = 0;
        RenderWorkerJob? job = null;
        try
        {
            job = await ReadJobAsync(_jobPath).ConfigureAwait(false);
            if (!job.JobKind.Equals("Export", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This is not an export job.");
            if (job.ExportOptions is null)
                throw new InvalidOperationException("The export job is missing its export settings.");

            UpdateTray("CutFlow Export • rendering");
            var started = DateTime.UtcNow;
            var ffmpeg = new FFmpegService();
            await ffmpeg.EnsureAvailableAsync(null).ConfigureAwait(false);

            MediaInfo currentMedia;
            try { currentMedia = await ffmpeg.ProbeAsync(job.SourcePath).ConfigureAwait(false); }
            catch { currentMedia = job.Media; }

            var project = new CutFlowProject
            {
                Name = job.ProjectName,
                SourcePath = job.SourcePath,
                OriginalSourcePath = job.SourcePath,
                Media = currentMedia,
                Silence = job.Silence,
                Segments = new List<SilenceSegment>()
            };

            WriteProgress(job, new RenderWorkerProgress
            {
                Progress = 0.005,
                EstimatedSecondsRemaining = -1,
                Stage = "Choosing fastest export path…",
                StartedUtc = started,
                UpdatedUtc = DateTime.UtcNow,
                WorkerProcessId = Environment.ProcessId
            });

            double smoothedEtaSeconds = -1;
            var progress = new InlineProgress<double>(p =>
            {
                p = Math.Clamp(p, 0, 0.995);
                var now = DateTime.UtcNow;
                var elapsedSeconds = Math.Max(0.001, (now - started).TotalSeconds);
                var eta = -1.0;
                if (elapsedSeconds >= 0.8 && p >= 0.0015)
                {
                    var rawEta = Math.Clamp(elapsedSeconds * (1.0 - p) / Math.Max(0.0001, p), 0, 24 * 60 * 60);
                    smoothedEtaSeconds = smoothedEtaSeconds < 0 ? rawEta : smoothedEtaSeconds * 0.72 + rawEta * 0.28;
                    eta = smoothedEtaSeconds;
                }

                var fastOriginal = string.Equals(job.ExportOptions.SpeedMode, "Fast", StringComparison.OrdinalIgnoreCase) &&
                                   job.ExportOptions.Width <= 0 && job.ExportOptions.Height <= 0 && job.ExportOptions.FrameRate <= 0.1;
                WriteProgress(job, new RenderWorkerProgress
                {
                    Progress = p,
                    EstimatedSecondsRemaining = eta,
                    Stage = fastOriginal ? $"Smart exporting {job.ExportOptions.Format}…" : $"Rendering {job.ExportOptions.Format}…",
                    StartedUtc = started,
                    UpdatedUtc = now,
                    WorkerProcessId = Environment.ProcessId
                });
            });

            await ffmpeg.ExportAsync(project, job.ExportOptions, progress).ConfigureAwait(false);
            if (!File.Exists(job.DestinationPath))
                throw new InvalidOperationException("FFmpeg finished but the exported file is missing.");

            MediaInfo exportedInfo;
            try { exportedInfo = await ffmpeg.ProbeAsync(job.DestinationPath).ConfigureAwait(false); }
            catch { exportedInfo = new MediaInfo { DurationSeconds = job.Media.DurationSeconds, HasVideo = job.Media.HasVideo, HasAudio = job.Media.HasAudio }; }

            await WriteResultAsync(job, new RenderWorkerResult
            {
                Success = true,
                DestinationPath = job.DestinationPath,
                Media = exportedInfo,
                SourceDurationSeconds = project.Media.DurationSeconds,
                ExpectedDurationSeconds = project.Media.DurationSeconds,
                ActualDurationSeconds = exportedInfo.DurationSeconds
            }).ConfigureAwait(false);

            WriteProgress(job, new RenderWorkerProgress
            {
                Progress = 1,
                EstimatedSecondsRemaining = 0,
                Stage = "Export complete",
                StartedUtc = started,
                UpdatedUtc = DateTime.UtcNow,
                WorkerProcessId = Environment.ProcessId,
                IsFinished = true
            });
            UpdateTray("CutFlow Export • complete");
        }
        catch (Exception ex)
        {
            exitCode = 2;
            UpdateTray("CutFlow Export • failed");
            if (job is not null)
            {
                try
                {
                    await WriteResultAsync(job, new RenderWorkerResult
                    {
                        Success = false,
                        Error = Friendly(ex),
                        DestinationPath = job.DestinationPath
                    }).ConfigureAwait(false);
                    WriteProgress(job, new RenderWorkerProgress
                    {
                        Progress = 0,
                        Stage = Friendly(ex),
                        StartedUtc = DateTime.UtcNow,
                        UpdatedUtc = DateTime.UtcNow,
                        WorkerProcessId = Environment.ProcessId,
                        IsFinished = true,
                        HasError = true
                    });
                }
                catch { }
            }
        }
        finally
        {
            await Task.Delay(1200).ConfigureAwait(false);
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Dispose();
                Application.Current.Shutdown(exitCode);
            }));
        }
    }

    private void UpdateTray(string text)
    {
        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon is null) return;
            try { _trayIcon.Text = text.Length > 63 ? text[..63] : text; } catch { }
        }));
    }

    private static async Task<RenderWorkerJob> ReadJobAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<RenderWorkerJob>(stream).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CutFlow export job file was empty.");
    }

    private static void WriteProgress(RenderWorkerJob job, RenderWorkerProgress progress)
    {
        if (string.IsNullOrWhiteSpace(job.ProgressPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.ProgressPath)!);
            var temp = job.ProgressPath + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(progress));
            File.Move(temp, job.ProgressPath, true);
        }
        catch { }
    }

    private static async Task WriteResultAsync(RenderWorkerJob job, RenderWorkerResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(job.ResultPath)!);
        var temp = job.ResultPath + $".{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        File.Move(temp, job.ResultPath, true);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }

    private static string Friendly(Exception ex)
    {
        var text = ex.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(text) ? ex.GetType().Name : (text.Length > 900 ? text[..900] + "…" : text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Icon?.Dispose();
                _trayIcon.Dispose();
            }
        }
        catch { }
        _trayIcon = null;
    }
}
