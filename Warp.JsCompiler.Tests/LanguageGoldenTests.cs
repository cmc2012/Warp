using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Language constructs selected from the reference language suite.</summary>
public sealed class LanguageGoldenTests
{
    [Fact]
    public void Array_destructuring_declaration()
        => GoldenAssert.Module("let [a, b] = [1, 2];", "/tmp/warp-golden-destructuring.js",
            "01033e4061696f742f776172702d676f6c64656e2d64657374727563747572696e67026102620fa803000000000e000203a4010000000502001c00aa030009ac03010908e802290611f0e90c7d80000edf80000ee083290eb4b5260200eaee");

    [Fact]
    public void Template_literal()
        => GoldenAssert.Module("var x = `a${1}b`;", "/tmp/warp-golden-template_literal.js",
            "0104444061696f742f776172702d676f6c64656e2d74656d706c6174655f6c69746572616c0278026102620fa803000000000e000203a4010000000401001900aa03000108e8022904d6000000425e000000b404d7000000240200df29");
}
