using Warp.Diagnostics;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Scripting;

/// <summary>
/// Framework-specific facts projected from <see cref="Warp.JsCompiler.Frontend.JsAstProgram"/>.
/// This is deliberately not a second JavaScript AST: it retains only the page-script
/// facts needed by WXAML analysis and Vela module emission.
/// </summary>
public sealed record ComponentLogic(
    IReadOnlyList<JsImport> Imports,
    IReadOnlyList<ConstDecl> Consts,
    IReadOnlyList<JsFunction> Functions,
    JsExportDefault? ExportDefault,
    IReadOnlyList<JsNamedExport> NamedExports,
    SourcePosition? Position = null);

public sealed record JsImport(
    string? DefaultName,
    IReadOnlyList<(string Imported, string Local)> Named,
    string Specifier,
    SourcePosition? Position = null)
{
    public bool IsSystem => Specifier.StartsWith("@system.", StringComparison.Ordinal);
    public bool IsRelative => Specifier.StartsWith("./") || Specifier.StartsWith("../");
}

public sealed record ConstDecl(
    string Name,
    JsExpression Expression,
    object? Folded,
    bool IsFoldable,
    SourcePosition? Position = null)
{
    public string Raw => JavaScriptAstWriter.Write(Expression);
}

public sealed record JsFunction(
    JsFunctionStatement Node,
    SourcePosition? Position = null)
{
    public string Name => Node.Name;
}

public sealed record JsExportDefault(
    IReadOnlyList<JsProperty> Properties,
    SourcePosition? Position = null);

public sealed record JsProperty(
    JsObjectProperty Node,
    JsPropertyKind Kind,
    SourcePosition? Position = null)
{
    public string Name => Node.Key;
    public string RawValue => JavaScriptAstWriter.Write(Node.Value);
}

public enum JsPropertyKind
{
    Data,
    Protected,
    Private,
    Public,
    Props,
    Lifecycle,
    Method,
    Unknown,
}

public sealed record JsNamedExport(
    string Exported,
    string Local,
    SourcePosition? Position = null);
