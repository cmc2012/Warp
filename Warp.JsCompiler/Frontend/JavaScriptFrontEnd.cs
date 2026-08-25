using Warp.JsCompiler.Api;

namespace Warp.JsCompiler.Frontend;

internal sealed class JavaScriptFrontEnd(string source, string fileName, JavaScriptSourceKind kind)
{
    public JavaScriptProgram Parse()
    {
        // This deliberately performs lexical validation before code generation so callers receive
        // deterministic, location-aware errors rather than malformed bytecode.
        var scanner = new JavaScriptScanner(source, fileName);
        var tokens = scanner.Scan();
        JsAstProgram ast;
        ast = new JavaScriptAstParser(tokens, fileName, kind).ParseProgram();
        var scopes = new JavaScriptScopeAnalyzer(fileName).Analyze(ast);
        var imports = kind == JavaScriptSourceKind.Module
            ? ast.Body.SelectMany(statement => statement switch
            {
                JsImportStatement import => new[] { new StaticModuleImport(import.Specifier, import.Line, import.Column) },
                JsExportStatement { Source: { } source } export => new[] { new StaticModuleImport(source, export.Line, export.Column) },
                JsExportAllStatement export => new[] { new StaticModuleImport(export.Source, export.Line, export.Column) },
                _ => Array.Empty<StaticModuleImport>(),
            }).ToArray()
            : [];
        return new JavaScriptProgram(source, fileName, kind, imports, tokens, ast, scopes);
    }
}

internal sealed record JavaScriptProgram(string Source, string FileName, JavaScriptSourceKind Kind,
    IReadOnlyList<StaticModuleImport> StaticImports, IReadOnlyList<JavaScriptToken> Tokens, JsAstProgram Ast, JsScopeAnalysis Scopes);

internal sealed record StaticModuleImport(string Specifier, int Line, int Column);
