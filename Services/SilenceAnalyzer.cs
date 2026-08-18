using CutFlow.Models;

namespace CutFlow.Services;

/// <summary>
/// v3.22 voice-first detector.
///
/// The old CutFlow detector tried to infer speech from dB/ZCR/crest heuristics. That can make a
/// real pause look like a row of tiny red slivers because mouse clicks, chair noise, compression
/// noise, or room hiss momentarily cross the amplitude threshold. v3.22 uses the Silero VAD
/// speech probability as the source of truth. Energy/ZCR are now only safety signals used to
/// protect very soft speech and classify short transient islands.
///
/// A scan is deterministic: previous automatic regions are never detector input. Only manual
/// regions and explicit Keep decisions survive a Rescan.
/// </summary>
public sealed class SilenceAnalyzer
{
    public List<SilenceSegment> Analyze(AudioAnalysisResult analysis, SilenceSettings settings, IReadOnlyList<SilenceSegment>? previous = null)
        => AnalyzeInternal(analysis, settings, previous, DetectorPass.Visible);

    public List<SilenceSegment> AnalyzeRescan(AudioAnalysisResult analysis, SilenceSettings settings, IReadOnlyList<SilenceSegment>? previous = null)
        => AnalyzeInternal(analysis, settings, previous, DetectorPass.Visible);

    /// <summary>
    /// Cut All's hidden safety planner. It does not render three times. It evaluates the same
    /// immutable voice-probability map at three deterministic cleanup levels, unions the result,
    /// protects explicit Keep ranges, and returns ONE final cut manifest for ONE render.
    /// </summary>
    public List<SilenceSegment> BuildThreePassCutAllPlan(
        AudioAnalysisResult analysis,
        SilenceSettings settings,
        IReadOnlyList<SilenceSegment> existing)
    {
        if (!HasVad(analysis))
            return existing.Where(IsActive).Select(CloneSegment).ToList();

        var explicitOnly = existing.Where(x => !x.IsAutomatic || x.IsIgnored).Select(CloneSegment).ToList();
        var pass1 = AnalyzeInternal(analysis, settings, explicitOnly, DetectorPass.Visible);
        var pass2 = AnalyzeInternal(analysis, settings, explicitOnly, DetectorPass.Refine);
        var pass3 = AnalyzeInternal(analysis, settings, explicitOnly, DetectorPass.FinalSweep);

        var combined = new List<SilenceSegment>();
        combined.AddRange(existing.Where(IsActive).Select(CloneSegment));
        combined.AddRange(pass1.Where(IsActive).Select(CloneSegment));
        combined.AddRange(pass2.Where(IsActive).Select(CloneSegment));
        combined.AddRange(pass3.Where(IsActive).Select(CloneSegment));
        combined = UnionOverlapping(combined);

        // One final sliver pass. Only bridge an island if Silero itself is not confidently seeing
        // sustained speech in that island. This is what turns [pause][click][pause] into one cut
        // without deleting an actual short word.
        MergeAcrossNonVoiceIslands(
            combined,
            analysis,
            maxIslandSeconds: Math.Clamp(Math.Max(settings.RemoveBlipsSeconds, 0.10) + 0.055, 0.10, 0.24),
            maxSpeechProbability: settings.ProtectSoftVoice ? 0.56 : 0.64,
            maxSustainedSpeechSeconds: settings.ProtectSoftVoice ? 0.045 : 0.060);

        foreach (var keep in existing.Where(x => x.IsIgnored))
            SubtractKeptRange(combined, keep.StartSeconds, keep.EndSeconds);

        combined = UnionOverlapping(combined);
        combined.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return combined;
    }

    public static double NoiseProfileOffset(string? profile) => profile switch
    {
        "Noisy Room" => 0.03,
        "Voice Focus" => 0.045,
        "Keep Ambience" => -0.045,
        _ => 0.0
    };

    private enum DetectorPass { Visible, Refine, FinalSweep }

    private List<SilenceSegment> AnalyzeInternal(
        AudioAnalysisResult analysis,
        SilenceSettings settings,
        IReadOnlyList<SilenceSegment>? previous,
        DetectorPass pass)
    {
        if (!HasVad(analysis))
            return AnalyzeEnergyFallback(analysis, settings, previous);

        var probabilities = analysis.SpeechProbability;
        var vadWindow = analysis.SpeechWindowSeconds;
        var duration = Math.Max(
            analysis.WindowDb.Count * Math.Max(0.001, analysis.WindowSeconds),
            probabilities.Count * vadWindow);

        var speechThreshold = BuildVadSpeechThreshold(settings, pass);
        var releaseThreshold = Math.Clamp(speechThreshold - (settings.ProtectSoftVoice ? 0.16 : 0.13), 0.12, 0.55);
        var speech = BuildSpeechMask(analysis, speechThreshold, releaseThreshold, settings, pass);

        var minPause = Math.Max(0.030, settings.MinimumSilenceSeconds);
        var padding = Math.Max(0, settings.PaddingSeconds);
        var transientBridge = Math.Clamp(Math.Max(settings.RemoveBlipsSeconds, 0.075), 0.060, 0.22);
        switch (pass)
        {
            case DetectorPass.Refine:
                minPause = Math.Max(0.045, minPause * 0.72);
                padding *= 0.45;
                transientBridge = Math.Min(0.22, transientBridge + 0.030);
                break;
            case DetectorPass.FinalSweep:
                minPause = Math.Max(0.035, minPause * 0.52);
                padding = 0;
                transientBridge = Math.Min(0.24, transientBridge + 0.055);
                break;
        }
        if (settings.NoiseProfile.Equals("Keep Ambience", StringComparison.OrdinalIgnoreCase))
            minPause += 0.025;

        // Remove short speech islands that are much more likely to be a click/bump than a word.
        // The max Silero probability check is the critical guard: a short but confident spoken
        // syllable remains speech even if it is brief.
        RemoveTransientSpeechIslands(
            speech,
            probabilities,
            vadWindow,
            Math.Max(transientBridge, pass == DetectorPass.Visible ? 0.075 : 0.10),
            pass == DetectorPass.FinalSweep ? 0.68 : 0.62,
            settings.ProtectSoftVoice);

        // Bridge microscopic model drop-outs inside an otherwise continuous word.
        BridgeFalseHoles(speech, Math.Max(1, (int)Math.Ceiling((settings.ProtectSoftVoice ? 0.050 : 0.035) / vadWindow)));

        var automatic = BuildGapRegions(speech, vadWindow, duration, minPause, padding);

        MergeAcrossNonVoiceIslands(
            automatic,
            analysis,
            transientBridge,
            pass switch
            {
                DetectorPass.Visible => settings.ProtectSoftVoice ? 0.54 : 0.62,
                DetectorPass.Refine => settings.ProtectSoftVoice ? 0.57 : 0.65,
                _ => settings.ProtectSoftVoice ? 0.60 : 0.68
            },
            pass == DetectorPass.Visible ? 0.040 : 0.055);

        PreserveExplicitOverrides(automatic, previous);
        automatic = UnionOverlapping(automatic);
        automatic.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        return automatic;
    }

    private static bool[] BuildSpeechMask(
        AudioAnalysisResult analysis,
        double speechThreshold,
        double releaseThreshold,
        SilenceSettings settings,
        DetectorPass pass)
    {
        var p = analysis.SpeechProbability;
        var ws = analysis.SpeechWindowSeconds;
        var speech = new bool[p.Count];
        if (p.Count == 0) return speech;

        var releaseFrames = Math.Max(1, (int)Math.Ceiling((pass == DetectorPass.Visible ? 0.064 : 0.048) / ws));
        var triggered = false;
        var belowCount = 0;
        var pendingEnd = -1;

        for (var i = 0; i < p.Count; i++)
        {
            var probability = Math.Clamp(p[i], 0, 1);
            if (!triggered)
            {
                // One very-confident frame is enough to start speech. Otherwise require two
                // neighbouring frames so an isolated impact does not become a word.
                var next = i + 1 < p.Count ? p[i + 1] : 0;
                if (probability >= Math.Min(0.86, speechThreshold + 0.22) ||
                    (probability >= speechThreshold && next >= releaseThreshold + 0.025))
                {
                    triggered = true;
                    belowCount = 0;
                    pendingEnd = -1;
                    speech[i] = true;
                }
                continue;
            }

            speech[i] = true;
            if (probability < releaseThreshold)
            {
                if (belowCount == 0) pendingEnd = i;
                belowCount++;
                if (belowCount >= releaseFrames)
                {
                    var end = Math.Max(0, pendingEnd);
                    for (var j = end; j <= i; j++) speech[j] = false;
                    triggered = false;
                    belowCount = 0;
                    pendingEnd = -1;
                }
            }
            else
            {
                belowCount = 0;
                pendingEnd = -1;
            }
        }

        // Soft speech rescue: Silero is primary, but low/whispered words can occasionally sit in
        // the uncertain band. Rescue only sustained voice-shaped energy above the file's own
        // noise floor, not isolated loud objects.
        if (settings.ProtectSoftVoice && analysis.WindowDb.Count > 0)
            RescueSoftSpeech(speech, analysis, speechThreshold);

        return speech;
    }

    private static void RescueSoftSpeech(bool[] speech, AudioAnalysisResult analysis, double speechThreshold)
    {
        var vadWs = analysis.SpeechWindowSeconds;
        var dbWs = analysis.WindowSeconds;
        var finite = analysis.WindowDb.Where(IsFinite).OrderBy(v => v).ToArray();
        if (finite.Length == 0 || dbWs <= 0) return;
        var noiseFloor = Percentile(finite, 0.12);
        var peaks = analysis.WindowPeakDb.Count == analysis.WindowDb.Count ? analysis.WindowPeakDb : analysis.WindowDb;
        var zcr = analysis.WindowZcr.Count == analysis.WindowDb.Count ? analysis.WindowZcr : Enumerable.Repeat(0.08, analysis.WindowDb.Count).ToList();

        var candidate = new bool[speech.Length];
        for (var i = 0; i < speech.Length; i++)
        {
            if (speech[i]) continue;
            var t0 = i * vadWs;
            var t1 = t0 + vadWs;
            var a = Math.Clamp((int)Math.Floor(t0 / dbWs), 0, analysis.WindowDb.Count - 1);
            var b = Math.Clamp((int)Math.Ceiling(t1 / dbWs), a + 1, analysis.WindowDb.Count);
            var avgDb = 0.0;
            var voiceLike = 0;
            var count = 0;
            for (var j = a; j < b; j++)
            {
                avgDb += analysis.WindowDb[j];
                var crest = Math.Max(0, peaks[j] - analysis.WindowDb[j]);
                if (zcr[j] >= 0.006 && zcr[j] <= 0.44 && crest < 16.0) voiceLike++;
                count++;
            }
            avgDb /= Math.Max(1, count);
            var p = i < analysis.SpeechProbability.Count ? analysis.SpeechProbability[i] : 0;
            candidate[i] = p >= Math.Max(0.12, speechThreshold - 0.26)
                           && avgDb >= noiseFloor + 2.2
                           && voiceLike >= Math.Max(1, count / 2);
        }

        var minFrames = Math.Max(2, (int)Math.Ceiling(0.064 / vadWs));
        var i0 = 0;
        while (i0 < candidate.Length)
        {
            if (!candidate[i0]) { i0++; continue; }
            var start = i0;
            while (i0 < candidate.Length && candidate[i0]) i0++;
            var end = i0;
            var touchesSpeech = (start > 0 && speech[start - 1]) || (end < speech.Length && speech[end]);
            if (end - start >= minFrames || touchesSpeech)
                for (var j = start; j < end; j++) speech[j] = true;
        }
    }

    private static void RemoveTransientSpeechIslands(
        bool[] speech,
        IReadOnlyList<double> probabilities,
        double ws,
        double maxDurationSeconds,
        double maxProbabilityForTransient,
        bool protectSoftVoice)
    {
        var i = 0;
        while (i < speech.Length)
        {
            if (!speech[i]) { i++; continue; }
            var start = i;
            while (i < speech.Length && speech[i]) i++;
            var end = i;
            var duration = (end - start) * ws;
            if (duration > maxDurationSeconds) continue;

            var maxP = 0.0;
            var avgP = 0.0;
            for (var j = start; j < end; j++)
            {
                var p = j < probabilities.Count ? probabilities[j] : 0;
                maxP = Math.Max(maxP, p);
                avgP += p;
            }
            avgP /= Math.Max(1, end - start);

            // Never swallow a confident short word. With soft-voice protection, also retain a
            // moderately confident short island.
            if (maxP >= maxProbabilityForTransient) continue;
            if (protectSoftVoice && avgP >= 0.42) continue;

            var boundedBySilence = start == 0 || !speech[start - 1];
            boundedBySilence &= end >= speech.Length || !speech[end];
            if (!boundedBySilence) continue;
            for (var j = start; j < end; j++) speech[j] = false;
        }
    }

    private static List<SilenceSegment> BuildGapRegions(
        IReadOnlyList<bool> speech,
        double ws,
        double duration,
        double minPause,
        double padding)
    {
        var regions = new List<SilenceSegment>();
        var i = 0;
        while (i < speech.Count)
        {
            if (speech[i]) { i++; continue; }
            var start = i;
            while (i < speech.Count && !speech[i]) i++;
            var end = i;

            var rawStart = start * ws;
            var rawEnd = Math.Min(duration, end * ws);
            var rawDuration = rawEnd - rawStart;
            if (rawDuration + 1e-9 < minPause) continue;

            var hasSpeechBefore = start > 0 && speech[start - 1];
            var hasSpeechAfter = end < speech.Count && speech[end];
            var cutStart = hasSpeechBefore ? rawStart + padding : rawStart;
            var cutEnd = hasSpeechAfter ? rawEnd - padding : rawEnd;
            if (cutEnd - cutStart < 0.012)
            {
                cutStart = rawStart;
                cutEnd = rawEnd;
            }
            if (cutEnd - cutStart < 0.008) continue;

            regions.Add(new SilenceSegment
            {
                StartSeconds = Math.Max(0, cutStart),
                EndSeconds = Math.Min(duration, cutEnd),
                IsCut = false,
                IsAutomatic = true,
                IsIgnored = false
            });
        }
        return regions;
    }

    private static void MergeAcrossNonVoiceIslands(
        List<SilenceSegment> regions,
        AudioAnalysisResult analysis,
        double maxIslandSeconds,
        double maxSpeechProbability,
        double maxSustainedSpeechSeconds)
    {
        if (regions.Count < 2 || !HasVad(analysis)) return;
        regions.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
        var ws = analysis.SpeechWindowSeconds;

        for (var i = regions.Count - 1; i > 0; i--)
        {
            var left = regions[i - 1];
            var right = regions[i];
            var gap = right.StartSeconds - left.EndSeconds;
            if (gap <= 0.0005)
            {
                left.EndSeconds = Math.Max(left.EndSeconds, right.EndSeconds);
                regions.RemoveAt(i);
                continue;
            }
            if (gap > maxIslandSeconds) continue;

            var startIndex = Math.Clamp((int)Math.Floor(left.EndSeconds / ws), 0, analysis.SpeechProbability.Count - 1);
            var endIndex = Math.Clamp((int)Math.Ceiling(right.StartSeconds / ws), startIndex + 1, analysis.SpeechProbability.Count);
            var maxP = 0.0;
            var run = 0;
            var longestStrongRun = 0;
            for (var j = startIndex; j < endIndex; j++)
            {
                var p = analysis.SpeechProbability[j];
                maxP = Math.Max(maxP, p);
                if (p >= Math.Max(0.42, maxSpeechProbability - 0.10))
                {
                    run++;
                    longestStrongRun = Math.Max(longestStrongRun, run);
                }
                else run = 0;
            }
            if (maxP >= maxSpeechProbability && longestStrongRun * ws >= maxSustainedSpeechSeconds) continue;

            left.EndSeconds = Math.Max(left.EndSeconds, right.EndSeconds);
            left.IsAutomatic = left.IsAutomatic && right.IsAutomatic;
            regions.RemoveAt(i);
        }
    }

    private static double BuildVadSpeechThreshold(SilenceSettings settings, DetectorPass pass)
    {
        // Higher threshold = more aggressive pause cutting. Manual dB threshold remains useful as
        // a sensitivity control even though dB is no longer the speech detector itself.
        var threshold = settings.SmartThreshold
            ? 0.43
            : 0.43 + (settings.ThresholdDb + 40.0) * 0.008;
        threshold += NoiseProfileOffset(settings.NoiseProfile);
        if (settings.ProtectSoftVoice) threshold -= 0.035;
        if (pass == DetectorPass.Refine) threshold += 0.015;
        if (pass == DetectorPass.FinalSweep) threshold += 0.025;
        return Math.Clamp(threshold, 0.28, 0.62);
    }

    private static void BridgeFalseHoles(bool[] mask, int maxFrames)
    {
        var i = 0;
        while (i < mask.Length)
        {
            if (mask[i]) { i++; continue; }
            var start = i;
            while (i < mask.Length && !mask[i]) i++;
            var end = i;
            if (start > 0 && end < mask.Length && end - start <= maxFrames && mask[start - 1] && mask[end])
                for (var j = start; j < end; j++) mask[j] = true;
        }
    }

    private static bool HasVad(AudioAnalysisResult analysis) =>
        analysis.SpeechProbability.Count > 0 && analysis.SpeechWindowSeconds > 0.001;

    /// <summary>
    /// Emergency fallback only for a legacy/incomplete cache. v3.22 projects are forced through a
    /// fresh VAD scan on upgrade, so normal scans never use this path.
    /// </summary>
    private static List<SilenceSegment> AnalyzeEnergyFallback(
        AudioAnalysisResult analysis,
        SilenceSettings settings,
        IReadOnlyList<SilenceSegment>? previous)
    {
        if (analysis.WindowDb.Count == 0 || analysis.WindowSeconds <= 0) return PreserveOnlyExplicit(previous);
        var finite = analysis.WindowDb.Where(IsFinite).OrderBy(v => v).ToArray();
        if (finite.Length == 0) return PreserveOnlyExplicit(previous);
        var noise = Percentile(finite, 0.12);
        var speech = Percentile(finite, 0.88);
        var threshold = settings.SmartThreshold
            ? noise + Math.Clamp((speech - noise) * 0.38, 3.5, 9.0)
            : settings.ThresholdDb;
        var mask = analysis.WindowDb.Select(x => x >= threshold).ToArray();
        BridgeFalseHoles(mask, Math.Max(1, (int)Math.Ceiling(0.04 / analysis.WindowSeconds)));
        var result = BuildGapRegions(mask, analysis.WindowSeconds, analysis.WindowDb.Count * analysis.WindowSeconds,
            Math.Max(0.04, settings.MinimumSilenceSeconds), Math.Max(0, settings.PaddingSeconds));
        PreserveExplicitOverrides(result, previous);
        return UnionOverlapping(result);
    }

    private static SilenceSegment CloneSegment(SilenceSegment s) => new()
    {
        StartSeconds = s.StartSeconds,
        EndSeconds = s.EndSeconds,
        IsCut = s.IsCut,
        IsAutomatic = s.IsAutomatic,
        IsIgnored = s.IsIgnored
    };

    private static bool IsActive(SilenceSegment segment) => !segment.IsIgnored && segment.EndSeconds > segment.StartSeconds;

    private static List<SilenceSegment> UnionOverlapping(IEnumerable<SilenceSegment> input)
    {
        var sorted = input
            .Where(x => x.EndSeconds > x.StartSeconds + 0.001)
            .OrderBy(x => x.StartSeconds)
            .ThenBy(x => x.EndSeconds)
            .Select(CloneSegment)
            .ToList();
        var result = new List<SilenceSegment>();
        foreach (var seg in sorted)
        {
            if (result.Count == 0 || seg.StartSeconds > result[^1].EndSeconds + 0.0005)
            {
                seg.IsCut = false;
                seg.IsIgnored = false;
                result.Add(seg);
                continue;
            }
            result[^1].EndSeconds = Math.Max(result[^1].EndSeconds, seg.EndSeconds);
            result[^1].IsAutomatic = result[^1].IsAutomatic && seg.IsAutomatic;
        }
        return result;
    }

    private static void SubtractKeptRange(List<SilenceSegment> regions, double keepStart, double keepEnd)
    {
        if (keepEnd <= keepStart) return;
        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (keepEnd <= r.StartSeconds || keepStart >= r.EndSeconds) continue;

            var leftStart = r.StartSeconds;
            var leftEnd = Math.Min(r.EndSeconds, keepStart);
            var rightStart = Math.Max(r.StartSeconds, keepEnd);
            var rightEnd = r.EndSeconds;
            regions.RemoveAt(i);
            if (rightEnd - rightStart >= 0.008) regions.Insert(i, new SilenceSegment
            {
                StartSeconds = rightStart, EndSeconds = rightEnd, IsAutomatic = r.IsAutomatic
            });
            if (leftEnd - leftStart >= 0.008) regions.Insert(i, new SilenceSegment
            {
                StartSeconds = leftStart, EndSeconds = leftEnd, IsAutomatic = r.IsAutomatic
            });
        }
    }

    private static void PreserveExplicitOverrides(List<SilenceSegment> fresh, IReadOnlyList<SilenceSegment>? previous)
    {
        if (previous is null) return;
        foreach (var old in previous.Where(x => !x.IsAutomatic || x.IsIgnored))
        {
            if (old.EndSeconds <= old.StartSeconds) continue;
            if (old.IsIgnored)
            {
                // Keep is absolute. Remove/split any fresh region that would re-cover it.
                SubtractKeptRange(fresh, old.StartSeconds, old.EndSeconds);
                fresh.Add(new SilenceSegment
                {
                    StartSeconds = old.StartSeconds,
                    EndSeconds = old.EndSeconds,
                    IsCut = false,
                    IsAutomatic = false,
                    IsIgnored = true
                });
            }
            else
            {
                fresh.RemoveAll(candidate => Overlap(candidate, old) >= Math.Min(0.10, Math.Max(0.025, old.DurationSeconds * 0.30)));
                fresh.Add(new SilenceSegment
                {
                    StartSeconds = old.StartSeconds,
                    EndSeconds = old.EndSeconds,
                    IsCut = old.IsCut,
                    IsAutomatic = false,
                    IsIgnored = false
                });
            }
        }
    }

    private static List<SilenceSegment> PreserveOnlyExplicit(IReadOnlyList<SilenceSegment>? previous)
    {
        if (previous is null) return new List<SilenceSegment>();
        return previous.Where(x => !x.IsAutomatic || x.IsIgnored).Select(x => new SilenceSegment
        {
            StartSeconds = x.StartSeconds,
            EndSeconds = x.EndSeconds,
            IsCut = x.IsCut,
            IsAutomatic = false,
            IsIgnored = x.IsIgnored
        }).ToList();
    }

    private static double Overlap(SilenceSegment a, SilenceSegment b) =>
        Math.Max(0, Math.Min(a.EndSeconds, b.EndSeconds) - Math.Max(a.StartSeconds, b.StartSeconds));

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Percentile(double[] sorted, double p) =>
        sorted[Math.Clamp((int)Math.Round((sorted.Length - 1) * p), 0, sorted.Length - 1)];
}
