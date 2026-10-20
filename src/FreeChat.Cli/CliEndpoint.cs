using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FreeChat.Cli;

internal static class CertificatePin
{
    public static byte[] ParseExpected(string value)
    {
        var hex = value
            .Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        if (hex.Length != 64)
            throw new CliException("Fingerprint must be 64 hex characters (SHA-256 of the server P-256 public key).");

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new CliException("Fingerprint contains non-hex characters.");
        }
    }

    public static byte[]? Compute(X509Certificate? certificate)
    {
        if (certificate is null)
            return null;

        var cert = certificate as X509Certificate2;
        var owned = cert is null;
        try
        {
            cert ??= new X509Certificate2(certificate);
            using var key = cert.GetECDsaPublicKey();
            if (key is null)
                return null;

            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (parameters.Q.X is null || parameters.Q.Y is null)
                return null;

            var raw = new byte[65];
            raw[0] = 0x04;
            parameters.Q.X.CopyTo(raw, 1);
            parameters.Q.Y.CopyTo(raw, 33);
            return SHA256.HashData(raw);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (owned)
                cert?.Dispose();
        }
    }

    public static bool Matches(X509Certificate? certificate, byte[] expected)
    {
        var actual = Compute(certificate);
        return actual is not null && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

internal sealed record CliEndpoint(string Host, int Port, byte[] Fingerprint, string? Code, string? ServerId)
{
    public static CliEndpoint FromOptions(CliOptions options)
    {
        string? host = options.Get("host");
        int? port = options.Has("port") ? options.GetInt("port", 14777) : null;
        string? fingerprint = options.Get("fingerprint");
        string? code = options.Get("code");
        string? serverId = options.Get("server-id");

        if (options.Get("uri") is { Length: > 0 } uriText)
            ApplyUri(uriText, ref host, ref port, ref fingerprint, ref code, ref serverId);

        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";
        port ??= 14777;
        if (port is < 1 or > 65535)
            throw new CliException("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new CliException(
                "Missing --fingerprint (or --uri from the plugin's pairing deep link).");
        }

        return new CliEndpoint(
            host,
            port.Value,
            CertificatePin.ParseExpected(fingerprint),
            string.IsNullOrWhiteSpace(code) ? null : code,
            string.IsNullOrWhiteSpace(serverId) ? null : serverId);
    }

    private static void ApplyUri(
        string uriText,
        ref string? host,
        ref int? port,
        ref string? fingerprint,
        ref string? code,
        ref string? serverId)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "freechat", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("--uri must be a freechat:// pairing link.");
        }

        var query = ParseQuery(uri.Query);
        if (!string.IsNullOrWhiteSpace(query.GetValueOrDefault("host")))
            host = query["host"];
        if (int.TryParse(query.GetValueOrDefault("port"), out var parsedPort))
            port = parsedPort;
        if (!string.IsNullOrWhiteSpace(query.GetValueOrDefault("fp")))
            fingerprint = query["fp"];
        if (!string.IsNullOrWhiteSpace(query.GetValueOrDefault("code")))
            code = query["code"];
        if (!string.IsNullOrWhiteSpace(query.GetValueOrDefault("sid")))
            serverId = query["sid"];
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var key = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            result[Uri.UnescapeDataString(key)] =
                Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }
}
