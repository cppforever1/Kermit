using System.Collections.Concurrent;
using System.Text;
using KermitCommon;

namespace KermitClient;

public sealed class KermitClientOptions : KermitSessionOptions
{
    public string DownloadDirectory { get; init; } = Directory.GetCurrentDirectory();
}

public sealed class KermitClientSession : KermitSessionBase
{
    private readonly KermitClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _downloadWaiters = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _downloadStream;
    private KermitFileMetadata? _downloadMetadata;
    private string? _downloadPath;
    private long _downloadBytes;
    private TaskCompletionSource<string>? _directoryListingWaiter;
    private MemoryStream? _directoryListingStream;
    private string? _directoryListingPath;
    private TaskCompletionSource<string>? _workingDirectoryWaiter;
    private MemoryStream? _workingDirectoryStream;
    private TaskCompletionSource<string>? _changeDirectoryWaiter;
    private MemoryStream? _changeDirectoryStream;

    public KermitClientSession(IKermitTransport transport, KermitClientOptions? options = null)
        : base(transport, options)
    {
        _options = options ?? new KermitClientOptions();
    }

    public event EventHandler<KermitProgressEventArgs>? UploadProgressChanged;

    public event EventHandler<KermitProgressEventArgs>? DownloadProgressChanged;

    public event EventHandler<KermitFileTransferEventArgs>? UploadCompleted;

    public event EventHandler<KermitFileTransferEventArgs>? DownloadCompleted;

    public event EventHandler<DirectoryListingEventArgs>? DirectoryListingReceived;

    public event EventHandler<WorkingDirectoryEventArgs>? WorkingDirectoryReceived;

    public event EventHandler<ChangeDirectoryEventArgs>? ChangeDirectoryReceived;

    public event EventHandler<RemoveEventArgs>? RemoveReceived;

    public event EventHandler<MakeDirectoryEventArgs>? MakeDirectoryReceived;

    public async Task SendFileAsync(string localFilePath, string? remoteFileName = null, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(localFilePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Local file not found.", localFilePath);
        }

        var metadata = new KermitFileMetadata(
            remoteFileName ?? fileInfo.Name,
            fileInfo.Length,
            fileInfo.LastWriteTimeUtc,
            Encoding.UTF8.WebName,
            Environment.OSVersion.Platform.ToString());

        var initResponse = await ExchangeAsync(KermitPacketType.SendInit, KermitCodec.EncodeNegotiation(_options.Negotiation), cancellationToken).ConfigureAwait(false);
        if (initResponse.Type != KermitPacketType.Ack)
        {
            throw new InvalidOperationException($"Unexpected response to SEND-INIT: {initResponse.Type}");
        }

        UpdateNegotiatedOptions(KermitCodec.DecodeNegotiation(initResponse.Data.Span));
        await ExchangeAsync(KermitPacketType.FileHeader, Encoding.UTF8.GetBytes(metadata.FileName), cancellationToken).ConfigureAwait(false);
        await ExchangeAsync(KermitPacketType.FileAttributes, KermitCodec.EncodeAttributes(metadata), cancellationToken).ConfigureAwait(false);

        var chunkSize = Math.Max(1, NegotiatedOptions.MaxPacketLength - 12);
        var buffer = new byte[chunkSize];
        await using var stream = File.OpenRead(localFilePath);
        long transferred = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await ExchangeAsync(KermitPacketType.Data, buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            UploadProgressChanged?.Invoke(this, new KermitProgressEventArgs(new KermitTransferProgress
            {
                FileName = metadata.FileName,
                BytesTransferred = transferred,
                TotalBytes = metadata.Length
            }));
        }

        await ExchangeAsync(KermitPacketType.EndOfFile, Encoding.UTF8.GetBytes("EOF"), cancellationToken).ConfigureAwait(false);
        await ExchangeAsync(KermitPacketType.Break, Encoding.UTF8.GetBytes("EOT"), cancellationToken).ConfigureAwait(false);
        UploadCompleted?.Invoke(this, new KermitFileTransferEventArgs(metadata, localFilePath));
    }

    public async Task<string> RequestFileAsync(string remoteFileName, string? localFilePath = null, CancellationToken cancellationToken = default)
    {
        localFilePath ??= Path.Combine(_options.DownloadDirectory, Path.GetFileName(remoteFileName));
        Directory.CreateDirectory(Path.GetDirectoryName(localFilePath) ?? _options.DownloadDirectory);

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_downloadWaiters.TryAdd(localFilePath, waiter))
        {
            throw new InvalidOperationException("A download is already in progress for the same destination path.");
        }

        _downloadPath = localFilePath;
        _downloadBytes = 0;

        try
        {
            var response = await ExchangeAsync(KermitPacketType.GenericCommand, Encoding.UTF8.GetBytes($"GET {remoteFileName}"), cancellationToken).ConfigureAwait(false);
            if (response.Type != KermitPacketType.Ack)
            {
                throw new InvalidOperationException($"Unexpected response to GET command: {response.Type}");
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            _downloadWaiters.TryRemove(localFilePath, out _);
        }
    }

    public async Task<IReadOnlyList<KermitDirectoryEntry>> ListRemoteDirectoryAsync(string remotePath = ".", CancellationToken cancellationToken = default)
    {
        if (_directoryListingWaiter is not null)
        {
            throw new InvalidOperationException("A directory listing request is already in progress.");
        }

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directoryListingWaiter = waiter;
        _directoryListingStream = null;
        _directoryListingPath = remotePath;

        try
        {
            var command = string.IsNullOrWhiteSpace(remotePath) || remotePath == "."
                ? "LS"
                : $"LS {remotePath}";

            var response = await ExchangeAsync(KermitPacketType.GenericCommand, Encoding.UTF8.GetBytes(command), cancellationToken).ConfigureAwait(false);
            if (response.Type != KermitPacketType.Ack)
            {
                throw new InvalidOperationException($"Unexpected response to LS command: {response.Type}");
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            var rawListing = await waiter.Task.ConfigureAwait(false);
            return KermitDirectoryListingCodec.Decode(rawListing);
        }
        finally
        {
            _directoryListingWaiter = null;
            _directoryListingStream?.Dispose();
            _directoryListingStream = null;
            _directoryListingPath = null;
        }
    }

    public async Task<string> ChangeRemoteDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        if (_changeDirectoryWaiter is not null)
        {
            throw new InvalidOperationException("A change directory request is already in progress.");
        }

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _changeDirectoryWaiter = waiter;
        _changeDirectoryStream?.Dispose();
        _changeDirectoryStream = null;

        try
        {
            var command = string.IsNullOrWhiteSpace(remotePath) || remotePath == "."
                ? "CD ."
                : $"CD {remotePath}";

            var response = await ExchangeAsync(KermitPacketType.GenericCommand, Encoding.UTF8.GetBytes(command), cancellationToken).ConfigureAwait(false);
            if (response.Type != KermitPacketType.Ack)
            {
                throw new InvalidOperationException($"Unexpected response to CD command: {response.Type}");
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            _changeDirectoryWaiter = null;
            _changeDirectoryStream?.Dispose();
            _changeDirectoryStream = null;
        }
    }

    public async Task MakeRemoteDirectoryAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            KermitPacketType.GenericCommand,
            Encoding.UTF8.GetBytes($"MKDIR {remotePath}"),
            cancellationToken).ConfigureAwait(false);

        if (response.Type != KermitPacketType.Ack)
        {
            throw new InvalidOperationException($"Unexpected response to MKDIR command: {response.Type}");
        }

        MakeDirectoryReceived?.Invoke(this, new MakeDirectoryEventArgs(remotePath));
    }

    public async Task RemoveRemoteAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var response = await ExchangeAsync(
            KermitPacketType.GenericCommand,
            Encoding.UTF8.GetBytes($"RM {remotePath}"),
            cancellationToken).ConfigureAwait(false);

        if (response.Type != KermitPacketType.Ack)
        {
            throw new InvalidOperationException($"Unexpected response to RM command: {response.Type}");
        }

        RemoveReceived?.Invoke(this, new RemoveEventArgs(remotePath, false));
    }

    public async Task<string> GetRemoteWorkingDirectoryAsync(CancellationToken cancellationToken = default)
    {
        if (_workingDirectoryWaiter is not null)
        {
            throw new InvalidOperationException("A working directory request is already in progress.");
        }

        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _workingDirectoryWaiter = waiter;
        _workingDirectoryStream?.Dispose();
        _workingDirectoryStream = null;

        try
        {
            var response = await ExchangeAsync(KermitPacketType.GenericCommand, Encoding.UTF8.GetBytes("PWD"), cancellationToken).ConfigureAwait(false);
            if (response.Type != KermitPacketType.Ack)
            {
                throw new InvalidOperationException($"Unexpected response to PWD command: {response.Type}");
            }

            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            _workingDirectoryWaiter = null;
            _workingDirectoryStream?.Dispose();
            _workingDirectoryStream = null;
        }
    }

    protected override async Task OnPacketReceivedAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.Type)
        {
            case KermitPacketType.SendInit:
                UpdateNegotiatedOptions(KermitCodec.DecodeNegotiation(packet.Data.Span));
                await SendAckAsync(packet.Sequence, Encoding.UTF8.GetString(KermitCodec.EncodeNegotiation(NegotiatedOptions)), cancellationToken).ConfigureAwait(false);
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

            case KermitPacketType.TextHeader:
                await HandleIncomingTextHeaderAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleIncomingTextHeaderAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var header = packet.DataAsText.Trim();
        if (header.StartsWith("LS", StringComparison.OrdinalIgnoreCase))
        {
            _directoryListingPath = header.Length > 2 ? header[2..].Trim() : ".";
            _directoryListingStream?.Dispose();
            _directoryListingStream = new MemoryStream();
        }
        else if (header.Equals("PWD", StringComparison.OrdinalIgnoreCase))
        {
            _workingDirectoryStream?.Dispose();
            _workingDirectoryStream = new MemoryStream();
        }
        else if (header.Equals("CD", StringComparison.OrdinalIgnoreCase))
        {
            _changeDirectoryStream?.Dispose();
            _changeDirectoryStream = new MemoryStream();
        }

        await SendAckAsync(packet.Sequence, "TEXT", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingFileHeaderAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var fileName = packet.DataAsText;
        _downloadPath ??= Path.Combine(_options.DownloadDirectory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(_downloadPath) ?? _options.DownloadDirectory);
        _downloadStream?.Dispose();
        _downloadStream = new FileStream(_downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        _downloadMetadata = new KermitFileMetadata(fileName);
        _downloadBytes = 0;
        await SendAckAsync(packet.Sequence, "FILE", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingAttributesAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var attributes = KermitCodec.DecodeAttributes(packet.Data.Span);
        if (_downloadMetadata is not null)
        {
            attributes.TryGetValue("size", out var sizeText);
            attributes.TryGetValue("date", out var dateText);
            attributes.TryGetValue("encoding", out var encoding);
            attributes.TryGetValue("system", out var systemId);

            _downloadMetadata = _downloadMetadata with
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
        if (_changeDirectoryStream is not null)
        {
            await _changeDirectoryStream.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
            await SendAckAsync(packet.Sequence, "DATA", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_workingDirectoryStream is not null)
        {
            await _workingDirectoryStream.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
            await SendAckAsync(packet.Sequence, "DATA", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_directoryListingStream is not null)
        {
            await _directoryListingStream.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
            await SendAckAsync(packet.Sequence, "DATA", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_downloadStream is null)
        {
            await SendErrorAsync(packet.Sequence, "Download stream not initialized.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await _downloadStream.WriteAsync(packet.Data, cancellationToken).ConfigureAwait(false);
        _downloadBytes += packet.Data.Length;
        await SendAckAsync(packet.Sequence, "DATA", cancellationToken).ConfigureAwait(false);

        if (_downloadMetadata is not null)
        {
            DownloadProgressChanged?.Invoke(this, new KermitProgressEventArgs(new KermitTransferProgress
            {
                FileName = _downloadMetadata.FileName,
                BytesTransferred = _downloadBytes,
                TotalBytes = _downloadMetadata.Length
            }));
        }
    }

    private async Task HandleIncomingEndOfFileAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        if (_changeDirectoryStream is not null)
        {
            await SendAckAsync(packet.Sequence, "EOF", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_workingDirectoryStream is not null)
        {
            await SendAckAsync(packet.Sequence, "EOF", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_directoryListingStream is not null)
        {
            await SendAckAsync(packet.Sequence, "EOF", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_downloadStream is not null)
        {
            await _downloadStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _downloadStream.DisposeAsync().ConfigureAwait(false);
            _downloadStream = null;
        }

        if (_downloadMetadata is not null && _downloadPath is not null && _downloadMetadata.LastWriteTime is not null && File.Exists(_downloadPath))
        {
            File.SetLastWriteTimeUtc(_downloadPath, _downloadMetadata.LastWriteTime.Value.UtcDateTime);
        }

        await SendAckAsync(packet.Sequence, "EOF", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleIncomingBreakAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        await SendAckAsync(packet.Sequence, "EOT", cancellationToken).ConfigureAwait(false);
        if (_changeDirectoryStream is not null)
        {
            var newPath = Encoding.UTF8.GetString(_changeDirectoryStream.ToArray());
            ChangeDirectoryReceived?.Invoke(this, new ChangeDirectoryEventArgs(newPath, newPath));
            _changeDirectoryWaiter?.TrySetResult(newPath);
            _changeDirectoryStream.Dispose();
            _changeDirectoryStream = null;
            return;
        }

        if (_workingDirectoryStream is not null)
        {
            var remotePath = Encoding.UTF8.GetString(_workingDirectoryStream.ToArray());
            WorkingDirectoryReceived?.Invoke(this, new WorkingDirectoryEventArgs(remotePath));
            _workingDirectoryWaiter?.TrySetResult(remotePath);
            _workingDirectoryStream.Dispose();
            _workingDirectoryStream = null;
            return;
        }

        if (_directoryListingStream is not null)
        {
            var rawListing = Encoding.UTF8.GetString(_directoryListingStream.ToArray());
            var entries = KermitDirectoryListingCodec.Decode(rawListing);
            DirectoryListingReceived?.Invoke(this, new DirectoryListingEventArgs(_directoryListingPath ?? ".", rawListing, entries));
            _directoryListingWaiter?.TrySetResult(rawListing);
            _directoryListingStream.Dispose();
            _directoryListingStream = null;
            _directoryListingPath = null;
            return;
        }

        if (_downloadMetadata is not null && _downloadPath is not null)
        {
            DownloadCompleted?.Invoke(this, new KermitFileTransferEventArgs(_downloadMetadata, _downloadPath));
            if (_downloadWaiters.TryGetValue(_downloadPath, out var waiter))
            {
                waiter.TrySetResult(_downloadPath);
            }
        }
    }
}