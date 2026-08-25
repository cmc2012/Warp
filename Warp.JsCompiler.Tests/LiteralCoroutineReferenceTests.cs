using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Differential probes for literal encoding, operator lowering, template and
/// regular-expression constants, and coroutine resume paths.
/// </summary>
public sealed class LiteralCoroutineReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // Numeric spellings can select different constant encodings.
            yield return Case("number-negative-zero", "const value = -0;");
            yield return Case("number-large-signed-integer", "const value = 2147483648;");
            yield return Case("number-large-negative-integer", "const value = -2147483649;");
            yield return Case("number-fraction", "const value = 0.125;");
            yield return Case("number-exponent", "const value = 1.25e-10;");
            yield return Case("number-hex", "const value = 0xdeadbeef;");
            yield return Case("number-binary", "const value = 0b101010;");
            yield return Case("number-octal", "const value = 0o755;");
            yield return Case("number-numeric-separators", "const value = 1_000_000.25;");

            // Operator cases stress stack order and update-result preservation.
            yield return Case("operator-bitwise-chain", "function bits(a, b, c) { return (a & b) | (b ^ c); }");
            yield return Case("operator-shift-chain", "function shift(value, count) { return [value << count, value >> count, value >>> count]; }");
            yield return Case("operator-exponentiation-chain", "function power(a, b, c) { return a ** b ** c; }");
            yield return Case("operator-unary-sequence", "function inspect(value) { return [!value, ~value, +value, -value, void value, typeof value]; }");
            yield return Case("operator-strict-equality", "function compare(left, right) { return [left === right, left !== right]; }");
            yield return Case("operator-relational-chain", "function compare(a, b) { return [a < b, a <= b, a > b, a >= b]; }");
            yield return Case("operator-in-instanceof", "function inspect(key, value, Type) { return [key in value, value instanceof Type]; }");
            yield return Case("operator-member-compound-assignment", "function update(target, key, value) { return target[key] += value; }");
            yield return Case("operator-member-post-decrement", "function update(target, key) { return target[key]--; }");
            yield return Case("operator-comma-assignment-value", "function update(target) { return (target.value = first(), second()); }");

            // Strings, templates, and regex literals have dedicated constant formats.
            yield return Case("string-escape-sequences", "const value = '\\0\\b\\f\\n\\r\\t\\v';");
            yield return Case("string-hex-unicode-escapes", "const value = '\\x41\\u0042\\u{43}';");
            yield return Case("string-non-bmp", "const value = '\\u{1f642}';");
            yield return Case("template-static-escapes", "const value = `line\\nvalue\\u{21}`;");
            yield return Case("template-nested-expression", "const value = `outer ${`inner ${item}`}`;");
            yield return Case("tagged-template-raw-escape", "tag`line\\n${value}tail`;");
            yield return Case("tagged-template-two-sites", "function render(tag) { return [tag`a${one}`, tag`a${two}`]; }");
            yield return Case("regexp-character-class", "const pattern = /[a-z]+\\/[0-9]*/gi;");
            yield return Case("regexp-lookahead", "const pattern = /item(?=:)/u;");
            yield return Case("regexp-named-capture", "const pattern = /(?<key>[a-z]+)=(?<value>.*)/;");

            // Resume values and abrupt completions create distinct coroutine edges.
            yield return Case("generator-receives-next-value", "function* exchange() { const first = yield request(1); const second = yield request(first); return second; }");
            yield return Case("generator-catches-thrown-resume", "function* guarded() { try { yield value(); } catch (error) { yield recover(error); } }");
            yield return Case("generator-yield-in-catch-finally", "function* guarded(task) { try { yield task(); } catch (error) { yield recover(error); } finally { yield cleanup(); } }");
            yield return Case("generator-delegation-return", "function* delegate(items) { return yield* items; }");
            yield return Case("generator-delegation-in-try", "function* delegate(items) { try { yield* items; } catch (error) { return recover(error); } }");
            yield return Case("async-multiple-await-expression", "async function combine(left, right) { return (await left()) + (await right()); }");
            yield return Case("async-await-in-catch", "async function recover(task) { try { return await task(); } catch (error) { return await fallback(error); } }");
            yield return Case("async-generator-yield-await", "async function* map(items) { for (const item of items) yield await convert(item); }");
            yield return Case("async-generator-yield-star", "async function* flatten(items) { yield* items; }");
            yield return Case("async-generator-return-finally", "async function* guarded() { try { yield value(); return result(); } finally { await cleanup(); } }");

            // Computed class keys and super updates depend on home-object state.
            yield return Case("class-numeric-and-string-methods", "class Lookup { 1() { return one(); } 'two'() { return two(); } }");
            yield return Case("class-symbol-computed-method", "class Sequence { [Symbol.iterator]() { return iterator(); } }");
            yield return Case("class-computed-accessor-order", "class Model { get [key(1)]() { return read(); } set [key(2)](value) { write(value); } }");
            yield return Case("class-computed-private-interleave", "class Model { [key(1)] = init(1); #value = init(2); static [key(3)] = init(3); read() { return this.#value; } }");
            yield return Case("super-prefix-update", "class Base { get value() { return read(); } set value(next) { write(next); } } class Child extends Base { update() { return ++super.value; } }");
            yield return Case("super-postfix-update", "class Base { get value() { return read(); } set value(next) { write(next); } } class Child extends Base { update() { return super.value--; } }");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_literal_or_coroutine_boundary(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source)
        => [name, source, $"/tmp/reference-units/literal-coroutine/{name}.js"];
}
