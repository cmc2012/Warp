using Warp.JsCompiler.Api;
using Warp.Testing;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Filesystem compilation behavior and value contracts of public diagnostic types.</summary>
public sealed class DirectoryAndDiagnosticBoundaryTests
{
    [Fact]
    public async Task Directory_compile_rejects_null_request()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => new JavaScriptDirectoryCompiler().CompileAsync(
            null!, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Directory_compile_rejects_missing_source_directory()
    {
        using var tree = new TempTree();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => CompileDirectory(tree.Path("missing"), tree.Path("output")));
    }

    [Fact]
    public async Task Directory_compile_empty_directory_returns_empty_result()
    {
        using var tree = new TempTree();
        var paths = await CompileDirectory(tree.CreateDirectory("source"), tree.Path("output"));
        Assert.Empty(paths);
        Assert.False(Directory.Exists(tree.Path("output")));
    }

    [Theory]
    [InlineData("entry.js")]
    [InlineData("entry.mjs")]
    [InlineData("ENTRY.JS")]
    [InlineData("ENTRY.MJS")]
    public async Task Directory_compile_accepts_default_extensions_case_insensitively(string fileName)
    {
        using var tree = new TempTree();
        tree.Write("source/" + fileName, "export const value = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"));
        var output = Assert.Single(paths);
        Assert.Equal(Path.ChangeExtension(tree.Path("output/" + fileName), ".jsc"), output);
        Assert.NotEmpty(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("entry.json")]
    [InlineData("entry.jsx")]
    [InlineData("entry.js.map")]
    public async Task Directory_compile_ignores_non_default_extensions(string fileName)
    {
        using var tree = new TempTree();
        tree.Write("source/" + fileName, "const value = 1;");
        Assert.Empty(await CompileDirectory(tree.Path("source"), tree.Path("output")));
    }

    [Fact]
    public async Task Directory_compile_preserves_nested_relative_tree()
    {
        using var tree = new TempTree();
        tree.Write("source/features/nested/entry.js", "export const value = 1;");
        var output = Assert.Single(await CompileDirectory(tree.Path("source"), tree.Path("output")));
        Assert.Equal(tree.Path("output/features/nested/entry.jsc"), output);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task Directory_compile_top_directory_only_skips_nested_files()
    {
        using var tree = new TempTree();
        tree.Write("source/root.js", "const root = 1;");
        tree.Write("source/nested/child.js", "const child = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            SearchOption = SearchOption.TopDirectoryOnly,
        });
        Assert.Equal([tree.Path("output/root.jsc")], paths);
    }

    [Fact]
    public async Task Directory_compile_all_directories_includes_nested_files()
    {
        using var tree = new TempTree();
        tree.Write("source/root.js", "const root = 1;");
        tree.Write("source/nested/child.js", "const child = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"));
        Assert.Equal(2, paths.Count);
        Assert.Contains(tree.Path("output/root.jsc"), paths);
        Assert.Contains(tree.Path("output/nested/child.jsc"), paths);
    }

    [Fact]
    public async Task Directory_compile_honors_custom_extension()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.custom", "const value = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            Extensions = [".custom"],
        });
        Assert.Equal([tree.Path("output/entry.jsc")], paths);
    }

    [Fact]
    public async Task Directory_compile_custom_extensions_replace_defaults()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "const value = 1;");
        tree.Write("source/entry.custom", "const value = 2;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            Extensions = [".custom"],
        });
        Assert.Equal([tree.Path("output/entry.jsc")], paths);
    }

    [Fact]
    public async Task Directory_compile_custom_extensions_are_case_insensitive()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.CUSTOM", "const value = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            Extensions = [".custom"],
        });
        Assert.Single(paths);
    }

    [Fact]
    public async Task Directory_compile_empty_extension_set_compiles_nothing()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "const value = 1;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            Extensions = [],
        });
        Assert.Empty(paths);
    }

    [Fact]
    public async Task Directory_compile_script_mode_accepts_plain_script()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "var value = this;");
        var paths = await CompileDirectory(tree.Path("source"), tree.Path("output"), request => request with
        {
            CompileAsModules = false,
        });
        Assert.Single(paths);
    }

    [Fact]
    public async Task Directory_compile_module_mode_reports_static_import_requirement()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "import 'dependency';");
        var error = await Assert.ThrowsAsync<JavaScriptCompilationException>(
            () => CompileDirectory(tree.Path("source"), tree.Path("output")));
        Assert.Equal("ECMA2001", error.Code);
    }

    [Fact]
    public async Task Directory_compile_overwrites_existing_output()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "const value = 1;");
        tree.WriteBytes("output/entry.jsc", [1, 2, 3]);
        var output = Assert.Single(await CompileDirectory(tree.Path("source"), tree.Path("output")));
        Assert.NotEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Directory_compile_replaces_source_extension_with_jsc()
    {
        using var tree = new TempTree();
        tree.Write("source/archive.test.js", "const value = 1;");
        var output = Assert.Single(await CompileDirectory(tree.Path("source"), tree.Path("output")));
        Assert.EndsWith("archive.test.jsc", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Directory_compile_returns_absolute_output_paths()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "const value = 1;");
        var output = Assert.Single(await CompileDirectory(tree.Path("source"), tree.Path("output")));
        Assert.True(Path.IsPathFullyQualified(output));
    }

    [Fact]
    public async Task Directory_compile_observes_pre_cancelled_token()
    {
        using var tree = new TempTree();
        tree.Write("source/entry.js", "const value = 1;");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new JavaScriptDirectoryCompiler().CompileAsync(
            new(tree.Path("source"), tree.Path("output")), cancellation.Token));
    }

    [Fact]
    public void Directory_request_defaults_are_stable()
    {
        var request = new JavaScriptDirectoryCompilationRequest("source", "output");
        Assert.Equal(SearchOption.AllDirectories, request.SearchOption);
        Assert.Equal([".js", ".mjs"], request.Extensions);
        Assert.True(request.CompileAsModules);
    }

    [Fact]
    public void Compile_request_defaults_are_stable()
    {
        var request = new JavaScriptCompilationRequest("", "entry.js");
        Assert.Equal(JavaScriptSourceKind.Auto, request.Kind);
        Assert.True(request.StripDebugInfo);
    }

    [Fact]
    public void Compile_request_has_value_equality()
        => Assert.Equal(new JavaScriptCompilationRequest("source", "entry.js"), new JavaScriptCompilationRequest("source", "entry.js"));

    [Fact]
    public void Compile_request_strip_debug_info_participates_in_equality()
        => Assert.NotEqual(new JavaScriptCompilationRequest("source", "entry.js"), new JavaScriptCompilationRequest("source", "entry.js") { StripDebugInfo = false });

    [Fact]
    public void Module_source_has_value_equality()
        => Assert.Equal(new JavaScriptModuleSource("module.js", "source"), new JavaScriptModuleSource("module.js", "source"));

    [Fact]
    public void Module_source_external_flag_participates_in_equality()
        => Assert.NotEqual(new JavaScriptModuleSource("module.js", "source"), new JavaScriptModuleSource("module.js", "source", true));

    [Theory]
    [InlineData("message", "input.js", 1, 1, "ECMA1000")]
    [InlineData("problem", "nested/input.js", 12, 34, "CUSTOM42")]
    [InlineData("", "", 0, 0, "")]
    public void Compile_exception_preserves_diagnostic_fields(string message, string fileName, int line, int column, string code)
    {
        var error = new JavaScriptCompilationException(message, fileName, line, column, code);
        Assert.Equal(message, error.Message);
        Assert.Equal(fileName, error.FileName);
        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Compile_exception_preserves_inner_exception()
    {
        var inner = new InvalidOperationException("inner");
        var error = new JavaScriptCompilationException("outer", "input.js", 2, 3, innerException: inner);
        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public void Compile_exception_default_code_is_ecma1000()
        => Assert.Equal("ECMA1000", new JavaScriptCompilationException("message", "input.js", 1, 1).Code);

    private static Task<IReadOnlyList<string>> CompileDirectory(string source, string output,
        Func<JavaScriptDirectoryCompilationRequest, JavaScriptDirectoryCompilationRequest>? configure = null)
    {
        var request = new JavaScriptDirectoryCompilationRequest(source, output);
        return new JavaScriptDirectoryCompiler().CompileAsync(
            configure?.Invoke(request) ?? request, TestContext.Current.CancellationToken);
    }

    private sealed class TempTree : IDisposable
    {
        private readonly string _root = TestWorkspace.CreateDirectory("warp-directory-tests");

        public string Path(string relative) => System.IO.Path.Combine(_root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string CreateDirectory(string relative)
        {
            var path = Path(relative);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Write(string relative, string contents)
        {
            var path = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void WriteBytes(string relative, byte[] contents)
        {
            var path = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
