using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FreeChat.Server;

public static class TlsCertificate
{
    private const string PfxPassword = "freechat-server";

    public static X509Certificate2 CreateSelfSigned(string commonName = "FreeChat Server")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: false));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));
    }

    /// <summary>
    /// SslStream on Windows rejects certificates whose private key is
    /// ephemeral. Round-trip through PKCS#12 so the key is persisted for the
    /// current user.
    /// </summary>
    public static X509Certificate2 CreateServerCertificate(string commonName = "FreeChat Server")
    {
        using var ephemeral = CreateSelfSigned(commonName);
        var pfx = ephemeral.Export(X509ContentType.Pfx, PfxPassword);
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            PfxPassword,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    public static byte[] Fingerprint(X509Certificate2 certificate)
    {
        using var ecdsa = certificate.GetECDsaPublicKey()
            ?? throw new InvalidOperationException("The certificate does not contain an ECDSA public key.");

        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        if (parameters.Q.X is null || parameters.Q.Y is null)
            throw new InvalidOperationException("The ECDSA public key is incomplete.");

        var raw = new byte[65];
        raw[0] = 0x04;
        parameters.Q.X.CopyTo(raw, 1);
        parameters.Q.Y.CopyTo(raw, 33);
        return SHA256.HashData(raw);
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

public sealed class CertificateManager : IDisposable
{
    private static readonly byte[] Entropy = "FreeChat.Server.v1"u8.ToArray();

    public CertificateManager(X509Certificate2 certificate) => this.Certificate = certificate;

    public X509Certificate2 Certificate { get; }
    public byte[] Fingerprint => TlsCertificate.Fingerprint(this.Certificate);

    public static CertificateManager LoadOrCreate(string? path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new CertificateManager(TlsCertificate.CreateServerCertificate());

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        if (File.Exists(fullPath))
        {
            var protectedPfx = File.ReadAllBytes(fullPath);
            var pfx = ProtectedData.Unprotect(protectedPfx, Entropy, DataProtectionScope.CurrentUser);
            var certificate = X509CertificateLoader.LoadPkcs12(
                pfx,
                password,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            return new CertificateManager(certificate);
        }

        var created = TlsCertificate.CreateServerCertificate();
        var exported = created.Export(X509ContentType.Pfx, password);
        var protectedBytes = ProtectedData.Protect(exported, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(fullPath, protectedBytes);
        return new CertificateManager(created);
    }

    public void Dispose() => this.Certificate.Dispose();
}
