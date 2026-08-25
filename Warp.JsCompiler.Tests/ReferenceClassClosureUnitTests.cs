using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Fine-grained class and closure probes.  Each source is deliberately small:
/// a failing comparison identifies one parser/lowering/serialization feature.
/// </summary>
public sealed class ReferenceClassClosureUnitTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return Case("class-empty", "class C {}", "class-empty");
            yield return Case("class-instance-method", "class C { read() { return 1; } }", "class-instance-method");
            yield return Case("class-static-method", "class C { static read() { return 1; } }", "class-static-method");
            yield return Case("class-constructor", "class C { constructor(value) { this.value = value; } }", "class-constructor");
            yield return Case("class-instance-field", "class C { value = 1; }", "class-instance-field");
            yield return Case("class-static-field", "class C { static value = 1; }", "class-static-field");
            yield return Case("class-getter", "class C { get value() { return this._value; } }", "class-getter");
            yield return Case("class-setter", "class C { set value(value) { this._value = value; } }", "class-setter");
            yield return Case("class-computed-method", "const key = 'read'; class C { [key]() { return 1; } }", "class-computed-method");
            yield return Case("class-computed-field", "const key = 'value'; class C { [key] = 1; }", "class-computed-field");

            yield return Case("extends-empty", "class Base {} class Derived extends Base {}", "extends-empty");
            yield return Case("derived-default-constructor", "class Base {} class Derived extends Base {} new Derived();", "derived-default-constructor");
            yield return Case("derived-constructor-super", "class Base {} class Derived extends Base { constructor() { super(); } }", "derived-constructor-super");
            yield return Case("derived-constructor-super-argument", "class Base { constructor(value) {} } class Derived extends Base { constructor() { super(1); } }", "derived-constructor-super-argument");
            yield return Case("super-instance-read", "class Base { read() { return 1; } } class Derived extends Base { read() { return super.read; } }", "super-instance-read");
            yield return Case("super-instance-call", "class Base { read() { return 1; } } class Derived extends Base { read() { return super.read(); } }", "super-instance-call");
            yield return Case("super-computed-read", "class Base { read() { return 1; } } class Derived extends Base { read() { return super['read']; } }", "super-computed-read");
            yield return Case("super-computed-call", "class Base { read() { return 1; } } class Derived extends Base { read() { return super['read'](); } }", "super-computed-call");
            yield return Case("super-static-call", "class Base { static read() { return 1; } } class Derived extends Base { static read() { return super.read(); } }", "super-static-call");
            yield return Case("super-assignment", "class Base {} class Derived extends Base { write(value) { super.value = value; } }", "super-assignment");

            yield return Case("private-instance-field", "class C { #value = 1; read() { return this.#value; } }", "private-instance-field");
            yield return Case("private-instance-write", "class C { #value = 1; write(value) { this.#value = value; } }", "private-instance-write");
            yield return Case("private-static-field", "class C { static #value = 1; static read() { return C.#value; } }", "private-static-field");
            yield return Case("private-method", "class C { #read() { return 1; } read() { return this.#read(); } }", "private-method");
            yield return Case("arrow-captures-local", "function make(value) { return () => value; }", "arrow-captures-local");
            yield return Case("arrow-captures-this", "class C { make() { return () => this.value; } }", "arrow-captures-this");
            yield return Case("arrow-captures-arguments", "function make() { return () => arguments[0]; }", "arrow-captures-arguments");
            yield return Case("arrow-lexical-super", "class Base { read() { return 1; } } class Derived extends Base { make() { return () => super.read(); } }", "arrow-lexical-super");
            yield return Case("nested-arrow-capture", "function make(value) { return () => () => value; }", "nested-arrow-capture");
            yield return Case("arrow-default-capture", "function make(value = 1) { return () => value; }", "arrow-default-capture");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_each_class_or_closure_unit(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source, string fileStem)
        => [name, source, $"/tmp/reference-class-closure/{fileStem}.js"];
}
