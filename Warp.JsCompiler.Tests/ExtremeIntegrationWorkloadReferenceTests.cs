using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Exercises interacting module, class, coroutine, destructuring, and cleanup paths as one program.</summary>
public sealed class ExtremeIntegrationWorkloadReferenceTests
{
    [Fact]
    public void Inherited_private_async_generator_destructuring_and_module_workload()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "extreme-integration-workload.js"));

        GoldenAssert.ReferenceModule(source, "/tmp/reference-extreme-integration-workload.js");
    }
}
