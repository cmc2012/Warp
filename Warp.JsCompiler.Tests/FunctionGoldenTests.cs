using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Function and closure forms selected from the reference closure suite.</summary>
public sealed class FunctionGoldenTests
{
    [Fact]
    public void Function_return_and_call()
        => GoldenAssert.Module("function add(a, b) { return a + b; } var x = add(1, 2);", "/tmp/warp-golden-function_return.js",
            "0102424061696f742f776172702d676f6c64656e2d66756e6374696f6e5f72657475726e02780fa803000000000e000203a4010000000302010d00d4010001aa03010108e805be00df29dbb4b5eee0290e430203d4010200020200000400cfd09d28");

    [Fact]
    public void Nested_closure_captures_outer_binding()
        => GoldenAssert.Module("function make(x) { return function(y) { return x + y; }; } var f = make(1);", "/tmp/warp-golden-closure.js",
            "0103324061696f742f776172702d676f6c64656e2d636c6f73757265086d616b6502660fa803000000000e000203a4010000000202010c00aa030001ac03010108e805be00df29dbb4ede0290e430203aa030100010100010300be00280e430203000100010201000400000003dbcf9d28");

    [Fact]
    public void Async_function_has_no_prototype_flag()
        => GoldenAssert.Module("async function f(){ await g(); return 1; }", "/tmp/async-function.js",
            "0103284061696f742f6173796e632d66756e6374696f6e026602670fa803000000000e000203a4010000000101010800aa03000108e805be00df29290e620203aa030000000100000a0038d6000000ec8b0eb42e");
    [Fact]
    public void New_target_creates_owning_function_cell()
        => GoldenAssert.Module("function f(){ return new.target; }", "/tmp/new-target.js",
            "0102204061696F742F6E65772D74617267657402660FA803000000000E000203A4010000000101010800AA03000108E805BE00DF29290E430203AA0300010001000005000C03C7C328");

    [Fact]
    public void Arrow_forwards_new_target_through_closure()
        => GoldenAssert.Module("function f(){ return () => new.target; }", "/tmp/arrow-new-target.js",
            "01022C4061696F742F6172726F772D6E65772D74617267657402660FA803000000000E000203A4010000000101010800AA03000108E805BE00DF29290E430203AA0300010001000106000C03C7BE00280E420203000000000101000200E6010001DB28");

    [Fact]
    public void Nested_arrows_forward_new_target_transitively()
        => GoldenAssert.Module("function f(){ return () => () => new.target; }", "/tmp/nested-arrow-new-target.js",
            "01023A4061696F742F6E65737465642D6172726F772D6E65772D74617267657402660FA803000000000E000203A4010000000101010800AA03000108E805BE00DF29290E430203AA0300010001000106000C03C7BE00280E420203000000000101010300E6010001BE00280E420203000000000101000200E6010000DB28");
}
