using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Reference units for exceptional control-flow edges and cleanup scopes.</summary>
public sealed class ExceptionControlFlowReferenceUnitTests
{
    [Fact]
    public void Catch_and_return()
        => GoldenAssert.ReferenceModule("function parse(input) { try { return decode(input); } catch (error) { return null; } }", "/tmp/reference-units/try-catch-return.js");

    [Fact]
    public void Finally_after_return()
        => GoldenAssert.ReferenceModule("function close(resource) { try { return resource.read(); } finally { resource.close(); } }", "/tmp/reference-units/try-finally-return.js");

    [Fact]
    public void Catch_and_finally()
        => GoldenAssert.ReferenceModule("function run(fn) { try { return fn(); } catch (error) { report(error); return false; } finally { finish(); } }", "/tmp/reference-units/try-catch-finally.js");

    [Fact]
    public void Nested_finally_scopes()
        => GoldenAssert.ReferenceModule("function nested() { try { try { work(); } finally { inner(); } } finally { outer(); } }", "/tmp/reference-units/try-nested-finally.js");

    [Fact]
    public void Throw_from_catch()
        => GoldenAssert.ReferenceModule("function rethrow(fn) { try { fn(); } catch (error) { throw error; } }", "/tmp/reference-units/try-catch-throw.js");

    [Fact]
    public void Loop_break_through_finally()
        => GoldenAssert.ReferenceModule("function find(items) { for (const item of items) { try { if (item.ok) break; } finally { observe(item); } } }", "/tmp/reference-units/try-loop-break-finally.js");

    [Fact]
    public void Loop_continue_through_finally()
        => GoldenAssert.ReferenceModule("function copy(items) { const out = []; for (const item of items) { try { if (!item) continue; out.push(item); } finally { observe(item); } } return out; }", "/tmp/reference-units/try-loop-continue-finally.js");

    [Fact]
    public void Catch_without_binding()
        => GoldenAssert.ReferenceModule("function optional(fn) { try { return fn(); } catch { return undefined; } }", "/tmp/reference-units/try-catch-no-binding.js");
}
