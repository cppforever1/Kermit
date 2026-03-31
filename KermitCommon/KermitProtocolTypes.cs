using System.Collections.ObjectModel;

namespace KermitCommon;

public static class KermitConstants
{
    public const byte StartOfHeader = 0x01;
    public const byte CarriageReturn = 0x0D;
    public const byte DefaultControlQuote = (byte)'#';
    public const byte DefaultEightBitPrefix = (byte)'&';
    public const byte DefaultRepeatPrefix = (byte)'~';
    public const int DefaultPacketSize = 94;
    public const int DefaultWindowSize = 1;
    public const int SequenceModulo = 64;
}

public enum KermitPacketType
{
    SendInit,
    Init,
    FileHeader,
    FileAttributes,
    Data,
    EndOfFile,
    Break,
    Ack,
    Nak,
    Error,
    GenericCommand,
    RemoteCommand,
    ReceiveInit,
    TextHeader,
    Unknown
}

public enum KermitBlockCheckType
{
    Checksum6 = 1,
    Checksum12 = 2,
    Crc16 = 3
}

public enum KermitPacketDirection
{
    Incoming,
    Outgoing
}

public enum KermitSessionState
{
    Created,
    Open,
    Closed,
    Faulted
}

public sealed record KermitNegotiationOptions(
    int MaxPacketLength = KermitConstants.DefaultPacketSize,
    int TimeoutSeconds = 5,
    int PaddingLength = 0,
    byte PaddingCharacter = 0,
    byte EndOfLine = KermitConstants.CarriageReturn,
    byte ControlQuote = KermitConstants.DefaultControlQuote,
    byte EightBitPrefix = KermitConstants.DefaultEightBitPrefix,
    KermitBlockCheckType BlockCheckType = KermitBlockCheckType.Checksum6,
    byte RepeatPrefix = KermitConstants.DefaultRepeatPrefix,
    int WindowSize = KermitConstants.DefaultWindowSize)
{
    public static KermitNegotiationOptions Default { get; } = new();
}

public sealed record KermitFileMetadata(
    string FileName,
    long? Length = null,
    DateTimeOffset? LastWriteTime = null,
    string? Encoding = null,
    string? SystemId = null,
    IReadOnlyDictionary<string, string>? AdditionalAttributes = null);

public sealed class KermitPacket
{
    public KermitPacket(byte sequence, KermitPacketType type, ReadOnlyMemory<byte> data)
    {
        Sequence = (byte)(sequence % KermitConstants.SequenceModulo);
        Type = type;
        Data = data;
    }

    public byte Sequence { get; }

    public KermitPacketType Type { get; }

    public ReadOnlyMemory<byte> Data { get; }

    public string DataAsText => System.Text.Encoding.UTF8.GetString(Data.Span);
}

public class KermitSessionOptions
{
    public KermitNegotiationOptions Negotiation { get; init; } = KermitNegotiationOptions.Default;

    public int MaxRetries { get; init; } = 5;

    public int ResponseTimeoutMilliseconds { get; init; } = 5000;

    public int StreamBufferSize { get; init; } = 4096;
}

public sealed class KermitTransferProgress
{
    public required string FileName { get; init; }

    public long BytesTransferred { get; init; }

    public long? TotalBytes { get; init; }

    public double? Percent => TotalBytes is > 0 ? (double)BytesTransferred / TotalBytes.Value * 100.0d : null;
}

public class KermitPacketEventArgs : EventArgs
{
    public KermitPacketEventArgs(KermitPacket packet, KermitPacketDirection direction)
    {
        Packet = packet;
        Direction = direction;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public KermitPacket Packet { get; }

    public KermitPacketDirection Direction { get; }

    public DateTimeOffset Timestamp { get; }
}

public sealed class SendInitEventArgs : KermitPacketEventArgs
{
    public SendInitEventArgs(KermitPacket packet, KermitPacketDirection direction, KermitNegotiationOptions negotiation)
        : base(packet, direction)
    {
        Negotiation = negotiation;
    }

    public KermitNegotiationOptions Negotiation { get; }
}

public sealed class FileHeaderEventArgs : KermitPacketEventArgs
{
    public FileHeaderEventArgs(KermitPacket packet, KermitPacketDirection direction, KermitFileMetadata metadata)
        : base(packet, direction)
    {
        Metadata = metadata;
    }

    public KermitFileMetadata Metadata { get; }
}

public sealed class FileAttributesEventArgs : KermitPacketEventArgs
{
    public FileAttributesEventArgs(KermitPacket packet, KermitPacketDirection direction, IReadOnlyDictionary<string, string> attributes)
        : base(packet, direction)
    {
        Attributes = attributes;
    }

    public IReadOnlyDictionary<string, string> Attributes { get; }
}

public sealed class DataEventArgs : KermitPacketEventArgs
{
    public DataEventArgs(KermitPacket packet, KermitPacketDirection direction)
        : base(packet, direction)
    {
        Payload = packet.Data.ToArray();
    }

    public byte[] Payload { get; }
}

public sealed class EndOfFileEventArgs : KermitPacketEventArgs
{
    public EndOfFileEventArgs(KermitPacket packet, KermitPacketDirection direction, string status)
        : base(packet, direction)
    {
        Status = status;
    }

    public string Status { get; }
}

public sealed class BreakEventArgs : KermitPacketEventArgs
{
    public BreakEventArgs(KermitPacket packet, KermitPacketDirection direction, string reason)
        : base(packet, direction)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

public sealed class AckEventArgs : KermitPacketEventArgs
{
    public AckEventArgs(KermitPacket packet, KermitPacketDirection direction, string message)
        : base(packet, direction)
    {
        Message = message;
    }

    public string Message { get; }
}

public sealed class NakEventArgs : KermitPacketEventArgs
{
    public NakEventArgs(KermitPacket packet, KermitPacketDirection direction, string message)
        : base(packet, direction)
    {
        Message = message;
    }

    public string Message { get; }
}

public sealed class ErrorEventArgs : KermitPacketEventArgs
{
    public ErrorEventArgs(KermitPacket packet, KermitPacketDirection direction, string message)
        : base(packet, direction)
    {
        Message = message;
    }

    public string Message { get; }
}

public sealed class GenericCommandEventArgs : KermitPacketEventArgs
{
    public GenericCommandEventArgs(KermitPacket packet, KermitPacketDirection direction, string command)
        : base(packet, direction)
    {
        Command = command;
    }

    public string Command { get; }
}

public sealed class RemoteCommandEventArgs : KermitPacketEventArgs
{
    public RemoteCommandEventArgs(KermitPacket packet, KermitPacketDirection direction, string command)
        : base(packet, direction)
    {
        Command = command;
    }

    public string Command { get; }
}

public sealed class ReceiveInitEventArgs : KermitPacketEventArgs
{
    public ReceiveInitEventArgs(KermitPacket packet, KermitPacketDirection direction, string request)
        : base(packet, direction)
    {
        Request = request;
    }

    public string Request { get; }
}

public sealed class TextHeaderEventArgs : KermitPacketEventArgs
{
    public TextHeaderEventArgs(KermitPacket packet, KermitPacketDirection direction, string text)
        : base(packet, direction)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed class KermitProgressEventArgs : EventArgs
{
    public KermitProgressEventArgs(KermitTransferProgress progress)
    {
        Progress = progress;
    }

    public KermitTransferProgress Progress { get; }
}

public sealed class KermitFileTransferEventArgs : EventArgs
{
    public KermitFileTransferEventArgs(KermitFileMetadata metadata, string fullPath)
    {
        Metadata = metadata;
        FullPath = fullPath;
    }

    public KermitFileMetadata Metadata { get; }

    public string FullPath { get; }
}

public static class KermitPacketTypeExtensions
{
    private static readonly ReadOnlyDictionary<KermitPacketType, char> PacketToCode = new(
        new Dictionary<KermitPacketType, char>
        {
            [KermitPacketType.SendInit] = 'S',
            [KermitPacketType.Init] = 'I',
            [KermitPacketType.FileHeader] = 'F',
            [KermitPacketType.FileAttributes] = 'A',
            [KermitPacketType.Data] = 'D',
            [KermitPacketType.EndOfFile] = 'Z',
            [KermitPacketType.Break] = 'B',
            [KermitPacketType.Ack] = 'Y',
            [KermitPacketType.Nak] = 'N',
            [KermitPacketType.Error] = 'E',
            [KermitPacketType.GenericCommand] = 'G',
            [KermitPacketType.RemoteCommand] = 'C',
            [KermitPacketType.ReceiveInit] = 'R',
            [KermitPacketType.TextHeader] = 'X'
        });

    private static readonly ReadOnlyDictionary<char, KermitPacketType> CodeToPacket = new(
        PacketToCode.ToDictionary(static pair => pair.Value, static pair => pair.Key));

    public static char ToCode(this KermitPacketType type) => PacketToCode.TryGetValue(type, out var code) ? code : '?';

    public static KermitPacketType FromCode(char code) => CodeToPacket.TryGetValue(code, out var type) ? type : KermitPacketType.Unknown;
}