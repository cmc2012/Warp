using System.Text;
using System.Text.Json;

namespace Warp.JsCompiler.Frontend;

/// <summary>Serializes the source AST back to parseable JavaScript.</summary>
/// <remarks>The writer deliberately favors explicit parentheses over pretty output so a round trip preserves meaning.</remarks>
public static class JavaScriptAstWriter
{
    public static string Write(JsAstProgram program)
        => string.Join("\n", program.Body.Select(Statement));

    /// <summary>Writes an expression for embedding in another generated artifact.</summary>
    public static string Write(JsExpression expression) => Expression(expression);

    /// <summary>Writes a single statement without requiring a synthetic program node.</summary>
    public static string Write(JsStatement statement) => Statement(statement);

    private static string Statement(JsStatement statement) => statement switch
    {
        JsEmptyStatement => ";",
        JsPrivateBrandStatement => throw new InvalidOperationException("Compiler-only private-brand markers cannot be serialized as source JavaScript."),
        JsBlockStatement block => Block(block),
        JsExpressionStatement expression => Expression(expression.Expression) + ";",
        JsVariableStatement variable => Variable(variable) + ";",
        JsReturnStatement { Argument: null } => "return;",
        JsReturnStatement value => "return " + Expression(value.Argument!) + ";",
        JsThrowStatement value => "throw " + Expression(value.Argument) + ";",
        JsIfStatement value => "if (" + Expression(value.Test) + ") " + StatementBody(value.Consequent) +
                               (value.Alternate is null ? "" : " else " + StatementBody(value.Alternate)),
        JsWhileStatement value => "while (" + Expression(value.Test) + ") " + StatementBody(value.Body),
        JsDoWhileStatement value => "do " + StatementBody(value.Body) + " while (" + Expression(value.Test) + ");",
        JsForStatement value => "for (" + ForInitializer(value.Initializer) + "; " + Optional(value.Test) + "; " + Optional(value.Update) + ") " + StatementBody(value.Body),
        JsForInOfStatement value => "for " + (value.IsAwait ? "await " : "") + "(" +
                                    (value.Declaration is null ? Optional(value.Left) : ForInitializer(value.Declaration)) +
                                    (value.IsOf ? " of " : " in ") + Expression(value.Right) + ") " + StatementBody(value.Body),
        JsSwitchStatement value => Switch(value),
        JsTryStatement value => "try " + Block(value.Body) +
                                (value.Handler is null ? "" : " catch" + Catch(value.Handler) + " " + Block(value.Handler.Body)) +
                                (value.Finalizer is null ? "" : " finally " + Block(value.Finalizer)),
        JsClassDeclaration value => Class(value.Name, value.SuperClass, value.Members),
        JsLabeledStatement value => value.Label + ": " + StatementBody(value.Body),
        JsWithStatement value => "with (" + Expression(value.Object) + ") " + StatementBody(value.Body),
        JsBreakStatement { Label: null } => "break;",
        JsBreakStatement value => "break " + value.Label + ";",
        JsContinueStatement { Label: null } => "continue;",
        JsContinueStatement value => "continue " + value.Label + ";",
        JsFunctionStatement value => Function(value.Name, value.Parameters, value.Body, value.Async, false, value.Generator, value.ParameterDefaults, value.ParameterPatterns),
        JsImportStatement value => Import(value) + ";",
        JsExportStatement value => Export(value),
        JsExportAllStatement value => "export * from " + Quote(value.Source) + ";",
        _ => throw new NotSupportedException($"Unsupported statement {statement.GetType().Name}.")
    };

    private static string Block(JsBlockStatement block) => "{" + string.Join("\n", block.Body.Select(Statement)) + "}";
    private static string StatementBody(JsStatement statement) => statement is JsBlockStatement ? Statement(statement) : "{" + Statement(statement) + "}";
    private static string Optional(JsExpression? expression) => expression is null ? "" : Expression(expression);
    private static string ForInitializer(JsStatement? statement) => statement switch
    {
        null => "",
        JsVariableStatement variable => Variable(variable),
        JsExpressionStatement expression => Expression(expression.Expression),
        _ => throw new NotSupportedException("Unsupported for-loop initializer.")
    };

    private static string Variable(JsVariableStatement variable) => variable.Kind + " " + string.Join(", ", variable.Declarations.Select(declaration =>
        Pattern(declaration.Pattern ?? new JsIdentifierPattern(declaration.Name, declaration.Line, declaration.Column)) +
        (declaration.Initializer is null ? "" : " = " + Expression(declaration.Initializer))));

    private static string Switch(JsSwitchStatement statement)
    {
        var cases = statement.Cases.Select(@case => (@case.Test is null ? "default:" : "case " + Expression(@case.Test) + ":") +
            string.Join("", @case.Consequent.Select(Statement)));
        return "switch (" + Expression(statement.Discriminant) + "){" + string.Join("", cases) + "}";
    }

    private static string Catch(JsCatchClause clause) => clause.Pattern is null && clause.Binding is null ? "" :
        " (" + Pattern(clause.Pattern ?? new JsIdentifierPattern(clause.Binding!, clause.Line, clause.Column)) + ")";

    private static string Class(string? name, JsExpression? superClass, IReadOnlyList<JsClassMember> members)
        => "class" + (string.IsNullOrEmpty(name) ? "" : " " + name) +
           (superClass is null ? "" : " extends " + Expression(superClass)) + "{" + string.Join("", members.Select(ClassMember)) + "}";

    private static string ClassMember(JsClassMember member)
    {
        if (member.Kind == JsClassMemberKind.StaticBlock) return "static " + Block(member.Body);
        var prefix = (member.IsStatic ? "static " : "") + (member.Async ? "async " : "") + (member.Generator ? "*" : "");
        var name = member.ComputedKey is null
            ? (member.Name.StartsWith('#') ? member.Name : PropertyKey(member.Name))
            : "[" + Expression(member.ComputedKey) + "]";
        if (member.Kind == JsClassMemberKind.Field) return prefix + name + (member.Initializer is null ? ";" : " = " + Expression(member.Initializer) + ";");
        var accessor = member.Kind switch { JsClassMemberKind.Getter => "get ", JsClassMemberKind.Setter => "set ", _ => "" };
        return prefix + accessor + name + Parameters(member.Parameters, member.ParameterDefaults, member.ParameterPatterns) + Block(member.Body);
    }

    private static string Function(string? name, IReadOnlyList<string> parameters, JsBlockStatement body, bool async, bool arrow,
        bool generator, IReadOnlyList<JsExpression?>? defaults, IReadOnlyList<JsBindingPattern>? patterns)
    {
        var args = Parameters(parameters, defaults, patterns);
        if (arrow) return (async ? "async " : "") + args + " => " + Block(body);
        return (async ? "async " : "") + "function" + (generator ? "*" : "") + (string.IsNullOrEmpty(name) ? "" : " " + name) + args + Block(body);
    }

    private static string Parameters(IReadOnlyList<string> names, IReadOnlyList<JsExpression?>? defaults, IReadOnlyList<JsBindingPattern>? patterns)
        => "(" + string.Join(", ", names.Select((name, index) =>
            Pattern(patterns is { Count: > 0 } ? patterns[index] : new JsIdentifierPattern(name, 0, 0)) +
            (defaults is not null && index < defaults.Count && defaults[index] is not null ? " = " + Expression(defaults[index]!) : ""))) + ")";

    private static string Import(JsImportStatement statement)
    {
        if (statement.Bindings.Count == 0) return "import " + Quote(statement.Specifier);
        var defaultBinding = statement.Bindings.FirstOrDefault(binding => binding.Kind == JsImportBindingKind.Default);
        var namespaceBinding = statement.Bindings.FirstOrDefault(binding => binding.Kind == JsImportBindingKind.Namespace);
        var named = statement.Bindings.Where(binding => binding.Kind == JsImportBindingKind.Named)
            .Select(binding => binding.ImportName == binding.LocalName ? binding.ImportName : binding.ImportName + " as " + binding.LocalName).ToArray();
        var parts = new List<string>();
        if (defaultBinding is not null) parts.Add(defaultBinding.LocalName);
        if (namespaceBinding is not null) parts.Add("* as " + namespaceBinding.LocalName);
        if (named.Length > 0) parts.Add("{ " + string.Join(", ", named) + " }");
        return "import " + string.Join(", ", parts) + " from " + Quote(statement.Specifier);
    }

    private static string Export(JsExportStatement statement)
    {
        if (statement.Declaration is not null)
        {
            if (statement.IsDefault && statement.Declaration is JsExpressionStatement expression)
                return "export default " + Expression(expression.Expression) + ";";
            return "export " + (statement.IsDefault ? "default " : "") + Statement(statement.Declaration);
        }
        if (statement.Bindings.Count == 1 && statement.Bindings[0] is { LocalName: "*" } binding)
            return "export * as " + binding.ExportName + " from " + Quote(statement.Source!) + ";";
        var bindings = string.Join(", ", statement.Bindings.Select(binding => binding.LocalName == binding.ExportName
            ? binding.LocalName : binding.LocalName + " as " + binding.ExportName));
        return "export { " + bindings + " }" + (statement.Source is null ? "" : " from " + Quote(statement.Source)) + ";";
    }

    private static string Pattern(JsBindingPattern pattern) => pattern switch
    {
        JsIdentifierPattern value => value.Name,
        JsRestPattern value => "..." + Pattern(value.Argument),
        JsAssignmentPattern value => Pattern(value.Left) + " = " + Expression(value.Right),
        JsAssignmentTargetPattern value => Expression(value.Target),
        JsArrayPattern value => "[" + string.Join(", ", value.Elements.Select(item => item is null ? "" : Pattern(item))) + "]",
        JsObjectPattern value => "{" + string.Join(", ", value.Properties.Select(BindingProperty)) + "}",
        _ => throw new NotSupportedException($"Unsupported binding pattern {pattern.GetType().Name}.")
    };

    private static string BindingProperty(JsObjectBindingProperty property)
    {
        if (property.Key == "...") return Pattern(property.Value);
        var key = property.ComputedKey is null ? PropertyKey(property.Key) : "[" + Expression(property.ComputedKey) + "]";
        return property.IsShorthand ? Pattern(property.Value) : key + ": " + Pattern(property.Value);
    }

    private static string Expression(JsExpression expression) => expression switch
    {
        JsIdentifierExpression value => value.Name,
        JsPrivateIdentifierExpression value => value.Name,
        JsSuperExpression => "super",
        JsNewTargetExpression => "new.target",
        JsImportMetaExpression => "import.meta",
        JsLiteralExpression value => Literal(value),
        JsUnaryExpression value => "(" + value.Operator + (char.IsLetter(value.Operator[^1]) ? " " : "") + Expression(value.Argument) + ")",
        JsUpdateExpression value => value.Prefix ? "(" + value.Operator + Expression(value.Argument) + ")" : "(" + Expression(value.Argument) + value.Operator + ")",
        JsBinaryExpression value => "(" + Expression(value.Left) + " " + value.Operator + " " + Expression(value.Right) + ")",
        JsAssignmentExpression value => "(" + Expression(value.Left) + " " + value.Operator + " " + Expression(value.Right) + ")",
        JsConditionalExpression value => "(" + Expression(value.Test) + " ? " + Expression(value.Consequent) + " : " + Expression(value.Alternate) + ")",
        JsMemberExpression value => Member(value),
        JsCallExpression value => Call(value),
        JsFunctionExpression value => Function(value.Name, value.Parameters, value.Body, value.Async, value.Arrow, value.Generator, value.ParameterDefaults, value.ParameterPatterns),
        JsClassExpression value => Class(value.Name, value.SuperClass, value.Members),
        JsNewExpression value => "new " + Expression(value.Callee) + "(" + string.Join(", ", value.Arguments.Select(Expression)) + ")",
        JsDynamicImportExpression value => "import(" + Expression(value.Specifier) + ")",
        JsTaggedTemplateExpression value => Expression(value.Tag) + Template(value.Raw, value.Substitutions),
        JsSpreadExpression value => "..." + Expression(value.Argument),
        JsSequenceExpression value => "(" + string.Join(", ", value.Expressions.Select(Expression)) + ")",
        JsYieldExpression value => "yield" + (value.Delegate ? "*" : "") + (value.Argument is null ? "" : " " + Expression(value.Argument)),
        JsAwaitExpression value => "await " + Expression(value.Argument),
        JsArrayExpression value => "[" + string.Join(", ", value.Elements.Select(item => item is null ? "" : Expression(item))) + "]",
        JsObjectExpression value => "{" + string.Join(", ", value.Properties.Select(ObjectProperty)) + "}",
        _ => throw new NotSupportedException($"Unsupported expression {expression.GetType().Name}.")
    };

    private static string Member(JsMemberExpression member)
        => "(" + Expression(member.Object) +
           (member.Computed ? (member.Optional ? "?.[" : "[") : (member.Optional ? "?." : ".")) +
           Expression(member.Property) + (member.Computed ? "]" : "") + ")";

    private static string Call(JsCallExpression call)
    {
        var arguments = string.Join(", ", call.Arguments.Select(Expression));
        // Keep a member expression directly adjacent to its call. Besides
        // producing idiomatic output, this preserves its receiver for the
        // device JavaScript engine's page-hook dispatcher.
        var callee = call.Callee is JsMemberExpression member ? MemberForCall(member) : Expression(call.Callee);
        return "(" + callee + (call.DirectOptional ? "?." : "") + "(" + arguments + "))";
    }

    private static string MemberForCall(JsMemberExpression member)
        => Expression(member.Object) +
           (member.Computed ? (member.Optional ? "?.[" : "[") : (member.Optional ? "?." : ".")) +
           Expression(member.Property) + (member.Computed ? "]" : "");

    private static string ObjectProperty(JsObjectProperty property)
    {
        if (property.Key == "...") return "..." + Expression(property.Value);
        var key = property.ComputedKey is null ? PropertyKey(property.Key) : "[" + Expression(property.ComputedKey) + "]";
        if (property.Kind != JsObjectPropertyKind.Value)
        {
            var function = (JsFunctionExpression)property.Value;
            var prefix = property.Kind switch { JsObjectPropertyKind.Getter => "get ", JsObjectPropertyKind.Setter => "set ", _ => "" };
            return (function.Async ? "async " : "") + prefix + (function.Generator ? "*" : "") + key +
                   Parameters(function.Parameters, function.ParameterDefaults, function.ParameterPatterns) + Block(function.Body);
        }
        if (!property.Shorthand) return key + ": " + Expression(property.Value);
        return property.IsAssignmentPatternDefault && property.Value is JsAssignmentExpression assignment
            ? Expression(assignment.Left) + " = " + Expression(assignment.Right)
            : Expression(property.Value);
    }

    private static string Literal(JsLiteralExpression literal) => literal.Kind switch
    {
        JavaScriptTokenKind.String => Quote(literal.Raw),
        _ => literal.Raw
    };

    private static string Template(IReadOnlyList<string> raw, IReadOnlyList<JsExpression> substitutions)
    {
        var builder = new StringBuilder("`");
        for (var index = 0; index < raw.Count; index++)
        {
            builder.Append(raw[index]);
            if (index < substitutions.Count) builder.Append("${").Append(Expression(substitutions[index])).Append('}');
        }
        return builder.Append('`').ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string PropertyKey(string key)
    {
        if (key.Length > 0 && (char.IsLetter(key[0]) || key[0] is '_' or '$') &&
            key.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '_' or '$'))
            return key;
        return Quote(key);
    }
}
