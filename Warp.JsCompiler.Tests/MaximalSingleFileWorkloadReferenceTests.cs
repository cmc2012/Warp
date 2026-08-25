using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>High-interaction single-module regression spanning the compiler's most stateful lowering paths.</summary>
public sealed class MaximalSingleFileWorkloadReferenceTests
{
    [Fact]
    public void Compiles_maximal_inheritance_generator_destructuring_and_cleanup_workload()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "maximal-single-file-workload.js"));

        GoldenAssert.ReferenceModule(source, "/tmp/reference-maximal-single-file-workload.js");
    }
}
