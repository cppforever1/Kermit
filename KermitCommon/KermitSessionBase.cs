using System.Collections.Concurrent;
using System.Text;
using NLog;

namespace KermitCommon;

public abstract class KermitSessionBase : IAsyncDisposable
{
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<KermitPacket>> _responseWaiters = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Logger _logger;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private byte _nextSequence;
    private bool _disposed;

    protected KermitSessionBase(IKermitTransport transport, KermitSessionOptions? options = null)
    {
        Transport = transport;
        Options = options ?? new KermitSessionOptions();
        NegotiatedOptions = Options.Negotiation;
        _logger = KermitLog.For(GetType().FullName ?? GetType().Name);
    }

    protected IKermitTransport Transport { get; }

    protected KermitSessionOptions Options { get; }

    protected KermitNegotiationOptions NegotiatedOptions { get; private set; }

    public KermitSessionState State { get; private set; } = KermitSessionState.Created;

    public event EventHandler<KermitPacketEventArgs>? PacketReceived;

    public event EventHandler<KermitPacketEventArgs>? PacketSent;

    public event EventHandler<SendInitEventArgs>? SendInitReceived;

    public event EventHandler<SendInitEventArgs>? SendInitSent;

    public event EventHandler<FileHeaderEventArgs>? FileHeaderReceived;

    public event EventHandler<FileHeaderEventArgs>? FileHeaderSent;

    public event EventHandler<FileAttributesEventArgs>? FileAttributesReceived;

    public event EventHandler<FileAttributesEventArgs>? FileAttributesSent;

    public event EventHandler<DataEventArgs>? DataReceived;

    public event EventHandler<DataEventArgs>? DataSent;

    public event EventHandler<EndOfFileEventArgs>? EndOfFileReceived;

    public event EventHandler<EndOfFileEventArgs>? EndOfFileSent;

    public event EventHandler<BreakEventArgs>? BreakReceived;

    public event EventHandler<BreakEventArgs>? BreakSent;

    public event EventHandler<AckEventArgs>? AckReceived;

    public event EventHandler<AckEventArgs>? AckSent;

    public event EventHandler<NakEventArgs>? NakReceived;

    public event EventHandler<NakEventArgs>? NakSent;

    public event EventHandler<ErrorEventArgs>? ErrorReceived;

    public event EventHandler<ErrorEventArgs>? ErrorSent;

    public event EventHandler<GenericCommandEventArgs>? GenericCommandReceived;

    public event EventHandler<GenericCommandEventArgs>? GenericCommandSent;

    public event EventHandler<RemoteCommandEventArgs>? RemoteCommandReceived;

    public event EventHandler<RemoteCommandEventArgs>? RemoteCommandSent;

    public event EventHandler<ReceiveInitEventArgs>? ReceiveInitReceived;

    public event EventHandler<ReceiveInitEventArgs>? ReceiveInitSent;

    public event EventHandler<TextHeaderEventArgs>? TextHeaderReceived;

    public event EventHandler<TextHeaderEventArgs>? TextHeaderSent;

    public event EventHandler<Exception>? ReceiveLoopFaulted;

    public virtual async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (State == KermitSessionState.Open)
        {
            return;
        }

        await Transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        _receiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveLoopCts.Token), CancellationToken.None);
        State = KermitSessionState.Open;
        _logger.Info("Kermit session opened.");
    }

    public virtual async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State == KermitSessionState.Closed)
        {
            return;
        }

        _receiveLoopCts?.Cancel();
        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Transport.CloseAsync(cancellationToken).ConfigureAwait(false);
        State = KermitSessionState.Closed;
        _logger.Info("Kermit session closed.");
    }

    protected async Task SendPacketAsync(KermitPacketType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var packet = new KermitPacket(_nextSequence, type, data);
        _nextSequence = (byte)((_nextSequence + 1) % KermitConstants.SequenceModulo);
        await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    protected async Task SendPacketAsync(KermitPacket packet, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = KermitCodec.EncodePacket(packet, NegotiatedOptions);
            await Transport.Stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await Transport.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.Debug("Sent packet {Type} seq={Sequence} bytes={Bytes}", packet.Type, packet.Sequence, packet.Data.Length);
            RaisePacketEvent(packet, KermitPacketDirection.Outgoing);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    protected async Task<KermitPacket> ExchangeAsync(KermitPacketType type, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= Options.MaxRetries; attempt++)
        {
            var packet = new KermitPacket(_nextSequence, type, data);
            var waiter = new TaskCompletionSource<KermitPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_responseWaiters.TryAdd(packet.Sequence, waiter))
            {
                throw new InvalidOperationException("Sequence collision detected while waiting for response.");
            }

            _nextSequence = (byte)((_nextSequence + 1) % KermitConstants.SequenceModulo);
            try
            {
                await SendPacketAsync(packet, cancellationToken).ConfigureAwait(false);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(Options.ResponseTimeoutMilliseconds);
                await using var _ = timeoutCts.Token.Register(() => waiter.TrySetCanceled(timeoutCts.Token));
                var response = await waiter.Task.ConfigureAwait(false);
                if (response.Type == KermitPacketType.Nak)
                {
                    _logger.Warn("Received NAK for packet seq={Sequence}, retry {Attempt}/{MaxRetries}.", packet.Sequence, attempt, Options.MaxRetries);
                    continue;
                }

                if (response.Type == KermitPacketType.Error)
                {
                    throw new InvalidOperationException($"Remote error: {response.DataAsText}");
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < Options.MaxRetries)
            {
                _logger.Warn("Timeout waiting for response to packet seq={Sequence}, retry {Attempt}/{MaxRetries}.", packet.Sequence, attempt, Options.MaxRetries);
            }
            finally
            {
                _responseWaiters.TryRemove(packet.Sequence, out _);
            }
        }

        throw new TimeoutException("No valid response received from remote Kermit peer.");
    }

    protected virtual Task OnPacketReceivedAsync(KermitPacket packet, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected Task SendAckAsync(byte sequence, string? message = null, CancellationToken cancellationToken = default)
    {
        return SendPacketAsync(new KermitPacket(sequence, KermitPacketType.Ack, Encoding.UTF8.GetBytes(message ?? string.Empty)), cancellationToken);
    }

    protected Task SendNakAsync(byte sequence, string? message = null, CancellationToken cancellationToken = default)
    {
        return SendPacketAsync(new KermitPacket(sequence, KermitPacketType.Nak, Encoding.UTF8.GetBytes(message ?? string.Empty)), cancellationToken);
    }

    protected Task SendErrorAsync(byte sequence, string message, CancellationToken cancellationToken = default)
    {
        return SendPacketAsync(new KermitPacket(sequence, KermitPacketType.Error, Encoding.UTF8.GetBytes(message)), cancellationToken);
    }

    protected void UpdateNegotiatedOptions(KermitNegotiationOptions options)
    {
        NegotiatedOptions = options;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await KermitCodec.ReadPacketAsync(Transport.Stream, NegotiatedOptions, cancellationToken).ConfigureAwait(false);
                _logger.Debug("Received packet {Type} seq={Sequence} bytes={Bytes}", packet.Type, packet.Sequence, packet.Data.Length);
                RaisePacketEvent(packet, KermitPacketDirection.Incoming);

                if ((packet.Type == KermitPacketType.Ack || packet.Type == KermitPacketType.Nak || packet.Type == KermitPacketType.Error)
                    && _responseWaiters.TryGetValue(packet.Sequence, out var waiter))
                {
                    waiter.TrySetResult(packet);
                }

                await OnPacketReceivedAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            State = KermitSessionState.Faulted;
            _logger.Error(exception, "Kermit receive loop faulted.");
            ReceiveLoopFaulted?.Invoke(this, exception);
        }
    }

    private void RaisePacketEvent(KermitPacket packet, KermitPacketDirection direction)
    {
        var genericArgs = new KermitPacketEventArgs(packet, direction);
        if (direction == KermitPacketDirection.Incoming)
        {
            PacketReceived?.Invoke(this, genericArgs);
        }
        else
        {
            PacketSent?.Invoke(this, genericArgs);
        }

        switch (packet.Type)
        {
            case KermitPacketType.SendInit:
            {
                var args = new SendInitEventArgs(packet, direction, KermitCodec.DecodeNegotiation(packet.Data.Span));
                if (direction == KermitPacketDirection.Incoming)
                {
                    SendInitReceived?.Invoke(this, args);
                }
                else
                {
                    SendInitSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.FileHeader:
            {
                var metadata = new KermitFileMetadata(Encoding.UTF8.GetString(packet.Data.Span));
                var args = new FileHeaderEventArgs(packet, direction, metadata);
                if (direction == KermitPacketDirection.Incoming)
                {
                    FileHeaderReceived?.Invoke(this, args);
                }
                else
                {
                    FileHeaderSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.FileAttributes:
            {
                var args = new FileAttributesEventArgs(packet, direction, KermitCodec.DecodeAttributes(packet.Data.Span));
                if (direction == KermitPacketDirection.Incoming)
                {
                    FileAttributesReceived?.Invoke(this, args);
                }
                else
                {
                    FileAttributesSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.Data:
            {
                var args = new DataEventArgs(packet, direction);
                if (direction == KermitPacketDirection.Incoming)
                {
                    DataReceived?.Invoke(this, args);
                }
                else
                {
                    DataSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.EndOfFile:
            {
                var args = new EndOfFileEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    EndOfFileReceived?.Invoke(this, args);
                }
                else
                {
                    EndOfFileSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.Break:
            {
                var args = new BreakEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    BreakReceived?.Invoke(this, args);
                }
                else
                {
                    BreakSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.Ack:
            {
                var args = new AckEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    AckReceived?.Invoke(this, args);
                }
                else
                {
                    AckSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.Nak:
            {
                var args = new NakEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    NakReceived?.Invoke(this, args);
                }
                else
                {
                    NakSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.Error:
            {
                var args = new ErrorEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    ErrorReceived?.Invoke(this, args);
                }
                else
                {
                    ErrorSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.GenericCommand:
            {
                var args = new GenericCommandEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    GenericCommandReceived?.Invoke(this, args);
                }
                else
                {
                    GenericCommandSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.RemoteCommand:
            {
                var args = new RemoteCommandEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    RemoteCommandReceived?.Invoke(this, args);
                }
                else
                {
                    RemoteCommandSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.ReceiveInit:
            {
                var args = new ReceiveInitEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    ReceiveInitReceived?.Invoke(this, args);
                }
                else
                {
                    ReceiveInitSent?.Invoke(this, args);
                }

                break;
            }
            case KermitPacketType.TextHeader:
            {
                var args = new TextHeaderEventArgs(packet, direction, packet.DataAsText);
                if (direction == KermitPacketDirection.Incoming)
                {
                    TextHeaderReceived?.Invoke(this, args);
                }
                else
                {
                    TextHeaderSent?.Invoke(this, args);
                }

                break;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _receiveLoopCts?.Cancel();
        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _receiveLoopCts?.Dispose();
        _sendLock.Dispose();
        await Transport.DisposeAsync().ConfigureAwait(false);
    }
}