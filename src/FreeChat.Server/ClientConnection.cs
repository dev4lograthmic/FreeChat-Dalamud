using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public sealed class ClientConnection : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly bool isJson;
    private readonly int maxMessageBytes;
    private readonly Func<Envelope, CancellationToken, Task> handler;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(512)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private Task? writerTask;
    private int closed;

    public ClientConnection(
        WebSocket socket,
        bool isJson,
        IPAddress? remoteAddress,
        int maxMessageBytes,
        Func<Envelope, CancellationToken, Task> handler)
    {
        this.socket = socket;
        this.isJson = isJson;
        this.RemoteAddress = remoteAddress;
        this.maxMessageBytes = maxMessageBytes;
        this.handler = handler;
    }

    public IPAddress? RemoteAddress { get; }
    public bool IsJson => this.isJson;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.lifetime.Token);
        this.writerTask = Task.Run(() => this.WriteLoopAsync(linked.Token), CancellationToken.None);
        try
        {
            await this.ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await this.CloseAsync(CloseCode.Normal, "done").ConfigureAwait(false);
        }
    }

    public bool Send(Envelope envelope)
    {
        if (Volatile.Read(ref this.closed) != 0)
            return false;

        var bytes = EnvelopeCodec.Encode(envelope, this.isJson);
        if (this.outgoing.Writer.TryWrite(bytes))
            return true;

        _ = this.CloseAsync(CloseCode.SlowConsumer, "outgoing queue is full");
        return false;
    }

    public bool Send(byte[] encoded)
    {
        if (Volatile.Read(ref this.closed) != 0)
            return false;

        if (this.outgoing.Writer.TryWrite(encoded))
            return true;

        _ = this.CloseAsync(CloseCode.SlowConsumer, "outgoing queue is full");
        return false;
    }

    public async Task CloseAsync(CloseCode code, string reason)
    {
        if (Interlocked.Exchange(ref this.closed, 1) != 0)
            return;

        this.outgoing.Writer.TryComplete();
        try
        {
            if (this.socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await this.socket.CloseAsync(
                    (WebSocketCloseStatus)(int)code,
                    reason,
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort; the connection is already going away.
        }
        finally
        {
            this.lifetime.Cancel();
            this.socket.Dispose();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (this.socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await this.socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;

                message.Write(buffer, 0, result.Count);
                if (message.Length > this.maxMessageBytes)
                {
                    await this.CloseAsync(CloseCode.MessageTooBig, "message too large").ConfigureAwait(false);
                    return;
                }
            }
            while (!result.EndOfMessage);

            Envelope envelope;
            try
            {
                envelope = EnvelopeCodec.Decode(message.GetBuffer().AsSpan(0, (int)message.Length), this.isJson);
            }
            catch (Exception)
            {
                await this.CloseAsync(CloseCode.ProtocolError, "invalid envelope").ConfigureAwait(false);
                return;
            }

            try
            {
                await this.handler(envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                await this.CloseAsync(CloseCode.InternalError, "handler error").ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var bytes in this.outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = this.isJson ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                await this.socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    type,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.CloseAsync(CloseCode.Normal, "disposed").ConfigureAwait(false);
        if (this.writerTask is not null)
        {
            try
            {
                await this.writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        this.lifetime.Dispose();
    }
}
