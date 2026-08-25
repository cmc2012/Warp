using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class NewExpressionGoldenTests
{
    [Theory]
    [InlineData("var x = new Date();", "/tmp/warp-new-empty.js")]
    [InlineData("var x = new Date(value);", "/tmp/warp-new-one.js")]
    [InlineData("var x = new factory.Type(first, second);", "/tmp/warp-new-member.js")]
    [InlineData("var x = new Factory(test ? left : right);", "/tmp/warp-new-conditional.js")]
    public void Matches_reference_constructor_call(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
