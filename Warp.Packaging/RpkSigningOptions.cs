namespace Warp.Packaging;

/// <summary>Locates project signing material without coupling packaging to a fixed certificate.</summary>
public sealed record RpkSigningOptions(
    string? ProjectDirectory = null,
    string? PrivateKeyPath = null,
    string? CertificatePath = null)
{
    internal IEnumerable<(string PrivateKeyPath, string CertificatePath)> Candidates()
    {
        if (PrivateKeyPath is not null || CertificatePath is not null)
        {
            if (PrivateKeyPath is null || CertificatePath is null)
                throw new ArgumentException("both private key and certificate paths are required");
            yield return (PrivateKeyPath, CertificatePath);
        }

        if (ProjectDirectory is null) yield break;
        var root = Path.GetFullPath(ProjectDirectory);
        yield return (Path.Combine(root, "sign", "debug", "private.pem"), Path.Combine(root, "sign", "debug", "certificate.pem"));
        yield return (Path.Combine(root, "sign", "private.pem"), Path.Combine(root, "sign", "certificate.pem"));
    }
}
