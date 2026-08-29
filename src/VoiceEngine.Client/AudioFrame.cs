using System.Buffers.Binary;

namespace VoiceEngine.Client;

public enum AudioFrameKind : byte
{
    Input = 1,
    Output = 2
}

public sealed record AudioFrame(AudioFrameKind Kind, byte Flags, uint Sequence, ulong TurnId, byte[] Pcm)
{
    public const byte ProtocolVersion = 1;
    public const int HeaderSize = 16;
    public const int InputPcmBytes = 640;
    public const int OutputPcmBytes = 960;

    public byte[] Encode()
    {
        ArgumentNullException.ThrowIfNull(Pcm);
        if ((Pcm.Length & 1) != 0) throw new VoiceProtocolException("PCM16 data must have an even byte count.");

        var result = new byte[HeaderSize + Pcm.Length];
        result[0] = ProtocolVersion;
        result[1] = (byte)Kind;
        result[2] = Flags;
        result[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), TurnId);
        Pcm.CopyTo(result, HeaderSize);
        return result;
    }

    public static AudioFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new VoiceProtocolException("Binary frame is shorter than 16 bytes.");
        if (data[0] != ProtocolVersion) throw new VoiceProtocolException($"Unsupported protocol version {data[0]}.");
        if (data[3] != 0) throw new VoiceProtocolException("Reserved header byte must be zero.");
        if (!Enum.IsDefined(typeof(AudioFrameKind), data[1])) throw new VoiceProtocolException($"Unknown frame kind {data[1]}.");
        if (((data.Length - HeaderSize) & 1) != 0) throw new VoiceProtocolException("PCM16 data must have an even byte count.");

        return new AudioFrame(
            (AudioFrameKind)data[1],
            data[2],
            BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            data[HeaderSize..].ToArray());
    }
}
