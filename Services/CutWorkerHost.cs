using System.Diagnostics;
using System.Text.Json;
using CutFlow.Models;

namespace CutFlow.Services;

/// <summary>
/// Owns the heavy Cut/Cut All job in a process that has no editor window.
/// The FFmpeg worker and the lightweight progress monitor are intentionally
/// separate processes so a busy encoder can never freeze the notice UI.
/// </summary>
public sealed class CutWorkerHost : IDisposable
{
    private readonly string _jobPath;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _disposed;

    public CutWorkerHost(string jobPath)
    {
        _jobPath = jobPath;
    }

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
            var show = new System.Windows.Forms.ToolStripMenuItem("Show processing progress");
            show.Click += (_, _) => LaunchMonitor();
            menu.Items.Add(show);
            var open = new System.Windows.Forms.ToolStripMenuItem("Open processing folder");
            open.Click += (_, _) =>
            {
                try
                {
                    var folder = Path.GetDirectoryName(_jobPath);
                    if (!string.IsNullOrWhiteSpace(folder)) Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                }
                catch { }
            };
            menu.Items.Add(open);

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "CutFlow Processor • starting",
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
            psi.ArgumentList.Add("--cut-monitor");
            psi.ArgumentList.Add(_jobPath);
            Process.Start(psi);
        }
        catch { }
    }

    private async Task RunCoreAsync()
    {
        var exitCode = 0;
        RenderWorkerJob? job = null;
        using var cts = new CancellationTokenSource();
        Task? editorWatch = null;
        try
        {
            job = await ReadJobAsync(_jobPath).ConfigureAwait(false);
            UpdateTray($"CutFlow Processor • {job.CutRanges.Count} regions");

            // The editor/project file is the transaction owner. If the editor is actually closed
            // or the user explicitly requests cancellation, stop FFmpeg and discard the temporary
            // working output. A frozen/not-responding editor is still alive, so processing continues.
            editorWatch = WatchForEditorExitAsync(job, cts);
            await CutWorkerEngine.RunAsync(job, cts.Token).ConfigureAwait(false);

            // The rendered file is only a transaction candidate until the editor atomically saves
            // the updated project and acknowledges it. If the editor closes/fails before that,
            // discard the candidate so reopening the project always returns to the pre-cut state.
            var accepted = await WaitForEditorAcknowledgementAsync(job, cts.Token).ConfigureAwait(false);
            if (!accepted)
            {
                try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
                RestoreProjectBackupBestEffort(job);
                await CutWorkerEngine.WriteFailureResultAsync(job,
                    new InvalidOperationException("The editor did not commit this cut. The temporary render was discarded and the saved project was left unchanged.")).ConfigureAwait(false);
                exitCode = 3;
                UpdateTray("CutFlow Processor • discarded safely");
            }
            else
            {
                DeleteProjectBackupBestEffort(job);
                UpdateTray("CutFlow Processor • finished");
            }
        }
        catch (OperationCanceledException)
        {
            exitCode = 3;
            UpdateTray("CutFlow Processor • cancelled safely");
            if (job is not null)
            {
                try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
                RestoreProjectBackupBestEffort(job);
                try
                {
                    await CutWorkerEngine.WriteFailureResultAsync(job,
                        new InvalidOperationException("The cut was cancelled because the editor was closed or cancellation was requested. The saved project was left unchanged.")).ConfigureAwait(false);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            exitCode = 2;
            UpdateTray("CutFlow Processor • failed");
            try
            {
                job ??= await ReadJobAsync(_jobPath).ConfigureAwait(false);
                try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
                RestoreProjectBackupBestEffort(job);
                await CutWorkerEngine.WriteFailureResultAsync(job, ex).ConfigureAwait(false);
            }
            catch { }
        }
        finally
        {
            try { if (!cts.IsCancellationRequested) cts.Cancel(); } catch { }
            if (editorWatch is not null)
            {
                try { await editorWatch.ConfigureAwait(false); } catch { }
            }
            // Leave the tray result visible briefly so the user can see that the background
            // processor completed, then shut down only this helper process.
            await Task.Delay(900).ConfigureAwait(false);
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Dispose();
                Application.Current.Shutdown(exitCode);
            }));
        }
    }

    private static void RestoreProjectBackupBestEffort(RenderWorkerJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ProjectPath) || string.IsNullOrWhiteSpace(job.ProjectBackupPath)) return;
        try
        {
            if (!File.Exists(job.ProjectBackupPath)) return;
            var directory = Path.GetDirectoryName(job.ProjectPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temp = job.ProjectPath + $".rollback.{Environment.ProcessId}.tmp";
            File.Copy(job.ProjectBackupPath, temp, true);
            File.Move(temp, job.ProjectPath, true);
        }
        catch { }
    }

    private static void DeleteProjectBackupBestEffort(RenderWorkerJob job)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(job.ProjectBackupPath) && File.Exists(job.ProjectBackupPath))
                File.Delete(job.ProjectBackupPath);
        }
        catch { }
    }

    private static async Task<bool> WaitForEditorAcknowledgementAsync(RenderWorkerJob job, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(job.AcknowledgePath) && File.Exists(job.AcknowledgePath))
            {
                try
                {
                    var text = (await File.ReadAllTextAsync(job.AcknowledgePath, cancellationToken).ConfigureAwait(false)).Trim();
                    return text.Equals("editor-applied", StringComparison.OrdinalIgnoreCase);
                }
                catch (IOException) { }
            }
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WatchForEditorExitAsync(RenderWorkerJob job, CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            if (!string.IsNullOrWhiteSpace(job.CancelPath) && File.Exists(job.CancelPath))
            {
                cts.Cancel();
                return;
            }

            if (job.ParentProcessId > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(job.ParentProcessId);
                    if (parent.HasExited)
                    {
                        cts.Cancel();
                        return;
                    }
                }
                catch
                {
                    cts.Cancel();
                    return;
                }
            }

            try { await Task.Delay(250, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<RenderWorkerJob>(stream).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The CutFlow processor job file was empty.");
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
                _trayIcon.Dispose();
            }
        }
        catch { }
        _trayIcon = null;
    }
}

internal static class CutWorkerEngine
{
    public static async Task RunAsync(RenderWorkerJob job, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;

        // Never trust a cached project duration for destructive cut verification. The project may
        // have been upgraded from an older CutFlow build or may currently point at a shortened
        // working revision. Probe the exact file this worker is about to cut and use THAT as the
        // timing source of truth for clamping ranges, progress, and verification.
        var ffmpeg = new FFmpegService();
        await ffmpeg.EnsureAvailableAsync(null, cancellationToken).ConfigureAwait(false);
        var sourceInfo = await ffmpeg.ProbeAsync(job.SourcePath, cancellationToken).ConfigureAwait(false);
        if (sourceInfo.DurationSeconds <= 0) throw new InvalidOperationException("The cut worker could not determine the current source duration.");

        var rawPlanHash = BuildCutPlanHash(job.CutRanges);
        if (string.IsNullOrWhiteSpace(job.InputPlanHash) || !string.Equals(rawPlanHash, job.InputPlanHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The exact red-region snapshot changed before the cut worker started. Nothing was cut.");

        var normalizedRanges = NormalizeRangesToSourceTimeline(job.CutRanges, sourceInfo);
        var merged = MergeRanges(normalizedRanges, sourceInfo.DurationSeconds);
        if (merged.Count == 0) throw new InvalidOperationException("The cut job did not contain any valid regions.");

        var removed = merged.Sum(x => x.end - x.start);
        var expectedDuration = Math.Max(0, sourceInfo.DurationSeconds - removed);
        WriteProgress(job, new RenderWorkerProgress
        {
            Progress = 0.005,
            Stage = $"Preparing {merged.Count} cut regions…",
            StartedUtc = started,
            UpdatedUtc = DateTime.UtcNow,
            WorkerProcessId = Environment.ProcessId,
            InputRangeCount = job.CutRanges.Count,
            MergedRangeCount = merged.Count
        });

        // FFmpeg availability was established before probing the exact source file above.

        var progress = new InlineProgress<double>(p =>
        {
            var mapped = 0.02 + Math.Clamp(p, 0, 1) * 0.90;
            WriteProgress(job, new RenderWorkerProgress
            {
                Progress = mapped,
                Stage = $"Removing all {merged.Count} regions • {removed:0.00}s total…",
                StartedUtc = started,
                UpdatedUtc = DateTime.UtcNow,
                WorkerProcessId = Environment.ProcessId,
                InputRangeCount = job.CutRanges.Count,
                MergedRangeCount = merged.Count
            });
        });

        // Dedicated working-cut renderer: every requested cut is converted into the exact ranges
        // that must be kept, and those kept ranges are trimmed/concatenated in one FFmpeg pass.
        await ffmpeg.CutExactRangesAsync(
            job.SourcePath,
            job.DestinationPath,
            sourceInfo,
            job.Silence,
            merged,
            progress,
            cancellationToken).ConfigureAwait(false);

        WriteProgress(job, new RenderWorkerProgress
        {
            Progress = 0.94,
            Stage = "Verifying the shortened media…",
            StartedUtc = started,
            UpdatedUtc = DateTime.UtcNow,
            WorkerProcessId = Environment.ProcessId,
            InputRangeCount = job.CutRanges.Count,
            MergedRangeCount = merged.Count
        });

        var info = await ffmpeg.ProbeAsync(job.DestinationPath, cancellationToken).ConfigureAwait(false);
        if (Path.GetFullPath(job.SourcePath).Equals(Path.GetFullPath(job.DestinationPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The cut worker refused to overwrite its own input media.");
        var frameDuration = sourceInfo.HasVideo && sourceInfo.FrameRate > 1 ? 1.0 / sourceInfo.FrameRate : 0.0;
        var frameTolerance = frameDuration > 0 ? 4.0 * frameDuration : 0.09;
        // Each keep/cut boundary can move by a fraction of a frame and AAC containers can add a
        // tiny encoder delay. Allow a bounded cumulative timing tolerance while still rejecting
        // genuinely partial renders (for example, only 2 of 64 requested cuts being applied).
        var cumulativeBoundaryTolerance = frameDuration > 0
            ? Math.Min(0.75, 0.10 + merged.Count * frameDuration * 0.25)
            : 0.25;
        var tolerance = Math.Max(0.20, Math.Max(frameTolerance, cumulativeBoundaryTolerance));
        var durationError = Math.Abs(info.DurationSeconds - expectedDuration);
        if (info.DurationSeconds <= 0 || durationError > tolerance)
        {
            try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
            throw new InvalidOperationException($"Cut verification failed. {merged.Count} ranges totaling {removed:0.000}s were requested; expected about {expectedDuration:0.000}s but the output is {info.DurationSeconds:0.000}s.");
        }
        if (removed > 0.05 && info.DurationSeconds >= sourceInfo.DurationSeconds - 0.02)
        {
            try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
            throw new InvalidOperationException("Cut verification failed because the output was not actually shorter than the source.");
        }

        // Video and audio are trimmed from the SAME keep-range list in CutExactRangesAsync. Prove
        // that they also came out aligned before the editor is allowed to accept this working file.
        // This catches a broken mux/filter result instead of letting A/V slowly drift after cuts.
        var streamTolerance = Math.Max(0.12, frameDuration > 0 ? frameDuration * 4.0 : 0.12);
        if (info.HasVideo && info.HasAudio)
        {
            var videoDuration = info.VideoDurationSeconds > 0 ? info.VideoDurationSeconds : info.DurationSeconds;
            var audioDuration = info.AudioDurationSeconds > 0 ? info.AudioDurationSeconds : info.DurationSeconds;
            if (Math.Abs(videoDuration - audioDuration) > streamTolerance)
            {
                try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
                throw new InvalidOperationException($"Cut verification failed because audio/video are not aligned (video {videoDuration:0.000}s, audio {audioDuration:0.000}s). Nothing was applied.");
            }
            if (Math.Abs(videoDuration - expectedDuration) > tolerance + streamTolerance ||
                Math.Abs(audioDuration - expectedDuration) > tolerance + streamTolerance)
            {
                try { if (File.Exists(job.DestinationPath)) File.Delete(job.DestinationPath); } catch { }
                throw new InvalidOperationException("Cut verification failed because one media stream did not contain the complete cut plan. Nothing was applied.");
            }
        }

        var appliedRanges = merged.Select(r => new RenderCutRange { StartSeconds = r.start, EndSeconds = r.end }).ToList();
        await WriteResultAsync(job, new RenderWorkerResult
        {
            Success = true,
            DestinationPath = job.DestinationPath,
            Media = info,
            SourceDurationSeconds = sourceInfo.DurationSeconds,
            RemovedSeconds = removed,
            ExpectedDurationSeconds = expectedDuration,
            ActualDurationSeconds = info.DurationSeconds,
            InputRangeCount = job.CutRanges.Count,
            MergedRangeCount = merged.Count,
            InputPlanHash = rawPlanHash,
            AppliedPlanHash = BuildCutPlanHash(appliedRanges),
            AppliedRanges = appliedRanges
        }).ConfigureAwait(false);

        WriteProgress(job, new RenderWorkerProgress
        {
            Progress = 1.0,
            Stage = $"Done • all {merged.Count} regions were included in the render plan.",
            StartedUtc = started,
            UpdatedUtc = DateTime.UtcNow,
            WorkerProcessId = Environment.ProcessId,
            InputRangeCount = job.CutRanges.Count,
            MergedRangeCount = merged.Count,
            IsFinished = true
        });
    }

    public static async Task WriteFailureResultAsync(RenderWorkerJob job, Exception ex)
    {
        var message = Friendly(ex);
        await WriteResultAsync(job, new RenderWorkerResult
        {
            Success = false,
            Error = message,
            DestinationPath = job.DestinationPath,
            SourceDurationSeconds = job.Media.DurationSeconds,
            InputRangeCount = job.CutRanges.Count,
            InputPlanHash = job.InputPlanHash
        }).ConfigureAwait(false);
        WriteProgress(job, new RenderWorkerProgress
        {
            Progress = 0,
            Stage = message,
            StartedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
            WorkerProcessId = Environment.ProcessId,
            InputRangeCount = job.CutRanges.Count,
            IsFinished = true,
            HasError = true
        });
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

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }

    private static async Task WriteResultAsync(RenderWorkerJob job, RenderWorkerResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(job.ResultPath)!);
        var temp = job.ResultPath + $".{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        File.Move(temp, job.ResultPath, true);
    }

    private static List<RenderCutRange> NormalizeRangesToSourceTimeline(IEnumerable<RenderCutRange> ranges, MediaInfo media)
    {
        // The editor timeline and this source file MUST agree. Never silently clamp a visibly
        // marked region by a large amount: that could turn "cut this red box" into "cut a
        // different interval" while still passing duration verification.
        var duration = Math.Max(0, media.DurationSeconds);
        var result = new List<RenderCutRange>();
        foreach (var range in ranges.OrderBy(r => r.StartSeconds).ThenBy(r => r.EndSeconds))
        {
            var rawStart = Math.Min(range.StartSeconds, range.EndSeconds);
            var rawEnd = Math.Max(range.StartSeconds, range.EndSeconds);
            if (rawStart < -0.020 || rawEnd > duration + 0.020)
                throw new InvalidOperationException($"A marked region ({rawStart:0.###}-{rawEnd:0.###}) does not fit the current {duration:0.###}s working file. Rescan the current media before cutting.");
            var start = Math.Clamp(rawStart, 0, duration);
            var end = Math.Clamp(rawEnd, 0, duration);
            if (end - start < 0.003) continue;
            result.Add(new RenderCutRange { StartSeconds = start, EndSeconds = end });
        }
        return result;
    }

    private static string BuildCutPlanHash(IEnumerable<RenderCutRange> ranges)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var payload = string.Join("|", ranges
            .OrderBy(r => r.StartSeconds)
            .ThenBy(r => r.EndSeconds)
            .Select(r => $"{r.StartSeconds.ToString("0.######", inv)}:{r.EndSeconds.ToString("0.######", inv)}"));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    private static List<(double start, double end)> MergeRanges(IEnumerable<RenderCutRange> ranges, double duration)
    {
        var sorted = ranges
            .Select(r => (start: Math.Clamp(Math.Min(r.StartSeconds, r.EndSeconds), 0, duration), end: Math.Clamp(Math.Max(r.StartSeconds, r.EndSeconds), 0, duration)))
            .Where(r => r.end - r.start >= 0.003)
            .OrderBy(r => r.start)
            .ToList();
        var merged = new List<(double start, double end)>();
        foreach (var r in sorted)
        {
            if (merged.Count == 0 || r.start > merged[^1].end + 0.0005) merged.Add(r);
            else merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, r.end));
        }
        return merged;
    }

    private static string Friendly(Exception ex)
    {
        var text = ex.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(text) ? ex.GetType().Name : (text.Length > 900 ? text[..900] + "…" : text);
    }
}
