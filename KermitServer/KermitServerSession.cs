using System.Collections.Concurrent;
using System.Text;
using KermitCommon;

namespace KermitServer;

public sealed class KermitServerOptions : KermitSessionOptions
{
    public string RootDirectory { get; init; } = Directory.GetCurrentDirectory();
}

public sealed class KermitServerSession : KermitSessionBase
{
    private readonly KermitServerOptions _options;
    private readonly ConcurrentDictionary<string, Task> _outgoingTransfers = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _uploadStream;
    private KermitFileMetadata? _uploadMetadata;
    private string? _uploadPath;
    private long _uploadedBytes;

    public KermitServerSession(IKermitTransport transport, KermitServerOptions? options = null)
        : base(transport, options)
    {
        _options = options ?? new KermitServerOptions();
    }

    public event EventHandler<KermitProgressEventArgs>? UploadProgressChanged;

    public event EventHandler<KermitProgressEventArgs>? DownloadProgressChanged;

    public event EventHandler<KermitFileTransferEventArgs>? UploadCompleted;

    public event EventHandler<KermitFileTransferEventArgs>? DownloadCompleted;

    public async Task SendFileAsync(string fullPath, string? remoteName = null, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Server file not found.", fullPath);
        }

        var metadata = new KermitFileMetadata(
            remoteName ?? fileInfo.Name,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            Encoding.UTF8.WebName,
            Environment.OSVersion.Platform.ToString());

        await SendPacketAsync(KermitPacketType.SendInit, KermitCodec.EncodeNegotiation(_options.Negotiation), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.FileHeader, Encoding.UTF8.GetBytes(metadata.FileName), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.FileAttributes, KermitCodec.EncodeAttributes(metadata), cancellationToken).ConfigureAwait(false);

        var chunkSize = Math.Max(1, NegotiatedOptions.MaxPacketLength - 12);
        var buffer = new byte[chunkSize];
        await using var stream = File.OpenRead(fullPath);
        long transferred = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await SendPacketAsync(KermitPacketType.Data, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            DownloadProgressChanged?.Invoke(this, new KermitProgressEventArgs(new KermitTransferProgress
            {
                FileName = metadata.FileName,
                BytesTransferred = transferred,
                TotalBytes = metadata.Length
            }));
        }

        await SendPacketAsync(KermitPacketType.EndOfFile, Encoding.UTF8.GetBytes("EOF"), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.Break, Encoding.UTF8.GetBytes("EOT"), cancellationToken).ConfigureAwait(false);
        DownloadCompleted?.Invoke(this, new KermitFileTransferEventArgs(metadata, fullPath));
    }

    protected override async Task OnPacketReceivedAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.Type)
        {
            case KermitPacketType.SendInit:
                UpdateNegotiatedOptions(KermitCodec.DecodeNegotiation(packet.Data.Span));
                await SendAckAsync(packet.Sequence, Encoding.UTF8.GetString(KermitCodec.EncodeNegotiation(_options.Negotiation)), cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.FileHeader:
                await HandleIncomingFileHeaderAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.FileAttributes:
                await HandleIncomingAttributesAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.Data:
                await HandleIncomingDataAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.EndOfFile:
                await HandleIncomingEndOfFileAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.Break:
                await HandleIncomingBreakAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.GenericCommand:
                await HandleGenericCommandAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case KermitPacketType.RemoteCommand:
                await HandleRemoteCommandAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleIncomingFileHeaderAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(packet.DataAsText);
        _uploadPath = Path.Combine(_options.RootDirectory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_uploadPath) ?? _options.RootDirectory);
        _uploadStream?.Dispose();
        _uploadStream = new FileStream(_uploadPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        _uploadMetadata = new KermitFileMetadata(fileName);
        _uploadedBytes = 0;
        await SendAckAsync(packet.Sequence, "FILE", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingAttributesAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var attributes = KermitCodec.DecodeAttributes(packet.Data.Span);
        if (_uploadMetadata is not null)
        {
            attributes.TryGetValue("size", out var sizeText);
            attributes.TryGetValue("date", out var dateText);
            attributes.TryGetValue("encoding", out var encoding);
            attributes.TryGetValue("system", out var systemId);

            _uploadMetadata = _uploadMetadata with
            {
                Length = long.TryParse(sizeText, out var size) ? size : null,
                LastWriteTime = DateTimeOffset.TryParse(dateText, out var dt) ? dt : null,
                Encoding = encoding,
                SystemId = systemId,
                AdditionalAttributes = attributes
            };
        }

        await SendAckAsync(packet.Sequence, "ATTR", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingDataAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        if (_uploadStream is null)
        {
            await SendErrorAsync(packet.Sequence, "Upload stream not initialized.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _uploadStream.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
        _uploadedBytes += packet.Data.Length;
        await SendAckAsync(packet.Sequence, "DATA", cancellationToken).ConfigureAwait(false);

        if (_uploadMetadata is not null)
        {
            UploadProgressChanged?.Invoke(this, new KermitProgressEventArgs(new KermitTransferProgress
            {
                FileName = _uploadMetadata.FileName,
                BytesTransferred = _uploadedBytes,
                TotalBytes = _uploadMetadata.Length
            }));
        }
    }

    private async Task HandleIncomingEndOfFileAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        if (_uploadStream is not null)
        {
            await _uploadStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _uploadStream.DisposeAsync().ConfigureAwait(false);
            _uploadStream = null;
        }

        if (_uploadMetadata is not null && _uploadPath is not null && _uploadMetadata.LastWriteTime is not null && File.Exists(_uploadPath))
        {
            File.SetLastWriteTimeUtc(_uploadPath, _uploadMetadata.LastWriteTime.Value.UtcDateTime);
        }

        await SendAckAsync(packet.Sequence, "EOF", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingBreakAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        await SendAckAsync(packet.Sequence, "EOT", cancellationToken).ConfigureAwait(false);
        if (_uploadMetadata is not null && _uploadPath is not null)
        {
            UploadCompleted?.Invoke(this, new KermitFileTransferEventArgs(_uploadMetadata, _uploadPath));
        }
    }

    private async Task HandleGenericCommandAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var command = packet.DataAsText.Trim();
        if (command.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[4..].Trim();
            var fullPath = Path.Combine(_options.RootDirectory, relativePath);
            if (!File.Exists(fullPath))
            {
                await SendErrorAsync(packet.Sequence, $"Requested file not found: {relativePath}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendAckAsync(packet.Sequence, "GET", cancellationToken).ConfigureAwait(false);
            var transferTask = SendFileAsync(fullPath, Path.GetFileName(relativePath), cancellationToken);
            _outgoingTransfers[fullPath] = transferTask;
            _ = transferTask.ContinueWith(_ =>
            {
                _outgoingTransfers.TryRemove(fullPath, out Task? removedTask);
            }, CancellationToken.None);
            return;
        }

        await SendAckAsync(packet.Sequence, $"GENERIC:{command}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRemoteCommandAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var command = packet.DataAsText.Trim();
        if (command.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[7..].Trim();
            var fullPath = Path.Combine(_options.RootDirectory, relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            await SendAckAsync(packet.Sequence, "DELETE", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAckAsync(packet.Sequence, $"REMOTE:{command}", cancellationToken).ConfigureAwait(false);
    }
}