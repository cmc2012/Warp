using Warp.JsCompiler.Api;
using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Contract tests for the public compiler, module graph, and directory APIs.</summary>
public sealed class CompilerApiBoundaryTests
{
    [Fact]
    public void Compile_rejects_null_request()
        => Assert.Throws<ArgumentNullException>(() => new JavaScriptCompiler().Compile(null!));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void Compile_rejects_blank_file_name(string fileName)
        => Assert.Throws<ArgumentException>(() => Compile("", fileName));

    [Fact]
    public void Compile_rejects_null_source()
        => Assert.Throws<ArgumentNullException>(() => Compile(null!, "input.js"));

    [Theory]
    [InlineData("entry.mjs")]
    [InlineData("ENTRY.MJS")]
    [InlineData("path/to/entry.MjS")]
    public void Auto_detects_module_from_mjs_extension(string fileName)
        => Assert.Equal(JavaScriptSourceKind.Module, Compile("export const value = 1;", fileName).Kind);

    [Theory]
    [InlineData("export const value = 1;")]
    public void Auto_detects_module_syntax(string source)
        => Assert.Equal(JavaScriptSourceKind.Module, Compile(source, "entry.js").Kind);

    [Fact]
    public void Auto_uses_script_grammar_for_import_meta()
        => Assert.Throws<JavaScriptCompilationException>(() => Compile("import.meta.url;", "entry.js"));

    [Theory]
    [InlineData("")]
    [InlineData("const value = 1;")]
    [InlineData("import('dynamic');")]
    public void Auto_defaults_to_script_without_static_module_syntax(string source)
        => Assert.Equal(JavaScriptSourceKind.Script, Compile(source, "entry.js").Kind);

    [Fact]
    public void Explicit_script_overrides_mjs_extension()
        => Assert.Equal(JavaScriptSourceKind.Script, Compile("const value = 1;", "entry.mjs", JavaScriptSourceKind.Script).Kind);

    [Fact]
    public void Explicit_module_overrides_js_extension()
        => Assert.Equal(JavaScriptSourceKind.Module, Compile("const value = 1;", "entry.js", JavaScriptSourceKind.Module).Kind);

    [Fact]
    public void Bytecode_reports_original_file_name()
        => Assert.Equal("virtual/nested/input.js", Compile("const value = 1;", "virtual/nested/input.js").FileName);

    [Fact]
    public void Repeated_compilation_is_deterministic()
    {
        var first = Compile("function read(value) { return value + 1; }", "same.js").Bytes;
        var second = Compile("function read(value) { return value + 1; }", "same.js").Bytes;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compiles_loops_with_short_circuit_test()
    {
        var bytes = Compile("function read(items) { while (items.length > 0 && items.length < 3) work(items.pop()); for (let index = 0; index < items.length && index < 3; index++) work(items[index]); }", "loop.js").Bytes;
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Compiles_top_level_lexical_compound_updates()
    {
        var bytes = Compile("let total = 0; total += 1; total -= 2; globalThis.result = total;", "globals.js").Bytes;
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Compiles_discarded_postfix_loop_update()
    {
        var bytes = Compile("let total = 0; for (let index = 0; index < 5; index++) total += index; globalThis.result = total;", "postfix.js").Bytes;
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void File_name_participates_in_unstripped_serialized_bytecode()
    {
        var first = Compile("const value = 1;", "first.js", stripDebugInfo: false).Bytes;
        var second = Compile("const value = 1;", "second.js", stripDebugInfo: false).Bytes;
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Strip_debug_info_changes_debug_bearing_output()
    {
        var stripped = Compile("function read(value) { return value; }", "debug.js", stripDebugInfo: true).Bytes;
        var debug = Compile("function read(value) { return value; }", "debug.js", stripDebugInfo: false).Bytes;
        Assert.NotEqual(stripped, debug);
    }

    [Fact]
    public void Bytecode_collection_is_read_only()
    {
        var bytes = Compile("", "empty.js").Bytes;
        var list = (IList<byte>)bytes;
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list[0] = 0);
    }

    [Theory]
    [InlineData("import 'dependency';", 1, 1)]
    [InlineData("\n\n  import value from 'dependency';", 3, 3)]
    [InlineData("export * from 'dependency';", 1, 1)]
    public void Compile_reports_first_static_import_location(string source, int line, int column)
    {
        var error = Assert.Throws<JavaScriptCompilationException>(() => Compile(source, "entry.js", JavaScriptSourceKind.Module));
        Assert.Equal("ECMA2001", error.Code);
        Assert.Equal("entry.js", error.FileName);
        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
    }

    [Fact]
    public void Compile_allows_dynamic_import_without_resolver()
        => Assert.NotEmpty(Compile("import(name);", "entry.js", JavaScriptSourceKind.Module).Bytes);

    [Fact]
    public void Module_graph_rejects_null_entry()
        => Assert.Throws<ArgumentNullException>(() => new JavaScriptCompiler().CompileModuleGraph(null!, new MapResolver()));

    [Fact]
    public void Module_graph_rejects_null_resolver()
        => Assert.Throws<ArgumentNullException>(() => new JavaScriptCompiler().CompileModuleGraph(new("", "entry.js"), null!));

    [Fact]
    public void Module_graph_contains_entry_without_dependencies()
    {
        var graph = Graph("export const value = 1;", new MapResolver());
        Assert.Equal(["entry.js"], graph.Modules.Keys);
    }

    [Fact]
    public void Module_graph_compiles_dependency_before_entry()
    {
        var resolver = new MapResolver(("dependency", new("dependency.js", "export const value = 1;")));
        var graph = Graph("import { value } from 'dependency'; export { value };", resolver);
        Assert.Equal(["dependency.js", "entry.js"], graph.Modules.Keys);
    }

    [Fact]
    public void Module_graph_deduplicates_shared_dependency()
    {
        var resolver = new MapResolver(
            ("left", new("left.js", "import 'shared';")),
            ("right", new("right.js", "import 'shared';")),
            ("shared", new("shared.js", "export const value = 1;")));
        var graph = Graph("import 'left'; import 'right';", resolver);
        Assert.Equal(4, graph.Modules.Count);
        Assert.Equal(1, resolver.Requests.Count(request => request.Specifier == "shared"));
    }

    [Fact]
    public void Module_graph_allows_dependency_cycle()
    {
        var resolver = new MapResolver(
            ("left", new("left.js", "import 'right';")),
            ("right", new("right.js", "import 'left';")));
        var graph = new JavaScriptCompiler().CompileModuleGraph(
            new("import 'right';", "left.js", JavaScriptSourceKind.Module), resolver);
        Assert.Equal(2, graph.Modules.Count);
    }

    [Fact]
    public void Module_graph_skips_external_dependency_output()
    {
        var resolver = new MapResolver(("external", new("external", "", IsExternal: true)));
        var graph = Graph("import 'external';", resolver);
        Assert.Equal(["entry.js"], graph.Modules.Keys);
    }

    [Fact]
    public void Module_graph_passes_specifier_and_referrer_to_resolver()
    {
        var resolver = new MapResolver(("./dependency.js", new("dependency.js", "")));
        _ = Graph("import './dependency.js';", resolver);
        Assert.Equal(("./dependency.js", "entry.js"), Assert.Single(resolver.Requests));
    }

    [Fact]
    public void Module_graph_rejects_blank_canonical_name()
    {
        var resolver = new MapResolver(("dependency", new(" ", "")));
        var error = Assert.Throws<JavaScriptCompilationException>(() => Graph("import 'dependency';", resolver));
        Assert.Equal("ECMA2003", error.Code);
    }

    [Fact]
    public void Module_graph_wraps_resolver_exception()
    {
        var inner = new InvalidOperationException("resolver failed");
        var error = Assert.Throws<JavaScriptCompilationException>(() => Graph("import 'dependency';", new ThrowingResolver(inner)));
        Assert.Equal("ECMA2002", error.Code);
        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public void Module_graph_preserves_compile_exception_from_resolver()
    {
        var expected = new JavaScriptCompilationException("resolver diagnostic", "resolver", 2, 3, "CUSTOM");
        var actual = Assert.Throws<JavaScriptCompilationException>(() => Graph("import 'dependency';", new ThrowingResolver(expected)));
        Assert.Same(expected, actual);
    }

    [Fact]
    public void Module_graph_propagates_strip_debug_info_to_dependencies()
    {
        var resolver = new MapResolver(("dependency", new("dependency.js", "export function read(value) { return value; }")));
        var compiler = new JavaScriptCompiler();
        var graph = compiler.CompileModuleGraph(new("import 'dependency';", "entry.js", JavaScriptSourceKind.Module)
        {
            StripDebugInfo = false,
        }, resolver);
        var standalone = compiler.CompileModuleGraph(new("export function read(value) { return value; }", "dependency.js", JavaScriptSourceKind.Module)
        {
            StripDebugInfo = false,
        }, new MapResolver());
        Assert.Equal(standalone.Modules["dependency.js"].Bytes, graph.Modules["dependency.js"].Bytes);
    }

    [Fact]
    public void Module_graph_minifies_internal_exports_but_keeps_entry_exports_stable()
    {
        var resolver = new MapResolver(("dependency", new("dependency.js", "export const value = 2;")));
        var compiler = new JavaScriptCompiler();
        var baseline = compiler.CompileModuleGraph(new("import { value } from 'dependency'; export const result = value;", "entry.js", JavaScriptSourceKind.Module), resolver);
        var minified = compiler.CompileModuleGraph(new("import { value } from 'dependency'; export const result = value;", "entry.js", JavaScriptSourceKind.Module)
        {
            MinifyLocalBindings = true,
        }, new MapResolver(("dependency", new("dependency.js", "export const value = 2;"))));

        Assert.NotEqual(baseline.Modules["dependency.js"].Bytes, minified.Modules["dependency.js"].Bytes);
        Assert.NotEmpty(minified.Modules["entry.js"].Bytes);
        Assert.NotEmpty(minified.Modules["dependency.js"].Bytes);
    }

    [Fact]
    public void Module_graph_minifies_through_internal_export_star_chain()
    {
        static MapResolver Resolver() => new(
            ("relay", new("relay.js", "export * from 'leaf';")),
            ("leaf", new("leaf.js", "export const value = 2;")));
        const string entry = "import { value } from 'relay'; export const result = value;";
        var compiler = new JavaScriptCompiler();
        var baseline = compiler.CompileModuleGraph(new(entry, "entry.js", JavaScriptSourceKind.Module), Resolver());
        var minified = compiler.CompileModuleGraph(new(entry, "entry.js", JavaScriptSourceKind.Module) { MinifyLocalBindings = true }, Resolver());

        Assert.NotEqual(baseline.Modules["leaf.js"].Bytes, minified.Modules["leaf.js"].Bytes);
        Assert.NotEmpty(minified.Modules["relay.js"].Bytes);
    }

    [Fact]
    public void Module_graph_warns_when_export_star_or_namespace_import_blocks_export_name_minification()
    {
        var warningsFromSink = new List<JavaScriptCompilerWarning>();
        var compiler = new JavaScriptCompiler(new JavaScriptCompilerOptions { WarningSink = warningsFromSink.Add });
        var graph = compiler.CompileModuleGraph(new("import * as api from 'relay'; export { api };", "entry.js", JavaScriptSourceKind.Module),
            new MapResolver(("relay", new("relay.js", "export * from 'leaf';")), ("leaf", new("leaf.js", "export const value = 2;"))));

        Assert.Equal(["WARP3001", "WARP3002"], graph.Warnings.Select(warning => warning.Code).OrderBy(code => code));
        Assert.Equal(graph.Warnings, warningsFromSink);
        Assert.Contains(graph.Warnings, warning => warning.Message.Contains("explicit named imports"));
        Assert.Contains(graph.Warnings, warning => warning.Message.Contains("explicit named re-exports"));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(188, 190)]
    public void Abi_translation_preserves_or_shifts_expected_atom_ids(int upstream, int expected)
        => Assert.Equal(expected, BytecodeAbiProbe.TranslatePredefinedAtom(upstream));

    [Fact]
    public void Abi_module_name_adds_target_prefix_once()
    {
        var converted = BytecodeAbiProbe.ToModuleName("module.js");
        Assert.Equal(converted, BytecodeAbiProbe.ToModuleName(converted));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Abi_module_name_rejects_blank_input(string value)
        => Assert.Throws<ArgumentException>(() => BytecodeAbiProbe.ToModuleName(value));

    private static JavaScriptBytecode Compile(string source, string fileName,
        JavaScriptSourceKind kind = JavaScriptSourceKind.Auto, bool stripDebugInfo = true)
        => new JavaScriptCompiler().Compile(new(source, fileName, kind) { StripDebugInfo = stripDebugInfo });

    private static JavaScriptModuleGraph Graph(string source, IJavaScriptModuleResolver resolver)
        => new JavaScriptCompiler().CompileModuleGraph(new(source, "entry.js", JavaScriptSourceKind.Module), resolver);

    private sealed class MapResolver(params (string Specifier, JavaScriptModuleSource Source)[] modules) : IJavaScriptModuleResolver
    {
        private readonly Dictionary<string, JavaScriptModuleSource> _modules = modules.ToDictionary(item => item.Specifier, item => item.Source);
        public List<(string Specifier, string Referrer)> Requests { get; } = [];

        public JavaScriptModuleSource Resolve(string specifier, string referrer)
        {
            Requests.Add((specifier, referrer));
            return _modules[specifier];
        }
    }

    private sealed class ThrowingResolver(Exception exception) : IJavaScriptModuleResolver
    {
        public JavaScriptModuleSource Resolve(string specifier, string referrer) => throw exception;
    }
}
