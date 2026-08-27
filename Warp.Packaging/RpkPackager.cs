using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Warp.Diagnostics;

namespace Warp.Packaging;

/// <summary>Produces a Vela/Quick App RPK, including the required RPK signature blocks.</summary>
public sealed class RpkPackager
{
    private const string MetaDirectory = "META-INF";
    private const string CertificatePath = "META-INF/CERT";
    private readonly DiagnosticSink _sink;

    public RpkPackager(DiagnosticSink sink) => _sink = sink;

    public Task<string> PackAsync(string buildDir, string outputRpk, CancellationToken ct = default) =>
        PackAsync(buildDir, outputRpk, new RpkSigningOptions(), ct);

    public async Task<string> PackAsync(string buildDir, string outputRpk, RpkSigningOptions signing, CancellationToken ct = default)
    {
        if (!Directory.Exists(buildDir))
        {
            _sink.Error($"build dir not found: {buildDir}");
            return "";
        }
        var hasBytecodeEntry = File.Exists(Path.Combine(buildDir, "app.jsc"));
        var hasJavaScriptEntry = File.Exists(Path.Combine(buildDir, "app.js"));
        // AIoT debug builds set enableJsc=false so that HMR can replace the
        // JavaScript sources at runtime.  A deployable package therefore needs
        // an app entry in either supported representation, not specifically JSC.
        if (!hasBytecodeEntry && !hasJavaScriptEntry)
        {
            _sink.Error($"build output has no app entry (app.jsc or app.js) in {buildDir}. Run 'warp build --output {Path.GetFileName(buildDir)}' before packing.");
            return "";
        }
        if (!hasBytecodeEntry)
        {
            _sink.Warning("Packaging JavaScript without JSC bytecode: the package can be modified more easily.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputRpk) ?? ".");

        var buildMetadata = BuildMetadata.Create();
        await UpdateManifestAsync(buildDir, buildMetadata, ct);

        // META-INF is generated for each archive. Never package stale metadata from a prior run.
        var contents = new List<RpkFile>();
        foreach (var file in Directory.GetFiles(buildDir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var path = Path.GetRelativePath(buildDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (path.StartsWith(MetaDirectory + "/", StringComparison.Ordinal)) continue;
            contents.Add(new RpkFile(path, await File.ReadAllBytesAsync(file, ct)));
        }

        if (contents.Count == 0)
        {
            _sink.Error("build dir contains no files to package");
            return "";
        }

        // The toolkit writes build metadata to the build directory, but does
        // not include META-INF in the digest stored in CERT.  The directory is
        // also the source for a hot-reload diff, so it must have the same
        // layout as aiotpack's build output.
        var buildInfo = buildMetadata.ToBuildInfo();
        var entryPage = ReadEntryPage(contents);
        contents = OrderFiles(contents, entryPage).ToList();
        var digestDocument = JsonSerializer.SerializeToUtf8Bytes(new
        {
            algorithm = "SHA-256",
            digests = contents.ToDictionary(x => x.Path, x => Convert.ToHexString(SHA256.HashData(x.Content)).ToLowerInvariant(), StringComparer.Ordinal)
        });

        try
        {
            using var signingMaterial = LoadSigningMaterial(signing);

            var comment = buildMetadata.ToJson();
            var certZip = await CreateZipAsync([new RpkFile("hash.json", digestDocument)], comment, ct);
            var signedCert = RpkSignature.Sign(certZip,
                [new FileDigest("hash.json", SHA256.HashData(certZip))], signingMaterial.Key, signingMaterial.Certificate);

            var metadataDirectory = Path.Combine(buildDir, MetaDirectory);
            Directory.CreateDirectory(metadataDirectory);
            await File.WriteAllBytesAsync(Path.Combine(metadataDirectory, "build.txt"), buildInfo, ct);
            // aiotpack leaves this unsigned digest ZIP in build/.  Its full
            // package gets a signed copy, while a .diff.rpk directly unzips
            // this build-directory version onto the device.
            await File.WriteAllBytesAsync(Path.Combine(metadataDirectory, "CERT"), certZip, ct);

            contents.Add(new RpkFile("META-INF/build.txt", buildInfo));
            // The toolkit keeps directory entries in the ZIP, but signs only
            // real files.  Including generated directories in the signature
            // list makes the device reject the package before it extracts
            // manifest.json; omitting them from the ZIP itself is incompatible
            // with the target's unpacker.
            var packageFiles = ExpandDirectories(OrderFiles([new RpkFile(CertificatePath, signedCert), .. contents], entryPage)).ToList();
            var packageZip = await CreateZipAsync(packageFiles, comment, ct);
            var signedPackage = RpkSignature.Sign(packageZip,
                packageFiles.Where(x => !x.Path.EndsWith("/", StringComparison.Ordinal))
                    .Select(x => new FileDigest(x.Path, SHA256.HashData(x.Content))).ToArray(),
                signingMaterial.Key, signingMaterial.Certificate);

            await File.WriteAllBytesAsync(outputRpk, signedPackage, ct);
            _sink.Info("I-PACK-001", $"packed {buildDir} -> {outputRpk} ({signedPackage.Length} bytes, {contents.Count} files, signed=true)");
            return outputRpk;
        }
        catch (Exception ex)
        {
            _sink.Error($"RPK signing failed: {ex.Message}");
            return "";
        }
    }

    private static async Task UpdateManifestAsync(string buildDir, BuildMetadata metadata, CancellationToken ct)
    {
        var path = Path.Combine(buildDir, "manifest.json");
        if (!File.Exists(path)) return;

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject();
        if (root is null) throw new InvalidDataException($"manifest is not a JSON object: {path}");
        if (root["minAPILevel"] is null) root["minAPILevel"] = 1;
        // Preserve user/tooling supplied metadata, but refresh metadata that this
        // packer wrote on an earlier invocation so the manifest and ZIP comment
        // always describe the same artifact.
        if (root["packageInfo"] is not JsonObject packageInfo || packageInfo["toolkit"]?.GetValue<string>() == "Warp")
            root["packageInfo"] = metadata.ToJsonObject();
        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    private static string? ReadEntryPage(IEnumerable<RpkFile> files)
    {
        var manifest = files.FirstOrDefault(file => file.Path == "manifest.json");
        if (manifest is null) return null;
        try
        {
            return JsonNode.Parse(manifest.Content)?["router"]?["entry"]?.GetValue<string>()?.Trim('/');
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<RpkFile> OrderFiles(IEnumerable<RpkFile> files, string? entryPage) =>
        files.OrderBy(file => RpkPathComparer.Priority(file.Path, entryPage)).ThenBy(file => file.Path, StringComparer.Ordinal);

    private static IEnumerable<RpkFile> ExpandDirectories(IEnumerable<RpkFile> files)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.Path.Split('/');
            for (var length = 1; length < segments.Length; length++)
            {
                var directory = string.Join('/', segments.Take(length)) + "/";
                if (emitted.Add(directory)) yield return new RpkFile(directory, []);
            }
            if (emitted.Add(file.Path)) yield return file;
        }
    }

    private static Task<byte[]> CreateZipAsync(IEnumerable<RpkFile> files, string comment, CancellationToken ct) =>
        ReferenceZipArchiveWriter.CreateAsync(files.Select(file => (file.Path, file.Content)), comment, ct);

    private static SigningMaterial LoadSigningMaterial(RpkSigningOptions options)
    {
        foreach (var candidate in options.Candidates())
        {
            if (!File.Exists(candidate.PrivateKeyPath) || !File.Exists(candidate.CertificatePath)) continue;
            var certificate = X509Certificate2.CreateFromPemFile(candidate.CertificatePath, candidate.PrivateKeyPath);
            var key = certificate.GetRSAPrivateKey()
                ?? throw new CryptographicException("configured certificate does not contain an RSA private key");
            return new SigningMaterial(key, certificate);
        }
        return new SigningMaterial(LoadEmbeddedPrivateKey(), LoadEmbeddedCertificate());
    }

    private static RSA LoadEmbeddedPrivateKey()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(ReadEmbeddedText("private.pem"));
        return rsa;
    }

    private static X509Certificate2 LoadEmbeddedCertificate() => X509Certificate2.CreateFromPem(ReadEmbeddedText("certificate.pem"));

    private static string ReadEmbeddedText(string fileName)
    {
        var assembly = typeof(RpkPackager).Assembly;
        var resource = assembly.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith('.' + fileName, StringComparison.Ordinal));
        if (resource is null) throw new InvalidOperationException($"embedded signing resource '{fileName}' was not found");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record RpkFile(string Path, byte[] Content);
    private sealed record FileDigest(string Path, byte[] Digest);
    private sealed class SigningMaterial(RSA key, X509Certificate2 certificate) : IDisposable
    {
        public RSA Key { get; } = key;
        public X509Certificate2 Certificate { get; } = certificate;
        public void Dispose() { Certificate.Dispose(); Key.Dispose(); }
    }

    private sealed record BuildMetadata(string Toolkit, DateTimeOffset Timestamp, string Platform, string Architecture)
    {
        public static BuildMetadata Create() => new("Warp", DateTimeOffset.UtcNow, OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "unknown", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        private string TimestampText => Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        public JsonObject ToJsonObject() => new()
        {
            ["toolkit"] = Toolkit,
            ["timeStamp"] = TimestampText,
            ["node"] = "dotnet",
            ["platform"] = Platform,
            ["arch"] = Architecture,
            ["component"] = true
        };
        public string ToJson() => ToJsonObject().ToJsonString();
        public byte[] ToBuildInfo() => Encoding.UTF8.GetBytes($"originType=undefined\ntoolkit={Toolkit}\ntimeStamp={TimestampText}\nnode=dotnet\nplatform={Platform}\narch={Architecture}\ncomponent=true");
    }

    private static class RpkPathComparer
    {
        public static int Priority(string? path, string? entryPage) => path switch
        {
            CertificatePath => 0,
            "manifest.json" => 3,
            "app.js" => 4,
            "META-INF/build.txt" => 11,
            _ when path?.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase) == true => 1,
            _ when path is not null && path.StartsWith("manifest-", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) => 2,
            _ when path is not null && entryPage is not null && (path == entryPage || path.StartsWith(entryPage + "/", StringComparison.Ordinal)) => 7,
            _ when path?.StartsWith("common/", StringComparison.OrdinalIgnoreCase) == true => 8,
            _ when path?.EndsWith(".js", StringComparison.OrdinalIgnoreCase) == true => 10,
            _ => 9
        };
    }

    private static class RpkSignature
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RPK Sig Block 42");

        public static byte[] Sign(byte[] zip, IReadOnlyList<FileDigest> files, RSA key, X509Certificate2 certificate)
        {
            var eocd = FindEocd(zip);
            var centralOffset = ReadInt32(zip, eocd + 16);
            var headerHash = SectionHash(zip, 0, centralOffset);
            var centralHash = SectionHash(zip, centralOffset, eocd - centralOffset);
            var footerHash = SectionHash(zip, eocd, zip.Length - eocd);

            using var whole = new MemoryStream();
            whole.WriteByte(0x5a); WriteInt32(whole, 3); whole.Write(headerHash); whole.Write(centralHash); whole.Write(footerHash);
            var archiveDigest = SHA256.HashData(whole.ToArray());
            var signatureBlock = BuildSignatureBlock(archiveDigest, files, key, certificate);

            var result = new byte[zip.Length + signatureBlock.Length];
            Buffer.BlockCopy(zip, 0, result, 0, centralOffset);
            Buffer.BlockCopy(signatureBlock, 0, result, centralOffset, signatureBlock.Length);
            Buffer.BlockCopy(zip, centralOffset, result, centralOffset + signatureBlock.Length, zip.Length - centralOffset);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(eocd + signatureBlock.Length + 16, 4), centralOffset + signatureBlock.Length);
            return result;
        }

        private static byte[] BuildSignatureBlock(byte[] archiveDigest, IReadOnlyList<FileDigest> files, RSA key, X509Certificate2 certificate)
        {
            var cert = certificate.Export(X509ContentType.Cert);
            var publicKey = certificate.GetRSAPublicKey()?.ExportSubjectPublicKeyInfo()
                ?? throw new CryptographicException("certificate does not contain an RSA public key");

            // This layout is deliberately compatible with the Vela aiotpack SignUtil
            // implementation. In particular, the digest and certificate collections
            // each have a length prefix; omitting them causes the device verifier to
            // misread the signing block and can crash while it unpacks META-INF/CERT.
            using var digest = new MemoryStream();
            WriteInt32(digest, archiveDigest.Length + 8);
            WriteInt32(digest, 0x0103);
            WriteInt32(digest, archiveDigest.Length);
            digest.Write(archiveDigest);
            var digestBytes = digest.ToArray();

            using var certBlock = new MemoryStream();
            WriteInt32(certBlock, cert.Length);
            certBlock.Write(cert);
            var certBytes = certBlock.ToArray();

            using var signedData = new MemoryStream();
            WriteInt32(signedData, digestBytes.Length);
            signedData.Write(digestBytes);
            WriteInt32(signedData, certBytes.Length);
            signedData.Write(certBytes);
            WriteInt32(signedData, 0);
            var signedDataBytes = signedData.ToArray();
            var mainSignature = key.SignData(signedDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // signer block: signer-size, signed-data, signature collection and SPKI
            using var mainValue = new MemoryStream();
            var signerSize = 4 + signedDataBytes.Length + 4 + (12 + mainSignature.Length) + 4 + publicKey.Length;
            WriteInt32(mainValue, signerSize + 4); // signBlocks.size
            WriteInt32(mainValue, signerSize);
            WriteInt32(mainValue, signedDataBytes.Length);
            mainValue.Write(signedDataBytes);
            WriteInt32(mainValue, mainSignature.Length + 12); // signatures.size
            WriteInt32(mainValue, mainSignature.Length + 8);
            WriteInt32(mainValue, 0x0103);
            WriteInt32(mainValue, mainSignature.Length);
            mainValue.Write(mainSignature);
            WriteInt32(mainValue, publicKey.Length);
            mainValue.Write(publicKey);
            var mainValueBytes = mainValue.ToArray();

            var fileSignatures = BuildFileSignatures(files, key);
            using var fileValue = new MemoryStream();
            WriteInt32(fileValue, fileSignatures.Length); // filesignBlocks.size
            fileValue.Write(fileSignatures);
            using var payload = new MemoryStream();
            WriteKeyValue(payload, 0x01000101, mainValueBytes);
            WriteKeyValue(payload, 0x01000201, fileValue.ToArray());
            var payloadBytes = payload.ToArray();

            using var block = new MemoryStream();
            WriteInt32(block, payloadBytes.Length + 24); WriteInt32(block, 0); block.Write(payloadBytes);
            WriteInt32(block, payloadBytes.Length + 24); WriteInt32(block, 0); block.Write(Magic);
            return block.ToArray();
        }

        private static byte[] BuildFileSignatures(IReadOnlyList<FileDigest> files, RSA key)
        {
            using var digestData = new MemoryStream();
            WriteInt32(digestData, 0x0103);
            foreach (var file in files)
            {
                WriteInt32(digestData, unchecked((int)Crc32(file.Path)));
                WriteInt16(digestData, checked((short)file.Digest.Length));
                digestData.Write(file.Digest);
            }
            var digestDataBytes = digestData.ToArray();
            var signature = key.SignData(digestDataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var signedList = new MemoryStream();
            WriteInt32(signedList, digestDataBytes.Length); signedList.Write(digestDataBytes);
            WriteInt32(signedList, signature.Length + 8); WriteInt32(signedList, 0x0103); WriteInt32(signedList, signature.Length); signedList.Write(signature);
            var signedListBytes = signedList.ToArray();
            using var value = new MemoryStream();
            WriteInt32(value, signedListBytes.Length); value.Write(signedListBytes);
            return value.ToArray();
        }

        private static void WriteKeyValue(Stream stream, int id, byte[] value)
        {
            WriteInt32(stream, value.Length + 4); WriteInt32(stream, 0); WriteInt32(stream, id); stream.Write(value);
        }

        private static byte[] SectionHash(byte[] source, int offset, int length)
        {
            using var stream = new MemoryStream(length + 5);
            stream.WriteByte(0xa5); WriteInt32(stream, length); stream.Write(source, offset, length);
            return SHA256.HashData(stream.ToArray());
        }

        private static int FindEocd(byte[] zip)
        {
            for (var offset = zip.Length - 22; offset >= 0; offset--)
                if (ReadInt32(zip, offset) == 0x06054b50) return offset;
            throw new InvalidDataException("ZIP end-of-central-directory record was not found");
        }

        private static int ReadInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        private static void WriteInt32(Stream stream, int value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); stream.Write(bytes); }
        private static void WriteInt16(Stream stream, short value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(bytes, value); stream.Write(bytes); }

        private static uint Crc32(string value)
        {
            uint crc = 0xffffffff;
            foreach (var b in Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n")))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }
    }
}
