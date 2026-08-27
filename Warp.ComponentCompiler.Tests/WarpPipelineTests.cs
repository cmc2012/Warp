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
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Import Name=\"ItemCard\" Source=\"../../components/ItemCard/ItemCard.wxaml\" Inline=\"true\" /><ItemCard Item=\"{Binding item}\" /></Page>");
            await WriteAsync(Path.Combine(component, "ItemCard.js"), "export default { props: [\"item\"], data: {} };");
            await WriteAsync(Path.Combine(component, "ItemCard.wxaml"), "<Component><Component.Styles><Style Class=\"slot\"><Setter Property=\"color\" Value=\"#fff\" /></Style></Component.Styles><Text Class=\"slot\" Text=\"{Binding item.label}\" /></Component>");

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
    public async Task Merges_inline_methods_and_nested_lifecycles_into_the_page()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-inline-behavior-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            var outer = Path.Combine(project, "src", "components", "Outer");
            var inner = Path.Combine(project, "src", "components", "Inner");
            Directory.CreateDirectory(page);
            Directory.CreateDirectory(outer);
            Directory.CreateDirectory(inner);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.inlinebehavior
                name: InlineBehavior
                versionCode: 1
                icon: /icon.png
                config: { designWidth: device-width }
                router: { entry: pages/home, pages: { pages/home: { component: home } } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {}, onReady() { return this.work(); }, work() { return 1; } };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Import Name=\"Outer\" Source=\"../../components/Outer/Outer.wxaml\" Inline=\"true\" /><Button onTap=\"work\" /><Outer /></Page>");
            await WriteAsync(Path.Combine(outer, "Outer.js"), "export default { data: {}, onReady() { outerReady(); }, tap() { tapped(); } };");
            await WriteAsync(Path.Combine(outer, "Outer.wxaml"), "<Component><Import Name=\"Inner\" Source=\"../Inner/Inner.wxaml\" Inline=\"true\" /><Button onTap=\"tap\" /><Inner /></Component>");
            await WriteAsync(Path.Combine(inner, "Inner.js"), "export default { data: {}, onReady() { innerReady(); }, select() { selected(); } };");
            await WriteAsync(Path.Combine(inner, "Inner.wxaml"), "<Component><Button onTap=\"select\" /></Component>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var output = await ReadTextAsync(Path.Combine(project, "build", "pages", "home", "home.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("a()", output, StringComparison.Ordinal);
            Assert.Contains("b()", output, StringComparison.Ordinal);
            Assert.Contains("c()", output, StringComparison.Ordinal);
            Assert.Contains("_vm_.a", output, StringComparison.Ordinal);
            Assert.Contains("_vm_.b", output, StringComparison.Ordinal);
            Assert.Contains("_vm_.c", output, StringComparison.Ordinal);
            Assert.DoesNotContain("__warp_inline_", output, StringComparison.Ordinal);
            Assert.DoesNotContain("this.work", output, StringComparison.Ordinal);
            Assert.Contains("outerReady()", output, StringComparison.Ordinal);
            Assert.Contains("innerReady()", output, StringComparison.Ordinal);
            Assert.DoesNotContain("__cc__", output, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(project, "build", "components", "Outer", "Outer.jsc")));
            Assert.False(File.Exists(Path.Combine(project, "build", "components", "Inner", "Inner.jsc")));
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Projects_inline_props_into_merged_method_bodies_at_each_call_site()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-inline-prop-method-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            var component = Path.Combine(project, "src", "components", "Row");
            Directory.CreateDirectory(page);
            Directory.CreateDirectory(component);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.inlineprops
                name: InlineProps
                versionCode: 1
                icon: /icon.png
                config: { minifyIdentifiers: false }
                router: { entry: pages/home, pages: { pages/home: { component: home } } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {}, private: { first: { id: 1 }, second: { id: 2 } } };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Import Name=\"Row\" Source=\"../../components/Row/Row.wxaml\" Inline=\"true\" /><Row Item=\"{Binding first}\" /><Row Item=\"{Binding second}\" /></Page>");
            await WriteAsync(Path.Combine(component, "Row.js"), "export default { props: [\"item\"], data: {}, onReady() { return this.item.id; }, tap() { return this.item.id; } };");
            await WriteAsync(Path.Combine(component, "Row.wxaml"), "<Component><div onTap=\"tap\" /></Component>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var output = await ReadTextAsync(Path.Combine(project, "build", "pages", "home", "home.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("this.first", output, StringComparison.Ordinal);
            Assert.Contains("this.second", output, StringComparison.Ordinal);
            Assert.DoesNotContain("this.item", output, StringComparison.Ordinal);
            Assert.Equal(2, output.Split("this.first", StringSplitOptions.None).Length - 1);
            Assert.Equal(2, output.Split("this.second", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Gives_each_inline_import_its_own_method_name_even_when_they_share_a_source_file()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-inline-aliases-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            var component = Path.Combine(project, "src", "components", "Action");
            Directory.CreateDirectory(page);
            Directory.CreateDirectory(component);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.inlinealiases
                name: InlineAliases
                versionCode: 1
                icon: /icon.png
                config: { designWidth: device-width }
                router: { entry: pages/home, pages: { pages/home: { component: home } } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Import Name=\"First\" Source=\"../../components/Action/Action.wxaml\" Inline=\"true\" /><Import Name=\"Second\" Source=\"../../components/Action/Action.wxaml\" Inline=\"true\" /><First /><Second /></Page>");
            await WriteAsync(Path.Combine(component, "Action.js"), "export default { data: {}, tap() { return 1; } };");
            await WriteAsync(Path.Combine(component, "Action.wxaml"), "<Component><Button onTap=\"tap\" /></Component>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var output = await ReadTextAsync(Path.Combine(project, "build", "pages", "home", "home.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("_vm_.a", output, StringComparison.Ordinal);
            Assert.Contains("_vm_.b", output, StringComparison.Ordinal);
            Assert.Equal(2, output.Split("return 1", StringSplitOptions.None).Length - 1);
        }
        finally
        {
            if (Directory.Exists(project)) Directory.Delete(project, recursive: true);
        }
    }

    [Fact]
    public async Task Preserves_method_names_when_a_dynamic_this_member_access_is_present()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-dynamic-method-name-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            Directory.CreateDirectory(page);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.dynamicmethod
                name: DynamicMethod
                versionCode: 1
                icon: /icon.png
                config: { designWidth: device-width }
                router: { entry: pages/home, pages: { pages/home: { component: home } } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {}, tap() { return 1; }, invoke(name) { return this[name](); } };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Button onTap=\"tap\" /></Page>");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);
            var output = await ReadTextAsync(Path.Combine(project, "build", "pages", "home", "home.js"));

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Contains("tap(){return 1;}", output, StringComparison.Ordinal);
            Assert.Contains("_vm_.tap", output, StringComparison.Ordinal);
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
    public async Task Removes_stale_bytecode_from_the_generated_output_directory()
    {
        var project = Path.Combine(Path.GetTempPath(), "warp-clean-output-" + Guid.NewGuid());
        try
        {
            var page = Path.Combine(project, "src", "pages", "home");
            var stale = Path.Combine(project, "build", "pages", "removed", "removed.jsc");
            Directory.CreateDirectory(page);
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            await WriteAsync(Path.Combine(project, "manifest.yaml"), """
                package: com.example.clean
                name: Clean
                versionCode: 1
                icon: /icon.png
                config: { designWidth: device-width }
                router: { entry: pages/home, pages: { pages/home: { component: home } } }
                """);
            await WriteAsync(Path.Combine(project, "src", "app.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.js"), "export default { data: {} };");
            await WriteAsync(Path.Combine(page, "home.wxaml"), "<Page><Text Text=\"ok\" /></Page>");
            await WriteAsync(stale, "stale bytecode");

            var result = await new WarpPipeline(new BuildOptions(project, KeepJavaScript: true)).BuildAsync(TestContext.Current.CancellationToken);

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(Path.Combine(project, "build", "pages", "home", "home.js")));
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
