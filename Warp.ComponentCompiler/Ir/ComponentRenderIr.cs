using Warp.ComponentCompiler.Translation;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;

namespace Warp.ComponentCompiler.Ir;

/// <summary>
/// The component render IR sits after WXAML parsing but before JavaScript AST
/// emission. It preserves bindings and control-flow logic while making the
/// final runtime shape (tags, structural roots, and inline expansion points)
/// explicit.
/// </summary>
public sealed record ComponentRenderProgram(IReadOnlyList<ComponentRenderNode> Nodes);
public abstract record ComponentRenderNode;
public sealed record ComponentRenderElement(string SourceTag, string RuntimeTag, bool IsComponent, IReadOnlyList<UxAttr> Attrs, IReadOnlyList<ComponentRenderNode> Children, bool IsStatic, bool IsConst, IReadOnlyList<string> GeneratedClasses) : ComponentRenderNode;
public sealed record ComponentRenderText(AttrValue Value, IReadOnlyList<string> GeneratedClasses) : ComponentRenderNode;
public sealed record ComponentRenderList(AttrValue ItemsSource, string? Key, ComponentRenderNode ItemTemplateRoot, IReadOnlyList<string> GeneratedClasses) : ComponentRenderNode;
public sealed record ComponentRenderIf(IReadOnlyList<ComponentRenderIfBranch> Branches) : ComponentRenderNode;
public sealed record ComponentRenderIfBranch(IfBranchKind Kind, AttrValue? Test, IReadOnlyList<string>? Modifiers, IReadOnlyList<ComponentRenderNode> Children);
public sealed record ComponentRenderInline(UxElement Invocation, InlineComponentDefinition Definition, InlineInvocationPlan? Plan, IReadOnlyList<string> GeneratedClasses) : ComponentRenderNode;

public sealed class ComponentRenderIrLowerer
{
    private readonly IReadOnlyDictionary<string, InlineComponentDefinition> _inlineComponents;
    private readonly StyleSelectorTransform? _styleSelectors;
    private readonly IReadOnlyDictionary<SourcePosition, InlineInvocationPlan> _inlineInvocationPlans;

    public ComponentRenderIrLowerer(IReadOnlyDictionary<string, InlineComponentDefinition> inlineComponents, StyleSelectorTransform? styleSelectors, IReadOnlyDictionary<SourcePosition, InlineInvocationPlan>? inlineInvocationPlans = null)
    {
        _inlineComponents = inlineComponents;
        _styleSelectors = styleSelectors;
        _inlineInvocationPlans = inlineInvocationPlans ?? new Dictionary<SourcePosition, InlineInvocationPlan>();
    }

    public ComponentRenderProgram Lower(IReadOnlyList<UxNode> nodes, IReadOnlyList<string>? generatedClasses = null)
        => new(LowerMany(nodes, generatedClasses ?? []));

    private IReadOnlyList<ComponentRenderNode> LowerMany(IReadOnlyList<UxNode> nodes, IReadOnlyList<string> generatedClasses)
        => nodes.SelectMany(node => LowerNode(node, generatedClasses)).ToArray();

    private IReadOnlyList<ComponentRenderNode> LowerNode(UxNode node, IReadOnlyList<string> generatedClasses) => node switch
    {
        UxElement element => [LowerElement(element, generatedClasses)],
        UxTextNode text => [new ComponentRenderText(text.Value, generatedClasses)],
        UxListNode list => [new ComponentRenderList(list.ItemsSource, list.Key, LowerNode(list.ItemTemplateRoot, WithGeneratedClass(WithGeneratedClass(generatedClasses, GeneratedClass("list")), GeneratedClass("itemtemplate"))).Single(), WithGeneratedClass(WithGeneratedClass(generatedClasses, GeneratedClass("list")), GeneratedClass("itemtemplate")))],
        UxIfChain chain => [new ComponentRenderIf(chain.Branches.Select(branch => new ComponentRenderIfBranch(branch.Kind, branch.Test, branch.Modifiers, LowerMany(branch.Children, WithGeneratedClass(generatedClasses, GeneratedClass(BranchTag(branch.Kind)))))).ToArray())],
        _ => []
    };

    private ComponentRenderNode LowerElement(UxElement element, IReadOnlyList<string> generatedClasses)
    {
        var classes = WithGeneratedClass(generatedClasses, GeneratedClass(element.Tag));
        if (element.IsComponent && _inlineComponents.TryGetValue(element.Tag, out var inline))
            return new ComponentRenderInline(element, inline, element.Position is { } position && _inlineInvocationPlans.TryGetValue(position, out var plan) ? plan : null, WithGeneratedClass(classes, GeneratedClass("component")));
        var runtimeTag = element.IsComponent ? element.Tag : element.Tag.ToLowerInvariant();
        return new ComponentRenderElement(element.Tag, runtimeTag, element.IsComponent, element.Attrs, LowerMany(element.Children, []), element.IsStatic, element.IsConst, classes);
    }

    private string? GeneratedClass(string tag) => _styleSelectors?.GeneratedClassFor(tag);
    private static string BranchTag(IfBranchKind kind) => kind switch { IfBranchKind.If => "if", IfBranchKind.ElseIf => "elseif", _ => "else" };
    private static IReadOnlyList<string> WithGeneratedClass(IReadOnlyList<string> classes, string? generatedClass)
        => generatedClass is null || classes.Contains(generatedClass, StringComparer.Ordinal) ? classes : classes.Append(generatedClass).ToArray();
}
