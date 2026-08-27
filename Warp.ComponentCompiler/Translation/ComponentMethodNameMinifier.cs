using Warp.ComponentCompiler.Scripting;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Translation;

/// <summary>Renames non-contract component methods and every statically known self-call.</summary>
public static class ComponentMethodNameMinifier
{
    // Keep this defensive list alongside ComponentScriptParser's lifecycle
    // classification. A framework callback is name-addressed by Vela and
    // cannot be shortened even if it reaches this pass with stale metadata.
    private static readonly HashSet<string> RuntimeCallbackNames = new(StringComparer.Ordinal)
    {
        "onInit", "onReady", "onShow", "onHide", "onDestroy", "onBackPress",
        "onRefresh", "onConfigurationChanged", "onCreate", "onError",
    };

    public static (ComponentLogic Logic, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, InlineComponentDefinition> Components) Minify(
        ComponentLogic logic, IReadOnlyDictionary<string, InlineComponentDefinition> components)
    {
        var properties = logic.ExportDefault?.Properties ?? [];
        // A computed self-member access can select any method at runtime.  Do
        // not shorten the method table unless every such access is statically
        // representable as a normal `this.name` reference.
        if (properties.Any(property => HasComputedThisAccess(property.Node.Value)))
            return (logic, new Dictionary<string, string>(StringComparer.Ordinal), components);
        var reserved = new HashSet<string>(RuntimeCallbackNames, StringComparer.Ordinal);
        reserved.UnionWith(properties.Where(property => property.Kind != JsPropertyKind.Method).Select(property => property.Name));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var method in properties.Where(property => property.Kind == JsPropertyKind.Method && !RuntimeCallbackNames.Contains(property.Name)))
        {
            string shortName;
            do { shortName = NameAt(next++); } while (!reserved.Add(shortName));
            names[method.Name] = shortName;
        }
        if (names.Count == 0) return (logic, names, components);

        var rewritten = properties.Select(property => property.Kind switch
        {
            JsPropertyKind.Method when names.TryGetValue(property.Name, out var renamed) => property with { Node = RewriteComponentMethod(property.Node with { Key = renamed }, names) },
            JsPropertyKind.Method or JsPropertyKind.Lifecycle => property with { Node = RewriteComponentMethod(property.Node, names) },
            _ => property with { Node = RewriteProperty(property.Node, names) },
        }).ToArray();
        var minified = logic with { ExportDefault = new JsExportDefault(rewritten, logic.ExportDefault?.Position) };
        var inline = components.ToDictionary(pair => pair.Key, pair => RewriteInline(pair.Value, names), StringComparer.OrdinalIgnoreCase);
        return (minified, names, inline);
    }

    private static InlineComponentDefinition RewriteInline(InlineComponentDefinition definition, IReadOnlyDictionary<string, string> names)
        => definition with
        {
            MethodNames = definition.MethodNames.ToDictionary(pair => pair.Key, pair => names.TryGetValue(pair.Value, out var renamed) ? renamed : pair.Value, StringComparer.Ordinal),
            InlineComponents = definition.InlineComponents.ToDictionary(pair => pair.Key, pair => RewriteInline(pair.Value, names), StringComparer.OrdinalIgnoreCase)
        };

    private static string NameAt(int index)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var value = "";
        do { value = alphabet[index % alphabet.Length] + value; index = index / alphabet.Length - 1; } while (index >= 0);
        return value;
    }

    /// <summary>Rewrites self calls in a component method while preserving nested normal-function <c>this</c>.</summary>
    internal static JsObjectProperty RewriteComponentMethod(JsObjectProperty property, IReadOnlyDictionary<string, string> names)
        => property.Value is JsFunctionExpression function
            ? property with
            {
                Value = function with
                {
                    Body = (JsBlockStatement)RewriteStatement(function.Body, names),
                    ParameterDefaults = RewriteDefaults(function.ParameterDefaults, names),
                },
                ComputedKey = property.ComputedKey is null ? null : RewriteExpression(property.ComputedKey, names),
            }
            : RewriteProperty(property, names);

    private static JsObjectProperty RewriteProperty(JsObjectProperty property, IReadOnlyDictionary<string, string> names)
        => property with { Value = RewriteExpression(property.Value, names), ComputedKey = property.ComputedKey is null ? null : RewriteExpression(property.ComputedKey, names) };

    private static JsStatement RewriteStatement(JsStatement statement, IReadOnlyDictionary<string, string> names) => statement switch
    {
        JsBlockStatement block => block with { Body = block.Body.Select(item => RewriteStatement(item, names)).ToArray() },
        JsExpressionStatement expression => expression with { Expression = RewriteExpression(expression.Expression, names) },
        JsVariableStatement variable => variable with { Declarations = variable.Declarations.Select(item => item with { Initializer = item.Initializer is null ? null : RewriteExpression(item.Initializer, names) }).ToArray() },
        JsReturnStatement @return => @return with { Argument = @return.Argument is null ? null : RewriteExpression(@return.Argument, names) },
        JsThrowStatement @throw => @throw with { Argument = RewriteExpression(@throw.Argument, names) },
        JsIfStatement @if => @if with { Test = RewriteExpression(@if.Test, names), Consequent = RewriteStatement(@if.Consequent, names), Alternate = @if.Alternate is null ? null : RewriteStatement(@if.Alternate, names) },
        JsWhileStatement @while => @while with { Test = RewriteExpression(@while.Test, names), Body = RewriteStatement(@while.Body, names) },
        JsDoWhileStatement @do => @do with { Body = RewriteStatement(@do.Body, names), Test = RewriteExpression(@do.Test, names) },
        JsForStatement @for => @for with { Initializer = @for.Initializer is null ? null : RewriteStatement(@for.Initializer, names), Test = @for.Test is null ? null : RewriteExpression(@for.Test, names), Update = @for.Update is null ? null : RewriteExpression(@for.Update, names), Body = RewriteStatement(@for.Body, names) },
        JsForInOfStatement @for => @for with { Declaration = @for.Declaration is null ? null : RewriteStatement(@for.Declaration, names), Left = @for.Left is null ? null : RewriteExpression(@for.Left, names), Right = RewriteExpression(@for.Right, names), Body = RewriteStatement(@for.Body, names) },
        JsSwitchStatement @switch => @switch with { Discriminant = RewriteExpression(@switch.Discriminant, names), Cases = @switch.Cases.Select(@case => @case with { Test = @case.Test is null ? null : RewriteExpression(@case.Test, names), Consequent = @case.Consequent.Select(item => RewriteStatement(item, names)).ToArray() }).ToArray() },
        JsTryStatement @try => @try with { Body = (JsBlockStatement)RewriteStatement(@try.Body, names), Handler = @try.Handler is null ? null : @try.Handler with { Body = (JsBlockStatement)RewriteStatement(@try.Handler.Body, names) }, Finalizer = @try.Finalizer is null ? null : (JsBlockStatement)RewriteStatement(@try.Finalizer, names) },
        // A normal nested function gets its own `this`; it is not a component
        // self-call context. Arrow functions are handled by RewriteExpression.
        JsFunctionStatement function => function,
        _ => statement
    };

    private static JsExpression RewriteExpression(JsExpression expression, IReadOnlyDictionary<string, string> names) => expression switch
    {
        JsMemberExpression member => RewriteMember(member, names),
        JsUnaryExpression unary => unary with { Argument = RewriteExpression(unary.Argument, names) },
        JsUpdateExpression update => update with { Argument = RewriteExpression(update.Argument, names) },
        JsBinaryExpression binary => binary with { Left = RewriteExpression(binary.Left, names), Right = RewriteExpression(binary.Right, names) },
        JsAssignmentExpression assignment => assignment with { Left = RewriteExpression(assignment.Left, names), Right = RewriteExpression(assignment.Right, names) },
        JsConditionalExpression conditional => conditional with { Test = RewriteExpression(conditional.Test, names), Consequent = RewriteExpression(conditional.Consequent, names), Alternate = RewriteExpression(conditional.Alternate, names) },
        // InlineComponentScriptMerger combines lifecycle callbacks as
        // `function () { ... }.call(this)`. Although it is a normal function,
        // that explicit call binds the ViewModel, so self references in its
        // body must follow the renamed method table.
        JsCallExpression call when IsCapturedThisCallback(call) => call with
        {
            Callee = ((JsMemberExpression)call.Callee) with
            {
                Object = RewriteCapturedThisFunction((JsFunctionExpression)((JsMemberExpression)call.Callee).Object, names)
            },
            Arguments = call.Arguments.Select(item => RewriteExpression(item, names)).ToArray()
        },
        JsCallExpression call => call with { Callee = RewriteExpression(call.Callee, names), Arguments = call.Arguments.Select(item => RewriteExpression(item, names)).ToArray() },
        JsNewExpression @new => @new with { Callee = RewriteExpression(@new.Callee, names), Arguments = @new.Arguments.Select(item => RewriteExpression(item, names)).ToArray() },
        JsFunctionExpression function when function.Arrow => function with { Body = (JsBlockStatement)RewriteStatement(function.Body, names), ParameterDefaults = RewriteDefaults(function.ParameterDefaults, names) },
        JsFunctionExpression function => function,
        JsDynamicImportExpression import => import with { Specifier = RewriteExpression(import.Specifier, names) },
        JsTaggedTemplateExpression template => template with { Tag = RewriteExpression(template.Tag, names), Substitutions = template.Substitutions.Select(item => RewriteExpression(item, names)).ToArray() },
        JsArrayExpression array => array with { Elements = array.Elements.Select(item => item is null ? null : RewriteExpression(item, names)).ToArray() },
        JsObjectExpression obj => obj with { Properties = obj.Properties.Select(item => RewriteProperty(item, names)).ToArray() },
        JsSpreadExpression spread => spread with { Argument = RewriteExpression(spread.Argument, names) },
        JsSequenceExpression sequence => sequence with { Expressions = sequence.Expressions.Select(item => RewriteExpression(item, names)).ToArray() },
        JsAwaitExpression awaitExpression => awaitExpression with { Argument = RewriteExpression(awaitExpression.Argument, names) },
        JsYieldExpression yield => yield with { Argument = yield.Argument is null ? null : RewriteExpression(yield.Argument, names) },
        _ => expression
    };

    private static JsMemberExpression RewriteMember(JsMemberExpression member, IReadOnlyDictionary<string, string> names)
    {
        var target = RewriteExpression(member.Object, names);
        var property = member.Computed ? RewriteExpression(member.Property, names) : member.Property;
        return member with
        {
            Object = target,
            Property = !member.Computed && IsThis(target) && member.Property is JsIdentifierExpression { Name: var name } && names.TryGetValue(name, out var renamed)
                ? new JsIdentifierExpression(renamed, member.Property.Line, member.Property.Column) : property
        };
    }

    private static bool IsThis(JsExpression expression) => expression is JsLiteralExpression { Raw: "this" } or JsIdentifierExpression { Name: "this" };

    private static bool IsCapturedThisCallback(JsCallExpression call)
        => call.Callee is JsMemberExpression
        {
            Computed: false,
            Object: JsFunctionExpression { Arrow: false },
            Property: JsIdentifierExpression { Name: "call" }
        } && call.Arguments.Count > 0 && IsThis(call.Arguments[0]);

    private static JsFunctionExpression RewriteCapturedThisFunction(JsFunctionExpression function, IReadOnlyDictionary<string, string> names)
        => function with
        {
            Body = (JsBlockStatement)RewriteStatement(function.Body, names),
            ParameterDefaults = RewriteDefaults(function.ParameterDefaults, names)
        };

    private static IReadOnlyList<JsExpression?>? RewriteDefaults(IReadOnlyList<JsExpression?>? defaults, IReadOnlyDictionary<string, string> names)
        => defaults?.Select(value => value is null ? null : RewriteExpression(value, names)).ToArray();

    private static bool HasComputedThisAccess(JsExpression expression)
    {
        // The AST writer produces a canonical representation, including the
        // optional-chain spelling (`this?.[...]`). Checking both forms avoids
        // mistaking a dynamic method lookup for a statically known self call.
        var source = JavaScriptAstWriter.Write(expression);
        return source.Contains("this[", StringComparison.Ordinal) || source.Contains("this?.[", StringComparison.Ordinal);
    }
}
