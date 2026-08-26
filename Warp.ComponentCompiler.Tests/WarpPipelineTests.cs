using Warp.ComponentCompiler.Pipeline;
using System.Text.Json;
using Xunit;

namespace Warp.ComponentCompiler.Tests;

public sealed class WarpPipelineTests
{
    [Fact]
    public async Task Expands_an_explicitly_inline_stateless_component_into_its_host_template()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-inline-component-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            var component = Path.Combine(project, "src", "components", "ItemCard");
            Directory.CreateDirectory(page);
            Directory.CreateDirectory(component);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.inline
                name: Inline
                versionCode: 1
                icon: /icon.png
                config: { designWidth: device-width }
                router:
                  entry: pages/home
                  pages: { pages/home: { component: home } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "const FACTOR = 2; export default { private: { item: { label: 'slot' } }, read() { return FACTOR; } };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Import Name=\"ItemCard\" Source=\"../../components/ItemCard/ItemCard.wxaml\" Inline=\"true\" /><ItemCard item=\"{Binding item}\" /></Page>");
            await WriteAsync(Path.Combine(component, "ItemCard.js"), "export default { props: [\"item\"], data: {} };");
            await WriteAsync(Path.Combine(component, "ItemCard.wxaml"), "<Component><Component.Styles><Style Selector=\".slot\"><Setter Property=\"color\" Value=\"#fff\" /></Style></Component.Styles><Text Class=\"slot\" Text=\"{Binding item.label}\" /></Component>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var output = await ReadTextAsync(Path.Combine(project, "build", "pages", "home", "home.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.DoesNotContain("__cc__", output, StringComparison.Ordinal);
            Assert.DoesNotContain("require(\"../../components/ItemCard/ItemCard.js\")", output, StringComparison.Ordinal);
            Assert.Contains("(_vm_.item).label", output, StringComparison.Ordinal);
            Assert.Contains("private: {item:", output, StringComparison.Ordinal);
            Assert.DoesNotContain("data: {item:", output, StringComparison.Ordinal);
            Assert.Contains("moduleOwn._descriptor", output, StringComparison.Ordinal);
            Assert.Contains("const FACTOR = 2", output, StringComparison.Ordinal);
            Assert.Contains("slot", output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(project, "build", "components", "ItemCard", "ItemCard.jsc")));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Requires_a_top_level_yaml_manifest()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-manifest-location-" + Guid.NewGuid());
        try
        {
            var source = Path.Combine(project, "src");
            Directory.CreateDirectory(source);
            await WriteAsync(Path.Combine(project, "manifest.json"), "{\"package\":\"com.example.invalid\"}");
            await WriteAsync(Path.Combine(source, "manifest.yaml"), "package: com.example.invalid");

            var result = await new WarpPipeline(new BuildOptions(project)).BuildAsync(TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("missing project manifest", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Builds_a_discovered_page_into_a_parseable_runtime_module()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-pipeline-" + Guid.NewGuid());
        try
        {
            var pageDirectory = Path.Combine(project, "src", "pages", "home");
            Directory.CreateDirectory(pageDirectory);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.sample
                name: Sample
                versionCode: 1
                icon: /icon.png
                deviceTypeList:
                  - watch
                minPlatformVersion: 1000
                minAPILevel: 2
                config:
                  logLevel: log
                  designWidth: device-width
                router:
                  entry: pages/home
                  pages:
                    pages/home:
                      component: home
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(project, "src", "config-watch.json"), "{}");
            await WriteAsync(Path.Combine(pageDirectory, "home.js"), "export default { data: { title: 'Hello' } };");
            await WriteAsync(Path.Combine(pageDirectory, "home.wxaml"), "<Page x:Class=\"Home\"><Stack><Text Text=\"{Binding title}\" /></Stack></Page>");

            var result = await new WarpPipeline(new BuildOptions(project)).BuildAsync(TestContext.Current.CancellationToken);
            var output = Path.Combine(project, "build", "pages", "home", "home.jsc");

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.True(File.Exists(output));
            Assert.NotEmpty(await ReadBytesAsync(output));
            Assert.True(File.Exists(Path.Combine(project, "build", "app.jsc")));
            Assert.False(File.Exists(Path.Combine(project, "build", "app.js")));
            using var manifest = JsonDocument.Parse(await ReadTextAsync(Path.Combine(project, "build", "manifest.json")));
            Assert.Equal(1000, manifest.RootElement.GetProperty("minPlatformVersion").GetInt32());
            Assert.Equal(2, manifest.RootElement.GetProperty("minAPILevel").GetInt32());
            Assert.True(File.Exists(Path.Combine(project, "build", "config-watch.json")));
            Assert.True(File.Exists(Path.Combine(project, "build", "manifest-watch.json")));

        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Bundles_relative_script_modules_instead_of_leaving_runtime_relative_requires()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-relative-modules-" + Guid.NewGuid());
        try
        {
            var source = Path.Combine(project, "src");
            Directory.CreateDirectory(Path.Combine(source, "game"));
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.sample
                name: Sample
                versionCode: 1
                icon: /icon.png
                config:
                  designWidth: device-width
                router:
                  entry: pages/home
                  pages:
                    pages/home:
                      component: home
                """);
            await WriteAsync(Path.Combine(source, "app.js"), "import state from './game/state'; export default { data: state };");
            await WriteAsync(Path.Combine(source, "game", "state.js"), "import { score } from './values'; export default { score };");
            await WriteAsync(Path.Combine(source, "game", "values.js"), "export const score = 42;");
            var page = Path.Combine(source, "pages", "home");
            Directory.CreateDirectory(page);
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page x:Class=\"Home\"><Stack /></Page>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var javascript = await ReadTextAsync(Path.Combine(project, "build", "app.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("__warp_modules__", javascript);
            Assert.Contains("__warp_require__(\"game/state.js\")", javascript);
            Assert.Contains("__warp_require__(\"game/values.js\")", javascript);
            Assert.Contains("exports: {__esModule: true}", javascript);
            Assert.Contains("export default function(global, globalThis, window, $app_exports$, $app_evaluate$)", javascript);
            Assert.Contains("createAppHandler", javascript);
            Assert.DoesNotContain("$app_require$(\"./game", javascript);
            Assert.True(File.Exists(Path.Combine(project, "build", "app.jsc")));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    private static Task WriteAsync(string path, string contents)
        => File.WriteAllTextAsync(path, contents, TestContext.Current.CancellationToken);

    private static Task<string> ReadTextAsync(string path)
        => File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

    private static Task<byte[]> ReadBytesAsync(string path)
        => File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
}
