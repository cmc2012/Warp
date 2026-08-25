using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Exception control flow selected from the reference language suite.</summary>
public sealed class ExceptionGoldenTests
{
    [Fact]
    public void Catch_binds_thrown_value()
        => GoldenAssert.Module("var x; try { throw 1; } catch (e) { x = e; }", "/tmp/warp-golden-try_catch.js",
            "0101364061696f742f776172702d676f6c64656e2d7472795f63617463680fa803000000000e000203a401000100020100160000000108e802296c06000000b42fc76c08000000c3df0e292f");

    [Fact]
    public void Finally_runs_after_normal_completion()
        => GoldenAssert.Module("var x = 0; try { x = 1; } finally { x = 2; }", "/tmp/warp-golden-try_finally.js",
            "01023a4061696f742f776172702d676f6c64656e2d7472795f66696e616c6c7902780fa803000000000e000203a4010000000301002000aa03000108e80229b3df6c0f000000b4df0e066d0c0000000e296d050000002fb5df6e29");

    [Fact]
    public void Throwing_try_does_not_emit_unreachable_normal_finally_path()
        => GoldenAssert.Module("var x = 0; try { throw 1; } finally { x = 2; }", "try-finally-throw.js",
            "01022e4061696f742f7472792d66696e616c6c792d7468726f7702780fa803000000000e000203a4010000000301001600aa03000108e80229b3df6c06000000b42f6d050000002fb5df6e");
}
