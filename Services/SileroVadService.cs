using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CutFlow.Services;

/// <summary>
/// Small stateful wrapper around the official Silero VAD ONNX model.
/// CutFlow feeds 16 kHz mono PCM in 512-sample (32 ms) chunks. The model keeps a
/// recurrent state plus a 64-sample context window, exactly like Silero's official
/// ONNX wrapper. One instance is used for one audio stream and then reset/disposed.
/// </summary>
public sealed class SileroVadService : IDisposable
{
    public const int SampleRate = 16000;
    public const int ChunkSamples = 512;
    public const int ContextSamples = 64;
    public const double WindowSeconds = ChunkSamples / (double)SampleRate;

    private readonly InferenceSession _session;
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _context = new float[ContextSamples];
    private bool _disposed;

    public SileroVadService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("CutFlow.Assets.silero_vad.onnx")
            ?? throw new InvalidOperationException("CutFlow's voice activity model is missing. Rebuild CutFlow with build.bat so the official Silero VAD model is embedded.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        using var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        _session = new InferenceSession(memory.ToArray(), options);

        if (!_session.InputMetadata.ContainsKey("input") ||
            !_session.InputMetadata.ContainsKey("state") ||
            !_session.InputMetadata.ContainsKey("sr"))
            throw new InvalidOperationException("The embedded Silero VAD model has an unexpected input layout.");
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_context);
    }

    public float ProcessChunk(ReadOnlySpan<float> samples)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SileroVadService));
        if (samples.Length != ChunkSamples)
            throw new ArgumentException($"Silero VAD requires exactly {ChunkSamples} samples per chunk.", nameof(samples));

        var modelInput = new float[ContextSamples + ChunkSamples];
        Array.Copy(_context, 0, modelInput, 0, ContextSamples);
        samples.CopyTo(modelInput.AsSpan(ContextSamples));

        var inputTensor = new DenseTensor<float>(modelInput, new[] { 1, modelInput.Length });
        var stateTensor = new DenseTensor<float>(_state.ToArray(), new[] { 2, 1, 128 });
        var sampleRateTensor = new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 });
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", inputTensor),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", sampleRateTensor)
        };

        using var outputs = _session.Run(inputs);
        var probability = outputs.FirstOrDefault(x => x.Name.Equals("output", StringComparison.OrdinalIgnoreCase))?.AsTensor<float>().ToArray().FirstOrDefault()
                          ?? outputs.First().AsTensor<float>().ToArray().FirstOrDefault();
        var stateOutput = outputs.FirstOrDefault(x => x.Name.Equals("stateN", StringComparison.OrdinalIgnoreCase));
        if (stateOutput is null)
            stateOutput = outputs.Skip(1).FirstOrDefault();
        if (stateOutput is null)
            throw new InvalidOperationException("Silero VAD did not return its recurrent state.");

        var nextState = stateOutput.AsTensor<float>().ToArray();
        if (nextState.Length != _state.Length)
            throw new InvalidOperationException("Silero VAD returned an unexpected recurrent-state size.");
        Array.Copy(nextState, _state, _state.Length);

        // Official wrapper carries the last 64 samples into the next 16 kHz call.
        for (var i = 0; i < ContextSamples; i++)
            _context[i] = modelInput[modelInput.Length - ContextSamples + i];

        return Math.Clamp(probability, 0f, 1f);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
