using System.Net;
using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IPairingApproval
{
    Task<bool> ApproveAsync(PairingRequest request, CancellationToken cancellationToken);
}

public sealed class AutoApprovePairing : IPairingApproval
{
    public Task<bool> ApproveAsync(PairingRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

public interface IChatSink
{
    Task<SendResult> SendAsync(SendContext context, SendRequest request, CancellationToken cancellationToken);

    Task<SendResult> CancelAsync(SendContext context, string targetRequestId, CancellationToken cancellationToken);
}

/// <summary>Identity and session information for a read-only social request.</summary>
public sealed record SocialRequestContext(
    string SessionId,
    string? OwnerKey,
    CharacterRef? Owner);

public sealed record FriendListData(
    PlayerListStatus Status,
    IReadOnlyList<PlayerInfo> Players,
    string Message = "");

public sealed record FriendPresenceData(
    PlayerListStatus Status,
    PresenceState State,
    string Message = "");

/// <summary>
/// Reads game social data on behalf of authenticated clients. Implementations
/// must keep native pointers on the game/framework thread and return only
/// managed snapshots.
/// </summary>
public interface ISocialDataProvider
{
    bool FriendListAvailable { get; }
    bool FriendPresenceAvailable { get; }

    Task<FriendListData> GetFriendListAsync(
        SocialRequestContext context,
        CancellationToken cancellationToken);

    Task<FriendPresenceData> GetFriendPresenceAsync(
        ulong contentId,
        SocialRequestContext context,
        CancellationToken cancellationToken);
}

public sealed record SendContext(
    string SessionId,
    string? OwnerKey,
    CharacterRef? Character,
    string RequestId);

public sealed record ClientSessionInfo(
    string SessionId,
    string? DeviceId,
    string? DeviceName,
    string? Platform,
    string? RemoteAddress,
    bool Authenticated,
    bool Subscribed,
    DateTimeOffset ConnectedAt);

public sealed class SendRejectedException : Exception
{
    public SendRejectedException(ErrorCode code, string message) : base(message) => this.Code = code;

    public ErrorCode Code { get; }
}

public sealed class ServerOptions
{
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 14777;
    public string ServerId { get; init; } = Guid.NewGuid().ToString("N");
    public string ServerName { get; init; } = "FreeChat";
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    public string? DeviceStorePath { get; init; }
    public bool EnableJsonDebug { get; init; }
    public int MaxClients { get; init; } = 32;
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PairingTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ChallengeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SubscribeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PairingCodeLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public int BacklogMessages { get; init; } = 5000;
    public long BacklogBytes { get; init; } = 32L * 1024 * 1024;
    public int SendRateLimitPerSecond { get; init; } = 4;
    public int SendRateLimitBurst { get; init; } = 4;
}
