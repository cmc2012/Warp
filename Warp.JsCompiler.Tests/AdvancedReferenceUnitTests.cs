using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class AdvancedReferenceUnitTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return new object[]
            {
                "for-of lexical continue",
                "function collect(items) { const out = []; for (let value of items) { if (value == null) continue; out.push(value); } return out; }",
                "/tmp/reference-units/for-of-lexical-continue.js",
            };
            yield return new object[]
            {
                "object rest default",
                "function unpack({ head = 0, ...rest } = {}) { return [head, rest]; }",
                "/tmp/reference-units/object-rest-default.js",
            };
            yield return new object[]
            {
                "derived method arrow closure",
                "class Parent { read() { return this.value; } } class Child extends Parent { constructor(value) { super(); this.value = value; } makeReader() { return () => super.read(); } }",
                "/tmp/reference-units/derived-arrow-super.js",
            };
            yield return new object[]
            {
                "generator finally",
                "function* values(items) { try { for (const item of items) yield item; } finally { cleanup(); } }",
                "/tmp/reference-units/generator-finally.js",
            };
            yield return new object[]
            {
                "async iterator finally",
                "async function collect(items) { const result = []; for await (const item of items) { try { result.push(await item); } finally { trace(item); } } return result; }",
                "/tmp/reference-units/async-iterator-finally.js",
            };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_each_advanced_unit(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
