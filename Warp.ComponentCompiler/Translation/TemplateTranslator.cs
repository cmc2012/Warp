using System.Globalization;
using System.Text.RegularExpressions;
using Warp.ComponentCompiler.Analysis;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Translation;

/// <summary>Lowers component markup directly into the JavaScript source AST.</summary>
public sealed class TemplateTranslator
{
    private readonly ConstantTable _constTable;
    private readonly HashSet<string> _moduleBindings;
    private readonly string? _templatePath;
    private readonly string? _sourceRootPath;
    private readonly DiagnosticSink? _diagnostics;
    private readonly IReadOnlyDictionary<string, InlineComponentDefinition> _inlineComponents;
    private readonly IReadOnlyDictionary<string, JsExpression> _inlineBindings;
    private readonly IReadOnlyDictionary<string, AttrValue> _inlineEvents;
    private static readonly HashSet<string> Globals = new(StringComparer.Ordinal) { "Math", "Date", "JSON", "Number", "String", "Boolean", "Array", "Object", "RegExp", "Promise", "Symbol", "Map", "Set", "WeakMap", "Error", "console", "setTimeout", "setInterval", "clearTimeout", "clearInterval", "parseInt", "parseFloat", "isNaN", "isFinite", "NaN", "Infinity", "undefined", "global", "require", "$app_require$", "$translateStyle$" };
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "true", "false", "null", "undefined", "NaN", "Infinity", "arguments" };

    public TemplateTranslator(ConstantTable constTable, IEnumerable<string>? moduleBindings = null, string? templatePath = null, string? sourceRootPath = null, DiagnosticSink? diagnostics = null, IReadOnlyDictionary<string, InlineComponentDefinition>? inlineComponents = null, IReadOnlyDictionary<string, JsExpression>? inlineBindings = null, IReadOnlyDictionary<string, AttrValue>? inlineEvents = null)
    {
        _constTable = constTable;
        _moduleBindings = moduleBindings is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(moduleBindings, StringComparer.Ordinal);
        _templatePath = templatePath;
        _sourceRootPath = sourceRootPath;
        _diagnostics = diagnostics;
        _inlineComponents = inlineComponents ?? new Dictionary<string, InlineComponentDefinition>(StringComparer.OrdinalIgnoreCase);
        _inlineBindings = inlineBindings ?? new Dictionary<string, JsExpression>(StringComparer.Ordinal);
        _inlineEvents = inlineEvents ?? new Dictionary<string, AttrValue>(StringComparer.Ordinal);
    }

    public IReadOnlyList<JsExpression> TranslateAst(IReadOnlyList<UxNode> children, bool itemScope = false)
        => children.SelectMany(node => TranslateNode(node, itemScope)).ToArray();

    private IReadOnlyList<JsExpression> TranslateNode(UxNode node, bool itemScope) => node switch
    {
        UxElement element => [TranslateElement(element, itemScope)],
        UxTextNode text => [TranslateText(text, itemScope)],
        UxListNode list => [TranslateList(list, itemScope)],
        UxIfChain chain => TranslateIfChain(chain, itemScope),
        _ => []
    };

    private JsExpression TranslateElement(UxElement element, bool itemScope)
    {
        if (element.IsComponent && _inlineComponents.TryGetValue(element.Tag, out var inline))
            return TranslateInlineComponent(element, itemScope, inline);
        var context = Object([Property("__vm__", Id("_vm_")), Property("__opts__", TranslateAttrs(element.Attrs, itemScope, element.IsComponent, element.Tag, element.IsStatic || element.IsConst))]);
        var isDynamicComponent = element.Tag.Equals("component", StringComparison.OrdinalIgnoreCase)
            && element.Attrs.Any(attribute => attribute.Name.Equals("is", StringComparison.OrdinalIgnoreCase) || attribute.Name.Equals("remotewidget", StringComparison.OrdinalIgnoreCase));
        var factory = element.IsComponent ? "__cc__" : isDynamicComponent ? "__cdc__" : "__ce__";
        return Call(Member(Id("aiot"), factory), [String(element.IsComponent ? element.Tag : element.Tag.ToLowerInvariant()), context, Array(TranslateAst(element.Children, itemScope))]);
    }

    private JsExpression TranslateInlineComponent(UxElement element, bool itemScope, InlineComponentDefinition inline)
    {
        var bindings = new Dictionary<string, JsExpression>(StringComparer.Ordinal);
        var events = new Dictionary<string, AttrValue>(StringComparer.Ordinal);
        foreach (var attribute in element.Attrs)
        {
            if (attribute.Kind == AttrKind.Event) events[attribute.Name] = attribute.Value;
            else bindings[attribute.Name] = AttrValueToExpression(attribute.Value, itemScope);
        }
        var translator = new TemplateTranslator(_constTable, _moduleBindings, inline.SourcePath, _sourceRootPath, _diagnostics, _inlineComponents, bindings, events);
        var children = translator.TranslateAst(inline.Document.Children);
        return children.Count == 1 ? children[0] : Array(children);
    }

    private JsExpression TranslateText(UxTextNode text, bool itemScope)
    {
        var context = Object([Property("__vm__", Id("_vm_")), Property("__opts__", Object([Property("value", AttributeValue(text.Value, itemScope))]))]);
        return Call(Member(Id("aiot"), "__ce__"), [String("span"), context, Array([])]);
    }

    private JsExpression TranslateList(UxListNode list, bool itemScope)
    {
        var source = AttrValueToExpression(list.ItemsSource, itemScope);
        JsExpression exp = list.Key is null ? source : Function([], [new JsReturnStatement(Object([Property("__list__", source), Property("__tid__", String(list.Key))]), 0, 0)]);
        var opts = Object([Property("exp", exp), Property("key", String("$idx")), Property("value", String("$item"))]);
        var context = Object([Property("__vm__", Id("_vm_")), Property("__opts__", opts)]);
        return Call(Member(Id("aiot"), "__cf__"), [context, Function(["$idx", "$item"], [new JsReturnStatement(Array(TranslateNode(list.ItemTemplateRoot, true)), 0, 0)])]);
    }

    private IReadOnlyList<JsExpression> TranslateIfChain(UxIfChain chain, bool itemScope)
    {
        var output = new List<JsExpression>();
        var previous = new List<JsExpression>();
        foreach (var branch in chain.Branches)
        {
            JsExpression shown;
            var terminal = branch.Kind == IfBranchKind.Else;
            if (branch.Kind == IfBranchKind.Else) shown = AndAll(previous.Select(Not), Bool(true));
            else
            {
                var test = AttrValueToExpression(branch.Test!, itemScope);
                if (TryBoolean(test, out var constant) && !constant) continue;
                shown = AndAll(previous.Select(Not).Append(test), Bool(true));
                previous.Add(test);
                terminal = TryBoolean(test, out constant) && constant;
            }
            var opts = Object([Property("shown", Function([], [new JsReturnStatement(shown, 0, 0)])), Property("modifiers", Decorators(branch.Modifiers, "shown"))]);
            var context = Object([Property("__vm__", Id("_vm_")), Property("__opts__", opts)]);
            output.Add(Call(Member(Id("aiot"), "__ci__"), [context, Function([], [new JsReturnStatement(Array(TranslateAst(branch.Children, itemScope)), 0, 0)])]));
            if (terminal) break;
        }
        return output;
    }

    private JsObjectExpression TranslateAttrs(IReadOnlyList<UxAttr> attrs, bool itemScope, bool isComponent, string? elementTag = null, bool isStatic = false)
    {
        var properties = new List<JsObjectProperty>();
        var events = new List<JsObjectProperty>();
        var dataset = new List<JsObjectProperty>();
        var modifiers = new List<JsObjectProperty>();
        foreach (var attribute in attrs)
        {
            var scoped = attribute.Kind == AttrKind.Event ? false : itemScope;
            switch (attribute.Kind)
            {
                case AttrKind.Class: properties.Add(Property("classList", ClassValue(attribute.Value, scoped))); break;
                case AttrKind.Style: properties.Add(Property("style", attribute.Value is LiteralValue literal ? ParseInlineStyle(literal.Text) : StyleValue(attribute.Value, scoped))); break;
                case AttrKind.Text: properties.Add(Property("value", AttributeValue(attribute.Value, scoped))); break;
                case AttrKind.Source: properties.Add(Property("src", ResourceValue(elementTag, "source", attribute.Value, scoped))); break;
                case AttrKind.Value: properties.Add(Property(elementTag?.Equals("progress", StringComparison.OrdinalIgnoreCase) == true ? "percent" : "value", AttributeValue(attribute.Value, scoped))); break;
                case AttrKind.Event: events.Add(Property(EventName(attribute.Name, isComponent), EventValue(attribute.Value))); break;
                case AttrKind.Dataset: dataset.Add(Property(ToCamelCase(attribute.Name["data-".Length..]), AttributeValue(attribute.Value, scoped))); break;
                case AttrKind.Model: properties.Add(Property("model", ModelValue(attribute.Value, scoped))); break;
                case AttrKind.Plain:
                    var dynamicComponentAttribute = elementTag?.Equals("component", StringComparison.OrdinalIgnoreCase) == true
                        && (attribute.Name.Equals("is", StringComparison.OrdinalIgnoreCase) || attribute.Name.Equals("remotewidget", StringComparison.OrdinalIgnoreCase));
                    var resource = attribute.Name.Equals("alt", StringComparison.OrdinalIgnoreCase)
                        ? ResourceValue(elementTag, "alt", attribute.Value, scoped)
                        : AttributeValue(attribute.Value, scoped);
                    properties.Add(Property(ToCamelCase(attribute.Name), dynamicComponentAttribute ? ForceDynamicValue(attribute.Value, scoped) : resource));
                    break;
            }
            if (attribute.Modifiers is { Count: > 0 })
                modifiers.Add(Property(ToCamelCase(attribute.Name), Object(attribute.Modifiers.Select(modifier => Property(ToCamelCase(modifier), Bool(true))).ToArray())));
        }
        if (events.Count > 0) properties.Add(Property("events", Object(events)));
        if (dataset.Count > 0) properties.Add(Property("dataset", Object(dataset)));
        if (isStatic) properties.Add(Property("static", Bool(true)));
        if (isStatic && events.Count > 0) properties.Add(Property("interactive", Bool(true)));
        // `modifiers` is a runtime-only field and must be absent when no
        // decorator was authored.  Emitting an empty object sends an unknown
        // attribute to every native element.
        if (modifiers.Count > 0) properties.Add(Property("modifiers", Object(modifiers)));
        return Object(properties);
    }

    private JsExpression ClassValue(AttrValue value, bool itemScope)
    {
        if (value is LiteralValue literal) return Array(literal.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(String).ToArray());
        var declaration = new JsVariableStatement("var", [new JsVariableDeclarator("v", AttrValueToExpression(value, itemScope), 0, 0)], 0, 0);
        var condition = new JsBinaryExpression("===", new JsUnaryExpression("typeof", Id("v"), 0, 0), String("string"), 0, 0);
        var split = Call(Member(Id("v"), "split"), [String(" ")]);
        var trim = Function(["item"], [new JsReturnStatement(Call(Member(Id("item"), "trim"), []), 0, 0)]);
        var normalized = Call(Member(Call(Member(split, "map"), [trim]), "filter"), [Id("Boolean")]);
        return Function([], [declaration, new JsReturnStatement(new JsConditionalExpression(condition, normalized, Id("v"), 0, 0), 0, 0)]);
    }

    private JsExpression StyleValue(AttrValue value, bool itemScope)
        => Function([], [new JsReturnStatement(Call(Member(Id("global"), "$translateStyle$"), [AttrValueToExpression(value, itemScope)]), 0, 0)]);

    private JsExpression ModelValue(AttrValue value, bool itemScope)
    {
        var target = value switch
        {
            BindingValue binding => BindingExpression(binding, itemScope),
            ExprValue expression => RewriteExpression(ParseAuthorExpression(expression.Expr), itemScope || expression.ItemScope),
            _ => AttrValueToExpression(value, itemScope)
        };
        return Object([
            Property("value", Function([], [new JsReturnStatement(target, 0, 0)])),
            Property("callback", Function(["evt"], [new JsExpressionStatement(new JsAssignmentExpression("=", target, Member(Id("evt"), "detail"), 0, 0), 0, 0)]))
        ]);
    }

    private static string EventName(string name, bool isComponent)
    {
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && name.Length > 2)
            return name[2..].ToLowerInvariant();
        return name.ToLowerInvariant();
    }
    private static JsObjectExpression Decorators(IReadOnlyList<string>? decorators, string target)
        => decorators is { Count: > 0 }
            ? Object([Property(target, Object(decorators.Select(decorator => Property(ToCamelCase(decorator), Bool(true))).ToArray()))])
            : Object([]);

    private JsExpression EventValue(AttrValue value)
    {
        var raw = value switch { LiteralValue literal => literal.Text.Trim(), BindingValue binding => binding.Path, ExprValue expression => expression.Expr.Trim(), _ => "" };
        if (_inlineEvents.TryGetValue(raw, out var substituted) && !ReferenceEquals(substituted, value))
            return EventValue(substituted);
        if (raw.Length == 0) return Function(["evt"], []);
        if (raw.Contains("=>", StringComparison.Ordinal) || raw.TrimStart().StartsWith("function", StringComparison.Ordinal)) return ParseAuthorExpression(raw);
        var open = raw.IndexOf('('); JsExpression callee; IReadOnlyList<JsExpression> args;
        if (open < 0) { callee = RewriteExpression(ParseAuthorExpression(raw), false); args = [Id("evt")]; }
        else { var close = raw.LastIndexOf(')'); callee = RewriteExpression(ParseAuthorExpression(raw[..open].Trim()), false); args = close <= open ? [Id("evt")] : ParseCallArguments(raw[(open + 1)..close]).Append(Id("evt")).ToArray(); }
        return Function(["evt"], [new JsReturnStatement(Call(callee, args), 0, 0)]);
    }

    private JsExpression AttrValueToExpression(AttrValue value, bool itemScope) => value switch
    {
        LiteralValue literal => String(literal.Text), BindingValue binding => BindingExpression(binding, itemScope),
        ExprValue expression => RewriteExpression(ParseAuthorExpression(expression.Expr), itemScope || expression.ItemScope), _ => String("")
    };
    private JsExpression AttributeValue(AttrValue value, bool itemScope)
        => value is LiteralValue ? AttrValueToExpression(value, itemScope)
            : Function([], [new JsReturnStatement(AttrValueToExpression(value, itemScope), 0, 0)]);
    private JsExpression ForceDynamicValue(AttrValue value, bool itemScope)
        => Function([], [new JsReturnStatement(AttrValueToExpression(value, itemScope), 0, 0)]);
    private JsExpression ResourceValue(string? elementTag, string attributeName, AttrValue value, bool itemScope)
    {
        if (value is not LiteralValue literal || !IsResourceAttribute(elementTag, attributeName) || _templatePath is null || _sourceRootPath is null)
        {
            return AttributeValue(value, itemScope);
        }
        if (attributeName.Equals("alt", StringComparison.OrdinalIgnoreCase) && literal.Text.Equals("blank", StringComparison.OrdinalIgnoreCase)) return String(literal.Text);
        var source = literal.Text;
        if (source.Length == 0 || IsAbsoluteResource(source)) return String(source);
        var templateDirectory = System.IO.Path.GetDirectoryName(_templatePath);
        if (string.IsNullOrEmpty(templateDirectory)) return String(source);
        var resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(templateDirectory, source));
        var sourceRoot = System.IO.Path.GetFullPath(_sourceRootPath);
        if (!File.Exists(resolved))
            _diagnostics?.Error($"resource '{source}' does not exist (resolved to '{resolved}')", value.Position);
        if (resolved.Equals(sourceRoot, StringComparison.Ordinal) || resolved.StartsWith(sourceRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return String("/" + System.IO.Path.GetRelativePath(sourceRoot, resolved).Replace(System.IO.Path.DirectorySeparatorChar, '/'));
        return Call(Id("require"), [String(source)]);
    }
    private static bool IsResourceAttribute(string? elementTag, string attributeName)
    {
        if (elementTag is null) return false;
        if (attributeName.Equals("alt", StringComparison.OrdinalIgnoreCase))
            return elementTag.Equals("image", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("img", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("video", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("maml", StringComparison.OrdinalIgnoreCase);
        return (attributeName.Equals("source", StringComparison.OrdinalIgnoreCase) || attributeName.Equals("src", StringComparison.OrdinalIgnoreCase))
            && (elementTag.Equals("image", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("img", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("video", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("maml", StringComparison.OrdinalIgnoreCase)
                || elementTag.Equals("lottie", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsAbsoluteResource(string value)
        => value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)
            || Uri.TryCreate(value, UriKind.Absolute, out _);
    private JsExpression BindingExpression(BindingValue binding, bool itemScope)
    {
        if (binding.Path.Length == 0) return Id("$item"); if (binding.Path.StartsWith('$')) return Id(binding.Path);
        var segments = binding.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0 && _inlineBindings.TryGetValue(segments[0], out var replacement))
            return segments.Skip(1).Aggregate(replacement, (current, segment) => Member(current, segment));
        if (_constTable.TryGet(binding.Path, out var constant) && constant.IsFoldable) return Folded(constant.Folded);
        return Path(itemScope || binding.ItemScope ? Id("$item") : Id("_vm_"), binding.Path);
    }
    private JsExpression ParseAuthorExpression(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return String("");
        if (_constTable.TryGet(source.Trim(), out var constant) && constant.IsFoldable) return Folded(constant.Folded);
        try { return ((JsExpressionStatement)JavaScriptSyntax.ParseScript("(" + source + ");", "<template-expression>").Body.Single()).Expression; }
        catch (JavaScriptCompilationException) { return String(""); }
    }
    private IReadOnlyList<JsExpression> ParseCallArguments(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return [];
        try { return ((JsArrayExpression)((JsExpressionStatement)JavaScriptSyntax.ParseScript("[" + source + "];", "<template-event-arguments>").Body.Single()).Expression).Elements.OfType<JsExpression>().Select(value => RewriteExpression(value, false)).ToArray(); }
        catch (JavaScriptCompilationException) { return []; }
    }

    private JsExpression RewriteExpression(JsExpression expression, bool itemScope, ISet<string>? bound = null)
    {
        bound ??= new HashSet<string>(StringComparer.Ordinal) { "$idx", "$item", "evt", "this" };
        return expression switch
        {
            JsIdentifierExpression identifier => RewriteIdentifier(identifier, itemScope, bound),
            JsUnaryExpression unary => unary with { Argument = RewriteExpression(unary.Argument, itemScope, bound) },
            JsUpdateExpression update => update with { Argument = RewriteExpression(update.Argument, itemScope, bound) },
            JsBinaryExpression binary => binary with { Left = RewriteExpression(binary.Left, itemScope, bound), Right = RewriteExpression(binary.Right, itemScope, bound) },
            JsAssignmentExpression assignment => assignment with { Left = RewriteExpression(assignment.Left, itemScope, bound), Right = RewriteExpression(assignment.Right, itemScope, bound) },
            JsConditionalExpression conditional => conditional with { Test = RewriteExpression(conditional.Test, itemScope, bound), Consequent = RewriteExpression(conditional.Consequent, itemScope, bound), Alternate = RewriteExpression(conditional.Alternate, itemScope, bound) },
            JsMemberExpression member => member with { Object = RewriteExpression(member.Object, itemScope, bound), Property = member.Computed ? RewriteExpression(member.Property, itemScope, bound) : member.Property },
            JsCallExpression call => call with { Callee = RewriteExpression(call.Callee, itemScope, bound), Arguments = call.Arguments.Select(argument => RewriteExpression(argument, itemScope, bound)).ToArray() },
            JsNewExpression @new => @new with { Callee = RewriteExpression(@new.Callee, itemScope, bound), Arguments = @new.Arguments.Select(argument => RewriteExpression(argument, itemScope, bound)).ToArray() },
            JsArrayExpression array => array with { Elements = array.Elements.Select(item => item is null ? null : RewriteExpression(item, itemScope, bound)).ToArray() },
            JsObjectExpression obj => obj with { Properties = obj.Properties.Select(property =>
            {
                var value = RewriteExpression(property.Value, itemScope, bound);
                var shorthand = property.Shorthand && value is JsIdentifierExpression { Name: var name } && name == property.Key;
                return property with { Value = value, Shorthand = shorthand, ComputedKey = property.ComputedKey is null ? null : RewriteExpression(property.ComputedKey, itemScope, bound) };
            }).ToArray() },
            JsSpreadExpression spread => spread with { Argument = RewriteExpression(spread.Argument, itemScope, bound) },
            JsSequenceExpression sequence => sequence with { Expressions = sequence.Expressions.Select(item => RewriteExpression(item, itemScope, bound)).ToArray() },
            JsAwaitExpression awaitExpression => awaitExpression with { Argument = RewriteExpression(awaitExpression.Argument, itemScope, bound) },
            JsYieldExpression yieldExpression => yieldExpression with { Argument = yieldExpression.Argument is null ? null : RewriteExpression(yieldExpression.Argument, itemScope, bound) },
            JsFunctionExpression function => RewriteFunction(function, itemScope, bound), _ => expression
        };
    }
    private JsExpression RewriteIdentifier(JsIdentifierExpression identifier, bool itemScope, ISet<string> bound)
    {
        var name = identifier.Name;
        if (bound.Contains(name) || Reserved.Contains(name) || Globals.Contains(name)) return identifier;
        if (_inlineBindings.TryGetValue(name, out var replacement)) return replacement;
        if (_constTable.TryGet(name, out var constant)) return constant.IsFoldable ? Folded(constant.Folded) : identifier;
        if (_moduleBindings.Contains(name)) return identifier;
        return Member(itemScope ? Id("$item") : Id("_vm_"), name);
    }
    private JsFunctionExpression RewriteFunction(JsFunctionExpression function, bool itemScope, ISet<string> bound)
    {
        var local = new HashSet<string>(bound, StringComparer.Ordinal); foreach (var parameter in function.Parameters) local.Add(parameter);
        return function with { Body = function.Body with { Body = RewriteStatements(function.Body.Body, itemScope, local) } };
    }
    private IReadOnlyList<JsStatement> RewriteStatements(IReadOnlyList<JsStatement> statements, bool itemScope, ISet<string> bound)
    {
        var output = new List<JsStatement>(statements.Count);
        foreach (var statement in statements) output.Add(RewriteStatement(statement, itemScope, bound));
        return output;
    }
    private JsStatement RewriteStatement(JsStatement statement, bool itemScope, ISet<string> bound) => statement switch
    {
        JsExpressionStatement expression => expression with { Expression = RewriteExpression(expression.Expression, itemScope, bound) },
        JsReturnStatement value => value with { Argument = value.Argument is null ? null : RewriteExpression(value.Argument, itemScope, bound) },
        JsVariableStatement variable => RewriteVariable(variable, itemScope, bound),
        JsBlockStatement block => block with { Body = RewriteStatements(block.Body, itemScope, new HashSet<string>(bound, StringComparer.Ordinal)) }, _ => statement
    };
    private JsVariableStatement RewriteVariable(JsVariableStatement variable, bool itemScope, ISet<string> bound)
    {
        var declarations = variable.Declarations.Select(declaration => declaration with { Initializer = declaration.Initializer is null ? null : RewriteExpression(declaration.Initializer, itemScope, bound) }).ToArray();
        foreach (var declaration in declarations) bound.Add(declaration.Name);
        return variable with { Declarations = declarations };
    }

    private static JsExpression AndAll(IEnumerable<JsExpression> expressions, JsExpression empty) => expressions.Aggregate(empty, (left, right) => new JsBinaryExpression("&&", left, right, 0, 0));
    private static JsExpression Not(JsExpression expression) => new JsUnaryExpression("!", expression, 0, 0);
    private static bool TryBoolean(JsExpression expression, out bool value)
    {
        if (expression is JsLiteralExpression { Raw: "true" }) { value = true; return true; }
        if (expression is JsLiteralExpression { Raw: "false" }) { value = false; return true; }
        value = false;
        return false;
    }
    private static JsObjectExpression ParseInlineStyle(string css)
    {
        var properties = new List<JsObjectProperty>();
        foreach (var declaration in css.Split(';', StringSplitOptions.RemoveEmptyEntries)) { var colon = declaration.IndexOf(':'); if (colon < 0) continue; var name = declaration[..colon].Trim(); var value = declaration[(colon + 1)..].Trim(); if (name.Length == 0 || value.Length == 0) continue; properties.Add(Property(ToCamelCase(name), double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Regex.IsMatch(value, @"^-?\d+(\.\d+)?$") ? Number(number) : String(value))); }
        return Object(properties);
    }
    private static string ToCamelCase(string value) { var output = Regex.Replace(value, @"-([a-zA-Z])", match => match.Groups[1].Value.ToUpperInvariant()); return output.Length == 0 ? output : char.ToLowerInvariant(output[0]) + output[1..]; }
    private static JsIdentifierExpression Id(string name) => new(name, 0, 0);
    private static JsLiteralExpression String(string value) => new(value, JavaScriptTokenKind.String, 0, 0);
    private static JsLiteralExpression Number(double value) => new(value.ToString(CultureInfo.InvariantCulture), JavaScriptTokenKind.Number, 0, 0);
    private static JsLiteralExpression Bool(bool value) => new(value ? "true" : "false", JavaScriptTokenKind.Identifier, 0, 0);
    private static JsLiteralExpression Folded(object? value) => value switch { null => new JsLiteralExpression("null", JavaScriptTokenKind.Identifier, 0, 0), string text => String(text), bool boolean => Bool(boolean), double number => Number(number), int integer => Number(integer), _ => String(value.ToString() ?? "") };
    private static JsMemberExpression Member(JsExpression target, string property) => new(target, Id(property), false, 0, 0);
    private static JsExpression Path(JsExpression target, string path) => path.Split('.', StringSplitOptions.RemoveEmptyEntries).Aggregate(target, (current, property) => Member(current, property));
    private static JsCallExpression Call(JsExpression callee, IReadOnlyList<JsExpression> arguments) => new(callee, arguments, 0, 0);
    private static JsArrayExpression Array(IReadOnlyList<JsExpression> values) => new(values, 0, 0);
    private static JsObjectExpression Object(IReadOnlyList<JsObjectProperty> values) => new(values, 0, 0);
    private static JsObjectProperty Property(string name, JsExpression value) => new(name, value, false, 0, 0);
    private static JsFunctionExpression Function(IReadOnlyList<string> parameters, IReadOnlyList<JsStatement> body) => new(null, parameters, new JsBlockStatement(body, 0, 0), false, false, 0, 0);
}
