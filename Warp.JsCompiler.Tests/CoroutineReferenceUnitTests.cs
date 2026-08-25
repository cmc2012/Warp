using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Reference units for generator and async state-machine lowering.</summary>
public sealed class CoroutineReferenceUnitTests
{
    [Fact]
    public void Generator_single_yield()
        => GoldenAssert.ReferenceModule("function* once(value) { yield value; }", "/tmp/reference-units/generator-single-yield.js");

    [Fact]
    public void Generator_yield_expression_value()
        => GoldenAssert.ReferenceModule("function* echo() { const value = yield 1; return value; }", "/tmp/reference-units/generator-yield-expression.js");

    [Fact]
    public void Generator_delegation()
        => GoldenAssert.ReferenceModule("function* flatten(items) { yield* items; }", "/tmp/reference-units/generator-delegation.js");

    [Fact]
    public void Generator_finally_cleanup()
        => GoldenAssert.ReferenceModule("function* guarded() { try { yield 1; } finally { cleanup(); } }", "/tmp/reference-units/generator-finally-cleanup.js");

    [Fact]
    public void Async_await_return()
        => GoldenAssert.ReferenceModule("async function load(value) { return await value; }", "/tmp/reference-units/async-await-return.js");

    [Fact]
    public void Async_await_in_finally()
        => GoldenAssert.ReferenceModule("async function guarded(task) { try { return await task(); } finally { await cleanup(); } }", "/tmp/reference-units/async-await-finally.js");

    [Fact]
    public void Async_for_await_loop()
        => GoldenAssert.ReferenceModule("async function collect(items) { const result = []; for await (const item of items) result.push(item); return result; }", "/tmp/reference-units/async-for-await.js");

    [Fact]
    public void Async_for_await_break()
        => GoldenAssert.ReferenceModule("async function first(items) { for await (const item of items) { if (item) return item; } return null; }", "/tmp/reference-units/async-for-await-break.js");
}
