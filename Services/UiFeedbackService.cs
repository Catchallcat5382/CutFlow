using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;

namespace CutFlow.Services;

/// <summary>
/// Reliable UI sound queue independent of MediaElement/video playback. WinMM can only play
/// one PlaySound clip at a time; older builds fired sounds asynchronously so a second effect
/// interrupted the first and made sounds seem random. v3.11 serializes short effects on a
/// background worker and coalesces spammy generic clicks.
/// </summary>
public static class UiFeedbackService
{
    [Flags]
    private enum PlaySoundFlags : uint
    {
        SND_SYNC = 0x0000,
        SND_NODEFAULT = 0x0002,
        SND_FILENAME = 0x00020000
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, PlaySoundFlags fdwSound);

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string> Paths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentQueue<string> Queue = new();
    private static readonly SemaphoreSlim QueueSignal = new(0);
    private static bool _initialized;
    private static bool _workerStarted;
    private static long _lastClickQueuedMs;
    private static long _lastActionQueuedMs;
    private static int _pendingClickGeneration;

    private static readonly string[] KnownSounds =
    {
        "click", "navigate", "confirm", "cut", "undo", "scan", "success", "complete", "error"
    };

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) { EnsureWorker(); return; }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "Sounds");
            Directory.CreateDirectory(root);

            foreach (var name in KnownSounds)
            {
                try
                {
                    var path = Path.Combine(root, name + ".wav");
                    var uri = new Uri($"pack://application:,,,/Assets/Sounds/{name}.wav", UriKind.Absolute);
                    var resource = Application.GetResourceStream(uri);
                    if (resource?.Stream is null) continue;
                    using var copy = new MemoryStream();
                    resource.Stream.CopyTo(copy);
                    var bytes = copy.ToArray();
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
                        File.WriteAllBytes(path, bytes);
                    Paths[name] = path;
                }
                catch { }
            }

            _initialized = Paths.Count > 0;
            EnsureWorker();
        }
    }

    public static void Play(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (!_initialized) Initialize();
            if (!_initialized) return;

            var now = Environment.TickCount64;
            if (name.Equals("click", StringComparison.OrdinalIgnoreCase))
            {
                // Routed Button.Click fires for every button, including buttons that immediately
                // request a more meaningful sound. Delay the generic click very slightly; if an
                // action sound arrives, it cancels this pending generic sound instead of the two
                // competing/interleaving unpredictably.
                if (now - Interlocked.Read(ref _lastClickQueuedMs) < 55) return;
                Interlocked.Exchange(ref _lastClickQueuedMs, now);
                var generation = Interlocked.Increment(ref _pendingClickGeneration);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(38).ConfigureAwait(false);
                        if (generation != Volatile.Read(ref _pendingClickGeneration)) return;
                        if (Environment.TickCount64 - Interlocked.Read(ref _lastActionQueuedMs) < 70) return;
                        Queue.Enqueue("click");
                        QueueSignal.Release();
                    }
                    catch { }
                });
                return;
            }

            // Specific action sounds always win and are never intentionally dropped.
            Interlocked.Exchange(ref _lastActionQueuedMs, now);
            Interlocked.Increment(ref _pendingClickGeneration);
            Queue.Enqueue(name);
            QueueSignal.Release();
        }
        catch { }
    }

    private static void EnsureWorker()
    {
        if (_workerStarted) return;
        _workerStarted = true;
        _ = Task.Run(ProcessQueueAsync);
    }

    private static async Task ProcessQueueAsync()
    {
        while (true)
        {
            try
            {
                await QueueSignal.WaitAsync().ConfigureAwait(false);
                if (!Queue.TryDequeue(out var name)) continue;
                string? path;
                lock (Sync) Paths.TryGetValue(name, out path);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                // Synchronous only on this background worker. This guarantees one short effect
                // finishes instead of being silently cancelled by the next PlaySound call.
                PlaySound(path, IntPtr.Zero, PlaySoundFlags.SND_FILENAME | PlaySoundFlags.SND_NODEFAULT | PlaySoundFlags.SND_SYNC);
            }
            catch { }
        }
    }

    public static void Stop()
    {
        try
        {
            while (Queue.TryDequeue(out _)) { }
            while (QueueSignal.CurrentCount > 0) QueueSignal.Wait(0);
            PlaySound(null, IntPtr.Zero, 0);
        }
        catch { }
    }
}
