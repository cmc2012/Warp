using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Warp.Packaging;

/// <summary>Writes the legacy ZIP dialect used by the target packaging tool through its JSZip-compatible encoder.</summary>
internal static class ReferenceZipArchiveWriter
{
    private const string Script = """
        const fs = require('fs');
        const JSZip = require(process.argv[1]);
        const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
        const zip = new JSZip();
        for (const entry of input.entries) {
          if (entry.directory) zip.file(entry.path, null, { dir: true });
          else zip.file(entry.path, Buffer.from(entry.content, 'base64'));
        }
        zip.generateAsync({ type: 'nodebuffer', compression: 'DEFLATE', compressionOptions: { level: 9 }, comment: input.comment })
          .then(buffer => fs.writeFileSync(process.argv[3], buffer))
          .catch(error => { console.error(error.stack || error.message); process.exitCode = 1; });
        """;

    public static async Task<byte[]> CreateAsync(IEnumerable<(string Path, byte[] Content)> files, string comment, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "warp-rpk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var library = Path.Combine(directory, "jszip.min.js");
            var input = Path.Combine(directory, "input.json");
            var output = Path.Combine(directory, "output.zip");
            await WriteEmbeddedLibraryAsync(library, ct);
            var payload = new
            {
                comment,
                entries = files.Select(file => new
                {
                    path = file.Path,
                    directory = file.Path.EndsWith("/", StringComparison.Ordinal),
                    content = file.Path.EndsWith("/", StringComparison.Ordinal) ? null : Convert.ToBase64String(file.Content)
                })
            };
            await File.WriteAllTextAsync(input, JsonSerializer.Serialize(payload), ct);

            var startInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(Script);
            startInfo.ArgumentList.Add(library);
            startInfo.ArgumentList.Add(input);
            startInfo.ArgumentList.Add(output);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("could not start the Node.js ZIP encoder");
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new InvalidOperationException($"reference ZIP encoder failed: {error.Trim()}");
            return await File.ReadAllBytesAsync(output, ct);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteEmbeddedLibraryAsync(string path, CancellationToken ct)
    {
        var assembly = typeof(ReferenceZipArchiveWriter).Assembly;
        await using var source = assembly.GetManifestResourceStream("Warp.Packaging.Resources.Zip.jszip.min.js")
            ?? throw new InvalidOperationException("embedded JSZip resource was not found");
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination, ct);
    }
}
