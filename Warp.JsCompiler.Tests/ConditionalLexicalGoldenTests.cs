using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ConditionalLexicalGoldenTests
{
    [Fact]
    public void Conditional_initializer_matches_reference()
        => GoldenAssert.ReferenceModule("let value = flag ? left : right;",
            "/tmp/warp-conditional-lexical.js");
}
