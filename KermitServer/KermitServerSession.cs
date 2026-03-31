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
    private string? _currentDirectory;

    public KermitServerSession(IKermitTransport transport, KermitServerOptions? options = null)
        : base(transport, options)
    {
        _options = options ?? new KermitServerOptions();
    }

    public event EventHandler<KermitProgressEventArgs>? UploadProgressChanged;

    public event EventHandler<KermitProgressEventArgs>? DownloadProgressChanged;

    public event EventHandler<KermitFileTransferEventArgs>? UploadCompleted;

    public event EventHandler<KermitFileTransferEventArgs>? DownloadCompleted;

    public event EventHandler<DirectoryListingEventArgs>? DirectoryListingSent;

    public event EventHandler<WorkingDirectoryEventArgs>? WorkingDirectorySent;

    public event EventHandler<ChangeDirectoryEventArgs>? ChangeDirectorySent;

    public event EventHandler<RemoveEventArgs>? RemoveSent;

    public event EventHandler<MakeDirectoryEventArgs>? MakeDirectorySent;

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
        if (command.StartsWith("get ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[4..].Trim();
            string fullPath;
            try
            {
                fullPath = ResolvePath(relativePath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(fullPath))
            {
                await SendErrorAsync(packet.Sequence, $"Requested file not found: {relativePath}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendAckAsync(packet.Sequence, "get", cancellationToken).ConfigureAwait(false);
            var transferTask = SendFileAsync(fullPath, Path.GetFileName(relativePath), cancellationToken);
            _outgoingTransfers[fullPath] = transferTask;
            _ = transferTask.ContinueWith(_ =>
            {
                _outgoingTransfers.TryRemove(fullPath, out Task? removedTask);
            }, CancellationToken.None);
            return;
        }

        if (command.Equals("ls", StringComparison.OrdinalIgnoreCase) || command.StartsWith("ls ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command.Length > 2 ? command[2..].Trim() : ".";
            string fullPath;
            try
            {
                fullPath = ResolvePath(relativePath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!Directory.Exists(fullPath))
            {
                await SendErrorAsync(packet.Sequence, $"Requested directory not found: {relativePath}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await SendAckAsync(packet.Sequence, "ls", cancellationToken).ConfigureAwait(false);
            await SendDirectoryListingAsync(relativePath, fullPath, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Equals("pwd", StringComparison.OrdinalIgnoreCase))
        {
            await SendAckAsync(packet.Sequence, "pwd", cancellationToken).ConfigureAwait(false);
            await SendWorkingDirectoryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.Equals("cd", StringComparison.OrdinalIgnoreCase) || command.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
        {
            var requestedPath = command.Length > 2 ? command[2..].Trim() : ".";
            string newDirectory;
            try
            {
                newDirectory = ResolveDirectory(requestedPath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!Directory.Exists(newDirectory))
            {
                await SendErrorAsync(packet.Sequence, $"Directory not found: {requestedPath}", cancellationToken).ConfigureAwait(false);
                return;
            }

            _currentDirectory = newDirectory;
            await SendAckAsync(packet.Sequence, "cd", cancellationToken).ConfigureAwait(false);
            await SendChangeDirectoryAsync(requestedPath, newDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.StartsWith("rm ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[3..].Trim();
            string fullPath;
            try
            {
                fullPath = ResolvePath(relativePath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool wasDirectory;
            if (Directory.Exists(fullPath))
            {
                wasDirectory = true;
                Directory.Delete(fullPath);
            }
            else if (File.Exists(fullPath))
            {
                wasDirectory = false;
                File.Delete(fullPath);
            }
            else
            {
                await SendErrorAsync(packet.Sequence, $"File or directory not found: {relativePath}", cancellationToken).ConfigureAwait(false);
                return;
            }

            RemoveSent?.Invoke(this, new RemoveEventArgs(fullPath, wasDirectory));
            await SendAckAsync(packet.Sequence, "rm", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (command.StartsWith("mkdir ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[6..].Trim();
            string fullPath;
            try
            {
                fullPath = ResolvePath(relativePath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(fullPath);
            MakeDirectorySent?.Invoke(this, new MakeDirectoryEventArgs(fullPath));
            await SendAckAsync(packet.Sequence, "mkdir", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAckAsync(packet.Sequence, $"GENERIC:{command}", cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRemoteCommandAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        var command = packet.DataAsText.Trim();
        if (command.StartsWith("delete ", StringComparison.OrdinalIgnoreCase))
        {
            var relativePath = command[7..].Trim();
            string fullPath;
            try
            {
                fullPath = ResolvePath(relativePath);
            }
            catch (InvalidOperationException exception)
            {
                await SendErrorAsync(packet.Sequence, exception.Message, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            await SendAckAsync(packet.Sequence, "delete", cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAckAsync(packet.Sequence, $"REMOTE:{command}", cancellationToken).ConfigureAwait(false);
    }

    private async Task SendDirectoryListingAsync(string remotePath, string fullPath, CancellationToken cancellationToken)
    {
        var entries = Directory
            .EnumerateFileSystemEntries(fullPath)
            .Select(path =>
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var info = isDirectory
                    ? new DirectoryInfo(path)
                    : new FileInfo(path) as FileSystemInfo;

                return new KermitDirectoryEntry(
                    Path.GetFileName(path),
                    isDirectory,
                    info is FileInfo fileInfo ? fileInfo.Length : null,
                    info.LastWriteTimeUtc);
            })
            .OrderBy(static entry => entry.IsDirectory ? 0 : 1)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var listing = KermitDirectoryListingCodec.Encode(entries);
        DirectoryListingSent?.Invoke(this, new DirectoryListingEventArgs(remotePath, listing, entries));

        await SendPacketAsync(KermitPacketType.TextHeader, Encoding.UTF8.GetBytes($"ls {remotePath}"), cancellationToken).ConfigureAwait(false);

        var chunkSize = Math.Max(1, NegotiatedOptions.MaxPacketLength - 12);
        var payload = Encoding.UTF8.GetBytes(listing);
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, payload.Length - offset);
            await SendPacketAsync(KermitPacketType.Data, payload.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        await SendPacketAsync(KermitPacketType.EndOfFile, Encoding.UTF8.GetBytes("EOF"), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.Break, Encoding.UTF8.GetBytes("EOT"), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendChangeDirectoryAsync(string requestedPath, string newDirectory, CancellationToken cancellationToken)
    {
        ChangeDirectorySent?.Invoke(this, new ChangeDirectoryEventArgs(requestedPath, newDirectory));

        await SendPacketAsync(KermitPacketType.TextHeader, Encoding.UTF8.GetBytes("cd"), cancellationToken).ConfigureAwait(false);

        var chunkSize = Math.Max(1, NegotiatedOptions.MaxPacketLength - 12);
        var payload = Encoding.UTF8.GetBytes(newDirectory);
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, payload.Length - offset);
            await SendPacketAsync(KermitPacketType.Data, payload.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        await SendPacketAsync(KermitPacketType.EndOfFile, Encoding.UTF8.GetBytes("EOF"), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.Break, Encoding.UTF8.GetBytes("EOT"), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendWorkingDirectoryAsync(CancellationToken cancellationToken)
    {
        var remotePath = _currentDirectory ?? Path.GetFullPath(_options.RootDirectory);
        WorkingDirectorySent?.Invoke(this, new WorkingDirectoryEventArgs(remotePath));

        await SendPacketAsync(KermitPacketType.TextHeader, Encoding.UTF8.GetBytes("pwd"), cancellationToken).ConfigureAwait(false);

        var chunkSize = Math.Max(1, NegotiatedOptions.MaxPacketLength - 12);
        var payload = Encoding.UTF8.GetBytes(remotePath);
        for (var offset = 0; offset < payload.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, payload.Length - offset);
            await SendPacketAsync(KermitPacketType.Data, payload.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        await SendPacketAsync(KermitPacketType.EndOfFile, Encoding.UTF8.GetBytes("EOF"), cancellationToken).ConfigureAwait(false);
        await SendPacketAsync(KermitPacketType.Break, Encoding.UTF8.GetBytes("EOT"), cancellationToken).ConfigureAwait(false);
    }

    private string ResolvePath(string relativePath)
    {
        var base_ = _currentDirectory ?? Path.GetFullPath(_options.RootDirectory);
        return ResolveAgainst(relativePath, base_);
    }

    private string ResolveDirectory(string relativePath)
    {
        if (relativePath == "/" || relativePath == "\\" || relativePath == ".")
        {
            return Path.GetFullPath(_options.RootDirectory);
        }

        var base_ = _currentDirectory ?? Path.GetFullPath(_options.RootDirectory);
        return ResolveAgainst(relativePath, base_);
    }

    private string ResolveAgainst(string relativePath, string baseDirectory)
    {
        var normalizedRelativePath = string.IsNullOrWhiteSpace(relativePath) || relativePath == "."
            ? string.Empty
            : relativePath;

        var rootPath = Path.GetFullPath(_options.RootDirectory);
        var rootWithSeparator = rootPath.EndsWith(Path.DirectorySeparatorChar) || rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        var combinedPath = Path.GetFullPath(Path.Combine(baseDirectory, normalizedRelativePath));
        if (!combinedPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
            && !combinedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escapes the configured root directory.");
        }

        return combinedPath;
    }
}