using Warp.JsCompiler.Api;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Validates a realistic dependency graph whose modules exercise independent compiler paths.</summary>
public sealed class MultiFileExtremeIntegrationTests
{
    [Fact]
    public void Compiles_deep_async_private_module_graph_and_deduplicates_shared_dependency()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi-file");
        var modules = new Dictionary<string, JavaScriptModuleSource>(StringComparer.Ordinal)
        {
            ["./shared.mjs"] = Source(root, "shared.mjs"),
            ["./worker.mjs"] = Source(root, "worker.mjs"),
            ["./archive.mjs"] = Source(root, "archive.mjs"),
        };
        var resolver = new FixtureResolver(modules);
        var entry = Source(root, "entry.mjs");

        var compiler = new JavaScriptCompiler();
        var graph = compiler.CompileModuleGraph(
            new JavaScriptCompilationRequest(entry.Source, entry.CanonicalName, JavaScriptSourceKind.Module), resolver);
        var second = compiler.CompileModuleGraph(
            new JavaScriptCompilationRequest(entry.Source, entry.CanonicalName, JavaScriptSourceKind.Module),
            new FixtureResolver(modules));

        Assert.Equal(["shared.mjs", "worker.mjs", "archive.mjs", "entry.mjs"], graph.Modules.Keys);
        Assert.Equal(1, resolver.Requests.Count(request => request.Specifier == "./shared.mjs"));
        Assert.Equal(4, graph.Modules.Count);
        Assert.All(graph.Modules.Values, module => Assert.NotEmpty(module.Bytes));
        foreach (var (name, module) in graph.Modules)
            Assert.Equal(module.Bytes, second.Modules[name].Bytes);
    }

    private static JavaScriptModuleSource Source(string root, string name) =>
        new(name, File.ReadAllText(Path.Combine(root, name)));

    private sealed class FixtureResolver(IReadOnlyDictionary<string, JavaScriptModuleSource> modules) : IJavaScriptModuleResolver
    {
        public List<(string Specifier, string Referrer)> Requests { get; } = [];

        public JavaScriptModuleSource Resolve(string specifier, string referrer)
        {
            Requests.Add((specifier, referrer));
            return modules[specifier];
        }
    }
}
