using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class OptionalChainGoldenTests
{
    [Theory]
    [InlineData("var x = target?.field;", "/tmp/warp-optional-field.js")]
    [InlineData("var x = target?.[key()];", "/tmp/warp-optional-computed.js")]
    [InlineData("var x = fn?.(argument);", "/tmp/warp-optional-function.js")]
    [InlineData("var x = target.method?.(argument);", "/tmp/warp-optional-method.js")]
    [InlineData("var x = target?.method(argument);", "/tmp/warp-optional-member-call.js")]
    [InlineData("var x = target?.method?.(argument);", "/tmp/warp-optional-method-call.js")]
    [InlineData("var x = target?.deep?.value ?? fallback();", "/tmp/warp-optional-nullish.js")]
    public void Matches_reference_optional_chain(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
