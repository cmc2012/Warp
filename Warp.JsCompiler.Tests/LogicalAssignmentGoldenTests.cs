using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class LogicalAssignmentGoldenTests
{
    [Theory]
    [InlineData("function update(value) { return value &&= next(); }", "/tmp/warp-and-assign.js")]
    [InlineData("function update(value) { return value ||= next(); }", "/tmp/warp-or-assign.js")]
    [InlineData("function update(value) { return value ??= next(); }", "/tmp/warp-nullish-assign.js")]
    [InlineData("function update(target) { return target.value ||= next(); }", "/tmp/warp-field-assign.js")]
    [InlineData("function update(target, key) { return target()[key()] &&= next(); }", "/tmp/warp-element-and-assign.js")]
    [InlineData("function update(target, key) { return target()[key()] ??= next(); }", "/tmp/warp-element-nullish-assign.js")]
    [InlineData("function update(value) { return value ||= () => 1; }", "/tmp/warp-logical-arrow-name.js")]
    [InlineData("function update(value) { return value &&= function() { return 1; }; }", "/tmp/warp-logical-function-name.js")]
    public void Matches_reference_logical_assignment(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);
}
