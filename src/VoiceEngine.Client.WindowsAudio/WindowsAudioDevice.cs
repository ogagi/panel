using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VoiceEngine.Client;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;

namespace VoiceEngine.Client.WindowsAudio;

public sealed class VoiceAudioDeviceException(string message, Exception? inner = null) : VoiceClientException(message, inner);

public sealed class WindowsAudioDevice : IAsyncDisposable
{
    private readonly AudioGraph _captureGraph;
    private readonly AudioDeviceInputNode _captureInput;
    private readonly AudioFrameOutputNode _captureOutput;
    private readonly AudioGraph _playbackGraph;
    private readonly AudioDeviceOutputNode _playbackOutput;
    private readonly AudioFrameInputNode _playbackInput;
    private readonly object _captureGate = new();
    private readonly object _playbackGate = new();
    private readonly List<byte> _captureBytes = new(AudioFrame.InputPcmBytes * 2);
    private readonly Dictionary<Windows.Media.AudioFrame, (uint Sequence, long Generation)> _submittedFrames = [];
    private long _playbackGeneration;
    private uint? _lastReceivedSequence;
    private bool _started;
    private int _disposed;

    private WindowsAudioDevice(AudioGraph captureGraph, AudioDeviceInputNode captureInput, AudioFrameOutputNode captureOutput,
        AudioGraph playbackGraph, AudioDeviceOutputNode playbackOutput, AudioFrameInputNode playbackInput)
    {
        _captureGraph = captureGraph;
        _captureInput = captureInput;
        _captureOutput = captureOutput;
        _playbackGraph = playbackGraph;
        _playbackOutput = playbackOutput;
        _playbackInput = playbackInput;
        _captureGraph.QuantumProcessed += CaptureQuantumProcessed;
        _playbackInput.AudioFrameCompleted += PlaybackFrameCompleted;
    }

    public event Action<ReadOnlyMemory<byte>>? InputFrameReady;
    public event Action<uint>? OutputFrameConsumed;
    public event Action<VoiceAudioDeviceException>? AudioDeviceFailed;

    public static async Task<WindowsAudioDevice> CreateAsync()
    {
        AudioGraph? captureGraph = null;
        AudioGraph? playbackGraph = null;
        try
        {
            captureGraph = await CreateGraphAsync(16_000).ConfigureAwait(false);
            // AudioGraph captures in its internal float format even when the graph settings request
            // PCM. Let the device node negotiate that graph format and convert at the protocol edge.
            var captureFormat = captureGraph.EncodingProperties;
            var captureInputResult = await captureGraph.CreateDeviceInputNodeAsync(MediaCategory.Communications,
                captureFormat).AsTask().ConfigureAwait(false);
            if (captureInputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new VoiceAudioDeviceException($"Microphone initialization failed: {captureInputResult.Status}.");
            var captureOutput = captureGraph.CreateFrameOutputNode(captureFormat);
            captureInputResult.DeviceInputNode.AddOutgoingConnection(captureOutput);

            playbackGraph = await CreateGraphAsync(24_000).ConfigureAwait(false);
            var playbackOutputResult = await playbackGraph.CreateDeviceOutputNodeAsync().AsTask().ConfigureAwait(false);
            if (playbackOutputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new VoiceAudioDeviceException($"Audio output initialization failed: {playbackOutputResult.Status}.");
            var playbackInput = playbackGraph.CreateFrameInputNode(AudioEncodingProperties.CreatePcm(24_000, 1, 16));
            playbackInput.AddOutgoingConnection(playbackOutputResult.DeviceOutputNode);

            return new WindowsAudioDevice(captureGraph, captureInputResult.DeviceInputNode, captureOutput,
                playbackGraph, playbackOutputResult.DeviceOutputNode, playbackInput);
        }
        catch
        {
            captureGraph?.Dispose();
            playbackGraph?.Dispose();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_started) return;
        _playbackGraph.Start();
        _captureGraph.Start();
        _captureInput.Start();
        _playbackInput.Start();
        _started = true;
    }

    public void SetMuted(bool muted)
    {
        if (muted) _captureInput.Stop(); else if (_started) _captureInput.Start();
    }

    public ValueTask QueueOutputAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (frame.Kind != AudioFrameKind.Output) throw new ArgumentException("Only output frames can be played.", nameof(frame));
        if (frame.Pcm.Length != AudioFrame.OutputPcmBytes) throw new VoiceProtocolException("Output audio must be exactly 960 bytes (20 ms)." );
        var audioFrame = CreatePlaybackFrame(frame.Pcm);
        lock (_playbackGate)
        {
            _lastReceivedSequence = frame.Sequence;
            _submittedFrames.Add(audioFrame, (frame.Sequence, _playbackGeneration));
            try { _playbackInput.AddFrame(audioFrame); }
            catch
            {
                _submittedFrames.Remove(audioFrame);
                audioFrame.Dispose();
                throw;
            }
        }
        return ValueTask.CompletedTask;
    }

    public uint? ClearOutput()
    {
        uint? sequence;
        lock (_playbackGate)
        {
            _playbackGeneration++;
            sequence = _lastReceivedSequence;
        }
        _playbackInput.DiscardQueuedFrames();
        return sequence;
    }

    private static async Task<AudioGraph> CreateGraphAsync(uint sampleRate)
    {
        var settings = new AudioGraphSettings(AudioRenderCategory.Communications)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
            EncodingProperties = AudioEncodingProperties.CreatePcm(sampleRate, 1, 16)
        };
        var result = await AudioGraph.CreateAsync(settings).AsTask().ConfigureAwait(false);
        if (result.Status != AudioGraphCreationStatus.Success)
            throw new VoiceAudioDeviceException($"Audio graph initialization failed: {result.Status}.");
        return result.Graph;
    }

    private void CaptureQuantumProcessed(AudioGraph sender, object args)
    {
        try { CaptureQuantumProcessedCore(); }
        catch (Exception exception) { AudioDeviceFailed?.Invoke(new VoiceAudioDeviceException("Microphone capture failed.", exception)); }
    }

    private unsafe void CaptureQuantumProcessedCore()
    {
        using var frame = _captureOutput.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
        var bytes = new ReadOnlySpan<byte>(data, Math.Min(checked((int)buffer.Length), checked((int)capacity)));
        if ((bytes.Length & 3) != 0)
            throw new VoiceAudioDeviceException($"Unexpected capture buffer length {bytes.Length} for float audio.");

        var protocolBytes = new byte[bytes.Length / 2];
        for (var sourceOffset = 0; sourceOffset < bytes.Length; sourceOffset += sizeof(float))
        {
            var sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[sourceOffset..]));
            var scaled = sample <= -1f ? short.MinValue
                : sample >= 1f ? short.MaxValue
                : (short)MathF.Round(sample * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(protocolBytes.AsSpan(sourceOffset / 2), scaled);
        }

        List<byte[]> ready = [];
        lock (_captureGate)
        {
            _captureBytes.AddRange(protocolBytes);
            while (_captureBytes.Count >= AudioFrame.InputPcmBytes)
            {
                ready.Add(_captureBytes.GetRange(0, AudioFrame.InputPcmBytes).ToArray());
                _captureBytes.RemoveRange(0, AudioFrame.InputPcmBytes);
            }
        }
        foreach (var pcm in ready) InputFrameReady?.Invoke(pcm);
    }

    private static unsafe Windows.Media.AudioFrame CreatePlaybackFrame(ReadOnlySpan<byte> pcm)
    {
        var output = new Windows.Media.AudioFrame(checked((uint)pcm.Length));
        try
        {
            using var buffer = output.LockBuffer(AudioBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var data, out var capacity);
            var destination = new Span<byte>(data, Math.Min(pcm.Length, checked((int)capacity)));
            pcm[..destination.Length].CopyTo(destination);
            buffer.Length = checked((uint)destination.Length);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void PlaybackFrameCompleted(AudioFrameInputNode sender, AudioFrameCompletedEventArgs args)
    {
        (uint Sequence, long Generation) completed;
        lock (_playbackGate)
        {
            if (!_submittedFrames.Remove(args.Frame, out completed))
            {
                args.Frame.Dispose();
                return;
            }
        }
        args.Frame.Dispose();
        if (completed.Generation == Volatile.Read(ref _playbackGeneration))
            OutputFrameConsumed?.Invoke(completed.Sequence);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _started = false;
        _captureGraph.Stop();
        _playbackGraph.Stop();
        _captureGraph.QuantumProcessed -= CaptureQuantumProcessed;
        _playbackInput.AudioFrameCompleted -= PlaybackFrameCompleted;
        _captureInput.Dispose();
        _captureOutput.Dispose();
        _captureGraph.Dispose();
        _playbackInput.Dispose();
        _playbackOutput.Dispose();
        _playbackGraph.Dispose();
        lock (_playbackGate) _submittedFrames.Clear();
        return ValueTask.CompletedTask;
    }
}

[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
