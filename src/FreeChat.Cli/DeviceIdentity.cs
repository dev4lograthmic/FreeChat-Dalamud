using System.Security.Cryptography;
using System.Text.Json;

namespace FreeChat.Cli;

internal sealed class DeviceIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string PrivateKeyPkcs8 { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;
    public string ServerId { get; set; } = string.Empty;

    public static string DefaultPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
                appData = Directory.GetCurrentDirectory();
            return Path.Combine(appData, "FreeChat", "cli-state.json");
        }
    }

    public static DeviceIdentity LoadExisting(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new CliException($"No CLI state at {fullPath}. Run `freechat pair` first.");

        var state = ReadState(fullPath);
        if (string.IsNullOrWhiteSpace(state.PrivateKeyPkcs8))
            throw new CliException($"CLI state at {fullPath} has no device private key.");

        ValidateKey(state.PrivateKeyPkcs8);
        return state;
    }

    public static DeviceIdentity LoadOrCreate(string path, string? deviceName = null)
    {
        var fullPath = Path.GetFullPath(path);
        DeviceIdentity state;
        if (File.Exists(fullPath))
        {
            state = ReadState(fullPath);
        }
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            state = new DeviceIdentity
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                PrivateKeyPkcs8 = Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            };
        }

        if (!string.IsNullOrWhiteSpace(deviceName))
            state.DeviceName = deviceName;
        if (string.IsNullOrWhiteSpace(state.DeviceName))
            state.DeviceName = Environment.MachineName;

        ValidateKey(state.PrivateKeyPkcs8);
        state.Save(fullPath);
        return state;
    }

    public ECDsa CreateKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(this.PrivateKeyPkcs8), out _);
        return key;
    }

    public byte[] GetPublicKey()
    {
        using var key = this.CreateKey();
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is null || parameters.Q.Y is null)
            throw new CliException("Device key is incomplete.");

        var raw = new byte[65];
        raw[0] = 0x04;
        parameters.Q.X.CopyTo(raw, 1);
        parameters.Q.Y.CopyTo(raw, 33);
        return raw;
    }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var key = this.CreateKey();
        return key.SignData(
            data,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public void Save(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, fullPath, overwrite: true);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static DeviceIdentity ReadState(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DeviceIdentity>(File.ReadAllText(path), JsonOptions)
                   ?? throw new CliException($"CLI state at {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new CliException($"CLI state at {path} is invalid: {ex.Message}");
        }
    }

    private static void ValidateKey(string privateKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
            if (key.KeySize != 256)
                throw new CliException("CLI device key must be P-256.");
        }
        catch (FormatException)
        {
            throw new CliException("CLI device private key is not valid base64.");
        }
        catch (CryptographicException ex)
        {
            throw new CliException($"CLI device private key is invalid: {ex.Message}");
        }
    }
}
