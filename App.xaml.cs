using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CutFlow.Models;
using CutFlow.Services;

namespace CutFlow;

public partial class App : Application
{
    private static readonly object DiagnosticLock = new();
    private static readonly Queue<string> RecentActions = new();

    public static void RecordAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action)) return;
        lock (DiagnosticLock)
        {
            RecentActions.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {action}");
            while (RecentActions.Count > 30) RecentActions.Dequeue();
        }
    }

    private static string ReadRecentActions()
    {
        lock (DiagnosticLock) return RecentActions.Count == 0 ? "(none recorded)" : string.Join(Environment.NewLine, RecentActions);
    }

    public SettingsService SettingsStore { get; } = new();
    public AppSettings Settings { get; private set; } = new();

    public App()
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            RecordAction("Application startup");
            Settings = SettingsStore.Load();
            ThemeService.Apply(Settings);

            // v3.14 deliberately separates heavy processing from the progress UI.
            // --cut-worker has NO window; it runs FFmpeg in the background and exposes a tray icon.
            // --cut-monitor only reads tiny JSON progress files, so it stays smooth even if the
            // worker/encoder is completely saturated.
            if (e.Args.Length >= 2 && e.Args[0].Equals("--cut-worker", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var host = new CutWorkerHost(e.Args[1]);
                host.Start();
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0].Equals("--cut-monitor", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                var monitor = new RenderMonitorWindow(e.Args[1]);
                MainWindow = monitor;
                monitor.Show();
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0].Equals("--export-worker", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var host = new ExportWorkerHost(e.Args[1]);
                host.Start();
                return;
            }
            if (e.Args.Length >= 2 && e.Args[0].Equals("--export-monitor", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                var monitor = new RenderMonitorWindow(e.Args[1]);
                MainWindow = monitor;
                monitor.Show();
                return;
            }
            if (e.Args.Length >= 4 && e.Args[0].Equals("--uninstall-monitor", StringComparison.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                var deleteUserData = e.Args[2].Equals("1", StringComparison.OrdinalIgnoreCase);
                _ = int.TryParse(e.Args[3], out var originalProcessId);
                var monitor = new UninstallMonitorWindow(e.Args[1], deleteUserData, originalProcessId);
                MainWindow = monitor;
                monitor.Show();
                return;
            }

            UiFeedbackService.Initialize();
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Button), System.Windows.Controls.Button.ClickEvent,
                new RoutedEventHandler((sender, args) =>
                {
                    if (Settings.UiSoundsEnabled) UiFeedbackService.Play("click");
                }));
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            var path = WriteCrashLog("Startup", ex);
            MessageBox.Show($"CutFlow could not start.\n\n{Friendly(ex)}\n\nA crash log was saved to:\n{path}", "CutFlow startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        Settings = SettingsService.Clone(settings);
        ThemeService.Apply(Settings);
        if (!Settings.UiSoundsEnabled) UiFeedbackService.Stop();
        await SettingsStore.SaveAsync(Settings);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = WriteCrashLog("Dispatcher", e.Exception);
        MessageBox.Show($"CutFlow hit an unexpected error.\n\n{Friendly(e.Exception)}\n\nCrash log:\n{path}", "CutFlow error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) WriteCrashLog("AppDomain", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("Task", e.Exception);
        e.SetObserved();
    }

    private static string WriteCrashLog(string kind, Exception ex)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "Logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var text = new StringBuilder()
                .AppendLine($"CutFlow crash: {kind}")
                .AppendLine($"Time: {DateTime.Now:O}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"64-bit process: {Environment.Is64BitProcess}")
                .AppendLine()
                .AppendLine("Recent UI actions:")
                .AppendLine(ReadRecentActions())
                .AppendLine()
                .AppendLine(ex.ToString())
                .ToString();
            File.WriteAllText(path, text);
            return path;
        }
        catch { return "(could not write crash log)"; }
    }

    private static string Friendly(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (string.IsNullOrWhiteSpace(message)) message = ex.GetType().Name;
        return message.Length > 700 ? message[..700] + "…" : message;
    }
}
