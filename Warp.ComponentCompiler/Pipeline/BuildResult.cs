using Microsoft.Extensions.Logging;
using Warp.ComponentCompiler.Analysis;
using Warp.Diagnostics;

namespace Warp.ComponentCompiler.Pipeline;

public sealed record PageBuildResult(
    string Name,                    // index / choice / ...
    string OutputPath,              // build/pages/index/index.js
    StaticAnalysis.PageBudget Budget,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed record BuildResult(
    IReadOnlyList<PageBuildResult> Pages,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool Success,
    bool BytecodeCompiled = true)
{
    public void Print(TextWriter w, bool verbose = false)
    {
        if (verbose)
            foreach (var p in Pages)
                w.WriteLine($"[{p.Name}] {p.OutputPath}");
        foreach (var d in Diagnostics.Where(d => d.Level >= LogLevel.Warning))
            w.WriteLine(d.ToString());
        w.WriteLine(Success
            ? BytecodeCompiled
                ? $"Build succeeded: {Pages.Count} page(s) compiled to bytecode."
                : $"Build succeeded: {Pages.Count} page(s) generated as JavaScript (JSC skipped)."
            : $"Build failed with {Diagnostics.Count(d => d.IsError)} error(s).");
    }
}
