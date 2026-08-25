using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class StaticModuleGoldenTests
{
    [Theory]
    [InlineData("export const value = 1;", "/tmp/warp-export-const.js")]
    [InlineData("const value = 1; export { value };", "/tmp/warp-export-list.js")]
    [InlineData("export function read(value) { return value; }", "/tmp/warp-export-function.js")]
    [InlineData("export default 1;", "/tmp/warp-export-default-number.js")]
    [InlineData("const value = 1; export default value;", "/tmp/warp-export-default-identifier.js")]
    [InlineData("export default function () {}", "/tmp/warp-export-default-anonymous-function.js")]
    [InlineData("export default function named() {}", "/tmp/warp-export-default-named-function.js")]
    public void Matches_reference_local_exports(string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    [Theory]
    [InlineData("import './dependency.js';", "/tmp/warp-bare-import.js")]
    [InlineData("import value from './dependency.js'; use(value);", "/tmp/warp-default-import.js")]
    [InlineData("import { value as local } from './dependency.js'; use(local);", "/tmp/warp-named-import.js")]
    [InlineData("import * as namespace from './dependency.js'; use(namespace);", "/tmp/warp-namespace-import.js")]
    [InlineData("export { value as publicValue } from './dependency.js';", "/tmp/warp-indirect-export.js")]
    [InlineData("export * from './dependency.js';", "/tmp/warp-star-export.js")]
    [InlineData("export * as dependency from './dependency.js';", "/tmp/warp-namespace-export.js")]
    public void Matches_reference_external_module_tables(string source, string fileName)
        => GoldenAssert.ReferenceModuleWithExternalImports(source, fileName);
}
