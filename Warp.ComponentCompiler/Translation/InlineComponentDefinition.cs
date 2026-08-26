using Warp.ComponentSyntax.Ast;

namespace Warp.ComponentCompiler.Translation;

/// <summary>A stateless component explicitly requested for compile-time expansion.</summary>
public sealed record InlineComponentDefinition(UxDocument Document, string SourcePath);
