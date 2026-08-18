using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace CutFlow.Services;

public static class UninstallService
{
    public static string? FindUninstaller()
    {
        try
        {
            var root = AppContext.BaseDirectory;
            return Directory.EnumerateFiles(root, "unins*.exe", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    public static void BeginUninstall(string uninstallerPath, bool deleteUserData)
    {
        if (!File.Exists(uninstallerPath)) throw new FileNotFoundException("CutFlow Setup uninstaller was not found.", uninstallerPath);
        var currentExe = Environment.ProcessPath ?? throw new InvalidOperationException("CutFlow could not locate its executable.");
        var tempRoot = Path.Combine(Path.GetTempPath(), "CutFlow-Uninstall");
        Directory.CreateDirectory(tempRoot);
        var tempExe = Path.Combine(tempRoot, $"CutFlow-UninstallMonitor-{Guid.NewGuid():N}.exe");
        File.Copy(currentExe, tempExe, true);

        var args = $"--uninstall-monitor \"{uninstallerPath}\" {(deleteUserData ? "1" : "0")} {Environment.ProcessId}";
        Process.Start(new ProcessStartInfo(tempExe, args)
        {
            UseShellExecute = false,
            WorkingDirectory = tempRoot
        });

        Application.Current.Shutdown(0);
    }
}
