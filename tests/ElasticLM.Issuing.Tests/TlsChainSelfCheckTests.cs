using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ElasticLM.Issuing.Tests;

/// <summary>
/// Which certificate that a server sent stands for an intermediate of its certificate file in the check at the start
/// (design §13.5, V1d3a). The check itself runs on Windows only; this rule is tested on every operating system.
/// </summary>
public sealed class TlsChainSelfCheckTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A sent certificate stands for the file's intermediate when it is that certificate, or another certificate of the same
    /// CA (the same subject and key) within its period; not otherwise.
    /// </summary>
    /// <remarks>
    /// Preconditions:
    /// - A root valid from Start − 2 days for 90 days; an intermediate CA signed by it, valid from Start − 1 day for 30 days;
    ///   a renewed certificate of the same intermediate (the same subject and key, another serial number), valid from Start
    ///   for 30 days; a certificate of another CA with the same subject and another key; a certificate of the same key under
    ///   another subject.
    ///
    /// Steps:
    /// - Ask whether each stands for the intermediate at Start + 1 day, and the renewed one also at Start − 1 hour and at
    ///   Start + 31 days.
    ///
    /// Expected:
    /// - The intermediate itself: yes, also at Start + 31 days. The renewed one: yes at Start + 1 day; no before its start and
    ///   after its end. The other key and the other subject: no.
    /// </remarks>
    [Fact]
    public void StandsFor_EachCandidate_FollowsNameKeyAndPeriod()
    {
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 root = Authority("CN=test root", rootKey, null, Start.AddDays(-2), 90);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 intermediate = Authority("CN=test intermediate", key, (root, rootKey), Start.AddDays(-1), 30);
        using X509Certificate2 renewed = Authority("CN=test intermediate", key, (root, rootKey), Start, 30);
        using X509Certificate2 otherCa = Authority("CN=test intermediate", otherKey, (root, rootKey), Start, 30);
        using X509Certificate2 otherName = Authority("CN=another intermediate", key, (root, rootKey), Start, 30);
        DateTimeOffset now = Start.AddDays(1);

        Assert.True(TlsChainSelfCheck.StandsFor(intermediate, intermediate, now));
        Assert.True(TlsChainSelfCheck.StandsFor(intermediate, intermediate, Start.AddDays(31)));
        Assert.True(TlsChainSelfCheck.StandsFor(renewed, intermediate, now));
        Assert.False(TlsChainSelfCheck.StandsFor(renewed, intermediate, Start.AddHours(-1)));
        Assert.False(TlsChainSelfCheck.StandsFor(renewed, intermediate, Start.AddDays(31)));
        Assert.False(TlsChainSelfCheck.StandsFor(otherCa, intermediate, now));
        Assert.False(TlsChainSelfCheck.StandsFor(otherName, intermediate, now));
    }

    private static X509Certificate2 Authority(
        string subject,
        ECDsa key,
        (X509Certificate2 Certificate, ECDsa Key)? issuer,
        DateTimeOffset notBefore,
        int days
    )
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return issuer is (X509Certificate2 certificate, _)
            ? request.Create(certificate, notBefore, notBefore.AddDays(days), RandomNumberGenerator.GetBytes(16))
            : request.CreateSelfSigned(notBefore, notBefore.AddDays(days));
    }
}
