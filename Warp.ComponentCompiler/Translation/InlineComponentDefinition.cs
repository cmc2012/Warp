using Warp.ComponentCompiler.Scripting;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Translation;

/// <summary>A component explicitly requested for compile-time expansion.</summary>
public sealed record InlineComponentDefinition(
    UxDocument Document,
    string SourcePath,
    ComponentLogic Logic,
    IReadOnlyDictionary<string, InlineComponentDefinition> InlineComponents,
    IReadOnlyDictionary<string, string>? MethodNames = null)
{
    public InlineComponentDefinition(UxDocument document, string sourcePath)
        : this(document, sourcePath, new ComponentLogic([], [], [], null, []), new Dictionary<string, InlineComponentDefinition>(StringComparer.OrdinalIgnoreCase)) { }

    public IReadOnlyDictionary<string, string> MethodNames { get; init; }
        = MethodNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The host-side specialization of one inline invocation.  An inline source
/// may be used more than once, so names and prop expressions deliberately
/// belong to the invocation rather than to <see cref="InlineComponentDefinition"/>.
/// </summary>
public sealed record InlineInvocationPlan(
    IReadOnlyDictionary<string, string> MethodNames,
    IReadOnlyDictionary<string, JsExpression> PropExpressions);

/// <summary>Output of inline script projection, including call-site plans used by template lowering.</summary>
public sealed record InlineMergeResult(
    ComponentLogic Logic,
    IReadOnlyDictionary<string, InlineComponentDefinition> Components,
    IReadOnlyDictionary<SourcePosition, InlineInvocationPlan> InvocationPlans)
{
    // Keep the original two-value consumption pattern source-compatible for
    // callers that do not need call-site plans.
    public void Deconstruct(out ComponentLogic logic, out IReadOnlyDictionary<string, InlineComponentDefinition> components)
    {
        logic = Logic;
        components = Components;
    }
}
