using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Grammar acceptance and early-error boundaries independent of bytecode generation.</summary>
public sealed class ParserBoundaryTests
{
    public static IEnumerable<object[]> InvalidSources
    {
        get
        {
            yield return Case("unterminated-string", "const value = 'missing;");
            yield return Case("unterminated-template", "const value = `missing;");
            yield return Case("unterminated-template-expression", "const value = `missing ${value;");
            yield return Case("unterminated-regexp", "const value = /missing;");
            yield return Case("unterminated-block-comment", "/* missing");
            yield return Case("invalid-hex-escape", "const value = '\\xGG';");
            yield return Case("invalid-unicode-escape", "const value = '\\uZZZZ';");
            yield return Case("unicode-code-point-out-of-range", "const value = '\\u{110000}';");
            yield return Case("missing-variable-binding", "let = value;");
            yield return Case("missing-const-initializer", "const value;");
            yield return Case("rest-binding-not-last", "const [...rest, last] = values;");
            yield return Case("object-rest-not-last", "const { ...rest, last } = value;");
            yield return Case("rest-parameter-not-last", "function read(...rest, last) {}");
            yield return Case("setter-two-parameters", "const value = { set item(left, right) {} };");
            yield return Case("setter-rest-parameter", "const value = { set item(...values) {} };");
            yield return Case("getter-with-parameter", "const value = { get item(value) {} };");
            yield return Case("duplicate-object-prototype", "const value = { __proto__: left, __proto__: right };");
            yield return Case("generator-accessor", "const value = { get *item() {} };");
            yield return Case("async-constructor", "class Value { async constructor() {} }");
            yield return Case("generator-constructor", "class Value { *constructor() {} }");
            yield return Case("duplicate-class-constructor", "class Value { constructor() {} constructor(value) {} }");
            yield return Case("static-prototype-method", "class Value { static prototype() {} }");
            yield return Case("private-constructor", "class Value { #constructor() {} }");
            yield return Case("await-outside-async", "function read() { return await value; }");
            yield return Case("yield-outside-generator", "function read() { return yield value; }");
            yield return Case("for-await-outside-async", "function read(items) { for await (const item of items) {} }");
            yield return Case("for-await-in", "async function read(items) { for await (const key in items) {} }");
            yield return Case("new-target-outside-function", "const value = new.target;");
            yield return Case("throw-line-terminator", "throw\nvalue;");
            yield return Case("nullish-logical-without-parentheses", "const value = left ?? middle || right;");
            yield return Case("unary-left-of-exponentiation", "const value = -left ** right;");
            yield return Case("invalid-assignment-literal", "1 = value;");
            yield return Case("invalid-update-call", "read()++;");
            yield return Case("optional-chain-assignment", "target?.value = next;");
            yield return Case("optional-chain-new", "new target?.Value();");
            yield return Case("optional-chain-tag", "target?.tag`value`;");
            yield return Case("break-outside-breakable", "break;");
            yield return Case("continue-outside-loop", "continue;");
            yield return Case("continue-to-block-label", "label: { continue label; }");
            yield return Case("unknown-break-label", "while (ready) { break missing; }");
            yield return Case("return-outside-function", "return value;");
            yield return Case("import-missing-specifier", "import value from;");
            yield return Case("export-from-missing-specifier", "export { value } from;");
            yield return Case("try-without-handler", "try { work(); }");
            yield return Case("catch-missing-binding-close", "try {} catch (error { }");
            yield return Case("private-brand-check", "class Value { #field; accepts(target) { return #field in target; } }");
        }
    }

    public static IEnumerable<object[]> ValidSources
    {
        get
        {
            yield return Case("empty-statements", ";;;");
            yield return Case("automatic-semicolon-return", "function read() { return\nvalue; }");
            yield return Case("automatic-semicolon-break-label", "outer: while (ready) { break\nouter; }");
            yield return Case("regex-after-return", "function pattern() { return /value+/gi; }");
            yield return Case("division-after-parenthesis", "const ratio = (left) / right;");
            yield return Case("nested-template-braces", "const value = `before ${{ value: { nested: true } }} after`;");
            yield return Case("template-with-regex-expression", "const value = `pattern ${/a{2}/.source}`;");
            yield return Case("string-line-continuation", "const value = 'before\\\nafter';");
            yield return Case("unicode-lone-surrogate", "const value = '\\u{d800}';");
            yield return Case("numeric-member-access", "const value = 1..toString();");
            yield return Case("parenthesized-nullish-logical", "const value = (left ?? middle) || right;");
            yield return Case("exponentiation-parenthesized-unary", "const value = (-left) ** right;");
            yield return Case("async-line-break-function-name", "async\nfunction read() {}");
            yield return Case("async-arrow", "const read = async value => await value;");
            yield return Case("async-generator", "async function* values() { yield await next(); }");
            yield return Case("generator-empty-yield", "function* values() { yield; }");
            yield return Case("generator-delegation", "function* values(items) { yield* items; }");
            yield return Case("for-await-of", "async function read(items) { for await (const item of items) use(item); }");
            yield return Case("catch-without-binding", "try { work(); } catch { recover(); }");
            yield return Case("destructured-catch-binding", "try { work(); } catch ({ message, code = 0 }) { use(message, code); }");
            yield return Case("computed-destructuring-binding", "const { [key()]: value = fallback() } = source;");
            yield return Case("array-trailing-elisions", "const [first, , ,] = values;");
            yield return Case("object-trailing-comma", "const value = { first: 1, second: 2, };");
            yield return Case("function-trailing-parameter-comma", "function read(first, second,) {}");
            yield return Case("call-trailing-comma", "read(first, second,);");
            yield return Case("new-trailing-comma", "new Value(first, second,);");
            yield return Case("class-empty-fields", "class Value { first; static second; #third; }");
            yield return Case("class-computed-generator", "class Value { *[key()]() { yield item; } }");
            yield return Case("class-async-computed-method", "class Value { async [key()]() { await item; } }");
            yield return Case("object-async-generator-method", "const value = { async *items() { yield await next(); } };");
            yield return Case("object-shorthand-default-in-pattern", "const { value = fallback() } = source;");
            yield return Case("optional-chain-computed", "const value = target?.[key()];");
            yield return Case("optional-chain-call", "const value = target?.method?.(argument);");
            yield return Case("export-default-async-generator", "export default async function* values() { yield await next(); }");
            yield return Case("export-namespace", "export * as api from 'module';");
            yield return Case("import-default-and-namespace", "import primary, * as api from 'module';");
            yield return Case("import-default-and-named", "import primary, { first, second as local } from 'module';");
            yield return Case("labelled-nested-loop", "outer: for (;;) { inner: while (ready) { continue outer; } }");
            yield return Case("switch-default-before-case", "switch (value) { default: fallback(); break; case 1: one(); }");
            yield return Case("do-while-without-semicolon", "do { work(); } while (ready)");
            yield return Case("debugger-statement", "debugger;");
        }
    }

    [Theory]
    [MemberData(nameof(InvalidSources))]
    public void Rejects_invalid_grammar_boundary(string caseName, string source)
    {
        Assert.NotEmpty(caseName);
        Assert.Throws<JavaScriptCompilationException>(() => Parse(source));
    }

    [Theory]
    [MemberData(nameof(ValidSources))]
    public void Accepts_valid_grammar_boundary(string caseName, string source)
    {
        Assert.NotEmpty(caseName);
        _ = Parse(source);
    }

    private static JsAstProgram Parse(string source)
    {
        var tokens = new JavaScriptScanner(source, "parser-boundary.js").Scan();
        return new JavaScriptAstParser(tokens, "parser-boundary.js").ParseProgram();
    }

    private static object[] Case(string name, string source) => [name, source];
}
