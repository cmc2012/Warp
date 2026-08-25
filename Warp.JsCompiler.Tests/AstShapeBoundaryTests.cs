using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Structural parser tests that pin AST node selection and nesting.</summary>
public sealed class AstShapeBoundaryTests
{
    public static IEnumerable<object[]> ExpressionRootCases
    {
        get
        {
            yield return Root("identifier", "value", typeof(JsIdentifierExpression));
            yield return Root("number", "123", typeof(JsLiteralExpression));
            yield return Root("string", "'value'", typeof(JsLiteralExpression));
            yield return Root("regexp", "/value/", typeof(JsLiteralExpression));
            yield return Root("array", "[first, second]", typeof(JsArrayExpression));
            yield return Root("object", "({ first, second: value })", typeof(JsObjectExpression));
            yield return Root("unary", "!value", typeof(JsUnaryExpression));
            yield return Root("prefix-update", "++value", typeof(JsUpdateExpression));
            yield return Root("postfix-update", "value--", typeof(JsUpdateExpression));
            yield return Root("binary", "left + right", typeof(JsBinaryExpression));
            yield return Root("assignment", "left = right", typeof(JsAssignmentExpression));
            yield return Root("compound-assignment", "left += right", typeof(JsAssignmentExpression));
            yield return Root("conditional", "test ? left : right", typeof(JsConditionalExpression));
            yield return Root("member", "target.value", typeof(JsMemberExpression));
            yield return Root("computed-member", "target[key]", typeof(JsMemberExpression));
            yield return Root("optional-member", "target?.value", typeof(JsMemberExpression));
            yield return Root("call", "read(value)", typeof(JsCallExpression));
            yield return Root("optional-call", "read?.(value)", typeof(JsCallExpression));
            yield return Root("function-expression", "function named() {}", typeof(JsFunctionExpression));
            yield return Root("arrow", "value => value", typeof(JsFunctionExpression));
            yield return Root("async-arrow", "async value => await value", typeof(JsFunctionExpression));
            yield return Root("class-expression", "class Named {}", typeof(JsClassExpression));
            yield return Root("new", "new Value()", typeof(JsNewExpression));
            yield return Root("dynamic-import", "import(name)", typeof(JsDynamicImportExpression));
            yield return Root("import-meta", "import.meta", typeof(JsImportMetaExpression));
            yield return Root("tagged-template", "tag`value`", typeof(JsTaggedTemplateExpression));
            yield return Root("sequence", "first, second, third", typeof(JsSequenceExpression));
        }
    }

    public static IEnumerable<object[]> StatementRootCases
    {
        get
        {
            yield return Root("empty", ";", typeof(JsEmptyStatement));
            yield return Root("block", "{}", typeof(JsBlockStatement));
            yield return Root("expression", "value;", typeof(JsExpressionStatement));
            yield return Root("var", "var value;", typeof(JsVariableStatement));
            yield return Root("let", "let value;", typeof(JsVariableStatement));
            yield return Root("const", "const value = 1;", typeof(JsVariableStatement));
            yield return Root("if", "if (test) left(); else right();", typeof(JsIfStatement));
            yield return Root("while", "while (test) work();", typeof(JsWhileStatement));
            yield return Root("do-while", "do work(); while (test);", typeof(JsDoWhileStatement));
            yield return Root("classic-for", "for (let i = 0; i < 1; i++) work();", typeof(JsForStatement));
            yield return Root("for-in", "for (const key in value) work();", typeof(JsForInOfStatement));
            yield return Root("for-of", "for (const item of value) work();", typeof(JsForInOfStatement));
            yield return Root("switch", "switch (value) { case 1: break; }", typeof(JsSwitchStatement));
            yield return Root("try", "try {} catch {}", typeof(JsTryStatement));
            yield return Root("class", "class Value {}", typeof(JsClassDeclaration));
            yield return Root("label", "label: while (test) break label;", typeof(JsLabeledStatement));
            yield return Root("with", "with (value) work();", typeof(JsWithStatement));
            yield return Root("function", "function read() {}", typeof(JsFunctionStatement));
            yield return Root("async-function", "async function read() {}", typeof(JsFunctionStatement));
            yield return Root("import", "import value from 'source';", typeof(JsImportStatement));
            yield return Root("export", "export const value = 1;", typeof(JsExportStatement));
            yield return Root("export-all", "export * from 'source';", typeof(JsExportAllStatement));
        }
    }

    [Theory]
    [MemberData(nameof(ExpressionRootCases))]
    public void Selects_expression_root_node(string caseName, string source, Type expectedType)
    {
        Assert.NotEmpty(caseName);
        Assert.IsType(expectedType, Expression(source));
    }

    [Theory]
    [MemberData(nameof(StatementRootCases))]
    public void Selects_statement_root_node(string caseName, string source, Type expectedType)
    {
        Assert.NotEmpty(caseName);
        Assert.IsType(expectedType, Assert.Single(Parse(source).Body));
    }

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        var addition = Assert.IsType<JsBinaryExpression>(Expression("left + middle * right"));
        Assert.Equal("+", addition.Operator);
        Assert.Equal("*", Assert.IsType<JsBinaryExpression>(addition.Right).Operator);
    }

    [Fact]
    public void Addition_binds_tighter_than_shift()
    {
        var shift = Assert.IsType<JsBinaryExpression>(Expression("left << middle + right"));
        Assert.Equal("<<", shift.Operator);
        Assert.Equal("+", Assert.IsType<JsBinaryExpression>(shift.Right).Operator);
    }

    [Fact]
    public void Logical_and_binds_tighter_than_logical_or()
    {
        var logicalOr = Assert.IsType<JsBinaryExpression>(Expression("left || middle && right"));
        Assert.Equal("||", logicalOr.Operator);
        Assert.Equal("&&", Assert.IsType<JsBinaryExpression>(logicalOr.Right).Operator);
    }

    [Fact]
    public void Assignment_is_right_associative()
    {
        var assignment = Assert.IsType<JsAssignmentExpression>(Expression("first = second = third"));
        Assert.IsType<JsAssignmentExpression>(assignment.Right);
    }

    [Fact]
    public void Conditional_alternate_accepts_assignment()
    {
        var conditional = Assert.IsType<JsConditionalExpression>(Expression("test ? first : second = third"));
        Assert.IsType<JsAssignmentExpression>(conditional.Alternate);
    }

    [Fact]
    public void Sequence_retains_source_order()
    {
        var sequence = Assert.IsType<JsSequenceExpression>(Expression("first, second, third"));
        Assert.Equal(["first", "second", "third"], sequence.Expressions.Cast<JsIdentifierExpression>().Select(item => item.Name));
    }

    [Fact]
    public void Member_call_wraps_member_as_callee()
    {
        var call = Assert.IsType<JsCallExpression>(Expression("target.method(argument)"));
        var member = Assert.IsType<JsMemberExpression>(call.Callee);
        Assert.False(member.Computed);
        Assert.Equal("method", Assert.IsType<JsIdentifierExpression>(member.Property).Name);
    }

    [Fact]
    public void Optional_chain_records_each_optional_segment()
    {
        var call = Assert.IsType<JsCallExpression>(Expression("target?.method?.(argument)"));
        Assert.True(call.Optional);
        Assert.True(call.DirectOptional);
        Assert.True(Assert.IsType<JsMemberExpression>(call.Callee).Optional);
    }

    [Fact]
    public void Array_preserves_elisions_and_spread()
    {
        var array = Assert.IsType<JsArrayExpression>(Expression("[first, , ...rest]"));
        Assert.Equal(3, array.Elements.Count);
        Assert.Null(array.Elements[1]);
        Assert.IsType<JsSpreadExpression>(array.Elements[2]);
    }

    [Fact]
    public void Object_distinguishes_shorthand_method_and_accessor()
    {
        var value = Assert.IsType<JsObjectExpression>(Expression("({ item, method() {}, get current() { return item; }, set current(value) {} })"));
        Assert.True(value.Properties[0].Shorthand);
        Assert.Equal(JsObjectPropertyKind.Method, value.Properties[1].Kind);
        Assert.Equal(JsObjectPropertyKind.Getter, value.Properties[2].Kind);
        Assert.Equal(JsObjectPropertyKind.Setter, value.Properties[3].Kind);
    }

    [Fact]
    public void Tagged_template_retains_cooked_raw_and_substitutions()
    {
        var tagged = Assert.IsType<JsTaggedTemplateExpression>(Expression("tag`line\\n${value}tail`"));
        Assert.Equal(["line\n", "tail"], tagged.Cooked);
        Assert.Equal(["line\\n", "tail"], tagged.Raw);
        Assert.Single(tagged.Substitutions);
    }

    [Fact]
    public void Function_records_length_defaults_rest_and_patterns()
    {
        var function = Assert.IsType<JsFunctionStatement>(Assert.Single(Parse("function read(first, second = 1, ...rest) {}").Body));
        Assert.Equal(1, function.DefinedArgCount);
        Assert.Equal(["first", "second", "rest"], function.Parameters);
        Assert.NotNull(function.ParameterDefaults![1]);
        Assert.IsType<JsRestPattern>(function.ParameterPatterns![2]);
    }

    [Fact]
    public void Arrow_expression_body_becomes_return_statement()
    {
        var arrow = Assert.IsType<JsFunctionExpression>(Expression("value => value + 1"));
        Assert.True(arrow.Arrow);
        Assert.IsType<JsBinaryExpression>(Assert.IsType<JsReturnStatement>(Assert.Single(arrow.Body.Body)).Argument);
    }

    [Fact]
    public void Generator_yield_star_records_delegation()
    {
        var function = Assert.IsType<JsFunctionStatement>(Assert.Single(Parse("function* values(items) { yield* items; }").Body));
        var yielded = Assert.IsType<JsYieldExpression>(Assert.IsType<JsExpressionStatement>(Assert.Single(function.Body.Body)).Expression);
        Assert.True(yielded.Delegate);
    }

    [Fact]
    public void Async_function_await_has_dedicated_node()
    {
        var function = Assert.IsType<JsFunctionStatement>(Assert.Single(Parse("async function read(value) { return await value; }").Body));
        Assert.True(function.Async);
        Assert.IsType<JsAwaitExpression>(Assert.IsType<JsReturnStatement>(Assert.Single(function.Body.Body)).Argument);
    }

    [Fact]
    public void For_in_and_for_of_have_distinct_flags()
    {
        var program = Parse("for (const key in source) use(key); for (const value of source) use(value);");
        Assert.False(Assert.IsType<JsForInOfStatement>(program.Body[0]).IsOf);
        Assert.True(Assert.IsType<JsForInOfStatement>(program.Body[1]).IsOf);
    }

    [Fact]
    public void For_await_records_async_iteration()
    {
        var function = Assert.IsType<JsFunctionStatement>(Assert.Single(Parse("async function read(items) { for await (const item of items) use(item); }").Body));
        var loop = Assert.IsType<JsForInOfStatement>(Assert.Single(function.Body.Body));
        Assert.True(loop.IsOf);
        Assert.True(loop.IsAwait);
    }

    [Fact]
    public void Try_records_pattern_catch_and_finally()
    {
        var statement = Assert.IsType<JsTryStatement>(Assert.Single(Parse("try {} catch ({ message }) {} finally {}").Body));
        Assert.IsType<JsObjectPattern>(statement.Handler!.Pattern);
        Assert.NotNull(statement.Finalizer);
    }

    [Fact]
    public void Class_records_member_kinds_and_flags()
    {
        var declaration = Assert.IsType<JsClassDeclaration>(Assert.Single(Parse("class Value { constructor() {} method() {} get item() {} set item(value) {} field = 1; static { init(); } }").Body));
        Assert.Equal([JsClassMemberKind.Constructor, JsClassMemberKind.Method, JsClassMemberKind.Getter,
            JsClassMemberKind.Setter, JsClassMemberKind.Field, JsClassMemberKind.StaticBlock], declaration.Members.Select(member => member.Kind));
    }

    [Fact]
    public void Import_records_default_named_and_namespace_binding_kinds()
    {
        var program = Parse("import primary, { value as local } from 'first'; import * as api from 'second';");
        var first = Assert.IsType<JsImportStatement>(program.Body[0]);
        var second = Assert.IsType<JsImportStatement>(program.Body[1]);
        Assert.Equal([JsImportBindingKind.Default, JsImportBindingKind.Named], first.Bindings.Select(binding => binding.Kind));
        Assert.Equal(JsImportBindingKind.Namespace, Assert.Single(second.Bindings).Kind);
    }

    [Fact]
    public void Reexport_records_alias_and_source()
    {
        var export = Assert.IsType<JsExportStatement>(Assert.Single(Parse("export { value as publicValue } from 'source';").Body));
        Assert.Equal("source", export.Source);
        var binding = Assert.Single(export.Bindings);
        Assert.Equal("value", binding.LocalName);
        Assert.Equal("publicValue", binding.ExportName);
    }

    private static JsAstProgram Parse(string source)
    {
        var tokens = new JavaScriptScanner(source, "ast-shape.js").Scan();
        return new JavaScriptAstParser(tokens, "ast-shape.js").ParseProgram();
    }

    private static JsExpression Expression(string source)
        // Function and class literals are declarations at program-statement
        // position but expressions when nested in an expression context.
        // Parentheses provide that context without changing the expression
        // node produced by the parser.
        => Assert.IsType<JsExpressionStatement>(Assert.Single(Parse("(" + source + ");").Body)).Expression;

    private static object[] Root(string name, string source, Type type) => [name, source, type];
}
