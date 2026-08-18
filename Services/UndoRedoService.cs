using CutFlow.Models;

namespace CutFlow.Services;

public sealed class UndoRedoService
{
    private readonly Stack<ProjectSnapshot> _undo = new();
    private readonly Stack<ProjectSnapshot> _redo = new();
    private const int MaxHistory = 80;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Push(CutFlowProject project)
    {
        _undo.Push(CreateSnapshot(project));
        _redo.Clear();
        TrimHistory();
    }

    public bool Undo(CutFlowProject project)
    {
        if (_undo.Count == 0) return false;
        _redo.Push(CreateSnapshot(project));
        ApplySnapshot(project, _undo.Pop());
        return true;
    }

    public bool Redo(CutFlowProject project)
    {
        if (_redo.Count == 0) return false;
        _undo.Push(CreateSnapshot(project));
        ApplySnapshot(project, _redo.Pop());
        return true;
    }

    public ProjectSnapshot Capture(CutFlowProject project) => CreateSnapshot(project);

    public void PushSnapshot(ProjectSnapshot snapshot)
    {
        _undo.Push(CloneSnapshot(snapshot));
        _redo.Clear();
        TrimHistory();
    }

    public void Restore(CutFlowProject project, ProjectSnapshot snapshot) => ApplySnapshot(project, snapshot);

    private void TrimHistory()
    {
        if (_undo.Count <= MaxHistory) return;
        var trimmed = _undo.Reverse().TakeLast(MaxHistory).Reverse().ToList();
        _undo.Clear();
        foreach (var item in trimmed.Reverse<ProjectSnapshot>()) _undo.Push(item);
    }

    private static ProjectSnapshot CloneSnapshot(ProjectSnapshot snapshot) => new()
    {
        SourcePath = snapshot.SourcePath,
        OriginalSourcePath = snapshot.OriginalSourcePath,
        Media = CloneMedia(snapshot.Media),
        Silence = CloneSilence(snapshot.Silence),
        Segments = snapshot.Segments.Select(CloneSegment).ToList(),
        WaveformPeaks = snapshot.WaveformPeaks.ToList(),
        AnalysisWindowDb = snapshot.AnalysisWindowDb.ToList(),
        AnalysisWindowPeakDb = snapshot.AnalysisWindowPeakDb.ToList(),
        AnalysisWindowZcr = snapshot.AnalysisWindowZcr.ToList(),
        VadSpeechProbability = snapshot.VadSpeechProbability.ToList(),
        VadWindowSeconds = snapshot.VadWindowSeconds,
        AnalysisWindowSeconds = snapshot.AnalysisWindowSeconds,
        SuggestedThresholdDb = snapshot.SuggestedThresholdDb,
        LastPlayheadSeconds = snapshot.LastPlayheadSeconds
    };

    private static ProjectSnapshot CreateSnapshot(CutFlowProject project) => new()
    {
        SourcePath = project.SourcePath,
        OriginalSourcePath = project.OriginalSourcePath,
        Media = CloneMedia(project.Media),
        Silence = CloneSilence(project.Silence),
        Segments = project.Segments.Select(CloneSegment).ToList(),
        WaveformPeaks = project.WaveformPeaks.ToList(),
        AnalysisWindowDb = project.AnalysisWindowDb.ToList(),
        AnalysisWindowPeakDb = project.AnalysisWindowPeakDb.ToList(),
        AnalysisWindowZcr = project.AnalysisWindowZcr.ToList(),
        VadSpeechProbability = project.VadSpeechProbability.ToList(),
        VadWindowSeconds = project.VadWindowSeconds,
        AnalysisWindowSeconds = project.AnalysisWindowSeconds,
        SuggestedThresholdDb = project.SuggestedThresholdDb,
        LastPlayheadSeconds = project.LastPlayheadSeconds
    };

    private static void ApplySnapshot(CutFlowProject project, ProjectSnapshot snapshot)
    {
        project.SourcePath = snapshot.SourcePath;
        project.OriginalSourcePath = snapshot.OriginalSourcePath;
        project.Media = CloneMedia(snapshot.Media);
        project.Silence = CloneSilence(snapshot.Silence);
        project.Segments = snapshot.Segments.Select(CloneSegment).ToList();
        project.WaveformPeaks = snapshot.WaveformPeaks.ToList();
        project.AnalysisWindowDb = snapshot.AnalysisWindowDb.ToList();
        project.AnalysisWindowPeakDb = snapshot.AnalysisWindowPeakDb.ToList();
        project.AnalysisWindowZcr = snapshot.AnalysisWindowZcr.ToList();
        project.VadSpeechProbability = snapshot.VadSpeechProbability.ToList();
        project.VadWindowSeconds = snapshot.VadWindowSeconds;
        project.AnalysisWindowSeconds = snapshot.AnalysisWindowSeconds;
        project.SuggestedThresholdDb = snapshot.SuggestedThresholdDb;
        project.LastPlayheadSeconds = snapshot.LastPlayheadSeconds;
    }

    private static MediaInfo CloneMedia(MediaInfo media) => new()
    {
        DurationSeconds = media.DurationSeconds,
        Width = media.Width,
        Height = media.Height,
        FrameRate = media.FrameRate,
        HasVideo = media.HasVideo,
        HasAudio = media.HasAudio,
        VideoCodec = media.VideoCodec,
        AudioCodec = media.AudioCodec
    };

    private static SilenceSettings CloneSilence(SilenceSettings s) => new()
    {
        SmartThreshold = s.SmartThreshold,
        ThresholdDb = s.ThresholdDb,
        MinimumSilenceSeconds = s.MinimumSilenceSeconds,
        PaddingSeconds = s.PaddingSeconds,
        RemoveBlipsSeconds = s.RemoveBlipsSeconds,
        NoiseProfile = s.NoiseProfile,
        ProtectSoftVoice = s.ProtectSoftVoice,
        ReduceMouthClicks = s.ReduceMouthClicks,
        ReduceImpulsiveNoise = s.ReduceImpulsiveNoise,
        ReduceBackgroundSqueaks = s.ReduceBackgroundSqueaks,
        BlendCutEdges = s.BlendCutEdges
    };

    private static SilenceSegment CloneSegment(SilenceSegment s) => new()
    {
        StartSeconds = s.StartSeconds,
        EndSeconds = s.EndSeconds,
        IsCut = s.IsCut,
        IsAutomatic = s.IsAutomatic,
        IsIgnored = s.IsIgnored
    };
}
