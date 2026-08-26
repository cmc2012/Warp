using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class LoopControlFlowGoldenTests
{
    [Fact]
    public void Matches_reference_for_short_circuit_while_loop()
        => GoldenAssert.ReferenceModule("export function choose(items) { let index = 0; while (items.length > 0 && index < 3) { index++; } return index; }", "control.js");

    [Fact]
    public void Matches_reference_for_short_circuit_for_loop()
        => GoldenAssert.ReferenceModule("export function choose(items) { for (let index = 0; index < items.length && index < 3; index++) work(items[index]); }", "for-control.js");

    [Fact]
    public void Matches_reference_for_computed_assignment_with_closure_value()
        => GoldenAssert.ReferenceModule("target[key] = function(value) { return value; };", "computed-assignment-closure.js");

    [Fact]
    public void Matches_reference_for_discarded_local_addition_assignment()
        => GoldenAssert.ReferenceModule("export function increment() { let count = 0; count += 1; }", "local-addition.js");




}
