using Warp.Diagnostics;

namespace Warp.ComponentSyntax.Ast;

public sealed record UxDocument(
    UxPage? Page,
    UxComponent? Component,
    SourcePosition FilePosition)
{
    public IReadOnlyList<UxNode> Children => Page?.Children ?? Component?.Children ?? [];
    public IReadOnlyList<UxImportRef> Imports => Page?.Imports ?? Component?.Imports ?? [];
    public UxStyleSheet? Styles => Page?.Styles ?? Component?.Styles;
}

public sealed record UxPage(
    string? ClassName,
    IReadOnlyList<UxImportRef> Imports,
    UxStyleSheet? Styles,
    IReadOnlyList<UxNode> Children);

public sealed record UxComponent(
    string ClassName,
    IReadOnlyList<UxImportRef> Imports,
    UxStyleSheet? Styles,
    IReadOnlyList<UxNode> Children);

public sealed record UxImportRef(
    string Name,
    string Src,
    bool IsInline = false,
    SourcePosition? Position = null);

public abstract record UxNode(SourcePosition? Position);

public sealed record UxElement(
    string Tag,
    bool IsComponent,
    IReadOnlyList<UxAttr> Attrs,
    IReadOnlyList<UxNode> Children,
    SourcePosition? Position = null,
    bool IsStatic = false,
    bool IsConst = false) : UxNode(Position);

/// <summary>Text child preserved by the markup grammar and lowered as a span node.</summary>
public sealed record UxTextNode(
    AttrValue Value,
    SourcePosition? Position = null) : UxNode(Position);

public sealed record UxListNode(
    AttrValue ItemsSource,
    string? Key,
    UxNode ItemTemplateRoot,
    SourcePosition? Position = null) : UxNode(Position);

public sealed record UxIfChain(
    IReadOnlyList<UxIfBranch> Branches,
    SourcePosition? Position = null) : UxNode(Position);

public sealed record UxIfBranch(
    IfBranchKind Kind,
    AttrValue? Test,
    IReadOnlyList<UxNode> Children,
    SourcePosition? Position = null,
    IReadOnlyList<string>? Modifiers = null);

public enum IfBranchKind { If, ElseIf, Else }

public sealed record UxAttr(
    AttrKind Kind,
    string Name,
    AttrValue Value,
    SourcePosition? Position = null,
    IReadOnlyList<string>? Modifiers = null);

public enum AttrKind
{
    Plain,
    Event,
    Class,
    Style,
    Source,
    Text,
    Value,
    ItemsSource,
    Key,
    Test,
    Dataset,
    Model,
}

public abstract record AttrValue(SourcePosition? Position);

public sealed record LiteralValue(string Text, SourcePosition? Position = null) : AttrValue(Position);

public sealed record BindingValue(string Path, bool ItemScope, SourcePosition? Position = null) : AttrValue(Position);

public sealed record ExprValue(string Expr, bool ItemScope, SourcePosition? Position = null) : AttrValue(Position);
