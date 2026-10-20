using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FreeChat.Server;

public sealed class WebSocketServer : IAsyncDisposable
{
    public delegate Task ConnectionHandler(
        WebSocket socket,
        bool isJson,
        IPAddress? remoteAddress,
        CancellationToken cancellationToken);

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly ServerOptions options;
    private readonly X509Certificate2 certificate;
    private readonly ConnectionHandler handler;
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private long connectionId;
    private Task? acceptTask;

    public WebSocketServer(ServerOptions options, X509Certificate2 certificate, ConnectionHandler handler)
    {
        this.options = options;
        this.certificate = certificate;
        this.handler = handler;
        this.listener = new TcpListener(options.BindAddress, options.Port);
    }

    public int Port { get; private set; }

    public void Start()
    {
        this.listener.Start();
        this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
        this.acceptTask = Task.Run(this.AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!this.lifetime.IsCancellationRequested)
            {
                var tcp = await this.listener.AcceptTcpClientAsync(this.lifetime.Token).ConfigureAwait(false);
                if (this.connections.Count >= this.options.MaxClients)
                {
                    tcp.Dispose();
                    continue;
                }

                var id = Interlocked.Increment(ref this.connectionId);
                var task = this.HandleClientAsync(tcp);
                this.connections[id] = task;
                _ = task.ContinueWith(
                    completed => this.connections.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (this.lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient tcp)
    {
        using (tcp)
        {
            IPAddress? remote = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
                handshake.CancelAfter(this.options.HandshakeTimeout);

                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = this.certificate,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    },
                    handshake.Token).ConfigureAwait(false);

                var request = await ReadHttpRequestAsync(ssl, handshake.Token).ConfigureAwait(false);
                var key = GetHeader(request, "Sec-WebSocket-Key");
                if (string.IsNullOrEmpty(key))
                {
                    await WriteHttpErrorAsync(ssl, "400 Bad Request", handshake.Token).ConfigureAwait(false);
                    return;
                }

                var offered = (GetHeader(request, "Sec-WebSocket-Protocol") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var selected = SelectSubProtocol(offered);
                if (selected is null)
                {
                    await WriteHttpErrorAsync(ssl, "400 Bad Request", handshake.Token).ConfigureAwait(false);
                    return;
                }

                var isJson = selected == FreeChat.Protocol.ProtocolConstants.JsonSubProtocol;
                var accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
                var response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n" +
                    $"Sec-WebSocket-Protocol: {selected}\r\n\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(response), handshake.Token).ConfigureAwait(false);

                using var socket = WebSocket.CreateFromStream(
                    ssl,
                    new WebSocketCreationOptions
                    {
                        IsServer = true,
                        SubProtocol = selected,
                        KeepAliveInterval = TimeSpan.FromSeconds(20),
                    });

                await this.handler(socket, isJson, remote, this.lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (AuthenticationException)
            {
            }
            catch (IOException)
            {
            }
            catch (WebSocketException)
            {
            }
        }
    }

    private string? SelectSubProtocol(IReadOnlyList<string> offered)
    {
        if (offered.Contains(FreeChat.Protocol.ProtocolConstants.ProtobufSubProtocol, StringComparer.Ordinal))
            return FreeChat.Protocol.ProtocolConstants.ProtobufSubProtocol;
        if (this.options.EnableJsonDebug
            && offered.Contains(FreeChat.Protocol.ProtocolConstants.JsonSubProtocol, StringComparer.Ordinal))
            return FreeChat.Protocol.ProtocolConstants.JsonSubProtocol;
        return null;
    }

    private static async Task<string> ReadHttpRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException();

            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                return text;
        }

        throw new InvalidDataException("HTTP request headers are too large.");
    }

    private static Task WriteHttpErrorAsync(Stream stream, string status, CancellationToken cancellationToken)
    {
        var response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).AsTask();
    }

    private static string? GetHeader(string request, string name)
    {
        foreach (var line in request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            if (line[..colon].Equals(name, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        this.lifetime.Cancel();
        this.listener.Stop();

        var pending = this.connections.Values.ToArray();
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }

        if (this.acceptTask is not null)
        {
            try
            {
                await this.acceptTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.lifetime.Dispose();
    }
}
