using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ImportExpressionGoldenTests
{
    [Theory]
    [InlineData("import(name);", "/tmp/warp-dynamic-import-script.js")]
    public void Matches_reference_script_import_expression(string source, string fileName)
        => GoldenAssert.ReferenceScript(source, fileName);

    [Theory]
    [InlineData("import(name);", "/tmp/warp-dynamic-import-module.js")]
    [InlineData("var url = import.meta.url;", "/tmp/warp-import-meta.js")]
    public void Matches_reference_module_import_expression(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
