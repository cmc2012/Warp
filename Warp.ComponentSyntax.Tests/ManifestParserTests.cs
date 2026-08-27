using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Xunit;

namespace Warp.ComponentSyntax.Tests;

public sealed class ManifestParserTests
{
    [Fact]
    public void Rejects_json_manifest_input()
    {
        const string json = """
            { "package":"com.example.demo", "name":"Demo" }
            """;
        var sink = new DiagnosticSink();
        var manifest = new ManifestParser().Parse(json, "manifest.json", sink);

        Assert.True(sink.HasErrors);
        Assert.Equal("", manifest.Package);
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("manifest.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void Retains_platform_fields_from_yaml()
    {
        const string yaml = """
            package: com.example.demo
            name: Demo
            versionCode: 1
            icon: /icon.png
            minPlatformVersion: 1000
            minAPILevel: 2
            config:
              logLevel: log
              designWidth: device-width
            router:
              entry: pages/index
              pages:
                pages/index:
                  component: index
            """;
        var sink = new DiagnosticSink();
        var manifest = new ManifestParser().Parse(yaml, "manifest.yaml", sink);

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Equal(1000, manifest.MinPlatformVersion);
        Assert.Equal(2, manifest.MinApiLevel);
    }

    [Fact]
    public void Reads_compiler_only_identifier_minification_switch()
    {
        const string yaml = """
            package: com.example.demo
            name: Demo
            versionCode: 1
            icon: /icon.png
            config:
              minifyIdentifiers: false
            router:
              pages: { pages/index: { component: index } }
            """;
        var sink = new DiagnosticSink();
        var manifest = new ManifestParser().Parse(yaml, "manifest.yaml", sink);

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.False(manifest.Config.MinifyIdentifiers);
        Assert.DoesNotContain("minifyIdentifiers", DeviceManifestEmitter.Emit(manifest), StringComparison.Ordinal);
    }

    [Fact]
    public void Warns_and_defaults_to_minification_for_an_invalid_identifier_minification_switch()
    {
        const string yaml = """
            package: com.example.demo
            name: Demo
            versionCode: 1
            icon: /icon.png
            config: { minifyIdentifiers: sometimes }
            router:
              pages: { pages/index: { component: index } }
            """;
        var sink = new DiagnosticSink();
        var manifest = new ManifestParser().Parse(yaml, "manifest.yaml", sink);

        Assert.True(manifest.Config.MinifyIdentifiers);
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("minifyIdentifiers must be true or false", StringComparison.Ordinal));
    }
}
