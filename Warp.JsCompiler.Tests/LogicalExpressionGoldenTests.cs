using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class LogicalExpressionGoldenTests
{
    [Theory]
    [InlineData("var x = left && right;", "/tmp/warp-logical-and.js")]
    [InlineData("var x = left || right;", "/tmp/warp-logical-or.js")]
    [InlineData("var x = left ?? right;", "/tmp/warp-nullish.js")]
    [InlineData("var x = test ? left : right;", "/tmp/warp-conditional.js")]
    [InlineData("var x = first && (second ? third : fourth);", "/tmp/warp-logical-nested.js")]
    [InlineData("var x = first && second && third;", "/tmp/warp-logical-chain.js")]
    public void Matches_reference_control_flow(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
