using System.Security.Cryptography;
using System.Text;
using FreeChat.Protocol.V1;

namespace FreeChat.Server;

public sealed record PairingRequest(
    string? DeviceId,
    string DeviceName,
    string Platform,
    byte[] PublicKey);

public sealed record PairingResult(
    bool Ok,
    string? DeviceId,
    ErrorCode Error,
    string? Message);

public sealed class PairingService
{
    private readonly IDeviceStore devices;
    private readonly IPairingApproval approval;
    private readonly IClock clock;
    private readonly TimeSpan codeLifetime;
    private readonly object gate = new();
    private readonly Dictionary<string, AttemptState> attempts = new(StringComparer.Ordinal);

    private string currentCode = string.Empty;
    private DateTimeOffset codeExpiresAt;
    private DateTimeOffset lockoutUntil;

    public PairingService(
        IDeviceStore devices,
        IPairingApproval approval,
        IClock clock,
        TimeSpan? codeLifetime = null)
    {
        this.devices = devices;
        this.approval = approval;
        this.clock = clock;
        this.codeLifetime = codeLifetime ?? TimeSpan.FromMinutes(5);
        this.GenerateCode();
    }

    public string CurrentCode
    {
        get
        {
            lock (this.gate)
                return this.currentCode;
        }
    }

    public DateTimeOffset CodeExpiresAt
    {
        get
        {
            lock (this.gate)
                return this.codeExpiresAt;
        }
    }

    public string GenerateCode()
    {
        lock (this.gate)
        {
            this.currentCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            this.codeExpiresAt = this.clock.UtcNow.Add(this.codeLifetime);
            this.lockoutUntil = default;
            this.attempts.Clear();
            return this.currentCode;
        }
    }

    public async Task<PairingResult> PairAsync(
        string code,
        PairingRequest request,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        var now = this.clock.UtcNow;
        lock (this.gate)
        {
            if (this.lockoutUntil > now)
                return Fail(ErrorCode.PairingRateLimited, "Too many pairing attempts. Try again later.");

            if (now >= this.codeExpiresAt)
                return Fail(ErrorCode.PairingExpired, "The pairing code has expired.");

            if (!string.Equals(code, this.currentCode, StringComparison.Ordinal))
            {
                this.RecordFailure(remoteAddress, now);
                return Fail(ErrorCode.PairingInvalid, "The pairing code is invalid.");
            }

            if (request.PublicKey.Length != 65 || request.PublicKey[0] != 0x04)
                return Fail(ErrorCode.PairingInvalid, "The device public key must be a 65-byte P-256 point.");
        }

        var approved = await this.approval.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
        if (!approved)
            return Fail(ErrorCode.PairingInvalid, "The device was rejected by the user.");

        lock (this.gate)
        {
            var existing = this.devices.FindByPublicKey(request.PublicKey)
                ?? (request.DeviceId is { Length: > 0 } id && this.devices.TryGet(id, out var byId) ? byId : null);

            var deviceId = existing?.DeviceId
                ?? (request.DeviceId is { Length: > 0 } requested ? requested : Guid.NewGuid().ToString("N"));

            var record = new DeviceRecord
            {
                DeviceId = deviceId,
                Name = request.DeviceName,
                Platform = request.Platform,
                PublicKey = request.PublicKey.ToArray(),
                CreatedAt = existing?.CreatedAt ?? now,
                LastSeenAt = now,
                Revoked = false,
            };

            this.devices.AddOrUpdate(record);
            this.currentCode = string.Empty;
            this.codeExpiresAt = now;
            this.attempts.Clear();
            return new PairingResult(true, deviceId, ErrorCode.Unspecified, null);
        }
    }

    private void RecordFailure(string? remoteAddress, DateTimeOffset now)
    {
        var key = remoteAddress ?? "unknown";
        this.attempts.TryGetValue(key, out var state);
        state.Count++;
        state.LastAttempt = now;
        this.attempts[key] = state;

        if (state.Count >= 5)
            this.lockoutUntil = now.AddMinutes(5);
    }

    private static PairingResult Fail(ErrorCode code, string message) =>
        new(false, null, code, message);

    private struct AttemptState
    {
        public int Count;
        public DateTimeOffset LastAttempt;
    }
}

public sealed record ChallengeState(byte[] Nonce, DateTimeOffset ExpiresAt);

public static class DeviceAuthenticator
{
    public const string Algorithm = "ECDSA_P256_SHA256_P1363";

    public static ChallengeState CreateChallenge(IClock clock, TimeSpan? lifetime = null)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        return new ChallengeState(nonce, clock.UtcNow.Add(lifetime ?? TimeSpan.FromSeconds(60)));
    }

    public static bool Verify(
        DeviceRecord device,
        string serverId,
        string deviceId,
        byte[] nonce,
        byte[] signature)
    {
        if (device.PublicKey.Length != 65 || device.PublicKey[0] != 0x04)
            return false;
        if (signature.Length != 64)
            return false;

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = device.PublicKey.AsSpan(1, 32).ToArray(),
                Y = device.PublicKey.AsSpan(33, 32).ToArray(),
            },
        });

        var input = BuildChallengeInput(serverId, deviceId, nonce);
        return ecdsa.VerifyData(
            input,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static byte[] BuildChallengeInput(string serverId, string deviceId, byte[] nonce)
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
}
