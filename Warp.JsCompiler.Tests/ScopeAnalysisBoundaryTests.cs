using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Binding-resolution contracts exposed by the front-end scope analysis.</summary>
public sealed class ScopeAnalysisBoundaryTests
{
    public static IEnumerable<object[]> GlobalBindingCases
    {
        get
        {
            yield return Binding("var", "var value;", "value", JsBindingKind.Var);
            yield return Binding("let", "let value;", "value", JsBindingKind.Let);
            yield return Binding("const", "const value = 1;", "value", JsBindingKind.Const);
            yield return Binding("function", "function value() {}", "value", JsBindingKind.Function);
            yield return Binding("class", "class Value {}", "Value", JsBindingKind.Let);
            yield return Binding("export-var", "export var value;", "value", JsBindingKind.Var);
            yield return Binding("export-let", "export let value;", "value", JsBindingKind.Let);
            yield return Binding("export-const", "export const value = 1;", "value", JsBindingKind.Const);
            yield return Binding("export-function", "export function value() {}", "value", JsBindingKind.Function);
            yield return Binding("export-class", "export class Value {}", "Value", JsBindingKind.Let);
            yield return Binding("array-pattern-first", "const [first, second] = source;", "first", JsBindingKind.Const);
            yield return Binding("array-pattern-second", "const [first, second] = source;", "second", JsBindingKind.Const);
            yield return Binding("array-rest", "let [first, ...rest] = source;", "rest", JsBindingKind.Let);
            yield return Binding("object-shorthand", "const { value } = source;", "value", JsBindingKind.Const);
            yield return Binding("object-alias", "const { source: local } = input;", "local", JsBindingKind.Const);
            yield return Binding("object-rest", "const { first, ...rest } = source;", "rest", JsBindingKind.Const);
            yield return Binding("nested-pattern", "let { outer: [value] } = source;", "value", JsBindingKind.Let);
        }
    }

    public static IEnumerable<object[]> ReferenceCases
    {
        get
        {
            yield return References("unused", "const value = 1;", "value", 0, false);
            yield return References("single-read", "const value = 1; use(value);", "value", 1, false);
            yield return References("multiple-reads", "const value = 1; use(value, value); value;", "value", 3, false);
            yield return References("assignment", "let value; value = next;", "value", 1, false);
            yield return References("update", "let value = 0; value++;", "value", 1, false);
            yield return References("computed-member", "const key = name; target[key];", "key", 1, false);
            yield return References("noncomputed-property-excluded", "const property = 1; target.property;", "property", 0, false);
            yield return References("object-shorthand-read", "const value = 1; ({ value });", "value", 1, false);
            yield return References("object-key-excluded", "const key = 1; ({ key: value });", "key", 0, false);
            yield return References("object-computed-key", "const key = 1; ({ [key]: value });", "key", 1, false);
            yield return References("nested-block-not-capture", "let value = 1; { use(value); }", "value", 1, false);
            yield return References("function-capture", "let value = 1; function read() { return value; }", "value", 1, true);
            yield return References("arrow-capture", "let value = 1; const read = () => value;", "value", 1, true);
            yield return References("nested-function-capture", "let value = 1; function outer() { return function inner() { return value; }; }", "value", 1, true);
            yield return References("class-method-capture", "let value = 1; class Reader { read() { return value; } }", "value", 1, true);
            yield return References("class-field-initializer", "let value = 1; class Reader { field = value; }", "value", 1, true);
            yield return References("computed-class-key-not-capture", "let key = name; class Reader { [key]() {} }", "key", 1, false);
            yield return References("parameter-shadows-global", "let value = 1; function read(value) { return value; }", "value", 0, false);
            yield return References("local-shadows-global", "let value = 1; function read() { let value = 2; return value; }", "value", 0, false);
            yield return References("catch-shadows-global", "let error = 1; try { work(); } catch (error) { use(error); }", "error", 0, false);
        }
    }

    public static IEnumerable<object[]> UnresolvedCases
    {
        get
        {
            yield return Unresolved("single-global", "missing;", "missing");
            yield return Unresolved("call-and-argument", "invoke(argument);", "invoke", "argument");
            yield return Unresolved("member-object-only", "target.property;", "target");
            yield return Unresolved("computed-member-both", "target[key];", "target", "key");
            yield return Unresolved("new-callee-and-argument", "new Type(value);", "Type", "value");
            yield return Unresolved("array-elements", "[first, second];", "first", "second");
            yield return Unresolved("object-value", "({ key: value });", "value");
            yield return Unresolved("spread", "[...items];", "items");
            yield return Unresolved("conditional", "test ? left : right;", "test", "left", "right");
            yield return Unresolved("function-body", "function read() { return missing; }", "missing");
            yield return Unresolved("default-parameter", "function read(value = fallback) {}", "fallback");
            yield return Unresolved("class-extends", "class Value extends Base {}", "Base");
            yield return Unresolved("class-computed-key", "class Value { [key]() {} }", "key");
            yield return Unresolved("try-paths", "try { work(); } catch { recover(); } finally { cleanup(); }", "work", "recover", "cleanup");
            yield return Unresolved("for-of-iterable-and-body", "for (const item of items) use(item);", "items", "use");
            yield return Unresolved("typeof-unbound", "typeof missing;", "missing");
        }
    }

    public static IEnumerable<object[]> DuplicateCases
    {
        get
        {
            yield return Duplicate("let-let", "let value; let value;");
            yield return Duplicate("const-const", "const value = 1; const value = 2;");
            yield return Duplicate("let-const", "let value; const value = 1;");
            yield return Duplicate("const-function", "const value = 1; function value() {}");
            yield return Duplicate("class-let", "class Value {} let Value;");
            yield return Duplicate("pattern-collision", "const [value, value] = source;");
            yield return Duplicate("object-pattern-collision", "let { first: value, second: value } = source;");
        }
    }

    [Theory]
    [MemberData(nameof(GlobalBindingCases))]
    public void Declares_expected_global_binding(string caseName, string source, string name, string kind)
    {
        Assert.NotEmpty(caseName);
        var binding = Analyze(source).GlobalScope.Bindings[name];
        Assert.Equal(kind, binding.Kind.ToString());
        Assert.Equal(name, binding.Name);
        Assert.True(binding.Scope.IsFunction);
        Assert.Null(binding.Scope.Parent);
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void Counts_and_marks_global_references(string caseName, string source, string name, int count, bool captured)
    {
        Assert.NotEmpty(caseName);
        var binding = Analyze(source).GlobalScope.Bindings[name];
        Assert.Equal(count, binding.ReferenceCount);
        Assert.Equal(captured, binding.Captured);
    }

    [Theory]
    [MemberData(nameof(UnresolvedCases))]
    public void Records_unresolved_references_in_source_order(string caseName, string source, string[] names)
    {
        Assert.NotEmpty(caseName);
        Assert.Equal(names, Analyze(source).UnresolvedReferences.Select(binding => binding.Name));
    }

    [Theory]
    [MemberData(nameof(DuplicateCases))]
    public void Rejects_duplicate_lexical_binding(string caseName, string source)
    {
        Assert.NotEmpty(caseName);
        var error = Assert.Throws<JavaScriptCompilationException>(() => Analyze(source));
        Assert.Equal("ECMA1003", error.Code);
        Assert.Equal("scope-analysis.js", error.FileName);
    }

    [Fact]
    public void Repeated_var_declarations_are_allowed()
    {
        var analysis = Analyze("var value; var value;");
        Assert.Equal(JsBindingKind.Var, analysis.GlobalScope.Bindings["value"].Kind);
    }

    [Fact]
    public void Var_and_function_redeclaration_are_allowed()
    {
        var analysis = Analyze("var value; function value() {}");
        Assert.True(analysis.GlobalScope.Bindings.ContainsKey("value"));
    }

    [Fact]
    public void Unresolved_reference_retains_location()
    {
        var binding = Assert.Single(Analyze("\n  missing;").UnresolvedReferences);
        Assert.Equal(2, binding.Line);
        Assert.Equal(3, binding.Column);
    }

    [Fact]
    public void Unresolved_repeated_name_keeps_each_reference()
    {
        var unresolved = Analyze("missing; missing;").UnresolvedReferences;
        Assert.Equal(2, unresolved.Count);
        Assert.All(unresolved, binding => Assert.Equal("missing", binding.Name));
    }

    [Fact]
    public void Global_scope_nearest_function_is_itself()
    {
        var global = Analyze("").GlobalScope;
        Assert.Same(global, global.NearestFunctionScope());
    }

    [Fact]
    public void Import_binding_is_declared_and_export_reference_resolves()
    {
        var analysis = Analyze("import { value as local } from 'source'; export { local };");
        var binding = analysis.GlobalScope.Bindings["local"];
        Assert.Equal(JsBindingKind.Import, binding.Kind);
        Assert.Equal(1, binding.ReferenceCount);
        Assert.Empty(analysis.UnresolvedReferences);
    }

    [Fact]
    public void Catch_destructuring_declares_all_pattern_bindings()
    {
        var analysis = Analyze("try { work(); } catch ({ code, message }) { use(code, message); }");
        Assert.DoesNotContain(analysis.UnresolvedReferences, binding => binding.Name is "code" or "message");
    }

    private static JsScopeAnalysis Analyze(string source)
    {
        var tokens = new JavaScriptScanner(source, "scope-analysis.js").Scan();
        var program = new JavaScriptAstParser(tokens, "scope-analysis.js").ParseProgram();
        return new JavaScriptScopeAnalyzer("scope-analysis.js").Analyze(program);
    }

    private static object[] Binding(string name, string source, string binding, JsBindingKind kind) => [name, source, binding, kind.ToString()];
    private static object[] References(string name, string source, string binding, int count, bool captured) => [name, source, binding, count, captured];
    private static object[] Unresolved(string name, string source, params string[] names) => [name, source, names];
    private static object[] Duplicate(string name, string source) => [name, source];
}
