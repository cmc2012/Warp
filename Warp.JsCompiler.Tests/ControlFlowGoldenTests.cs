using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Control-flow shapes selected from the reference loop and language suites.</summary>
public sealed class ControlFlowGoldenTests
{
    [Fact]
    public void If_else_branches()
        => GoldenAssert.Module("var x = 0; if (x) x = 1; else x = 2;", "/tmp/warp-golden-if_else.js",
            "0102324061696f742f776172702d676f6c64656e2d69665f656c736502780fa803000000000e000203a4010000000101000e00aa03000108e80229b3e3e804b4df29b5df29");

    [Fact]
    public void While_loop_with_post_increment()
        => GoldenAssert.Module("var x = 0; while (x < 3) x++;", "/tmp/warp-golden-while_loop.js",
            "0102384061696f742f776172702d676f6c64656e2d7768696c655f6c6f6f7002780fa803000000000e000203a4010000000201001100aa03000108e80229b3dfdbb6a3e806db8fdfeaf729");

    [Fact]
    public void For_loop_with_lexical_binding()
        => GoldenAssert.Module("var sum = 0; for (let i = 0; i < 3; i++) sum += i;", "/tmp/warp-golden-for_loop.js",
            "0101344061696f742f776172702d676f6c64656e2d666f725f6c6f6f700fa803000000000e000203a401000100020100230000000108e80229b3df610000b3c7620000b6a3e811db6200009ddf620000916300000eeaea29");

    [Fact]
    public void Do_while_loop()
        => GoldenAssert.Module("var x = 0; do { x++; } while (x < 3);", "/tmp/warp-golden-do_while.js",
            "0102344061696f742f776172702d676f6c64656e2d646f5f7768696c6502780fa803000000000e000203a4010000000201000f00aa03000108e80229b3dfdb8fdfdbb6a3e9f929");

    [Fact]
    public void Switch_with_break_and_default()
        => GoldenAssert.Module("var x = 1; switch (x) { case 0: x = 2; break; default: x = 3; }", "/tmp/warp-golden-switch_case.js",
            "01023a4061696f742f776172702d676f6c64656e2d7377697463685f6361736502780fa803000000000e000203a4010000000301001100aa03000108e80229b4e311b3abe804b5df29b6df29");
}
