using Warp.JsCompiler.Api;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Exercises a cyclic, diamond-shaped module graph with nontrivial code in every node.</summary>
public sealed class MultiFileCycleTopologyTests
{
    [Fact]
    public void Compiles_cyclic_diamond_graph_deterministically_without_re_resolving_shared_module()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi-file-cycle");
        var modules = new Dictionary<string, JavaScriptModuleSource>(StringComparer.Ordinal)
        {
            ["./common.mjs"] = Source(root, "common.mjs"),
            ["./left.mjs"] = Source(root, "left.mjs"),
            ["./right.mjs"] = Source(root, "right.mjs"),
        };
        var entry = Source(root, "entry.mjs");
        var firstResolver = new Resolver(modules);
        var compiler = new JavaScriptCompiler();

        var first = compiler.CompileModuleGraph(
            new JavaScriptCompilationRequest(entry.Source, entry.CanonicalName, JavaScriptSourceKind.Module), firstResolver);
        var second = compiler.CompileModuleGraph(
            new JavaScriptCompilationRequest(entry.Source, entry.CanonicalName, JavaScriptSourceKind.Module), new Resolver(modules));

        Assert.Equal(["common.mjs", "right.mjs", "left.mjs", "entry.mjs"], first.Modules.Keys);
        Assert.Equal(1, firstResolver.Requests.Count(request => request.Specifier == "./common.mjs"));
        Assert.Equal(1, firstResolver.Requests.Count(request => request.Specifier == "./left.mjs"));
        Assert.Equal(1, firstResolver.Requests.Count(request => request.Specifier == "./right.mjs"));
        foreach (var (name, module) in first.Modules)
            Assert.Equal(module.Bytes, second.Modules[name].Bytes);
    }

    private static JavaScriptModuleSource Source(string root, string name) =>
        new(name, File.ReadAllText(Path.Combine(root, name)));

    private sealed class Resolver(IReadOnlyDictionary<string, JavaScriptModuleSource> modules) : IJavaScriptModuleResolver
    {
        public List<(string Specifier, string Referrer)> Requests { get; } = [];

        public JavaScriptModuleSource Resolve(string specifier, string referrer)
        {
            Requests.Add((specifier, referrer));
            return modules[specifier];
        }
    }
}
