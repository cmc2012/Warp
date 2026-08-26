using Warp.Diagnostics;

namespace Warp.ComponentSyntax.Ast;

public sealed record UxStyleSheet(
    IReadOnlyList<UxStyleRule> Rules,
    IReadOnlyList<UxMediaRule>? MediaRules = null,
    SourcePosition? Position = null);

/// <summary>A group of rules guarded by a target-runtime media condition.</summary>
public sealed record UxMediaRule(
    string Condition,
    IReadOnlyList<UxStyleRule> Rules,
    SourcePosition? Position = null);

public sealed record UxStyleRule(
    IReadOnlyList<StyleSelector> Selectors,
    IReadOnlyList<StyleDeclaration> Declarations,
    SourcePosition? Position = null);

public sealed record StyleSelector(
    StyleSelectorKind Kind,
    string Name,
    SourcePosition? Position = null);

public enum StyleSelectorKind { Class = 0, Id = 1, Tag = 2 }

public sealed record StyleDeclaration(
    string Property,
    StyleValue Value,
    SourcePosition? Position = null);

public abstract record StyleValue;
public sealed record NumericStyleValue(double Number) : StyleValue;
public sealed record StringStyleValue(string Text) : StyleValue;
public sealed record ColorStyleValue(string Normalized) : StyleValue;
