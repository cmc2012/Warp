using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class PipelineWorkloadReferenceTests
{
    [Fact]
    public void Async_for_of_private_counter_destructured_catch_and_finally()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pipeline-workload.js"));
        GoldenAssert.ReferenceModule(source, "/tmp/reference-pipeline-workload.js");
    }
}
