using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ComposedSemanticsReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return Case("nested-finally-return",
                "function run(value) { try { try { if (value) return value; throw value; } finally { inner(value); } } finally { outer(value); } }");
            yield return Case("labelled-break-through-finally",
                "function scan(rows) { outer: for (const row of rows) { try { for (const cell of row) { if (cell) break outer; } } finally { close(row); } } }");
            yield return Case("labelled-continue-through-finally",
                "function scan(rows) { outer: for (const row of rows) { try { for (const cell of row) { if (cell) continue outer; use(cell); } } finally { close(row); } } }");
            yield return Case("generator-delegate-finally",
                "function* flatten(groups) { try { for (const group of groups) yield* group; } finally { cleanup(); } }");
            yield return Case("async-generator-await-yield-finally",
                "async function* map(items) { try { for await (const item of items) yield await convert(item); } finally { await cleanup(); } }");
            yield return Case("destructure-loop-catch-finally",
                "function collect(items) { const out = []; for (const { value: [head, ...tail] = [] } of items) { try { out.push(head, tail); } catch ({ message }) { out.push(message); } finally { tick(); } } return out; }");
            yield return Case("derived-fields-private-arrow",
                "class Base { constructor(value) { this.base = value; } } class Child extends Base { #value = this.base; reader = () => this.#value; constructor(value) { super(value); } }");
            yield return Case("computed-instance-field-order",
                "const first = key(1), second = key(2); class Entry { [first] = init(1); value = init(2); [second] = init(3); } new Entry();");
            yield return Case("static-field-order",
                "const first = key(1), second = key(2); class Entry { static [first] = init(1); static value = init(2); static [second] = init(3); }");
            yield return Case("private-accessors-and-method",
                "class Box { #value = 0; get #current() { return this.#value; } set #current(value) { this.#value = value; } #update(value) { return this.#current = value; } write(value) { return this.#update(value); } read() { return this.#current; } }");
            yield return Case("static-private-state",
                "class Counter { static #value = 0; static #next() { return ++this.#value; } static read() { return this.#next(); } } Counter.read();");
            yield return Case("super-computed-assignment-arrow",
                "class Base { set value(input) { consume(input); } } class Child extends Base { write(key, value) { const assign = () => super[key] = value; return assign(); } }");
            yield return Case("catch-rethrow-nested-finally",
                "function run(task) { try { try { return task(); } catch (error) { inspect(error); throw error; } finally { inner(); } } finally { outer(); } }");
            yield return Case("switch-break-through-finally",
                "function choose(tag) { outer: switch (tag) { case 1: try { work(); break outer; } finally { cleanup(); } case 2: return other(); default: return fallback(); } }");
            yield return Case("generator-return-through-finally",
                "function* values(items) { try { for (const item of items) { if (item.done) return item.value; yield item; } } finally { cleanup(); } }");
            yield return Case("generator-catch-yield-finally",
                "function* recover(task) { try { yield task(); } catch (error) { yield recoverValue(error); } finally { cleanup(); } }");
            yield return Case("async-return-nested-finally",
                "async function load(value) { try { try { return await resolve(value); } finally { await inner(); } } finally { await outer(); } }");
            yield return Case("for-of-destructure-finally",
                "function collect(entries) { const out = []; for (const [key, value] of entries) { try { out.push(key, value); } finally { trace(key); } } return out; }");
            yield return Case("destructure-assignment-in-finally",
                "function update(source) { let left, right; try { work(); } finally { [left, right] = source; } return [left, right]; }");
            yield return Case("closure-optional-chain-finally",
                "function make(target) { return function read(key) { try { return target?.[key]?.(); } finally { trace(key); } }; }");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_composed_semantics(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source)
        => [name, source, $"/tmp/reference-composed-semantics/{name}.js"];
}
