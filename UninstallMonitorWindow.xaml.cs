using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CutFlow;

public partial class UninstallMonitorWindow : Window
{
    private readonly string _uninstallerPath;
    private readonly bool _deleteUserData;
    private readonly int _originalProcessId;
    private System.Windows.Forms.NotifyIcon? _tray;
    private readonly Stopwatch _elapsed = new();

    public UninstallMonitorWindow(string uninstallerPath, bool deleteUserData, int originalProcessId)
    {
        _uninstallerPath = uninstallerPath;
        _deleteUserData = deleteUserData;
        _originalProcessId = originalProcessId;
        InitializeComponent();
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => DisposeTray();
        InitializeTray();
    }

    private void InitializeTray()
    {
        try
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty) ?? System.Drawing.SystemIcons.Application;
            _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Visible = true, Text = "CutFlow is uninstalling" };
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(() => { Show(); Activate(); }));
        }
        catch { }
    }

    private async Task RunAsync()
    {
        try
        {
            StageText.Text = "Waiting for CutFlow to close safely…";
            Progress.Value = 5;
            ProgressText.Text = "5%";
            await WaitForOriginalProcessAsync();

            if (!File.Exists(_uninstallerPath)) throw new FileNotFoundException("The CutFlow Setup uninstaller could not be found.", _uninstallerPath);
            StageText.Text = "Removing installed application files…";
            _elapsed.Restart();
            using var process = Process.Start(new ProcessStartInfo(_uninstallerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(_uninstallerPath) ?? Environment.CurrentDirectory
            }) ?? throw new InvalidOperationException("Could not launch the CutFlow Setup uninstaller.");

            while (!process.HasExited)
            {
                var seconds = _elapsed.Elapsed.TotalSeconds;
                var percent = Math.Min(88, 12 + (int)(76 * (1 - Math.Exp(-seconds / 7.5))));
                Progress.Value = percent;
                ProgressText.Text = $"{percent}%";
                var estimate = Math.Max(1, (int)Math.Round(Math.Max(0, 10 - seconds)));
                EtaText.Text = percent < 80 ? $"About {estimate}s remaining" : "Finishing…";
                await Task.Delay(150);
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"The CutFlow Setup uninstaller returned exit code {process.ExitCode}.");

            Progress.Value = 92;
            ProgressText.Text = "92%";
            if (_deleteUserData)
            {
                StageText.Text = "Removing CutFlow projects, settings and local data…";
                EtaText.Text = "Cleaning up…";
                await DeleteUserDataAsync();
            }

            Progress.Value = 100;
            ProgressText.Text = "100%";
            StageText.Text = "CutFlow has been uninstalled.";
            EtaText.Text = "Complete";
            if (_tray is not null) _tray.Text = "CutFlow uninstall complete";
            ScheduleSelfCleanup();
            await Task.Delay(1200);
            Close();
        }
        catch (Exception ex)
        {
            StageText.Text = "Uninstall could not finish.";
            EtaText.Text = "Failed";
            MessageBox.Show(ex.GetBaseException().Message, "CutFlow uninstall", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task WaitForOriginalProcessAsync()
    {
        if (_originalProcessId <= 0) return;
        try
        {
            using var original = Process.GetProcessById(_originalProcessId);
            var started = DateTime.UtcNow;
            while (!original.HasExited && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
                await Task.Delay(100);
        }
        catch { }
    }

    private static Task DeleteUserDataAsync() => Task.Run(() =>
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow");
        if (!Directory.Exists(root)) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { Directory.Delete(root, true); return; }
            catch when (attempt < 3) { Thread.Sleep(250); }
        }
    });

    private void ScheduleSelfCleanup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var cmd = Path.Combine(Path.GetTempPath(), $"cutflow-uninstall-cleanup-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(cmd, $"@echo off\r\nping 127.0.0.1 -n 3 >nul\r\ndel /f /q \\\"{exe}\\\" >nul 2>&1\r\ndel /f /q \\\"%~f0\\\" >nul 2>&1\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \\\"\\\" /min \\\"{cmd}\\\"") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
    }

    private void DisposeTray()
    {
        if (_tray is null) return;
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }
}
