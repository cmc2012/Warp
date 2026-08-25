using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Reference units for each independently-lowered binding pattern.</summary>
public sealed class DestructuringReferenceUnitTests
{
    [Fact]
    public void Array_elision_and_default()
        => GoldenAssert.ReferenceModule("const [first = 1, , third = 3] = values;", "/tmp/reference-units/destructure-array-elision-default.js");

    [Fact]
    public void Array_rest_binding()
        => GoldenAssert.ReferenceModule("const [head, ...tail] = values;", "/tmp/reference-units/destructure-array-rest.js");

    [Fact]
    public void Nested_array_binding()
        => GoldenAssert.ReferenceModule("const [first, [second]] = values;", "/tmp/reference-units/destructure-nested-array.js");

    [Fact]
    public void Object_alias_and_default()
        => GoldenAssert.ReferenceModule("const { value: renamed = 1 } = source;", "/tmp/reference-units/destructure-object-alias-default.js");

    [Fact]
    public void Nested_object_binding()
        => GoldenAssert.ReferenceModule("const { outer: { inner } } = source;", "/tmp/reference-units/destructure-nested-object.js");

    [Fact]
    public void Object_rest_binding()
        => GoldenAssert.ReferenceModule("const { id, ...metadata } = source;", "/tmp/reference-units/destructure-object-rest.js");

    [Fact]
    public void Assignment_pattern()
        => GoldenAssert.ReferenceModule("let left, right; [left, right] = values;", "/tmp/reference-units/destructure-assignment.js");

    [Fact]
    public void Parameter_array_pattern()
        => GoldenAssert.ReferenceModule("function first([value = 0]) { return value; }", "/tmp/reference-units/destructure-parameter-array.js");

    [Fact]
    public void Parameter_object_pattern()
        => GoldenAssert.ReferenceModule("function read({ name = 'unknown', active }) { return active ? name : ''; }", "/tmp/reference-units/destructure-parameter-object.js");

    [Fact]
    public void Catch_object_pattern()
        => GoldenAssert.ReferenceModule("function message(fn) { try { fn(); } catch ({ message: text }) { return text; } }", "/tmp/reference-units/destructure-catch-object.js");
}
