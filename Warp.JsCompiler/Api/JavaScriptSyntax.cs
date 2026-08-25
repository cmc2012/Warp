using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Api;

/// <summary>
/// Parses JavaScript into the compiler's canonical syntax tree without
/// producing ECMAScript bytecode. Consumers that need JavaScript semantics
/// should use this entry point instead of maintaining another parser.
/// </summary>
public static class JavaScriptSyntax
{
    public static JsAstProgram ParseModule(string source, string fileName)
        => Parse(source, fileName, JavaScriptSourceKind.Module);

    public static JsAstProgram ParseScript(string source, string fileName)
        => Parse(source, fileName, JavaScriptSourceKind.Script);

    private static JsAstProgram Parse(string source, string fileName, JavaScriptSourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A source file name is required.", nameof(fileName));

        var program = new JavaScriptFrontEnd(source, fileName, kind).Parse();
        return program.Ast;
    }
}
