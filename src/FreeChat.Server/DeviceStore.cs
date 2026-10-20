using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeChat.Server;

public sealed record DeviceRecord
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Platform { get; init; }
    public required byte[] PublicKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool Revoked { get; set; }
}

public interface IDeviceStore
{
    bool TryGet(string deviceId, out DeviceRecord device);
    DeviceRecord? FindByPublicKey(ReadOnlySpan<byte> publicKey);
    IReadOnlyCollection<DeviceRecord> List();
    DeviceRecord AddOrUpdate(DeviceRecord device);
    bool Revoke(string deviceId);
    void Touch(string deviceId, DateTimeOffset at);
}

public sealed class JsonDeviceStore : IDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<string, DeviceRecord> devices;

    public JsonDeviceStore(string path)
    {
        this.path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        this.devices = this.Load();
    }

    public bool TryGet(string deviceId, out DeviceRecord device)
    {
        lock (this.gate)
            return this.devices.TryGetValue(deviceId, out device!);
    }

    public DeviceRecord? FindByPublicKey(ReadOnlySpan<byte> publicKey)
    {
        lock (this.gate)
        {
            foreach (var device in this.devices.Values)
            {
                if (!device.Revoked && device.PublicKey.AsSpan().SequenceEqual(publicKey))
                    return device;
            }
        }

        return null;
    }

    public IReadOnlyCollection<DeviceRecord> List()
    {
        lock (this.gate)
            return this.devices.Values.Select(Clone).ToArray();
    }

    public DeviceRecord AddOrUpdate(DeviceRecord device)
    {
        lock (this.gate)
        {
            this.devices[device.DeviceId] = Clone(device);
            this.Save();
            return Clone(device);
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (this.gate)
        {
            if (!this.devices.TryGetValue(deviceId, out var device))
                return false;

            device.Revoked = true;
            this.Save();
            return true;
        }
    }

    public void Touch(string deviceId, DateTimeOffset at)
    {
        lock (this.gate)
        {
            if (!this.devices.TryGetValue(deviceId, out var device))
                return;

            device.LastSeenAt = at;
            this.Save();
        }
    }

    private Dictionary<string, DeviceRecord> Load()
    {
        if (!File.Exists(this.path))
            return new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);

        var json = File.ReadAllText(this.path);
        var list = JsonSerializer.Deserialize<List<DeviceRecord>>(json, JsonOptions)
                   ?? [];
        return list.ToDictionary(x => x.DeviceId, StringComparer.Ordinal);
    }

    private void Save()
    {
        var temp = this.path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this.devices.Values.ToArray(), JsonOptions));
        File.Move(temp, this.path, overwrite: true);
    }

    private static DeviceRecord Clone(DeviceRecord device) => new()
    {
        DeviceId = device.DeviceId,
        Name = device.Name,
        Platform = device.Platform,
        PublicKey = device.PublicKey.ToArray(),
        CreatedAt = device.CreatedAt,
        LastSeenAt = device.LastSeenAt,
        Revoked = device.Revoked,
    };
}
