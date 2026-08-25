using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class ScriptGoldenTests
{
    [Theory]
    [InlineData("", "/tmp/warp-empty-script.js")]
    [InlineData("var value = 1;", "/tmp/warp-variable-script.js")]
    [InlineData("function read(value) { return value; } read(1);", "/tmp/warp-function-script.js")]
    public void Matches_reference_script(string source, string fileName)
        => GoldenAssert.ReferenceScript(source, fileName);
}
