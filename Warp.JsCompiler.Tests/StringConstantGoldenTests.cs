using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class StringConstantGoldenTests
{
    [Theory]
    [InlineData("consume('123');", "/tmp/warp-tagged-integer-string.js")]
    [InlineData("consume('00', '2147483648');", "/tmp/warp-ordinary-numeric-strings.js")]
    public void Matches_reference_string_constant_storage(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
