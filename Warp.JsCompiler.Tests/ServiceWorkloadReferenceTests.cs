using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ServiceWorkloadReferenceTests
{
    [Fact]
    public void Inherited_async_service_with_private_state_and_recovery()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "service-workload.js"));
        GoldenAssert.ReferenceModule(source, "/tmp/reference-service-workload.js");
    }
}
