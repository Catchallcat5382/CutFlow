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
        psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration:stream=codec_type,codec_name,width,height,r_frame_rate,avg_frame_rate,duration,start_time");
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
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var durationProp))
        {
            if (double.TryParse(durationProp.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                info.DurationSeconds = duration;
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
                }
                else if (type == "audio" && !info.HasAudio)
                {
                    info.HasAudio = true;
                    info.AudioCodec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() ?? string.Empty : string.Empty;
                    if (stream.TryGetProperty("duration", out var ad) && double.TryParse(ad.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var audioDuration))
                        info.AudioDurationSeconds = audioDuration;
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

        var kept = BuildKeptRanges(project.Media.DurationSeconds, project.Segments);
        if (kept.Count == 0) throw new InvalidOperationException("Everything is marked for removal. Undo at least one cut before exporting.");

        var destinationPath = options.DestinationPath;
        var extension = Path.GetExtension(destinationPath).ToLowerInvariant();
        var audioOnly = options.AudioOnly || extension is ".mp3" or ".wav" or ".m4a";
        var includeVideo = project.Media.HasVideo && !audioOnly;
        var includeAudio = project.Media.HasAudio;
        if (!includeVideo && !includeAudio) throw new InvalidOperationException("There is no compatible media stream to export.");

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var outputDuration = kept.Sum(x => x.end - x.start);
        var filterGraph = BuildFilterScript(project.Media, project.Silence, kept, includeVideo, includeAudio, options);
        {
            // Some bundled/minimal FFmpeg builds do not expose -filter_complex_script.
            // Passing the graph as one ArgumentList entry avoids shell quoting and works with those builds.
            var psi = CreateProcess(FfmpegPath!);
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            if (options.LowPriority)
            {
                // Limit decoder work too. Output encoding is also limited below. This keeps
                // preview generation from starving WPF while the progress overlay is open.
                psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
            }
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(project.SourcePath);
            psi.ArgumentList.Add("-filter_complex"); psi.ArgumentList.Add(filterGraph);

            if (includeVideo)
            {
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outv]");
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset");
                var preset = options.VideoPreset?.Trim().ToLowerInvariant();
                if (preset is not ("ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium" or "slow")) preset = "fast";
                psi.ArgumentList.Add(preset);
                if (options.VideoThreads > 0)
                {
                    psi.ArgumentList.Add("-threads");
                    psi.ArgumentList.Add(Math.Clamp(options.VideoThreads, 1, 16).ToString());
                }
                psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add(Math.Clamp(options.VideoCrf, 14, 28).ToString());
                psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            }

            if (includeAudio)
            {
                psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[outa]");
                switch (extension)
                {
                    case ".wav":
                        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("pcm_s16le");
                        break;
                    case ".mp3":
                        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
                        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{Math.Clamp(options.AudioBitrateKbps, 128, 320)}k");
                        break;
                    default:
                        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                        psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add($"{Math.Clamp(options.AudioBitrateKbps, 128, 320)}k");
                        break;
                }
            }

            if (extension is ".mp4" or ".mov" or ".m4a")
            {
                psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
            }

            psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add(destinationPath);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg export.");
            if (options.LowPriority)
            {
                try { process.PriorityClass = ProcessPriorityClass.Idle; } catch { }
            }
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
                    // A broken encoder/filter must never leave CutFlow behind a permanent busy
                    // overlay. FFmpeg's -progress stream normally emits lines continuously; if
                    // it goes completely silent for a full minute on a working render, treat it
                    // as stalled, terminate it, and return control to the editor with an error.
                    line = await process.StandardOutput.ReadLineAsync()
                        .WaitAsync(TimeSpan.FromSeconds(options.LowPriority ? 60 : 120), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    throw new InvalidOperationException("Media processing stopped responding for too long. CutFlow cancelled it safely instead of leaving the editor frozen.");
                }
                if (line is null) break;
                if (line.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase) && long.TryParse(line[12..], out var us))
                {
                    var now = Environment.TickCount64;
                    if (now - lastExportProgressMs >= 90)
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
            lines.Add($"[joineda]{AudioCleanupTransform(silence)}[outa]");
        }
        else if (includeVideo)
        {
            lines.Add($"{inputs}concat=n={kept.Count}:v=1:a=0[concatv];");
            lines.Add($"[concatv]{VideoTransform(options, inv)}[outv]");
        }
        else
        {
            lines.Add($"{inputs}concat=n={kept.Count}:v=0:a=1[joineda];");
            lines.Add($"[joineda]{AudioCleanupTransform(silence)}[outa]");
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
