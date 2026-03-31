using System.Buffers;
using System.IO.Ports;
using System.Text;

namespace KermitCommon;

public static class KermitCodec
{
    public static byte ToPrintable(int value) => (byte)(value + 32);

    public static int FromPrintable(byte value) => value - 32;

    public static byte Control(byte value) => (byte)(value ^ 0x40);

    public static byte[] EncodePacket(KermitPacket packet, KermitNegotiationOptions options)
    {
        var encodedData = EncodeData(packet.Data.Span, options);
        var lengthValue = 3 + encodedData.Length + GetBlockCheckCharCount(options.BlockCheckType);
        if (lengthValue > 94)
        {
            throw new InvalidOperationException("Standard Kermit packet length exceeded 94 bytes. Reduce payload size or negotiate long packets.");
        }

        var frame = new List<byte>(lengthValue + 2)
        {
            KermitConstants.StartOfHeader,
            ToPrintable(lengthValue),
            ToPrintable(packet.Sequence),
            (byte)packet.Type.ToCode()
        };

        frame.AddRange(encodedData);
        var checkBytes = ComputeBlockCheck(frame.Skip(1).ToArray(), options.BlockCheckType);
        frame.AddRange(checkBytes);
        frame.Add(options.EndOfLine);
        return frame.ToArray();
    }

    public static KermitPacket DecodePacket(ReadOnlySpan<byte> frame, KermitNegotiationOptions options)
    {
        if (frame.Length < 6)
        {
            throw new InvalidDataException("Frame too short.");
        }

        if (frame[0] != KermitConstants.StartOfHeader)
        {
            throw new InvalidDataException("Invalid start of header.");
        }

        var length = FromPrintable(frame[1]);
        var blockCheckChars = GetBlockCheckCharCount(options.BlockCheckType);
        var expectedLength = length + 2;
        if (frame.Length != expectedLength)
        {
            throw new InvalidDataException($"Invalid frame size. Expected {expectedLength}, got {frame.Length}.");
        }

        var body = frame[1..^1];
        var receivedBlockCheck = body[^blockCheckChars..].ToArray();
        var payloadForChecksum = body[..^blockCheckChars].ToArray();
        var expectedBlockCheck = ComputeBlockCheck(payloadForChecksum, options.BlockCheckType);
        if (!receivedBlockCheck.SequenceEqual(expectedBlockCheck))
        {
            throw new InvalidDataException("Block check validation failed.");
        }

        var sequence = (byte)FromPrintable(frame[2]);
        var type = KermitPacketTypeExtensions.FromCode((char)frame[3]);
        var data = DecodeData(frame[4..^(1 + blockCheckChars)], options);
        return new KermitPacket(sequence, type, data);
    }

    public static byte[] EncodeNegotiation(KermitNegotiationOptions options)
    {
        return
        [
            ToPrintable(Math.Clamp(options.MaxPacketLength, 10, 94)),
            ToPrintable(Math.Clamp(options.TimeoutSeconds, 1, 94)),
            ToPrintable(Math.Clamp(options.PaddingLength, 0, 94)),
            options.PaddingCharacter == 0 ? ToPrintable(0) : Control(options.PaddingCharacter),
            options.EndOfLine,
            options.ControlQuote,
            options.EightBitPrefix,
            ToPrintable((int)options.BlockCheckType),
            options.RepeatPrefix,
            ToPrintable(Math.Clamp(options.WindowSize, 1, 31))
        ];
    }

    public static KermitNegotiationOptions DecodeNegotiation(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return KermitNegotiationOptions.Default;
        }

        return new KermitNegotiationOptions(
            MaxPacketLength: Math.Clamp(FromPrintable(data[0]), 10, 94),
            TimeoutSeconds: Math.Clamp(FromPrintable(data[1]), 1, 94),
            PaddingLength: Math.Clamp(FromPrintable(data[2]), 0, 94),
            PaddingCharacter: Control(data[3]),
            EndOfLine: data[4],
            ControlQuote: data[5],
            EightBitPrefix: data[6],
            BlockCheckType: Enum.IsDefined((KermitBlockCheckType)FromPrintable(data[7]))
                ? (KermitBlockCheckType)FromPrintable(data[7])
                : KermitBlockCheckType.Checksum6,
            RepeatPrefix: data[8],
            WindowSize: Math.Clamp(FromPrintable(data[9]), 1, 31));
    }

    public static IReadOnlyDictionary<string, string> DecodeAttributes(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                dictionary[parts[0]] = parts[1];
            }
        }

        return dictionary;
    }

    public static byte[] EncodeAttributes(KermitFileMetadata metadata)
    {
        var items = new List<string>();
        if (metadata.Length is not null)
        {
            items.Add($"size={metadata.Length.Value}");
        }

        if (metadata.LastWriteTime is not null)
        {
            items.Add($"date={metadata.LastWriteTime.Value:O}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Encoding))
        {
            items.Add($"encoding={metadata.Encoding}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.SystemId))
        {
            items.Add($"system={metadata.SystemId}");
        }

        if (metadata.AdditionalAttributes is not null)
        {
            items.AddRange(metadata.AdditionalAttributes.Select(static pair => $"{pair.Key}={pair.Value}"));
        }

        return Encoding.UTF8.GetBytes(string.Join(';', items));
    }

    public static async ValueTask<KermitPacket> ReadPacketAsync(Stream stream, KermitNegotiationOptions options, CancellationToken cancellationToken)
    {
        while (true)
        {
            var start = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (start == -1)
            {
                throw new EndOfStreamException();
            }

            if ((byte)start != KermitConstants.StartOfHeader)
            {
                continue;
            }

            var lengthByte = await ReadByteRequiredAsync(stream, cancellationToken).ConfigureAwait(false);
            var length = FromPrintable(lengthByte);
            if (length <= 0)
            {
                continue;
            }

            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = 0;
                while (read < length)
                {
                    var chunk = await stream.ReadAsync(rented.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
                    if (chunk == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    read += chunk;
                }

                var frame = new byte[length + 2];
                frame[0] = KermitConstants.StartOfHeader;
                frame[1] = lengthByte;
                Array.Copy(rented, 0, frame, 2, length);
                return DecodePacket(frame, options);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }

    private static async ValueTask<byte> ReadByteRequiredAsync(Stream stream, CancellationToken cancellationToken)
    {
        var value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)value;
    }

    private static byte[] EncodeData(ReadOnlySpan<byte> data, KermitNegotiationOptions options)
    {
        var output = new List<byte>(data.Length * 2);
        foreach (var value in data)
        {
            if ((value & 0x80) != 0)
            {
                output.Add(options.EightBitPrefix);
                AppendQuoted(output, (byte)(value & 0x7F), options);
                continue;
            }

            AppendQuoted(output, value, options);
        }

        return output.ToArray();
    }

    private static byte[] DecodeData(ReadOnlySpan<byte> data, KermitNegotiationOptions options)
    {
        var output = new List<byte>(data.Length);
        for (var index = 0; index < data.Length; index++)
        {
            var current = data[index];
            if (current == options.EightBitPrefix)
            {
                index++;
                if (index >= data.Length)
                {
                    throw new InvalidDataException("Unexpected end of eight-bit quoted sequence.");
                }

                output.Add((byte)(DecodeQuoted(data, ref index, options) | 0x80));
                continue;
            }

            if (current == options.ControlQuote)
            {
                output.Add(DecodeQuoted(data, ref index, options));
                continue;
            }

            output.Add(current);
        }

        return output.ToArray();
    }

    private static void AppendQuoted(List<byte> output, byte value, KermitNegotiationOptions options)
    {
        var requiresQuote = value < 32 || value == 127 || value == options.ControlQuote || value == options.RepeatPrefix || value == options.EightBitPrefix;
        if (!requiresQuote)
        {
            output.Add(value);
            return;
        }

        output.Add(options.ControlQuote);
        output.Add(Control(value));
    }

    private static byte DecodeQuoted(ReadOnlySpan<byte> data, ref int index, KermitNegotiationOptions options)
    {
        if (data[index] == options.ControlQuote)
        {
            index++;
        }

        if (index >= data.Length)
        {
            throw new InvalidDataException("Unexpected end of control quoted sequence.");
        }

        return Control(data[index]);
    }

    private static byte[] ComputeBlockCheck(ReadOnlySpan<byte> payload, KermitBlockCheckType blockCheckType)
    {
        return blockCheckType switch
        {
            KermitBlockCheckType.Checksum6 => [ToPrintable(ComputeChecksum6(payload))],
            KermitBlockCheckType.Checksum12 => ComputeChecksum12Printable(payload),
            KermitBlockCheckType.Crc16 => ComputeCrc16Printable(payload),
            _ => throw new ArgumentOutOfRangeException(nameof(blockCheckType))
        };
    }

    private static int GetBlockCheckCharCount(KermitBlockCheckType blockCheckType) => blockCheckType switch
    {
        KermitBlockCheckType.Checksum6 => 1,
        KermitBlockCheckType.Checksum12 => 2,
        KermitBlockCheckType.Crc16 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(blockCheckType))
    };

    private static int ComputeChecksum6(ReadOnlySpan<byte> payload)
    {
        var sum = 0;
        foreach (var value in payload)
        {
            sum += value;
        }

        return (sum + ((sum & 0xC0) >> 6)) & 0x3F;
    }

    private static byte[] ComputeChecksum12Printable(ReadOnlySpan<byte> payload)
    {
        var sum = 0;
        foreach (var value in payload)
        {
            sum += value;
        }

        sum &= 0x0FFF;
        return [ToPrintable((sum >> 6) & 0x3F), ToPrintable(sum & 0x3F)];
    }

    private static byte[] ComputeCrc16Printable(ReadOnlySpan<byte> payload)
    {
        ushort crc = 0;
        foreach (var value in payload)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }

        return [ToPrintable((crc >> 12) & 0x0F), ToPrintable((crc >> 6) & 0x3F), ToPrintable(crc & 0x3F)];
    }
}

public interface IKermitTransport : IAsyncDisposable
{
    Stream Stream { get; }

    bool IsOpen { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);
}

public sealed class SerialPortTransport : IKermitTransport
{
    private readonly SerialPort _serialPort;

    public SerialPortTransport(string portName, int baudRate = 115200, Parity parity = Parity.None, int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = SerialPort.InfiniteTimeout
        };
    }

    public Stream Stream => _serialPort.BaseStream;

    public bool IsOpen => _serialPort.IsOpen;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_serialPort.IsOpen)
        {
            _serialPort.Open();
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_serialPort.IsOpen)
        {
            _serialPort.Close();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _serialPort.Dispose();
        return ValueTask.CompletedTask;
    }
}