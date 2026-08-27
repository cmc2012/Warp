using Warp.ComponentCompiler.Scripting;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Translation;

/// <summary>Projects each inline invocation's behavior onto its owning component.</summary>
public static class InlineComponentScriptMerger
{
    public static InlineMergeResult Merge(ComponentLogic host, IReadOnlyDictionary<string, InlineComponentDefinition> components, UxDocument? hostDocument = null)
    {
        var plans = new Dictionary<SourcePosition, InlineInvocationPlan>();
        if (hostDocument is null || components.Count == 0) return new(host, components, plans);

        var used = new HashSet<string>(host.ExportDefault?.Properties.Select(property => property.Name) ?? [], StringComparer.Ordinal);
        var properties = host.ExportDefault?.Properties.ToList() ?? [];
        var lifecycles = properties.Where(property => property.Kind == JsPropertyKind.Lifecycle)
            .GroupBy(property => property.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var sequence = 0;
        Visit(hostDocument.Children, components, new Dictionary<string, JsExpression>(StringComparer.Ordinal), false);
        foreach (var (name, callbacks) in lifecycles)
        {
            var existing = properties.FindIndex(property => property.Kind == JsPropertyKind.Lifecycle && property.Name == name);
            if (existing >= 0) properties[existing] = Lifecycle(name, callbacks);
            else properties.Add(Lifecycle(name, callbacks));
        }
        return new(host with { ExportDefault = new JsExportDefault(properties, host.ExportDefault?.Position) }, components, plans);

        void Visit(IEnumerable<UxNode> nodes, IReadOnlyDictionary<string, InlineComponentDefinition> available, IReadOnlyDictionary<string, JsExpression> scope, bool itemScope)
        {
            foreach (var node in nodes) switch (node)
            {
                case UxElement element when element.IsComponent && available.TryGetValue(element.Tag, out var definition): Specialize(element, definition, scope, itemScope); break;
                case UxElement element: Visit(element.Children, available, scope, itemScope); break;
                case UxListNode list: Visit([list.ItemTemplateRoot], available, scope, true); break;
                case UxIfChain chain: Visit(chain.Branches.SelectMany(branch => branch.Children), available, scope, itemScope); break;
            };
        }

        void Specialize(UxElement invocation, InlineComponentDefinition definition, IReadOnlyDictionary<string, JsExpression> outerScope, bool itemScope)
        {
            var props = invocation.Attrs.Where(attribute => attribute.Kind != AttrKind.Event)
                .ToDictionary(attribute => Camel(attribute.Name), attribute => Resolve(attribute.Value, outerScope, itemScope), StringComparer.Ordinal);
            // A missing inline prop is not permission to fall through to a
            // same-named host member.  Preserve component prop semantics by
            // specializing it to undefined instead.
            foreach (var prop in DeclaredProps(definition))
                props.TryAdd(prop, new JsLiteralExpression("undefined", JavaScriptTokenKind.Identifier, 0, 0));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var method in definition.Logic.ExportDefault?.Properties.Where(property => property.Kind == JsPropertyKind.Method) ?? [])
                names[method.Name] = Unique("__warp_inline_" + method.Name, used, ref sequence);
            foreach (var method in definition.Logic.ExportDefault?.Properties.Where(property => property.Kind == JsPropertyKind.Method) ?? [])
                properties.Add(method with { Node = Rewrite(method.Node with { Key = names[method.Name] }, names, props) });
            if (invocation.Position is { } position) plans[position] = new(names, props);
            foreach (var lifecycle in definition.Logic.ExportDefault?.Properties.Where(property => property.Kind == JsPropertyKind.Lifecycle) ?? [])
            {
                if (!lifecycles.TryGetValue(lifecycle.Name, out var callbacks)) lifecycles[lifecycle.Name] = callbacks = [];
                callbacks.Add(lifecycle with { Node = Rewrite(lifecycle.Node, names, props) });
            }
            Visit(definition.Document.Children, definition.InlineComponents, props, itemScope);
        }
    }

    private static JsExpression Resolve(AttrValue value, IReadOnlyDictionary<string, JsExpression> scope, bool itemScope) => value switch
    {
        BindingValue binding => ResolvePath(binding.Path, scope, itemScope || binding.ItemScope),
        LiteralValue literal => new JsLiteralExpression(literal.Text, JavaScriptTokenKind.String, 0, 0),
        ExprValue expression => RewriteTemplateExpression(expression.Expr, scope, itemScope || expression.ItemScope),
        _ => new JsLiteralExpression("undefined", JavaScriptTokenKind.Identifier, 0, 0)
    };

    private static IEnumerable<string> DeclaredProps(InlineComponentDefinition definition)
        => definition.Logic.ExportDefault?.Properties.FirstOrDefault(property => property.Kind == JsPropertyKind.Props)?.Node.Value is JsArrayExpression array
            ? array.Elements.OfType<JsLiteralExpression>()
                .Select(value => value.Raw.Trim().Trim('\'', '"'))
                .Where(value => value.Length > 0)
            : [];

    private static JsExpression ResolvePath(string path, IReadOnlyDictionary<string, JsExpression> scope, bool itemScope)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && scope.TryGetValue(segments[0], out var replacement)) return segments.Skip(1).Aggregate(replacement, Member);
        return segments.Aggregate<string, JsExpression>(new JsLiteralExpression(itemScope ? "$item" : "this", JavaScriptTokenKind.Identifier, 0, 0), Member);
    }

    private static JsExpression RewriteTemplateExpression(string source, IReadOnlyDictionary<string, JsExpression> scope, bool itemScope)
    {
        try
        {
            var parsed = ((JsExpressionStatement)JavaScriptSyntax.ParseScript("(" + source + ");", "<inline-prop>").Body.Single()).Expression;
            return RewriteFreeIdentifiers(parsed, scope, itemScope);
        }
        catch (JavaScriptCompilationException) { return new JsLiteralExpression("undefined", JavaScriptTokenKind.Identifier, 0, 0); }
    }

    private static JsObjectProperty Rewrite(JsObjectProperty property, IReadOnlyDictionary<string, string> methods, IReadOnlyDictionary<string, JsExpression> props)
        => property.Value is JsFunctionExpression function
            ? property with { Value = function with { Body = (JsBlockStatement)RewriteStatement(function.Body, methods, props) } }
            : property with { Value = RewriteExpression(property.Value, methods, props) };

    private static JsStatement RewriteStatement(JsStatement statement, IReadOnlyDictionary<string, string> methods, IReadOnlyDictionary<string, JsExpression> props) => statement switch
    {
        JsBlockStatement block => block with { Body = block.Body.Select(item => RewriteStatement(item, methods, props)).ToArray() },
        JsExpressionStatement expression => expression with { Expression = RewriteExpression(expression.Expression, methods, props) },
        JsVariableStatement variable => variable with { Declarations = variable.Declarations.Select(item => item with { Initializer = item.Initializer is null ? null : RewriteExpression(item.Initializer, methods, props) }).ToArray() },
        JsReturnStatement result => result with { Argument = result.Argument is null ? null : RewriteExpression(result.Argument, methods, props) },
        JsThrowStatement thrown => thrown with { Argument = RewriteExpression(thrown.Argument, methods, props) },
        JsIfStatement conditional => conditional with { Test = RewriteExpression(conditional.Test, methods, props), Consequent = RewriteStatement(conditional.Consequent, methods, props), Alternate = conditional.Alternate is null ? null : RewriteStatement(conditional.Alternate, methods, props) },
        JsWhileStatement loop => loop with { Test = RewriteExpression(loop.Test, methods, props), Body = RewriteStatement(loop.Body, methods, props) },
        JsDoWhileStatement loop => loop with { Test = RewriteExpression(loop.Test, methods, props), Body = RewriteStatement(loop.Body, methods, props) },
        JsForStatement loop => loop with { Initializer = loop.Initializer is null ? null : RewriteStatement(loop.Initializer, methods, props), Test = loop.Test is null ? null : RewriteExpression(loop.Test, methods, props), Update = loop.Update is null ? null : RewriteExpression(loop.Update, methods, props), Body = RewriteStatement(loop.Body, methods, props) },
        JsForInOfStatement loop => loop with { Declaration = loop.Declaration is null ? null : RewriteStatement(loop.Declaration, methods, props), Left = loop.Left is null ? null : RewriteExpression(loop.Left, methods, props), Right = RewriteExpression(loop.Right, methods, props), Body = RewriteStatement(loop.Body, methods, props) },
        JsSwitchStatement sw => sw with { Discriminant = RewriteExpression(sw.Discriminant, methods, props), Cases = sw.Cases.Select(@case => @case with { Test = @case.Test is null ? null : RewriteExpression(@case.Test, methods, props), Consequent = @case.Consequent.Select(item => RewriteStatement(item, methods, props)).ToArray() }).ToArray() },
        JsTryStatement attempt => attempt with { Body = (JsBlockStatement)RewriteStatement(attempt.Body, methods, props), Handler = attempt.Handler is null ? null : attempt.Handler with { Body = (JsBlockStatement)RewriteStatement(attempt.Handler.Body, methods, props) }, Finalizer = attempt.Finalizer is null ? null : (JsBlockStatement)RewriteStatement(attempt.Finalizer, methods, props) },
        JsFunctionStatement function => function,
        _ => statement
    };

    private static JsExpression RewriteExpression(JsExpression expression, IReadOnlyDictionary<string, string> methods, IReadOnlyDictionary<string, JsExpression> props) => expression switch
    {
        JsMemberExpression member when !member.Computed && IsThis(member.Object) && member.Property is JsIdentifierExpression { Name: var name } && props.TryGetValue(name, out var prop) => prop,
        JsMemberExpression member => member with { Object = RewriteExpression(member.Object, methods, props), Property = member.Computed ? RewriteExpression(member.Property, methods, props) : member.Property is JsIdentifierExpression { Name: var name } && IsThis(member.Object) && methods.TryGetValue(name, out var target) ? new JsIdentifierExpression(target, member.Property.Line, member.Property.Column) : member.Property },
        JsUnaryExpression unary => unary with { Argument = RewriteExpression(unary.Argument, methods, props) },
        JsUpdateExpression update => update with { Argument = RewriteExpression(update.Argument, methods, props) },
        JsBinaryExpression binary => binary with { Left = RewriteExpression(binary.Left, methods, props), Right = RewriteExpression(binary.Right, methods, props) },
        JsAssignmentExpression assignment => assignment with { Left = RewriteExpression(assignment.Left, methods, props), Right = RewriteExpression(assignment.Right, methods, props) },
        JsConditionalExpression conditional => conditional with { Test = RewriteExpression(conditional.Test, methods, props), Consequent = RewriteExpression(conditional.Consequent, methods, props), Alternate = RewriteExpression(conditional.Alternate, methods, props) },
        JsCallExpression call => call with { Callee = RewriteExpression(call.Callee, methods, props), Arguments = call.Arguments.Select(item => RewriteExpression(item, methods, props)).ToArray() },
        JsNewExpression @new => @new with { Callee = RewriteExpression(@new.Callee, methods, props), Arguments = @new.Arguments.Select(item => RewriteExpression(item, methods, props)).ToArray() },
        JsFunctionExpression function when function.Arrow => function with { Body = (JsBlockStatement)RewriteStatement(function.Body, methods, props) },
        JsFunctionExpression function => function,
        JsArrayExpression array => array with { Elements = array.Elements.Select(item => item is null ? null : RewriteExpression(item, methods, props)).ToArray() },
        JsObjectExpression obj => obj with { Properties = obj.Properties.Select(item => item with { Value = RewriteExpression(item.Value, methods, props) }).ToArray() },
        JsSpreadExpression spread => spread with { Argument = RewriteExpression(spread.Argument, methods, props) },
        JsSequenceExpression sequence => sequence with { Expressions = sequence.Expressions.Select(item => RewriteExpression(item, methods, props)).ToArray() },
        JsAwaitExpression awaitExpression => awaitExpression with { Argument = RewriteExpression(awaitExpression.Argument, methods, props) },
        JsYieldExpression yieldExpression => yieldExpression with { Argument = yieldExpression.Argument is null ? null : RewriteExpression(yieldExpression.Argument, methods, props) },
        _ => expression
    };

    private static JsExpression RewriteFreeIdentifiers(JsExpression expression, IReadOnlyDictionary<string, JsExpression> scope, bool itemScope) => expression switch
    {
        JsIdentifierExpression identifier when scope.TryGetValue(identifier.Name, out var replacement) => replacement,
        JsIdentifierExpression identifier when identifier.Name is not ("true" or "false" or "null" or "undefined" or "this") => Member(new JsLiteralExpression(itemScope ? "$item" : "this", JavaScriptTokenKind.Identifier, 0, 0), identifier.Name),
        JsMemberExpression member => member with { Object = RewriteFreeIdentifiers(member.Object, scope, itemScope), Property = member.Computed ? RewriteFreeIdentifiers(member.Property, scope, itemScope) : member.Property },
        JsCallExpression call => call with { Callee = RewriteFreeIdentifiers(call.Callee, scope, itemScope), Arguments = call.Arguments.Select(item => RewriteFreeIdentifiers(item, scope, itemScope)).ToArray() },
        JsBinaryExpression binary => binary with { Left = RewriteFreeIdentifiers(binary.Left, scope, itemScope), Right = RewriteFreeIdentifiers(binary.Right, scope, itemScope) },
        _ => expression
    };

    private static JsProperty Lifecycle(string name, IReadOnlyList<JsProperty> callbacks)
    {
        var calls = callbacks.Select(callback => new JsExpressionStatement(new JsCallExpression(new JsMemberExpression(callback.Node.Value, Id("call"), false, 0, 0), [This()], 0, 0), 0, 0)).Cast<JsStatement>().ToArray();
        return new(new JsObjectProperty(name, new JsFunctionExpression(null, [], new JsBlockStatement(calls, 0, 0), false, false, 0, 0), false, 0, 0, JsObjectPropertyKind.Method), JsPropertyKind.Lifecycle);
    }

    private static string Unique(string baseName, ISet<string> used, ref int sequence) { var name = baseName + "_" + ++sequence; while (!used.Add(name)) name = baseName + "_" + ++sequence; return name; }
    private static bool IsThis(JsExpression expression) => expression is JsLiteralExpression { Raw: "this" } or JsIdentifierExpression { Name: "this" };
    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    private static JsIdentifierExpression Id(string name) => new(name, 0, 0);
    private static JsLiteralExpression This() => new("this", JavaScriptTokenKind.Identifier, 0, 0);
    private static JsMemberExpression Member(JsExpression target, string property) => new(target, Id(property), false, 0, 0);
}
