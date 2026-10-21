using System.Diagnostics;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using FreeChat.Protocol;
using FreeChat.Protocol.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace FreeChat.Cli;

internal sealed class FreeChatClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 8 * 1024 * 1024;

    private readonly CliEndpoint endpoint;
    private readonly DeviceIdentity identity;
    private readonly string statePath;
    private readonly bool json;
    private readonly bool printJson;

    private ClientWebSocket? socket;
    private string? actualFingerprint;

    public FreeChatClient(
        CliEndpoint endpoint,
        DeviceIdentity identity,
        string statePath,
        bool json,
        bool printJson)
    {
        this.endpoint = endpoint;
        this.identity = identity;
        this.statePath = statePath;
        this.json = json;
        this.printJson = printJson;
    }

    public async Task<Welcome> AuthenticateAsync(string? pairingCode, CancellationToken cancellationToken)
    {
        await this.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var hello = new Envelope
        {
            RequestId = NewRequestId(),
            ClientHello = new ClientHello
            {
                ProtocolVersion = ProtocolConstants.Version,
                ClientName = "FreeChat CLI",
                ClientVersion = "0.2.0",
                Platform = "cli",
                DeviceId = this.identity.DeviceId,
                DevicePublicKey = ByteString.CopyFrom(this.identity.GetPublicKey()),
                PairingCode = pairingCode ?? string.Empty,
            },
        };
        hello.ClientHello.Capabilities.Add(ProtocolConstants.DefaultFeatures);
        hello.ClientHello.Capabilities.Add(ProtocolConstants.FriendListFeature);
        hello.ClientHello.Capabilities.Add(ProtocolConstants.FriendPresenceFeature);
        await this.SendEnvelopeAsync(hello, cancellationToken).ConfigureAwait(false);

        using var authTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authTimeout.CancelAfter(TimeSpan.FromSeconds(120));

        while (true)
        {
            var envelope = await this.ReceiveAsync(authTimeout.Token).ConfigureAwait(false);
            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.PairingRequired:
                {
                    var required = envelope.PairingRequired;
                    this.RememberServer(required.Server);
                    if (this.printJson)
                        this.PrintEnvelope(envelope);
                    else
                        this.PrintServerInfo(required.Server, "Pairing required");
                    if (string.IsNullOrWhiteSpace(pairingCode))
                    {
                        throw new CliException(
                            "Pairing is required. Run `freechat pair` with --code or --uri.",
                            2);
                    }

                    var request = new Envelope
                    {
                        RequestId = NewRequestId(),
                        PairRequest = new PairRequest
                        {
                            Code = pairingCode,
                            DeviceName = this.identity.DeviceName,
                            Platform = "cli",
                            DevicePublicKey = ByteString.CopyFrom(this.identity.GetPublicKey()),
                        },
                    };
                    await this.SendEnvelopeAsync(request, authTimeout.Token).ConfigureAwait(false);
                    break;
                }
                case Envelope.PayloadOneofCase.PairResult:
                {
                    var result = envelope.PairResult;
                    if (!result.Ok)
                        throw new CliException($"Pairing failed: {result.Error}: {result.Message}");

                    this.identity.DeviceId = result.DeviceId;
                    this.identity.Save(this.statePath);
                    if (this.printJson)
                        this.PrintEnvelope(envelope);
                    else
                        Console.WriteLine($"Paired device id: {result.DeviceId}");
                    break;
                }
                case Envelope.PayloadOneofCase.Challenge:
                {
                    if (string.IsNullOrWhiteSpace(this.identity.ServerId))
                    {
                        throw new CliException(
                            "CLI state has no server id. Pair again or pass the --uri from the plugin.",
                            2);
                    }

                    var nonce = envelope.Challenge.Nonce.ToByteArray();
                    var signature = this.identity.Sign(
                        BuildChallengeInput(this.identity.ServerId, this.identity.DeviceId, nonce));
                    await this.SendEnvelopeAsync(new Envelope
                    {
                        RequestId = NewRequestId(),
                        ChallengeResponse = new ChallengeResponse
                        {
                            Signature = ByteString.CopyFrom(signature),
                        },
                    }, authTimeout.Token).ConfigureAwait(false);
                    break;
                }
                case Envelope.PayloadOneofCase.Welcome:
                {
                    var welcome = envelope.Welcome;
                    this.RememberServer(welcome.Server);
                    this.identity.DeviceId = welcome.DeviceId;
                    this.identity.Save(this.statePath);
                    if (this.printJson)
                        this.PrintEnvelope(envelope);
                    else
                    {
                        this.PrintServerInfo(welcome.Server, "Authenticated");
                        Console.WriteLine($"Session: {welcome.SessionId}");
                    }
                    return welcome;
                }
                case Envelope.PayloadOneofCase.AuthError:
                    throw new CliException(
                        $"Authentication failed: {envelope.AuthError.Code}: {envelope.AuthError.Message}");
                case Envelope.PayloadOneofCase.Error:
                    throw new CliException(
                        $"Server error: {envelope.Error.Code}: {envelope.Error.Message}");
                default:
                    this.PrintEnvelope(envelope);
                    break;
            }
        }
    }

    public async Task SubscribeAsync(
        string? sources,
        string? keyword,
        string? regex,
        bool includeBattle,
        bool excludeOwn,
        long fromSequence,
        CancellationToken cancellationToken)
    {
        var filter = new ChatFilter
        {
            Keyword = keyword ?? string.Empty,
            Regex = regex ?? string.Empty,
            IncludeBattle = includeBattle,
        };
        if (excludeOwn)
            filter.IncludeOwn = false;

        var subscribe = new Subscribe
        {
            Filter = filter,
            ContentMode = ContentMode.Current,
            FromSequence = fromSequence,
        };
        foreach (var source in ParseSources(sources))
            subscribe.Sources.Add(source);

        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = NewRequestId(),
            Subscribe = subscribe,
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.Subscribed:
                    if (!this.printJson)
                    {
                        Console.WriteLine(
                            $"Subscribed: sources={string.Join(",", envelope.Subscribed.EffectiveSources)} "
                            + $"oldest={envelope.Subscribed.OldestSequence} "
                            + $"latest={envelope.Subscribed.LatestSequence}");
                    }
                    else
                    {
                        this.PrintEnvelope(envelope);
                    }

                    return;
                case Envelope.PayloadOneofCase.Error:
                    throw new CliException(
                        $"Subscribe failed: {envelope.Error.Code}: {envelope.Error.Message}");
                case Envelope.PayloadOneofCase.AuthError:
                    throw new CliException(
                        $"Authentication failed: {envelope.AuthError.Code}: {envelope.AuthError.Message}");
                default:
                    this.PrintEnvelope(envelope);
                    break;
            }
        }
    }

    public async Task RunAsync(int maxChatEvents, CancellationToken cancellationToken)
    {
        var count = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            this.PrintEnvelope(envelope);

            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.ChatEvent:
                    count++;
                    if (maxChatEvents > 0 && count >= maxChatEvents)
                        return;
                    break;
                case Envelope.PayloadOneofCase.Ping:
                    await this.SendPongAsync(envelope.Ping, cancellationToken).ConfigureAwait(false);
                    break;
                case Envelope.PayloadOneofCase.SessionEnded:
                case Envelope.PayloadOneofCase.ServerShutdown:
                    return;
            }
        }
    }

    public async Task<SendResult> SendAsync(
        SendRequest request,
        CancellationToken cancellationToken,
        bool waitEcho = false)
    {
        var requestId = NewRequestId();
        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = requestId,
            SendRequest = request,
        }, cancellationToken).ConfigureAwait(false);

        SendResult? submitted = null;
        while (true)
        {
            Envelope envelope;
            if (submitted is not null && waitEcho)
            {
                using var echoTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                echoTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    envelope = await this.ReceiveAsync(echoTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!this.printJson)
                        Console.WriteLine("No echo observed within 3 seconds.");
                    return submitted;
                }
            }
            else
            {
                envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }

            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.SendResult:
                    if (!this.printJson)
                    {
                        var result = envelope.SendResult;
                        Console.WriteLine(
                            $"{result.Status}: {result.Message}"
                            + (result.PartCount > 1 ? $" ({result.PartCount} parts)" : string.Empty));
                    }
                    else
                    {
                        this.PrintEnvelope(envelope);
                    }

                    if (envelope.SendResult.Status == SendStatus.Echoed)
                        return submitted ?? envelope.SendResult;

                    if (IsTerminal(envelope.SendResult.Status))
                    {
                        if (!waitEcho || envelope.SendResult.Status != SendStatus.Submitted)
                            return envelope.SendResult;

                        submitted = envelope.SendResult;
                    }

                    break;
                case Envelope.PayloadOneofCase.Error:
                    throw new CliException(
                        $"Send failed: {envelope.Error.Code}: {envelope.Error.Message}");
                case Envelope.PayloadOneofCase.Ping:
                    await this.SendPongAsync(envelope.Ping, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    this.PrintEnvelope(envelope);
                    break;
            }
        }
    }

    public async Task<IReadOnlyList<PlayerListSnapshot>> RequestFriendListAsync(
        uint pageSize,
        string? expectedOwnerKey,
        string? expectedOwnerEpoch,
        CancellationToken cancellationToken)
    {
        var requestId = NewRequestId();
        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = requestId,
            PlayerListRequest = new PlayerListRequest
            {
                Type = PlayerListType.Friend,
                PageSize = pageSize,
                ExpectedOwnerKey = expectedOwnerKey ?? string.Empty,
                ExpectedOwnerEpoch = expectedOwnerEpoch ?? string.Empty,
            },
        }, cancellationToken).ConfigureAwait(false);

        var pages = new List<PlayerListSnapshot>();
        var expectedPageCount = 1u;
        while (pages.Count < expectedPageCount)
        {
            var envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.PlayerListSnapshot:
                {
                    var page = envelope.PlayerListSnapshot;
                    if (this.printJson)
                        this.PrintEnvelope(envelope);
                    else
                    {
                        Console.WriteLine(
                            $"Friend list page {page.PageIndex + 1}/{page.PageCount}: "
                            + $"status={page.Status}, players={page.Players.Count}"
                            + (string.IsNullOrEmpty(page.Message) ? string.Empty : $", message={page.Message}"));
                    }

                    if (page.Status != PlayerListStatus.Success)
                        return [page];

                    expectedPageCount = page.PageCount;
                    pages.Add(page);
                    break;
                }
                case Envelope.PayloadOneofCase.Error:
                    throw new CliException(
                        $"Friend list failed: {envelope.Error.Code}: {envelope.Error.Message}");
                case Envelope.PayloadOneofCase.AuthError:
                    throw new CliException(
                        $"Authentication failed: {envelope.AuthError.Code}: {envelope.AuthError.Message}");
                case Envelope.PayloadOneofCase.Ping:
                    await this.SendPongAsync(envelope.Ping, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    this.PrintEnvelope(envelope);
                    break;
            }
        }

        if (!this.printJson)
            Console.WriteLine($"Friend list complete: {pages.Sum(x => x.Players.Count)} players.");
        return pages;
    }

    public async Task<FriendPresence> RequestPresenceAsync(
        ulong contentId,
        CancellationToken cancellationToken)
    {
        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = NewRequestId(),
            FriendPresenceRequest = new FriendPresenceRequest { ContentId = contentId },
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (envelope.PayloadCase)
            {
                case Envelope.PayloadOneofCase.FriendPresence:
                    if (this.printJson)
                        this.PrintEnvelope(envelope);
                    else
                    {
                        var presence = envelope.FriendPresence;
                        Console.WriteLine(
                            $"Presence {presence.ContentId}: {presence.Status}, {presence.State}");
                    }

                    return envelope.FriendPresence;
                case Envelope.PayloadOneofCase.Error:
                    throw new CliException(
                        $"Presence failed: {envelope.Error.Code}: {envelope.Error.Message}");
                case Envelope.PayloadOneofCase.AuthError:
                    throw new CliException(
                        $"Authentication failed: {envelope.AuthError.Code}: {envelope.AuthError.Message}");
                case Envelope.PayloadOneofCase.Ping:
                    await this.SendPongAsync(envelope.Ping, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    this.PrintEnvelope(envelope);
                    break;
            }
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(16);
        var stopwatch = Stopwatch.StartNew();
        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = NewRequestId(),
            Ping = new Ping { Nonce = ByteString.CopyFrom(nonce) },
        }, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var envelope = await this.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.PayloadCase == Envelope.PayloadOneofCase.Pong
                && envelope.Pong.Nonce.ToByteArray().AsSpan().SequenceEqual(nonce))
            {
                stopwatch.Stop();
                Console.WriteLine($"Pong: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
                return;
            }

            this.PrintEnvelope(envelope);
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        client.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            var actual = CertificatePin.Compute(certificate);
            this.actualFingerprint = actual is null ? null : CertificatePin.Hex(actual);
            return actual is not null
                   && CryptographicOperations.FixedTimeEquals(actual, this.endpoint.Fingerprint);
        };
        client.Options.AddSubProtocol(
            this.json ? ProtocolConstants.JsonSubProtocol : ProtocolConstants.ProtobufSubProtocol);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(
                new Uri($"wss://{this.endpoint.Host}:{this.endpoint.Port}/ws"),
                timeout.Token).ConfigureAwait(false);
            this.socket = client;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or AuthenticationException)
        {
            client.Dispose();
            var actual = this.actualFingerprint ?? "(none)";
            throw new CliException(
                $"Could not connect or the TLS pin did not match. "
                + $"Expected {CertificatePin.Hex(this.endpoint.Fingerprint)}, got {actual}. {ex.Message}");
        }
    }

    private async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var socket = this.socket ?? throw new CliException("Not connected.");
        var bytes = EnvelopeCodec.Encode(envelope, this.json);
        await socket.SendAsync(
            bytes,
            this.json ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Envelope> ReceiveAsync(CancellationToken cancellationToken)
    {
        var socket = this.socket ?? throw new CliException("Not connected.");
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new CliException(
                    $"Server closed the connection: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            if (result.Count > 0)
            {
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                    throw new CliException("Server message is too large.");
            }

            if (!result.EndOfMessage)
                continue;

            if (message.Length == 0)
                throw new CliException("Server sent an empty message.");

            var data = message.ToArray();
            var useJson = this.json || result.MessageType == WebSocketMessageType.Text;
            return EnvelopeCodec.Decode(data, useJson);
        }
    }

    private async Task SendPongAsync(Ping ping, CancellationToken cancellationToken)
    {
        await this.SendEnvelopeAsync(new Envelope
        {
            RequestId = NewRequestId(),
            Pong = new Pong
            {
                Nonce = ping.Nonce,
                ServerTime = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
        }, cancellationToken).ConfigureAwait(false);
    }

    private void RememberServer(ServerInfo? server)
    {
        if (server is null)
            return;

        var advertised = server.PublicKeyFingerprint.ToByteArray();
        if (advertised.Length > 0)
        {
            var expected = CertificatePin.Hex(this.endpoint.Fingerprint);
            var actual = CertificatePin.Hex(advertised);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException(
                    $"ServerInfo fingerprint {actual} does not match the pinned {expected}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(this.endpoint.ServerId)
            && !string.Equals(this.endpoint.ServerId, server.ServerId, StringComparison.Ordinal))
        {
            throw new CliException(
                $"ServerInfo server_id {server.ServerId} does not match the pairing link.");
        }

        if (!string.IsNullOrWhiteSpace(server.ServerId)
            && !string.Equals(this.identity.ServerId, server.ServerId, StringComparison.Ordinal))
        {
            this.identity.ServerId = server.ServerId;
            this.identity.Save(this.statePath);
        }
    }

    private void PrintServerInfo(ServerInfo? server, string prefix)
    {
        if (server is null)
            return;

        var character = server.Character is { Name.Length: > 0 } value
            ? $"{value.Name}@{value.WorldName}"
            : "(not logged in)";
        Console.WriteLine($"{prefix}: {server.ServerName} ({character})");
        Console.WriteLine($"Server id: {server.ServerId}");
        Console.WriteLine($"Fingerprint: {CertificatePin.Hex(server.PublicKeyFingerprint.ToByteArray())}");
    }

    private void PrintEnvelope(Envelope envelope)
    {
        if (this.printJson)
        {
            Console.WriteLine(JsonFormatter.Default.Format(envelope));
            return;
        }

        switch (envelope.PayloadCase)
        {
            case Envelope.PayloadOneofCase.ChatEvent:
            {
                var chat = envelope.ChatEvent;
                var sender = chat.Sender is { Name.Length: > 0 } value ? value.Name : "-";
                Console.WriteLine(
                    $"[{chat.Sequence}] {chat.Channel} {sender}: {chat.Text.Replace('\n', ' ')}");
                break;
            }
            case Envelope.PayloadOneofCase.PlayerListSnapshot:
            {
                var page = envelope.PlayerListSnapshot;
                Console.WriteLine(
                    $"Friend list page {page.PageIndex + 1}/{page.PageCount}: "
                    + $"status={page.Status}, players={page.Players.Count}");
                break;
            }
            case Envelope.PayloadOneofCase.FriendPresence:
            {
                var presence = envelope.FriendPresence;
                Console.WriteLine($"Presence {presence.ContentId}: {presence.Status}, {presence.State}");
                break;
            }
            case Envelope.PayloadOneofCase.HistoryTruncated:
                Console.WriteLine(
                    $"History truncated: requested {envelope.HistoryTruncated.RequestedFromSequence}, "
                    + $"oldest {envelope.HistoryTruncated.OldestSequence}");
                break;
            case Envelope.PayloadOneofCase.Error:
                Console.WriteLine($"Error: {envelope.Error.Code}: {envelope.Error.Message}");
                break;
            case Envelope.PayloadOneofCase.SessionEnded:
                Console.WriteLine($"Session ended: {envelope.SessionEnded.Reason}");
                break;
            case Envelope.PayloadOneofCase.ServerShutdown:
                Console.WriteLine($"Server shutdown: {envelope.ServerShutdown.Reason}");
                break;
            case Envelope.PayloadOneofCase.Pong:
            case Envelope.PayloadOneofCase.Ping:
            case Envelope.PayloadOneofCase.PairResult:
                break;
            default:
                Console.WriteLine($"Received: {envelope.PayloadCase}");
                break;
        }
    }

    private static IEnumerable<ChatSource> ParseSources(string? value)
    {
        var names = string.IsNullOrWhiteSpace(value)
            ? ["post"]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new HashSet<ChatSource>();
        foreach (var name in names)
        {
            switch (name.ToLowerInvariant())
            {
                case "post":
                case "post_handle":
                    result.Add(ChatSource.PostHandle);
                    break;
                case "pre":
                case "pre_handle":
                    result.Add(ChatSource.PreHandle);
                    break;
                case "structured":
                    result.Add(ChatSource.Structured);
                    break;
                case "all":
                    result.Add(ChatSource.PreHandle);
                    result.Add(ChatSource.PostHandle);
                    result.Add(ChatSource.Structured);
                    break;
                default:
                    throw new CliException($"Unknown source: {name}");
            }
        }

        return result;
    }

    private static bool IsTerminal(SendStatus status) => status is
        SendStatus.Submitted or
        SendStatus.Failed or
        SendStatus.Blocked or
        SendStatus.RateLimited or
        SendStatus.QueueFull or
        SendStatus.Cancelled;

    private static byte[] BuildChallengeInput(string serverId, string deviceId, byte[] nonce)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes("FreeChat/v1 challenge"));
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(serverId));
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(deviceId));
        stream.WriteByte(0);
        stream.Write(nonce);
        return stream.ToArray();
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    public async ValueTask DisposeAsync()
    {
        var client = this.socket;
        this.socket = null;
        if (client is null)
            return;

        try
        {
            if (client.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "done",
                    timeout.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort.
        }
        finally
        {
            client.Dispose();
        }
    }
}
