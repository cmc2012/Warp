using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Differential probes for evaluation order and abrupt completion semantics.
/// Each case is intentionally small so a mismatch identifies one lowering rule.
/// </summary>
public sealed class SemanticEdgeReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // The base and key of an assignment target must each be evaluated once.
            yield return Case("member-nullish-assignment", "function update(target, key) { return target()[key()] ??= init(); }");
            yield return Case("member-logical-and-assignment", "function update(target, key) { return target()[key()] &&= next(); }");
            yield return Case("member-logical-or-assignment", "function update(target, key) { return target()[key()] ||= next(); }");
            yield return Case("super-logical-assignment", "class Base { get value() { return read(); } set value(v) { write(v); } } class Child extends Base { update() { return super.value ??= init(); } }");

            // Optional chains suppress computed keys and arguments on the nullish path.
            yield return Case("optional-computed-member", "function read(target) { return target?.[key()]; }");
            yield return Case("optional-member-call", "function invoke(target) { return target.method?.(argument()); }");
            yield return Case("optional-computed-call", "function invoke(target) { return target?.[key()]?.(argument()); }");
            yield return Case("optional-chain-followed-by-nullish", "function read(target) { return target?.deep?.value ?? fallback(); }");

            // IteratorClose is observable on every abrupt exit from a for-of loop.
            yield return Case("for-of-break-closes-iterator", "function scan(items) { for (const item of items) { if (accept(item)) break; } }");
            yield return Case("for-of-return-closes-iterator", "function find(items) { for (const item of items) { if (accept(item)) return item; } }");
            yield return Case("for-of-throw-closes-iterator", "function validate(items) { for (const item of items) { if (!valid(item)) throw item; } }");
            yield return Case("destructuring-closes-iterator", "function firstTwo(items) { const [first, second] = items; return [first, second]; }");
            yield return Case("spread-consumes-iterator", "function copy(items) { return [before(), ...items, after()]; }");

            // Completion values must survive cleanup code, including suspension points.
            yield return Case("return-expression-through-finally", "function run() { try { return value(); } finally { cleanup(); } }");
            yield return Case("throw-through-nested-finally", "function run() { try { try { throw fail(); } finally { inner(); } } finally { outer(); } }");
            yield return Case("break-through-nested-finally", "function run(items) { outer: for (const item of items) { try { try { if (stop(item)) break outer; } finally { inner(item); } } finally { outerCleanup(item); } } }");
            yield return Case("generator-return-through-finally-yield", "function* run() { try { return result(); } finally { yield cleanup(); } }");
            yield return Case("async-return-through-finally-await", "async function run() { try { return await result(); } finally { await cleanup(); } }");

            // Class initialization has strict source order and a distinct this binding.
            yield return Case("class-static-block", "class Registry { static value = init(1); static { this.value = init(this.value); } static last = init(3); }");
            yield return Case("class-static-block-private-field", "class Counter { static #value = init(); static { this.#value++; } static read() { return this.#value; } }");
            yield return Case("derived-field-after-super", "class Base { constructor(value) { observe(value); } } class Child extends Base { field = init(this); constructor(value) { super(value); observe(this.field); } }");
            yield return Case("private-brand-check", "class Box { #value; static accepts(value) { return #value in value; } }");
            yield return Case("private-post-increment", "class Counter { #value = 0; next() { return this.#value++; } }");

            // Defaults are lazy and nested patterns impose left-to-right ordering.
            yield return Case("parameter-default-sees-earlier-parameter", "function read(first = init(), second = first) { return second; }");
            yield return Case("nested-default-order", "function read({ first = init(1), nested: { second = init(first) } = make() } = source()) { return [first, second]; }");
            yield return Case("computed-binding-key-before-default", "function read({ [key()]: value = init() }) { return value; }");
            yield return Case("assignment-pattern-member-target", "function update(source, target) { ({ value: target[key()] = init() } = source); return target; }");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_semantic_edge(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source)
        => [name, source, $"/tmp/reference-units/semantic-edges/{name}.js"];
}
