using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Api;

/// <summary>
/// Transforms the canonical JavaScript AST after parsing and scope analysis,
/// but before IR lowering. Implementations must not alter static import or
/// export topology; module resolution has already observed that information.
/// </summary>
public interface IJavaScriptAstPass
{
    JsAstProgram Run(JsAstProgram program);
}

/// <summary>
/// Optional companion to <see cref="IJavaScriptAstPass"/> for passes that need
/// lexical metadata which the canonical AST deliberately does not retain (for
/// example, tool-owned pragmas in comments).  The compiler forwards this data
/// unchanged; it does not interpret pass-specific directives.
/// </summary>
public interface IJavaScriptAstPassWithContext
{
    JsAstProgram Run(JsAstProgram program, JavaScriptAstPassContext context);
}

public sealed record JavaScriptAstPassContext(string Source, string FileName, JavaScriptSourceKind Kind);
