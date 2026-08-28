using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Planner.Api.Auth;

/// <summary>Provides the signing and encryption certificates OpenIddict needs.
///
/// OpenIddict's development certificates live in the machine's X.509 store, which is empty again the
/// moment a container restarts — every restart would invalidate every issued token. These are
/// generated once into a mounted directory instead, so tokens survive restarts and upgrades on an
/// on-prem box. Production installs should mount certificates issued by the organisation's own CA
/// into the same directory.</summary>
public static class ServerCertificates
{
    private const string SigningFileName = "signing.pfx";
    private const string EncryptionFileName = "encryption.pfx";

    public static X509Certificate2 GetOrCreateSigning(string directory, string password) =>
        GetOrCreate(
            Path.Combine(directory, SigningFileName),
            password,
            "CN=Planner Server Signing Certificate",
            X509KeyUsageFlags.DigitalSignature);

    public static X509Certificate2 GetOrCreateEncryption(string directory, string password) =>
        GetOrCreate(
            Path.Combine(directory, EncryptionFileName),
            password,
            "CN=Planner Server Encryption Certificate",
            X509KeyUsageFlags.KeyEncipherment);

    private static X509Certificate2 GetOrCreate(
        string path,
        string password,
        string subject,
        X509KeyUsageFlags usage)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(path),
                password,
                X509KeyStorageFlags.EphemeralKeySet);
        }

        using var algorithm = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName(subject),
            algorithm,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: true));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        var exported = certificate.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(path, exported);

        return X509CertificateLoader.LoadPkcs12(exported, password, X509KeyStorageFlags.EphemeralKeySet);
    }
}
