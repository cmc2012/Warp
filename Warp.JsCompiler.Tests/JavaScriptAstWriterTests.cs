using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class JavaScriptAstWriterTests
{
    [Theory]
    [InlineData("const config = { id: 1, async load({ id } = {}) { return await fetch(id); } }; export { config };")]
    [InlineData("async function* work(items) { for (const item of items) { if (item?.enabled) yield tag`id:${item.id}`; } } export default work;")]
    [InlineData("class Counter extends Base { static total = 0; #value = 1; get value() { return this.#value; } set value(next) { this.#value = next; } }")]
    [InlineData("const { 'x-y': renamed, [key]: value = 1, ...rest } = source;")]
    [InlineData("const result = { 'x-y': 1, local = 2, ...rest, [key]: 3 }; ")]
    [InlineData("import main, * as ns from 'library'; export { main as entry } from 'library'; export * from 'other';")]
    [InlineData("try { throw error; } catch ({ message = 'unknown' }) { report(message); } finally { close(); }")]
    [InlineData("async function consume(stream) { for await (const item of stream) { switch (item.kind) { case 'ok': continue; default: break; } } }")]
    [InlineData("const invoke = target?.[key]?.(...args); const next = value ?? (left || right);")]
    public void Writes_a_program_that_the_parser_can_read_again(string source)
    {
        var original = Parse(source);
        var written = JavaScriptAstWriter.Write(original);
        var reparsed = Parse(written);

        Assert.Equal(original.Body.Count, reparsed.Body.Count);
        // A top-level statement count cannot catch a writer that silently
        // changes a nested pattern, optional call, class member, or export.
        // Writing the reparsed tree again must be stable: this is the
        // canonical source form used by the component emitter.
        Assert.Equal(written, JavaScriptAstWriter.Write(reparsed));
    }

    [Fact]
    public void Escapes_string_literals_instead_of_reusing_cooked_content_as_source()
    {
        var written = JavaScriptAstWriter.Write(Parse("const message = 'line\\nbreak';"));
        var declaration = Assert.IsType<JsVariableStatement>(Assert.Single(Parse(written).Body));
        var literal = Assert.IsType<JsLiteralExpression>(Assert.Single(declaration.Declarations).Initializer);

        Assert.Equal("line\nbreak", literal.Raw);
    }

    [Fact]
    public void Rejects_compiler_only_nodes_that_have_no_source_spelling()
    {
        var program = new JsAstProgram([new JsPrivateBrandStatement(1, 1)]);

        Assert.Throws<InvalidOperationException>(() => JavaScriptAstWriter.Write(program));
    }

    [Fact]
    public void Keeps_a_member_call_receiver_adjacent_to_the_call()
    {
        var written = JavaScriptAstWriter.Write(Parse("page.refresh();"));

        Assert.Contains("page.refresh()", written, StringComparison.Ordinal);
        Assert.DoesNotContain("(page.refresh)()", written, StringComparison.Ordinal);
    }

    private static JsAstProgram Parse(string source)
    {
        var tokens = new JavaScriptScanner(source, "writer-test.js").Scan();
        return new JavaScriptAstParser(tokens, "writer-test.js").ParseProgram();
    }
}
