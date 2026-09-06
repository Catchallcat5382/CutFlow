using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CutFlow.Models;

namespace CutFlow;

/// <summary>
/// Lightweight progress notice only. It never owns FFmpeg and never performs media work.
/// It polls a tiny progress JSON file written by the hidden CutFlow worker process.
/// </summary>
public partial class RenderMonitorWindow : Window
{
    private readonly string _jobPath;
    private RenderWorkerJob? _job;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _followTimer;
    private bool _polling;
    private bool _finished;
    private DateTime _startedUtc;
    private double _lastProgress;
    private double _workerEtaSeconds = -1;
    private string _completedDestinationPath = string.Empty;

    public RenderMonitorWindow(string jobPath)
    {
        _jobPath = jobPath;
        InitializeComponent();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _followTimer.Tick += (_, _) => FollowParentWindow();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, e) =>
        {
            if (!_finished)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private async Task StartAsync()
    {
        try
        {
            _job = await ReadJsonAsync<RenderWorkerJob>(_jobPath);
            ProjectText.Text = string.IsNullOrWhiteSpace(_job.ProjectName) ? "CutFlow project" : _job.ProjectName;
            var isExport = _job.JobKind.Equals("Export", StringComparison.OrdinalIgnoreCase);
            Height = isExport ? 342 : 286;
            TitleText.Text = isExport ? "Exporting media" : "Applying cuts";
            StageText.Text = isExport
                ? $"Preparing {_job.ExportOptions?.Format ?? "media"} export…"
                : $"Sending {_job.CutRanges.Count} marked regions to the background processor…";
            WarningText.Text = isExport
                ? "Export is running in a separate CutFlow worker. You can keep using the editor; if the editor is closed, the export worker can continue in the system tray."
                : "DO NOT CLOSE THE EDITOR while cuts are being applied. If it says Not Responding, leave it alone. This monitor and the hidden worker are separate. If the editor is force-closed, the worker cancels and the saved project stays unchanged.";
            FootnoteText.Text = isExport
                ? "The export worker writes directly to your chosen destination. Your CutFlow project and current working media are not modified by the export."
                : "The original imported media is not overwritten. CutFlow only switches to the shortened working file after this processor verifies its duration.";
            _startedUtc = DateTime.UtcNow;
            FollowParentWindow();
            _followTimer.Start();
            _pollTimer.Start();
            await PollAsync();
        }
        catch (Exception ex)
        {
            ShowFailure(Friendly(ex));
        }
    }

    private async Task PollAsync()
    {
        if (_polling || _job is null || _finished) return;
        _polling = true;
        try
        {
            RenderWorkerProgress? progress = null;
            if (!string.IsNullOrWhiteSpace(_job.ProgressPath) && File.Exists(_job.ProgressPath))
            {
                try { progress = await ReadJsonAsync<RenderWorkerProgress>(_job.ProgressPath); } catch { }
            }

            if (progress is not null)
            {
                if (progress.StartedUtc != default) _startedUtc = progress.StartedUtc;
                _lastProgress = Math.Clamp(progress.Progress, 0, 1);
                _workerEtaSeconds = progress.EstimatedSecondsRemaining;
                UpdateProgress(_lastProgress, progress.Stage, progress.MergedRangeCount > 0 ? progress.MergedRangeCount : progress.InputRangeCount);
                if (progress.HasError)
                {
                    ShowFailure(progress.Stage);
                    return;
                }
            }

            if (File.Exists(_job.ResultPath))
            {
                RenderWorkerResult? result = null;
                try { result = await ReadJsonAsync<RenderWorkerResult>(_job.ResultPath); } catch { }
                if (result is not null)
                {
                    if (!result.Success)
                    {
                        ShowFailure(string.IsNullOrWhiteSpace(result.Error) ? "The background cut failed." : result.Error);
                        return;
                    }

                    var isExport = _job.JobKind.Equals("Export", StringComparison.OrdinalIgnoreCase);
                    if (isExport)
                    {
                        _completedDestinationPath = result.DestinationPath ?? string.Empty;
                        UpdateProgress(1, "Export finished successfully", 0);
                        TitleText.Text = "Export finished";
                        WarningText.Text = string.IsNullOrWhiteSpace(_completedDestinationPath)
                            ? "Your export finished successfully."
                            : $"Saved as {Path.GetFileName(_completedDestinationPath)}";
                        FootnoteText.Text = "Press Open in Folder to show the exported file, or OK to close this window.";
                        PercentText.Text = "100%";
                        TimeText.Text = $"Finished in {FormatDuration((DateTime.UtcNow - _startedUtc).TotalSeconds)}";
                        _finished = true;
                        _pollTimer.Stop();
                        _followTimer.Stop();
                        CompletionActions.Visibility = Visibility.Visible;
                        OpenFolderButton.Visibility = string.IsNullOrWhiteSpace(_completedDestinationPath) ? Visibility.Collapsed : Visibility.Visible;
                        if (!IsVisible) Show();
                        Activate();
                        OkButton.Focus();
                        return;
                    }

                    UpdateProgress(1, $"Finished • {result.MergedRangeCount} regions included • {result.RemovedSeconds:0.00}s removed", result.MergedRangeCount);
                    WarningText.Text = "The shortened working file was verified. Keep the editor open while CutFlow commits the verified result to the project. If it pauses briefly, do not close it.";
                    PercentText.Text = "100%";
                    if (!await WaitForEditorAcknowledgeAsync()) return;
                    _finished = true;
                    _pollTimer.Stop();
                    _followTimer.Stop();
                    await Task.Delay(650);
                    Application.Current.Shutdown(0);
                }
            }
        }
        finally { _polling = false; }
    }

    private void UpdateProgress(double value, string stage, int rangeCount)
    {
        value = Math.Clamp(value, 0, 1);
        StageText.Text = string.IsNullOrWhiteSpace(stage) ? "Processing media…" : stage;
        PercentText.Text = $"{value * 100:0}%";
        if (rangeCount > 0)
            ProjectText.Text = $"{(_job?.ProjectName ?? "CutFlow project")}  •  {rangeCount} region{(rangeCount == 1 ? "" : "s")}";

        var target = Math.Max(0, ProgressTrack.ActualWidth - 2) * value;
        var animation = new DoubleAnimation(ProgressFill.ActualWidth, target, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressFill.BeginAnimation(WidthProperty, animation, HandoffBehavior.SnapshotAndReplace);

        var elapsed = DateTime.UtcNow - _startedUtc;
        if (value >= 0.995)
        {
            TimeText.Text = $"Elapsed {FormatDuration(elapsed.TotalSeconds)}  •  Finishing up…";
        }
        else if (_workerEtaSeconds >= 0 && !double.IsNaN(_workerEtaSeconds) && !double.IsInfinity(_workerEtaSeconds))
        {
            TimeText.Text = $"Elapsed {FormatDuration(elapsed.TotalSeconds)}  •  About {FormatDuration(_workerEtaSeconds)} remaining";
        }
        else if (elapsed.TotalSeconds >= 1.0 && value >= 0.0015)
        {
            // Fallback for old cut jobs that do not publish a worker ETA. The threshold is tiny so
            // long exports no longer need to reach 2.5% before the user sees an estimate.
            var remaining = elapsed.TotalSeconds * (1.0 - value) / Math.Max(0.0001, value);
            TimeText.Text = $"Elapsed {FormatDuration(elapsed.TotalSeconds)}  •  About {FormatDuration(remaining)} remaining";
        }
        else TimeText.Text = $"Elapsed {FormatDuration(elapsed.TotalSeconds)}  •  Estimating…";
    }

    private void ShowFailure(string message)
    {
        _finished = true;
        _pollTimer.Stop();
        _followTimer.Stop();
        TitleText.Text = _job?.JobKind.Equals("Export", StringComparison.OrdinalIgnoreCase) == true ? "Export could not finish" : "Cut could not finish";
        StageText.Text = message;
        WarningText.Text = _job?.JobKind.Equals("Export", StringComparison.OrdinalIgnoreCase) == true
            ? "The editor project was not changed. Return to CutFlow and try the export again."
            : "The original imported media was not overwritten. Return to the editor and try again.";
        PercentText.Text = "Error";
        ProgressFill.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(190, 76, 72));
        CompletionActions.Visibility = Visibility.Visible;
        OpenFolderButton.Visibility = Visibility.Collapsed;
        if (!IsVisible) Show();
        Activate();
        OkButton.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _finished = true;
        _pollTimer.Stop();
        _followTimer.Stop();
        Application.Current.Shutdown(0);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_completedDestinationPath)) return;
        try
        {
            var fullPath = Path.GetFullPath(_completedDestinationPath);
            if (File.Exists(fullPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    Arguments = $"/select,\"{fullPath.Replace("\"", string.Empty)}\""
                });
            }
            else
            {
                var folder = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private async Task<bool> WaitForEditorAcknowledgeAsync()
    {
        if (_job is null) return false;
        var started = Stopwatch.StartNew();
        while (true)
        {
            if (File.Exists(_job.AcknowledgePath))
            {
                try
                {
                    var ack = (await File.ReadAllTextAsync(_job.AcknowledgePath)).Trim();
                    if (ack.Equals("editor-applied", StringComparison.OrdinalIgnoreCase)) return true;
                    if (ack.Equals("editor-failed", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowFailure("The editor did not commit the render. The temporary output was discarded and the saved project remains unchanged.");
                        return false;
                    }
                }
                catch (IOException) { }
            }
            if (_job.ParentProcessId > 0)
            {
                try
                {
                    using var parent = Process.GetProcessById(_job.ParentProcessId);
                    if (parent.HasExited)
                    {
                        ShowFailure("The editor was closed before the cut could be committed. The project remains unchanged.");
                        return false;
                    }
                }
                catch
                {
                    ShowFailure("The editor was closed before the cut could be committed. The project remains unchanged.");
                    return false;
                }
            }
            if (started.Elapsed > TimeSpan.FromSeconds(18))
            {
                StageText.Text = "Media processing is done. Waiting for the editor to safely commit the verified result…";
                TimeText.Text = "Do not close CutFlow yet.";
            }
            await Task.Delay(160);
        }
    }

    private void FollowParentWindow()
    {
        if (_job is null || _job.ParentWindowHandle == 0 || !IsLoaded) return;
        if (!GetWindowRect(new IntPtr(_job.ParentWindowHandle), out var rect)) return;
        var width = ActualWidth > 10 ? ActualWidth : Width;
        var height = ActualHeight > 10 ? ActualHeight : Height;
        Left = rect.Left + Math.Max(12, (rect.Right - rect.Left - width) / 2.0);
        Top = rect.Top + Math.Max(18, (rect.Bottom - rect.Top - height) / 2.0);
    }

    private static async Task<T> ReadJsonAsync<T>(string path) where T : class
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? throw new InvalidOperationException("CutFlow received an empty processor file.");
    }

    private static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private static string Friendly(Exception ex)
    {
        var text = ex.GetBaseException().Message;
        return string.IsNullOrWhiteSpace(text) ? ex.GetType().Name : (text.Length > 700 ? text[..700] + "…" : text);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
