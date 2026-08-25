using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Differential coverage for module records, call receivers, function
/// environments, and property-definition evaluation order.
/// </summary>
public sealed class RuntimeBoundaryReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // Module records encode binding kinds, aliases, and request order.
            yield return Case("module-side-effect-import", "import 'setup'; run();");
            yield return Case("module-default-import", "import value from 'source'; export { value };");
            yield return Case("module-namespace-import", "import * as api from 'source'; export default api.value;");
            yield return Case("module-mixed-import", "import primary, { left, right as local } from 'source'; use(primary, left, local);");
            yield return Case("module-export-alias", "const local = init(); export { local as publicValue };");
            yield return Case("module-reexport-named", "export { value, other as renamed } from 'source';");
            yield return Case("module-reexport-star", "export * from 'source';");
            yield return Case("module-reexport-namespace", "export * as api from 'source';");
            yield return Case("module-default-anonymous-function", "export default function () { return value(); }");
            yield return Case("module-default-anonymous-class", "export default class { static value = init(); }");
            yield return Case("module-import-meta", "export const url = import.meta.url;");
            yield return Case("module-dynamic-import", "export function load(name) { return import(name); }");

            // Member calls preserve the receiver; comma and optional forms do not.
            yield return Case("computed-member-call-receiver", "function invoke(target, key) { return target[key](argument()); }");
            yield return Case("parenthesized-member-call-receiver", "function invoke(target) { return (target.method)(); }");
            yield return Case("comma-call-loses-receiver", "function invoke(target) { return (0, target.method)(); }");
            yield return Case("getter-before-call-arguments", "function invoke(target) { return target.method(argument()); }");
            yield return Case("call-spread-order", "function invoke(fn, items) { return fn(before(), ...items, after()); }");
            yield return Case("construct-spread-order", "function create(Type, items) { return new Type(before(), ...items, after()); }");
            yield return Case("new-member-precedence", "function create(registry, name) { return new registry[name](argument()); }");
            yield return Case("tagged-template-receiver", "function render(target, value) { return target.tag`before ${value} after`; }");

            // Function environments have special arguments and constructor cells.
            yield return Case("arguments-parameter-alias-sloppy", "function update(value) { value = 2; return arguments[0]; }");
            yield return Case("arguments-parameter-no-alias-strict", "function update(value) { 'use strict'; value = 2; return arguments[0]; }");
            yield return Case("arguments-default-parameter-environment", "function read(value = arguments[0]) { return value; }");
            yield return Case("arguments-rest-parameter", "function read(first, ...rest) { return [arguments.length, first, rest]; }");
            yield return Case("arrow-forwards-arguments-transitively", "function make(value) { return () => () => arguments[0] + value; }");
            yield return Case("new-target-in-default-parameter", "function Factory(value = new.target) { return value; }");
            yield return Case("derived-constructor-return-object", "class Base {} class Child extends Base { constructor(value) { if (value) return { value }; super(); } }");
            yield return Case("derived-constructor-super-in-arrow", "class Base { constructor(value) { this.value = value; } } class Child extends Base { constructor(value) { const initialize = () => super(value); initialize(); } }");

            // Property keys, values, spreads, and accessors are evaluated in source order.
            yield return Case("object-computed-key-value-order", "function make() { return { [key(1)]: value(1), fixed: value(2), [key(3)]: value(3) }; }");
            yield return Case("object-spread-interleaving", "function make(left, right) { return { before: init(1), ...left, middle: init(2), ...right, after: init(3) }; }");
            yield return Case("object-method-super-home", "const base = { read() { return 1; } }; const child = { __proto__: base, read() { return super.read() + 1; } };");
            yield return Case("object-computed-accessors", "function make(name) { return { get [name]() { return read(); }, set [name](value) { write(value); } }; }");
            yield return Case("array-holes-and-spread", "function make(items) { return [first(), , ...items, , last()]; }");

            // Scope exits and exceptional pattern initialization need cleanup edges.
            yield return Case("catch-binding-default-throws", "function run(task) { try { task(); } catch ({ value = init() }) { return value; } }");
            yield return Case("for-of-pattern-default-throws", "function run(items) { for (const [value = init()] of items) consume(value); }");
            yield return Case("for-of-body-continues-inner-loop", "function run(groups) { outer: for (const group of groups) { for (const item of group) { if (skip(item)) continue; if (done(item)) continue outer; consume(item); } } }");
            yield return Case("switch-lexical-fallthrough-closure", "function make(tag) { let read; switch (tag) { case 0: let value = init(); read = () => value; case 1: use(read); break; } return read; }");
            yield return Case("finally-overrides-return", "function run() { try { return first(); } finally { return second(); } }");
            yield return Case("finally-overrides-throw", "function run() { try { throw first(); } finally { throw second(); } }");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_runtime_boundary(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source)
        => [name, source, $"/tmp/reference-units/runtime-boundaries/{name}.js"];
}
