using System.IO;
using System.Linq;
using System.Net.Http;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using CutFlow.Models;

namespace CutFlow.Services;

public sealed class FFmpegService
{
    private const string WindowsBuildUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private readonly string _toolRoot;
    private bool? _nvencAvailable;

    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }

    public FFmpegService()
    {
        _toolRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutFlow", "Tools");
    }

    public async Task EnsureAvailableAsync(IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        if (IsValidToolPair(FfmpegPath, FfprobePath)) return;

        status?.Report("Checking media engine…");
        var candidates = new List<(string ffmpeg, string ffprobe)>();

        var appDir = AppContext.BaseDirectory;
        candidates.Add((Path.Combine(appDir, "tools", "ffmpeg", "ffmpeg.exe"), Path.Combine(appDir, "tools", "ffmpeg", "ffprobe.exe")));
        candidates.Add((Path.Combine(appDir, "tools", "ffmpeg", "bin", "ffmpeg.exe"), Path.Combine(appDir, "tools", "ffmpeg", "bin", "ffprobe.exe")));
        candidates.Add((Path.Combine(_toolRoot, "ffmpeg.exe"), Path.Combine(_toolRoot, "ffprobe.exe")));

        foreach (var (ffmpeg, ffprobe) in candidates)
        {
            if (!IsValidToolPair(ffmpeg, ffprobe)) continue;
            FfmpegPath = ffmpeg;
            FfprobePath = ffprobe;
            return;
        }

        var pathPair = FindOnPath();
        if (pathPair is not null)
        {
            FfmpegPath = pathPair.Value.ffmpeg;
            FfprobePath = pathPair.Value.ffprobe;
            return;
        }

        status?.Report("Installing the free local media engine (one-time setup)…");
        await DownloadWindowsBuildAsync(status, cancellationToken);

        var installedFfmpeg = Path.Combine(_toolRoot, "ffmpeg.exe");
        var installedFfprobe = Path.Combine(_toolRoot, "ffprobe.exe");
        if (!IsValidToolPair(installedFfmpeg, installedFfprobe))
            throw new InvalidOperationException("FFmpeg setup finished but the executables could not be found.");

        FfmpegPath = installedFfmpeg;
        FfprobePath = installedFfprobe;
        status?.Report("Media engine ready.");
    }

    public async Task<MediaInfo> ProbeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        RequireTools();
        var psi = CreateProcess(FfprobePath!);
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration,start_time:stream=codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,duration,start_time");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(sourcePath);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffprobe.");
        var jsonTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await jsonTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Unable to read media information." : error.Trim());

        using var doc = JsonDocument.Parse(json);
        var info = new MediaInfo();
        if (doc.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var durationProp) &&
                double.TryParse(durationProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                info.DurationSeconds = duration;
            if (format.TryGetProperty("start_time", out var formatStartProp) &&
                double.TryParse(formatStartProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var formatStart))
                info.FormatStartSeconds = formatStart;
        }

        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var typeProp) ? typeProp.GetString() : null;
                if (type == "video" && !info.HasVideo)
                {
                    info.HasVideo = true;
                    info.VideoCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    info.Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    info.Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                    var avg = stream.TryGetProperty("avg_frame_rate", out var avgFps) ? ParseFraction(avgFps.GetString()) : null;
                    var nominal = stream.TryGetProperty("r_frame_rate", out var fps) ? ParseFraction(fps.GetString()) : null;
                    info.FrameRate = avg.HasValue && avg.Value > 1 && avg.Value <= 240
                        ? avg.Value
                        : nominal.HasValue && nominal.Value > 1 && nominal.Value <= 240
                            ? nominal.Value
                            : 30;
                    if (stream.TryGetProperty("duration", out var vd) && double.TryParse(vd.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var videoDuration))
                        info.VideoDurationSeconds = videoDuration;
                    if (stream.TryGetProperty("start_time", out var vs) && double.TryParse(vs.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var videoStart))
                        info.VideoStartSeconds = videoStart;
                }
                else if (type == "audio" && !info.HasAudio)
                {
                    info.HasAudio = true;
                    info.AudioCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    if (stream.TryGetProperty("duration", out var ad) && double.TryParse(ad.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var audioDuration))
                        info.AudioDurationSeconds = audioDuration;
                    if (stream.TryGetProperty("start_time", out var ast) && double.TryParse(ast.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var audioStart))
                        info.AudioStartSeconds = audioStart;
                }
            }
        }

        if (info.HasVideo && info.VideoDurationSeconds <= 0) info.VideoDurationSeconds = info.DurationSeconds;
        if (info.HasAudio && info.AudioDurationSeconds <= 0) info.AudioDurationSeconds = info.DurationSeconds;
        return info;
    }

    public async Task<AudioAnalysisResult> ExtractAudioAnalysisAsync(string sourcePath, double durationSeconds, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireTools();
        const int sampleRate = SileroVadService.SampleRate;
        const double windowSeconds = 0.02;
        const int samplesPerWindow = (int)(sampleRate * windowSeconds);
        var targetPeakCount = 4800;
        var samplesPerPeak = Math.Max(1, (int)Math.Ceiling(Math.Max(1, durationSeconds) * sampleRate / targetPeakCount));

        var psi = CreateProcess(FfmpegPath!);
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0");
        psi.ArgumentList.Add("-vn");
        // Feed the VAD the actual mono recording. Speech/non-speech is decided by the voice
        // activity model; we no longer try to fake voice detection with a narrow dB filter.
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(sampleRate.ToString());
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg audio analysis.");
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var windowDb = new List<double>((int)(durationSeconds / windowSeconds) + 128);
        var windowPeakDb = new List<double>((int)(durationSeconds / windowSeconds) + 128);
        var windowZcr = new List<double>((int)(durationSeconds / windowSeconds) + 128);
        var peaks = new List<double>(targetPeakCount + 64);
        var speechProbability = new List<double>((int)Math.Ceiling(Math.Max(1, durationSeconds) / SileroVadService.WindowSeconds) + 8);
        using var vad = new SileroVadService();
        var vadChunk = new float[SileroVadService.ChunkSamples];
        var vadChunkCount = 0;
        long totalSamples = 0;
        int windowCount = 0;
        double windowSquares = 0;
        double windowPeak = 0;
        int windowZeroCrossings = 0;
        double previousWindowSample = 0;
        bool hasPreviousWindowSample = false;
        int peakCount = 0;
        double peakMax = 0;
        var buffer = new byte[64 * 1024];
        int leftoverByte = -1;
        long lastAnalysisProgressMs = 0;

        while (true)
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            var i = 0;
            if (leftoverByte >= 0 && read > 0)
            {
                ProcessSample((short)(leftoverByte | (buffer[0] << 8)));
                leftoverByte = -1;
                i = 1;
            }
            for (; i + 1 < read; i += 2)
            {
                ProcessSample((short)(buffer[i] | (buffer[i + 1] << 8)));
            }
            if (i < read) leftoverByte = buffer[i];

            if (durationSeconds > 0)
            {
                var now = Environment.TickCount64;
                if (now - lastAnalysisProgressMs >= 100)
                {
                    lastAnalysisProgressMs = now;
                    progress?.Report(Math.Min(0.99, totalSamples / (durationSeconds * sampleRate)));
                }
            }
        }

        if (windowCount > 0) FlushWindow();
        if (peakCount > 0) peaks.Add(peakMax);
        if (vadChunkCount > 0)
        {
            Array.Clear(vadChunk, vadChunkCount, vadChunk.Length - vadChunkCount);
            speechProbability.Add(vad.ProcessChunk(vadChunk));
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Audio analysis failed." : error.Trim());

        progress?.Report(1.0);
        return new AudioAnalysisResult
        {
            Peaks = peaks,
            WindowDb = windowDb,
            WindowPeakDb = windowPeakDb,
            WindowZcr = windowZcr,
            SpeechProbability = speechProbability,
            SpeechWindowSeconds = SileroVadService.WindowSeconds,
            SuggestedThresholdDb = SuggestThreshold(windowDb),
            WindowSeconds = windowSeconds
        };

        void ProcessSample(short raw)
        {
            var normalized = raw / 32768.0;
            var abs = Math.Abs(normalized);
            windowSquares += normalized * normalized;
            windowPeak = Math.Max(windowPeak, abs);
            if (hasPreviousWindowSample && Math.Abs(normalized) > 0.0008 && Math.Abs(previousWindowSample) > 0.0008 && Math.Sign(normalized) != Math.Sign(previousWindowSample))
                windowZeroCrossings++;
            previousWindowSample = normalized;
            hasPreviousWindowSample = true;
            windowCount++;
            peakMax = Math.Max(peakMax, abs);
            peakCount++;
            totalSamples++;

            vadChunk[vadChunkCount++] = (float)normalized;
            if (vadChunkCount >= vadChunk.Length)
            {
                speechProbability.Add(vad.ProcessChunk(vadChunk));
                vadChunkCount = 0;
            }

            if (windowCount >= samplesPerWindow) FlushWindow();
            if (peakCount >= samplesPerPeak)
            {
                peaks.Add(peakMax);
                peakMax = 0;
                peakCount = 0;
            }
        }

        void FlushWindow()
        {
            var count = Math.Max(1, windowCount);
            var rms = Math.Sqrt(windowSquares / count);
            var db = rms <= 0.0000001 ? -90.0 : 20.0 * Math.Log10(rms);
            var peakDb = windowPeak <= 0.0000001 ? -90.0 : 20.0 * Math.Log10(windowPeak);
            var zcr = windowCount <= 1 ? 0.0 : windowZeroCrossings / (double)(windowCount - 1);
            windowDb.Add(Math.Clamp(db, -90, 0));
            windowPeakDb.Add(Math.Clamp(peakDb, -90, 0));
            windowZcr.Add(Math.Clamp(zcr, 0, 1));
            windowSquares = 0;
            windowPeak = 0;
            windowZeroCrossings = 0;
            previousWindowSample = 0;
            hasPreviousWindowSample = false;
            windowCount = 0;
        }
    }

    public Task ExportAsync(
        CutFlowProject project,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var format = Path.GetExtension(destinationPath).TrimStart('.').ToUpperInvariant();
        return ExportAsync(project, new ExportOptions { DestinationPath = destinationPath, Format = format }, progress, cancellationToken);
    }

    public async Task ExportAsync(
        CutFlowProject project,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireTools();
        if (string.IsNullOrWhiteSpace(options.DestinationPath)) throw new InvalidOperationException("Choose an export destination first.");
        if (string.IsNullOrWhiteSpace(project.SourcePath) || !File.Exists(project.SourcePath))
            throw new FileNotFoundException("The current CutFlow working media could not be found.", project.SourcePath);

        var destinationPath = options.DestinationPath;
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        var audioOnly = options.AudioOnly || extension is ".mp3" or ".wav" or ".m4a";
        var includeVideo = project.Media.HasVideo && !audioOnly;
        var includeAudio = project.Media.HasAudio;
        if (!includeVideo && !includeAudio) throw new InvalidOperationException("There is no compatible media stream to export.");

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var kept = BuildKeptRanges(project.Media.DurationSeconds, project.Segments);
        if (kept.Count == 0) throw new InvalidOperationException("Everything is marked for removal. Undo at least one cut before exporting.");
        var hasVirtualTimelineCuts = kept.Count != 1 || kept[0].start > 0.0005 || Math.Abs(kept[0].end - project.Media.DurationSeconds) > 0.0005;

        // Modern CutFlow projects physically apply Cut/Cut All to SourcePath before Export opens.
        // That means the common export case is already a finished, shortened media file. Do not
        // decode + filter + re-encode that video again just to save it somewhere else.
        if (!hasVirtualTimelineCuts)
        {
            await ExportCurrentWorkingMediaAsync(project, options, includeVideo, includeAudio, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Compatibility path for older projects that still have committed virtual cut markers.
        await ExportFilteredTimelineAsync(project, options, kept, includeVideo, includeAudio, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExportCurrentWorkingMediaAsync(
        CutFlowProject project,
        ExportOptions options,
        bool includeVideo,
        bool includeAudio,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = options.DestinationPath;
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        var speedMode = NormalizeSpeedMode(options.SpeedMode);
        var noVideoTransform = options.Width <= 0 && options.Height <= 0 && options.FrameRate <= 0.1;
        var cleanupFilter = options.ApplyAudioCleanup ? AudioCleanupTransform(project.Silence) : "anull";
        var needsAudioCleanup = includeAudio && !cleanupFilter.Equals("anull", StringComparison.OrdinalIgnoreCase);
        var sourceExtension = Path.GetExtension(project.SourcePath).ToLowerInvariant();

        // Absolute fastest case: the user wants the same container, Original resolution/FPS and
        // no cleanup. This is a plain file copy of the already-cut working media, so a 4-minute
        // project normally exports in seconds instead of being encoded for 30+ minutes.
        if (speedMode == "Fast" && noVideoTransform && !needsAudioCleanup &&
            includeVideo == project.Media.HasVideo && includeAudio == project.Media.HasAudio &&
            sourceExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            await CopyFileWithProgressAsync(project.SourcePath, destinationPath, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var smartCopyVideo = speedMode == "Fast" && includeVideo && noVideoTransform && (extension is ".mp4" or ".mov" or ".mkv");
        try
        {
            await RunDirectExportAsync(project, options, includeVideo, includeAudio, smartCopyVideo, cleanupFilter, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (smartCopyVideo)
        {
            // An unusual codec/container or a failed opening-seconds verification automatically
            // falls back to fast GPU/CPU encoding. RunDirectExportAsync now stages its output, so
            // do NOT delete an existing destination until a verified replacement is ready.
            await RunDirectExportAsync(project, options, includeVideo, includeAudio, false, cleanupFilter, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunDirectExportAsync(
        CutFlowProject project,
        ExportOptions options,
        bool includeVideo,
        bool includeAudio,
        bool smartCopyVideo,
        string cleanupFilter,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = options.DestinationPath;
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        var speedMode = NormalizeSpeedMode(options.SpeedMode);
        var stagingPath = BuildStagingExportPath(destinationPath);

        try
        {
            var psi = CreateProcess(FfmpegPath!);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            // Generate missing presentation timestamps instead of trusting a quirky MP4/MOV edit
            // list. Smart Export also shifts the selected video/audio stream starts to zero below.
            psi.ArgumentList.Add("-fflags"); psi.ArgumentList.Add("+genpts");

            // Normalize input timestamps without doing extra disk I/O on normal files. When A/V
            // already share the same start timestamp, read the source once and shift both together.
            // Only files with different video/audio edit-list starts need the second input.
            var videoStart = CleanStartTimestamp(project.Media.VideoStartSeconds);
            var audioStart = CleanStartTimestamp(project.Media.AudioStartSeconds);
            var separateAvInputs = includeVideo && includeAudio && Math.Abs(videoStart - audioStart) > 0.0005;
            if (includeVideo)
            {
                AddTimestampNormalizedInput(psi, project.SourcePath, videoStart);
                if (includeAudio && separateAvInputs)
                    AddTimestampNormalizedInput(psi, project.SourcePath, audioStart);
            }
            else if (includeAudio)
            {
                AddTimestampNormalizedInput(psi, project.SourcePath, audioStart);
            }

            if (includeVideo)
            {
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0");
                if (smartCopyVideo)
                {
                    psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("copy");
                }
                else
                {
                    var transforms = VideoTransform(options, System.Globalization.CultureInfo.InvariantCulture);
                    if (!transforms.Equals("null", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(transforms);
                    }
                    await AddFastVideoEncoderAsync(psi, options, speedMode, cancellationToken).ConfigureAwait(false);
                    psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
                }
            }

            if (includeAudio)
            {
                // Most files use input 0 for both streams. Only mismatched A/V start timestamps
                // create a second, independently shifted audio input.
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add(includeVideo && separateAvInputs ? "1:a:0" : "0:a:0");
                // Keep audio timestamps continuous from sample zero. This prevents filter lookahead
                // or odd source edit lists from creating a tiny initial hole/dropout in the export.
                var audioFilter = cleanupFilter.Equals("anull", StringComparison.OrdinalIgnoreCase)
                    ? "aresample=async=1:first_pts=0"
                    : cleanupFilter + ",aresample=async=1:first_pts=0";
                psi.ArgumentList.Add("-af"); psi.ArgumentList.Add(audioFilter);
                AddAudioEncoder(psi, extension, options.AudioBitrateKbps);
            }

            psi.ArgumentList.Add("-map_metadata"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-avoid_negative_ts"); psi.ArgumentList.Add("make_zero");

            // Faststart costs a short final disk pass but prevents MP4/MOV players from seeing a
            // half-initialized index at the beginning. The old optimization that skipped this in
            // Fast mode saved seconds at most and was not worth fragile startup playback.
            if (extension is ".mp4" or ".mov" or ".m4a")
            {
                psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            }

            await RunExportProcessAsync(psi, stagingPath, project.Media.DurationSeconds, options.LowPriority, progress, cancellationToken).ConfigureAwait(false);

            // Never publish a Smart Export until its first seconds can actually be decoded and
            // its A/V streams begin together. If stream-copy cannot satisfy this, the caller
            // automatically falls back to the fast NVENC/ultrafast encoder path.
            await VerifyExportStartAsync(stagingPath, includeVideo, includeAudio, project.Media.DurationSeconds, cancellationToken).ConfigureAwait(false);

            if (Path.GetFullPath(stagingPath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CutFlow could not create a safe staging file for export.");
            File.Move(stagingPath, destinationPath, true);
            progress?.Report(1.0);
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
        }
    }

    private static string BuildStagingExportPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory)) directory = Directory.GetCurrentDirectory();
        var extension = Path.GetExtension(destinationPath);
        var stem = Path.GetFileNameWithoutExtension(destinationPath);
        return Path.Combine(directory, $".{stem}.cutflow-{Guid.NewGuid():N}{extension}");
    }

    private static double CleanStartTimestamp(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;

    private static void AddTimestampNormalizedInput(ProcessStartInfo psi, string sourcePath, double streamStartSeconds)
    {
        var start = CleanStartTimestamp(streamStartSeconds);
        if (Math.Abs(start) > 0.0005)
        {
            psi.ArgumentList.Add("-itsoffset");
            psi.ArgumentList.Add((-start).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
    }

    private async Task VerifyExportStartAsync(
        string path,
        bool expectVideo,
        bool expectAudio,
        double expectedDuration,
        CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (expectVideo && !info.HasVideo) throw new InvalidOperationException("The exported video stream is missing.");
        if (expectAudio && !info.HasAudio) throw new InvalidOperationException("The exported audio stream is missing.");

        const double startTolerance = 0.12;
        if (expectVideo && Math.Abs(info.VideoStartSeconds) > startTolerance)
            throw new InvalidOperationException($"The fast export video starts at {info.VideoStartSeconds:0.###}s instead of zero.");
        if (expectAudio && Math.Abs(info.AudioStartSeconds) > startTolerance)
            throw new InvalidOperationException($"The fast export audio starts at {info.AudioStartSeconds:0.###}s instead of zero.");
        if (expectVideo && expectAudio && Math.Abs(info.VideoStartSeconds - info.AudioStartSeconds) > 0.08)
            throw new InvalidOperationException("The fast export audio/video start timestamps are not aligned.");
        if (expectedDuration > 0.25 && Math.Abs(info.DurationSeconds - expectedDuration) > Math.Max(0.50, expectedDuration * 0.01))
            throw new InvalidOperationException($"The fast export duration changed unexpectedly ({info.DurationSeconds:0.###}s vs {expectedDuration:0.###}s).");
        if (expectVideo && expectAudio && info.VideoDurationSeconds > 0 && info.AudioDurationSeconds > 0 &&
            Math.Abs(info.VideoDurationSeconds - info.AudioDurationSeconds) > 0.30)
            throw new InvalidOperationException("The fast export audio/video durations are not aligned.");

        // Decode only the first few seconds. This catches damaged/missing reference frames and
        // timestamp corruption while adding only a tiny verification cost to a fast export.
        var verifySeconds = Math.Clamp(Math.Min(expectedDuration, 2.5), 0.25, 2.5);
        var psi = CreateProcess(FfmpegPath!);
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-xerror");
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(verifySeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
        if (expectVideo) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v:0?"); }
        if (expectAudio) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:a:0?"); }
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not verify the beginning of the export.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        _ = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "The first seconds of the fast export did not decode cleanly."
                : "The first seconds of the fast export did not decode cleanly: " + error.Trim());
    }

    private async Task ExportFilteredTimelineAsync(
        CutFlowProject project,
        ExportOptions options,
        IReadOnlyList<(double start, double end)> kept,
        bool includeVideo,
        bool includeAudio,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var destinationPath = options.DestinationPath;
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        var outputDuration = kept.Sum(x => x.end - x.start);
        var filterGraph = BuildFilterScript(project.Media, project.Silence, kept, includeVideo, includeAudio, options);
        var speedMode = NormalizeSpeedMode(options.SpeedMode);

        var psi = CreateProcess(FfmpegPath!);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(project.SourcePath);
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(filterGraph);

        if (includeVideo)
        {
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outv]");
            await AddFastVideoEncoderAsync(psi, options, speedMode, cancellationToken).ConfigureAwait(false);
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        }
        if (includeAudio)
        {
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outa]");
            AddAudioEncoder(psi, extension, options.AudioBitrateKbps);
        }
        if (speedMode != "Fast" && (extension is ".mp4" or ".mov" or ".m4a"))
        {
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        }

        await RunExportProcessAsync(psi, destinationPath, outputDuration, options.LowPriority, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFastVideoEncoderAsync(ProcessStartInfo psi, ExportOptions options, string speedMode, CancellationToken cancellationToken)
    {
        var useNvenc = speedMode != "Quality" && await CanUseNvencAsync(cancellationToken).ConfigureAwait(false);
        if (useNvenc)
        {
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("h264_nvenc");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add(speedMode == "Fast" ? "p1" : "p4");
            if (speedMode == "Fast")
            {
                psi.ArgumentList.Add("-tune"); psi.ArgumentList.Add("ll");
            }
            psi.ArgumentList.Add("-cq"); psi.ArgumentList.Add(Math.Clamp(options.VideoCrf + 1, 15, 28).ToString());
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("0");
            return;
        }

        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add(speedMode switch { "Fast" => "ultrafast", "Balanced" => "veryfast", _ => "medium" });
        psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add(Math.Clamp(options.VideoCrf, 14, 28).ToString());
        var threads = options.VideoThreads > 0
            ? Math.Clamp(options.VideoThreads, 1, 16)
            : Math.Clamp(Environment.ProcessorCount - 1, 2, 12);
        psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add(threads.ToString());
    }

    private static void AddAudioEncoder(ProcessStartInfo psi, string extension, int bitrateKbps)
    {
        switch (extension)
        {
            case ".wav":
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s16le");
                break;
            case ".mp3":
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{Math.Clamp(bitrateKbps, 128, 320)}k");
                break;
            default:
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{Math.Clamp(bitrateKbps, 128, 320)}k");
                break;
        }
    }

    private async Task RunExportProcessAsync(
        ProcessStartInfo psi,
        string destinationPath,
        double outputDuration,
        bool lowPriority,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add(destinationPath);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg export.");
        try { process.PriorityClass = lowPriority ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; } catch { }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        long lastExportProgressMs = 0;
        while (!process.StandardOutput.EndOfStream)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(lowPriority ? 90 : 120), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw new InvalidOperationException("Media export stopped reporting progress for too long and was cancelled safely.");
            }
            if (line is null) break;
            if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase) && long.TryParse(line[12..], out var us))
            {
                var now = Environment.TickCount64;
                if (now - lastExportProgressMs >= 80)
                {
                    lastExportProgressMs = now;
                    progress?.Report(Math.Clamp((us / 1_000_000.0) / Math.Max(0.001, outputDuration), 0, 0.995));
                }
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Media export failed." : error.Trim());
        progress?.Report(1.0);
    }

    private static async Task CopyFileWithProgressAsync(string sourcePath, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different export destination from the current working media file.");

        var stagingPath = BuildStagingExportPath(destinationPath);
        try
        {
            const int bufferSize = 4 * 1024 * 1024;
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var destination = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[bufferSize];
                long copied = 0;
                var length = Math.Max(1L, source.Length);
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(Math.Clamp(copied / (double)length, 0, 0.995));
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(stagingPath, destinationPath, true);
            progress?.Report(1.0);
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
        }
    }

    private static string NormalizeSpeedMode(string? mode)
    {
        if (mode?.Equals("Quality", StringComparison.OrdinalIgnoreCase) == true) return "Quality";
        if (mode?.Equals("Balanced", StringComparison.OrdinalIgnoreCase) == true) return "Balanced";
        return "Fast";
    }

    public async Task CutExactRangesAsync(
        string sourcePath,
        string destinationPath,
        MediaInfo media,
        SilenceSettings silence,
        IReadOnlyList<(double start, double end)> cutRanges,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        RequireTools();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The source media for this cut job could not be found.", sourcePath);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new InvalidOperationException("The cut worker did not receive an output path.");

        var merged = new List<(double start, double end)>();
        foreach (var raw in cutRanges
            .Select(r => (start: Math.Clamp(Math.Min(r.start, r.end), 0, media.DurationSeconds), end: Math.Clamp(Math.Max(r.start, r.end), 0, media.DurationSeconds)))
            .Where(r => r.end - r.start >= 0.003)
            .OrderBy(r => r.start))
        {
            if (merged.Count == 0 || raw.start > merged[^1].end + 0.0005) merged.Add(raw);
            else merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, raw.end));
        }
        if (merged.Count == 0) throw new InvalidOperationException("There are no valid ranges to remove.");

        // Build the exact ranges we KEEP and concatenate those in one FFmpeg pass. This avoids the
        // duration drift caused by select+setpts=N/(fps*TB), especially with 29.97/59.94 or VFR
        // recordings. Audio is trimmed to the same timestamps, so the resulting container duration
        // follows the source timeline instead of a guessed frame rate.
        var kept = new List<(double start, double end)>();
        var cursor = 0.0;
        foreach (var cut in merged)
        {
            if (cut.start > cursor + 0.0005) kept.Add((cursor, cut.start));
            cursor = Math.Max(cursor, cut.end);
        }
        if (cursor < media.DurationSeconds - 0.0005) kept.Add((cursor, media.DurationSeconds));
        if (kept.Count == 0) throw new InvalidOperationException("These cuts would remove the entire file.");

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var filters = new List<string>();
        var includeVideo = media.HasVideo;
        var includeAudio = media.HasAudio;
        if (!includeVideo && !includeAudio) throw new InvalidOperationException("The source has no usable audio or video stream.");

        // Normalize source timestamps first. For VIDEO, do not split the decoded stream into
        // dozens (or hundreds) of branches. The old trim+split graph duplicated every decoded
        // frame into every kept segment, which is exactly the kind of thing that made 80-100 cuts
        // take forever and hammer memory/CPU. v3.22 drops all cut frames in ONE select pass and
        // subtracts the cumulative deleted duration from each surviving frame's PTS. This preserves
        // VFR timing while collapsing every red interval without a giant video split graph.
        if (includeVideo)
        {
            var rejectTerms = new List<string>(merged.Count);
            var shiftTerms = new List<string>(merged.Count);
            foreach (var cut in merged)
            {
                var start = cut.start.ToString("0.######", inv);
                var end = cut.end.ToString("0.######", inv);
                var removedDuration = (cut.end - cut.start).ToString("0.######", inv);
                rejectTerms.Add($"gte(t\\,{start})*lt(t\\,{end})");
                shiftTerms.Add($"gte(PTS*TB\\,{end})*{removedDuration}/TB");
            }
            var rejectExpression = string.Join("+", rejectTerms);
            var shiftExpression = string.Join("+", shiftTerms);
            filters.Add($"[0:v:0]setpts=PTS-STARTPTS,select=not({rejectExpression}),setpts=PTS-({shiftExpression})[outv]");
        }

        // Audio stays sample-accurate: trim the exact kept ranges, apply the tiny optional edge
        // fades, then concatenate those audio pieces. Audio buffers are tiny compared with decoded
        // video frames, so keeping exact atrim boundaries here is both safe and much lighter than
        // the old split-video graph.
        if (includeAudio)
        {
            if (kept.Count == 1)
                filters.Add("[0:a:0]asetpts=PTS-STARTPTS[asrc0]");
            else
                filters.Add($"[0:a:0]asetpts=PTS-STARTPTS,asplit={kept.Count}{string.Concat(Enumerable.Range(0, kept.Count).Select(i => $"[asrc{i}]"))}");

            for (var i = 0; i < kept.Count; i++)
            {
                var startText = kept[i].start.ToString("0.######", inv);
                var endText = kept[i].end.ToString("0.######", inv);
                var audio = $"[asrc{i}]atrim=start={startText}:end={endText},asetpts=PTS-STARTPTS";
                if (silence.BlendCutEdges && kept.Count > 1)
                {
                    var pieceDuration = Math.Max(0.001, kept[i].end - kept[i].start);
                    var fadeSeconds = Math.Min(0.006, Math.Max(0.0015, pieceDuration / 10.0));
                    var fadeText = fadeSeconds.ToString("0.######", inv);
                    if (i > 0) audio += $",afade=t=in:st=0:d={fadeText}";
                    if (i < kept.Count - 1)
                    {
                        var fadeStart = Math.Max(0, pieceDuration - fadeSeconds).ToString("0.######", inv);
                        audio += $",afade=t=out:st={fadeStart}:d={fadeText}";
                    }
                }
                filters.Add(audio + $"[a{i}]");
            }

            var audioInputs = string.Concat(Enumerable.Range(0, kept.Count).Select(i => $"[a{i}]"));
            if (kept.Count == 1)
                filters.Add("[a0]anull[outa]");
            else
                filters.Add($"{audioInputs}concat=n={kept.Count}:v=0:a=1[outa]");
        }

        var removed = merged.Sum(r => r.end - r.start);
        var outputDuration = Math.Max(0.001, media.DurationSeconds - removed);
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        // Working cuts favor the GPU when FFmpeg + the installed NVIDIA driver can actually
        // encode with NVENC. A tiny one-frame capability probe is cached; systems without NVENC
        // automatically keep the software ultrafast path. Final Export keeps its own quality settings.
        var useNvenc = includeVideo && await CanUseNvencAsync(cancellationToken).ConfigureAwait(false);
        var psi = CreateProcess(FfmpegPath!);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(string.Join(";", filters));

        if (includeVideo)
        {
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outv]");
            if (useNvenc)
            {
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("h264_nvenc");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("p1");
                psi.ArgumentList.Add("-tune"); psi.ArgumentList.Add("ll");
                psi.ArgumentList.Add("-cq"); psi.ArgumentList.Add("20");
                psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("0");
            }
            else
            {
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("ultrafast");
                psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("18");
                psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount / 2, 2, 6).ToString());
            }
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        }
        if (includeAudio)
        {
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outa]");
            if (extension == ".wav")
            {
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s16le");
            }
            else
            {
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("256k");
            }
        }
        if (extension is ".mp4" or ".mov" or ".m4a")
        {
            psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        }
        // Do NOT force -t here. Older builds used -t to make the final duration look correct,
        // but that could hide a partial/misplaced render by chopping time off the END. The natural
        // concat output must itself have the expected duration or the worker rejects the cut.
        psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add(destinationPath);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the background cut encoder.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var lastProgressTick = Environment.TickCount64;
        while (!process.StandardOutput.EndOfStream)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(90), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw new InvalidOperationException("The background cut encoder stopped reporting progress for too long and was stopped safely.");
            }
            if (line is null) break;
            if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase) && long.TryParse(line[12..], out var us))
            {
                var now = Environment.TickCount64;
                if (now - lastProgressTick >= 100)
                {
                    lastProgressTick = now;
                    progress?.Report(Math.Clamp((us / 1_000_000.0) / outputDuration, 0, 0.995));
                }
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The background cut encoder failed." : error.Trim());
        progress?.Report(1.0);
    }

    private async Task<bool> CanUseNvencAsync(CancellationToken cancellationToken)
    {
        if (_nvencAvailable.HasValue) return _nvencAvailable.Value;
        if (string.IsNullOrWhiteSpace(FfmpegPath) || !File.Exists(FfmpegPath)) return false;
        try
        {
            var psi = CreateProcess(FfmpegPath!);
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("color=c=black:s=16x16:r=1:d=0.05");
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("h264_nvenc");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");
            psi.RedirectStandardError = true;
            using var process = Process.Start(psi);
            if (process is null) { _nvencAvailable = false; return false; }
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            _nvencAvailable = process.ExitCode == 0;
        }
        catch
        {
            _nvencAvailable = false;
        }
        return _nvencAvailable == true;
    }

    public static List<(double start, double end)> BuildKeptRanges(double duration, IEnumerable<SilenceSegment> segments)
    {
        var cuts = segments.Where(s => s.IsCut && s.EndSeconds > s.StartSeconds)
            .Select(s => (start: Math.Clamp(s.StartSeconds, 0, duration), end: Math.Clamp(s.EndSeconds, 0, duration)))
            .Where(s => s.end - s.start > 0.001)
            .OrderBy(s => s.start)
            .ToList();

        var merged = new List<(double start, double end)>();
        foreach (var cut in cuts)
        {
            if (merged.Count == 0 || cut.start > merged[^1].end + 0.0005) merged.Add(cut);
            else merged[^1] = (merged[^1].start, Math.Max(merged[^1].end, cut.end));
        }

        var kept = new List<(double start, double end)>();
        var cursor = 0.0;
        foreach (var cut in merged)
        {
            if (cut.start > cursor + 0.0005) kept.Add((cursor, cut.start));
            cursor = Math.Max(cursor, cut.end);
        }
        if (cursor < duration - 0.0005) kept.Add((cursor, duration));
        return kept;
    }

    private static string BuildFilterScript(MediaInfo media, SilenceSettings silence, IReadOnlyList<(double start, double end)> kept, bool includeVideo, bool includeAudio, ExportOptions options)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var lines = new List<string>();
        for (var i = 0; i < kept.Count; i++)
        {
            var rangeStart = kept[i].start;
            var rangeEnd = kept[i].end;
            var start = rangeStart.ToString("0.######", inv);
            var end = rangeEnd.ToString("0.######", inv);
            if (includeVideo)
                lines.Add($"[0:v:0]trim=start={start}:end={end},setpts=PTS-STARTPTS[v{i}];");
            if (includeAudio)
            {
                var audio = $"[0:a:0]atrim=start={start}:end={end},asetpts=PTS-STARTPTS";
                if (silence.BlendCutEdges && kept.Count > 1)
                {
                    // A tiny 4ms edge fade prevents hard sample discontinuities/clicks without
                    // shortening the timeline or creating audible crossfade drift.
                    var fade = Math.Min(0.004, Math.Max(0.001, (rangeEnd - rangeStart) / 6.0));
                    var fadeText = fade.ToString("0.######", inv);
                    if (i > 0) audio += $",afade=t=in:st=0:d={fadeText}";
                    if (i < kept.Count - 1)
                    {
                        var fadeStart = Math.Max(0, (rangeEnd - rangeStart) - fade).ToString("0.######", inv);
                        audio += $",afade=t=out:st={fadeStart}:d={fadeText}";
                    }
                }
                lines.Add(audio + $"[a{i}];");
            }
        }

        var inputs = string.Concat(Enumerable.Range(0, kept.Count).Select(i =>
            includeVideo && includeAudio ? $"[v{i}][a{i}]" : includeVideo ? $"[v{i}]" : $"[a{i}]"));

        if (includeVideo && includeAudio)
        {
            lines.Add($"{inputs}concat=n={kept.Count}:v=1:a=1[concatv][joineda];");
            lines.Add($"[concatv]{VideoTransform(options, inv)}[outv];");
            lines.Add($"[joineda]{(options.ApplyAudioCleanup ? AudioCleanupTransform(silence) : "anull")}[outa]");
        }
        else if (includeVideo)
        {
            lines.Add($"{inputs}concat=n={kept.Count}:v=1:a=0[concatv];");
            lines.Add($"[concatv]{VideoTransform(options, inv)}[outv]");
        }
        else
        {
            lines.Add($"{inputs}concat=n={kept.Count}:v=0:a=1[joineda];");
            lines.Add($"[joineda]{(options.ApplyAudioCleanup ? AudioCleanupTransform(silence) : "anull")}[outa]");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string AudioCleanupTransform(SilenceSettings settings)
    {
        var filters = new List<string>();
        // These are intentionally subtle. They are meant to tame distractions while keeping
        // spoken voice natural, not to perform aggressive restoration.
        if (settings.ReduceMouthClicks)
            filters.Add("adeclick=t=2:w=55:o=75");
        if (settings.ReduceImpulsiveNoise)
            filters.Add("alimiter=limit=0.92:attack=4:release=45");
        if (settings.ReduceBackgroundSqueaks)
            filters.Add("afftdn=nr=4:nf=-38:tn=1");
        return filters.Count == 0 ? "anull" : string.Join(',', filters);
    }

    private static string VideoTransform(ExportOptions options, System.Globalization.CultureInfo inv)
    {
        var filters = new List<string>();
        if (options.FrameRate > 0.1)
            filters.Add($"fps={options.FrameRate.ToString("0.###", inv)}");
        if (options.Width > 0 && options.Height > 0)
            filters.Add($"scale={options.Width}:{options.Height}:flags=lanczos");
        return filters.Count == 0 ? "null" : string.Join(',', filters);
    }

    private async Task DownloadWindowsBuildAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_toolRoot);
        var zipPath = Path.Combine(_toolRoot, "ffmpeg-download.zip");
        var extractPath = Path.Combine(_toolRoot, "extract-temp");
        try
        {
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
            Directory.CreateDirectory(extractPath);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var response = await http.GetAsync(WindowsBuildUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read <= 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    if (total > 0) status?.Report($"Installing media engine… {received * 100.0 / total:0}%");
                }
            }

            status?.Report("Finishing media engine setup…");
            ZipFile.ExtractToDirectory(zipPath, extractPath, true);
            var ffmpeg = Directory.EnumerateFiles(extractPath, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            var ffprobe = Directory.EnumerateFiles(extractPath, "ffprobe.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ffmpeg is null || ffprobe is null) throw new InvalidOperationException("The downloaded FFmpeg package did not contain the expected tools.");
            File.Copy(ffmpeg, Path.Combine(_toolRoot, "ffmpeg.exe"), true);
            File.Copy(ffprobe, Path.Combine(_toolRoot, "ffprobe.exe"), true);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        }
    }

    private static (string ffmpeg, string ffprobe)? FindOnPath()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = dir.Trim().Trim('"');
                var f = Path.Combine(clean, "ffmpeg.exe");
                var p = Path.Combine(clean, "ffprobe.exe");
                if (File.Exists(f) && File.Exists(p)) return (f, p);
            }
        }
        catch { }
        return null;
    }

    private static bool IsValidToolPair(string? ffmpeg, string? ffprobe) =>
        !string.IsNullOrWhiteSpace(ffmpeg) && !string.IsNullOrWhiteSpace(ffprobe) && File.Exists(ffmpeg) && File.Exists(ffprobe);

    private void RequireTools()
    {
        if (!IsValidToolPair(FfmpegPath, FfprobePath)) throw new InvalidOperationException("The media engine is not ready yet.");
    }

    private static ProcessStartInfo CreateProcess(string executable) => new(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = false
    };

    private static double? ParseFraction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], out var a) && double.TryParse(parts[1], out var b) && Math.Abs(b) > 0.000001)
            return a / b;
        if (double.TryParse(text, out var value)) return value;
        return null;
    }

    private static double SuggestThreshold(IReadOnlyList<double> windows)
    {
        var values = windows.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).OrderBy(x => x).ToArray();
        if (values.Length < 10) return -42;
        double Percentile(double p) => values[Math.Clamp((int)Math.Round((values.Length - 1) * p), 0, values.Length - 1)];
        var noise = Percentile(0.14);
        var speech = Percentile(0.78);
        var spread = Math.Max(5.0, speech - noise);
        var threshold = noise + Math.Clamp(spread * 0.24, 3.0, 6.0);
        threshold = Math.Min(threshold, speech - 7.0);
        return Math.Clamp(threshold, -58, -26);
    }
}
