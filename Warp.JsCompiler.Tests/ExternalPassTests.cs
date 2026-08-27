using Warp.JsCompiler.Api;
using Warp.JsCompiler.TestPass;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ExternalPassTests
{
    [Fact]
    public void External_dll_ir_and_assembly_passes_are_loaded()
    {
        var options = new JavaScriptCompilerOptions();
        options.ExternalPassAssemblyPaths.Add(typeof(AppendMarkerPass).Assembly.Location);

        var bytecode = new JavaScriptCompiler(options).Compile(new(
            "export const value = 1;", "entry.mjs", JavaScriptSourceKind.Module));
        var baseline = new JavaScriptCompiler().Compile(new(
            "export const value = 1;", "entry.mjs", JavaScriptSourceKind.Module));

        Assert.NotEqual(baseline.Bytes, bytecode.Bytes);
    }

    [Fact]
    public void Missing_external_pass_assembly_is_reported()
    {
        var options = new JavaScriptCompilerOptions();
        options.ExternalPassAssemblyPaths.Add("not-a-pass.dll");

        Assert.Throws<FileNotFoundException>(() => new JavaScriptCompiler(options));
    }
}
