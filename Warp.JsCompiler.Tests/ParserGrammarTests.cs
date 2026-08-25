using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Grammar and early-error units kept separate from bytecode goldens.</summary>
public sealed class ParserGrammarTests
{
    [Fact]
    public void Export_async_generator_is_a_function_declaration()
    {
        var program = Parse("export async function* work({ id }) { return id; }");
        var export = Assert.IsType<JsExportStatement>(Assert.Single(program.Body));
        var function = Assert.IsType<JsFunctionStatement>(export.Declaration);
        Assert.True(function.Async);
        Assert.True(function.Generator);
        Assert.IsType<JsObjectPattern>(Assert.Single(function.ParameterPatterns!));
    }

    [Fact]
    public void Object_methods_and_accessors_accept_binding_patterns()
    {
        var expression = Assert.IsType<JsObjectExpression>(
            Assert.IsType<JsExpressionStatement>(Assert.Single(Parse("({ method([first], { value }) {}, set item({ value }) {} });").Body)).Expression);

        Assert.All(expression.Properties.Where(property => property.Kind is JsObjectPropertyKind.Method or JsObjectPropertyKind.Setter),
            property => Assert.NotNull(Assert.IsType<JsFunctionExpression>(property.Value).ParameterPatterns));
    }

    [Fact]
    public void Exponentiation_is_right_associative()
    {
        var expression = Assert.IsType<JsBinaryExpression>(Expression("a ** b ** c"));
        Assert.Equal("**", expression.Operator);
        Assert.IsType<JsBinaryExpression>(expression.Right);
    }

    [Fact]
    public void Unary_left_operand_of_exponentiation_is_an_early_error()
        => Assert.Throws<JavaScriptCompilationException>(() => Expression("-a ** b"));

    [Theory]
    [InlineData("a ?? b || c")]
    [InlineData("a && b ?? c")]
    public void Nullish_and_logical_operators_require_parentheses(string source)
        => Assert.Throws<JavaScriptCompilationException>(() => Expression(source));

    [Theory]
    [InlineData("a ?? (b || c)")]
    [InlineData("(a && b) ?? c")]
    public void Parenthesized_nullish_and_logical_operators_are_valid(string source)
        => _ = Expression(source);

    [Fact]
    public void Line_terminator_ends_return_and_disables_postfix_update()
    {
        var program = Parse("function f() { return\nvalue; }\nx\n++y;");
        var function = Assert.IsType<JsFunctionStatement>(program.Body[0]);
        Assert.Null(Assert.IsType<JsReturnStatement>(function.Body.Body[0]).Argument);
        Assert.IsType<JsExpressionStatement>(function.Body.Body[1]);
        Assert.IsType<JsExpressionStatement>(program.Body[1]);
        Assert.IsType<JsUpdateExpression>(Assert.IsType<JsExpressionStatement>(program.Body[2]).Expression);
    }

    [Fact]
    public void Line_terminator_after_throw_is_an_error()
        => Assert.Throws<JavaScriptCompilationException>(() => Parse("throw\nvalue;"));

    [Fact]
    public void Static_can_be_a_class_field_name()
    {
        var declaration = Assert.IsType<JsClassDeclaration>(Assert.Single(Parse("class C { static; static = 1; }").Body));
        Assert.All(declaration.Members, member =>
        {
            Assert.Equal(JsClassMemberKind.Field, member.Kind);
            Assert.Equal("static", member.Name);
            Assert.False(member.IsStatic);
        });
    }

    private static JsAstProgram Parse(string source)
    {
        var tokens = new JavaScriptScanner(source, "parser-unit.js").Scan();
        return new JavaScriptAstParser(tokens, "parser-unit.js").ParseProgram();
    }

    private static JsExpression Expression(string source)
        => Assert.IsType<JsExpressionStatement>(Assert.Single(Parse(source + ";").Body)).Expression;
}
