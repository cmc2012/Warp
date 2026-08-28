using Warp.Diagnostics;
using Warp.Packaging;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Warp.Testing;
using Xunit;

namespace Warp.Packaging.Tests;

public sealed class RpkPackagerTests
{
    [Fact]
    public async Task Packages_build_files_and_emits_a_signed_archive()
    {
        var root = TestWorkspace.CreateDirectory("warp-package");
        var build = Path.Combine(root, "build");
        var output = Path.Combine(root, "app.rpk");
        try
        {
            Directory.CreateDirectory(Path.Combine(build, "common"));
            await File.WriteAllBytesAsync(Path.Combine(build, "app.jsc"), [0, 1, 2, 3], TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(build, "common", "asset.txt"), "asset", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(build, "manifest.json"), "{\"router\":{\"entry\":\"pages/home\"}}", TestContext.Current.CancellationToken);

            var sink = new DiagnosticSink();
            var result = await new RpkPackager(sink).PackAsync(build, output, TestContext.Current.CancellationToken);

            Assert.False(sink.HasErrors, string.Join(Environment.NewLine, sink.Diagnostics));
            Assert.Equal(output, result);
            var package = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
            Assert.True(package.Length > 4);
            Assert.Equal((byte)'P', package[0]);
            Assert.Equal((byte)'K', package[1]);
            Assert.True(package.AsSpan().IndexOf("RPK Sig Block 42"u8) >= 0);

            using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, entry => entry.FullName == "META-INF/");
            Assert.Contains(archive.Entries, entry => entry.FullName == "common/");
            Assert.Contains(archive.Entries, entry => entry.FullName == "META-INF/CERT");
            Assert.Contains(archive.Entries, entry => entry.FullName == "META-INF/build.txt");
            Assert.Equal("Warp", JsonNode.Parse(await ReadEntryAsync(archive, "manifest.json"))?["packageInfo"]?["toolkit"]?.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        await using var stream = archive.GetEntry(path)!.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reports_a_missing_build_directory_without_creating_an_archive()
    {
        var root = TestWorkspace.CreateDirectory("warp-missing-build");
        var sink = new DiagnosticSink();
        var output = Path.Combine(root, "output.rpk");
        try
        {
            var result = await new RpkPackager(sink).PackAsync(Path.Combine(root, "missing"), output, TestContext.Current.CancellationToken);

            Assert.Equal("", result);
            Assert.True(sink.HasErrors);
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Uses_project_debug_signing_material_when_present()
    {
        var root = TestWorkspace.CreateDirectory("warp-package");
        var build = Path.Combine(root, "build");
        var output = Path.Combine(root, "app.rpk");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sign", "debug"));
            Directory.CreateDirectory(build);
            await File.WriteAllBytesAsync(Path.Combine(build, "app.jsc"), [0, 1, 2, 3], TestContext.Current.CancellationToken);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=warp-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllTextAsync(Path.Combine(root, "sign", "debug", "private.pem"), key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "sign", "debug", "certificate.pem"), certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);

            var sink = new DiagnosticSink();
            var result = await new RpkPackager(sink).PackAsync(build, output, new RpkSigningOptions(ProjectDirectory: root), TestContext.Current.CancellationToken);

            Assert.False(sink.HasErrors, string.Join(Environment.NewLine, sink.Diagnostics));
            Assert.Equal(output, result);
            var package = await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken);
            Assert.True(package.AsSpan().IndexOf(certificate.Export(X509ContentType.Cert)) >= 0);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Rejects_a_build_without_an_app_entry()
    {
        var root = TestWorkspace.CreateDirectory("warp-package");
        var output = Path.Combine(root, "app.rpk");
        try
        {
            Directory.CreateDirectory(root);
            var sink = new DiagnosticSink();

            var result = await new RpkPackager(sink).PackAsync(root, output, TestContext.Current.CancellationToken);

            Assert.Equal("", result);
            Assert.True(sink.HasErrors);
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Warns_when_packaging_a_JavaScript_only_build()
    {
        var root = TestWorkspace.CreateDirectory("warp-package");
        var output = Path.Combine(root, "app.rpk");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "exports.default = {};", TestContext.Current.CancellationToken);
            var sink = new DiagnosticSink();

            var result = await new RpkPackager(sink).PackAsync(root, output, TestContext.Current.CancellationToken);

            Assert.False(sink.HasErrors, string.Join(Environment.NewLine, sink.Diagnostics));
            Assert.Equal(output, result);
            Assert.Contains(sink.Diagnostics, diagnostic =>
                diagnostic.Message.Contains("can be modified more easily", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
