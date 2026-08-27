using System.Text.RegularExpressions;
using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.Lsp;

/// <summary>Language-aware operations shared by the JSON-RPC host and tests.</summary>
public sealed class WxamlLanguageService
{
    private static readonly string[] Elements = [
        "Page", "Component", "Import", "Div", "Stack", "Text", "Span", "Label", "Image", "Video", "Lottie",
        "Input", "Textarea", "Slider", "Switch", "Picker", "Scroll", "List", "ItemTemplate", "If", "ElseIf", "Else",
        "Swiper", "Tabs", "Map", "Canvas", "Progress", "Page.Styles", "Component.Styles", "Style", "Setter", "Media"
    ];

    private static readonly string[] Attributes = [
        "x:Class", "Class", "Style", "Text", "Source", "Value", "Model", "Const", "Click", "LongPress",
        "ItemsSource", "Key", "Test", "Name", "Source", "Selector", "Property", "Query"
    ];

    private static readonly IReadOnlyDictionary<string, string> Documentation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Page"] = "WXAML page root. It owns page content, imports, and Page.Styles.",
        ["Component"] = "Reusable WXAML component root. Import it before using its name in a template.",
        ["Import"] = "Declares a component import. Name is the local component name; Source is a relative .wxaml path.",
        ["List"] = "Renders an ItemsSource collection using exactly one ItemTemplate child.",
        ["If"] = "Renders its children when Test evaluates to true. ElseIf and Else must immediately follow it.",
        ["Style"] = "Declares a style rule. Use Selector and one or more Setter children.",
        ["Binding"] = "Reads a dot-separated path from the current data context.",
        ["Expr"] = "Evaluates a JavaScript expression in the current data context.",
        ["Const"] = "Asserts that this subtree has no runtime bindings, events, control flow, or component instances.",
        ["ItemsSource"] = "The collection used by List.",
        ["ItemTemplate"] = "The single root template used for each List item.",
        ["Model"] = "Two-way binding target. It must be assignable.",
    };

    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string uri, string text)
    {
        var xmlDiagnostics = GetXmlSyntaxDiagnostics(text);
        if (xmlDiagnostics.Count > 0) return xmlDiagnostics;

        var sink = new DiagnosticSink();
        _ = new WxamlParser().Parse(text, PathFromUri(uri), sink);
        return sink.Diagnostics.Select(ToDiagnostic).ToArray();
    }

    public IReadOnlyList<LspCompletionItem> GetCompletions(string text, LspPosition position)
    {
        var offset = OffsetAt(text, position);
        var prefix = text[..Math.Min(offset, text.Length)];
        var tagStart = prefix.LastIndexOf('<');
        if (tagStart < 0 || tagStart < prefix.LastIndexOf('>')) return [];

        var tagText = prefix[(tagStart + 1)..];
        if (tagText.StartsWith('/'))
        {
            var closingPrefix = LastWord(tagText[1..]);
            return Complete(OpenElementNames(prefix[..tagStart]), closingPrefix, "Close WXAML element", 7,
                ReplacementRange(text, offset - closingPrefix.Length, offset), appendCloseBracket: true);
        }
        if (tagText.Contains('{') && tagText.LastIndexOf('{') > tagText.LastIndexOf('}'))
            return [new("Binding", 14, "Read from the current data context", "Binding "), new("Expr", 14, "Evaluate a JavaScript expression", "Expr ")];

        var trimmed = tagText.TrimStart();
        var firstWhitespace = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        if (firstWhitespace < 0)
            return Complete(Elements, trimmed, "WXAML element", 7,
                ReplacementRange(text, offset - trimmed.Length, offset), appendCloseBracket: true);

        var currentPrefix = LastWord(tagText);
        var usedAttributes = Regex.Matches(tagText, "(?<name>[A-Za-z_:][A-Za-z0-9_.:-]*)\\s*=", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Complete(Attributes.Where(attribute => !usedAttributes.Contains(attribute)), currentPrefix, "WXAML attribute", 10,
            ReplacementRange(text, offset - currentPrefix.Length, offset));
    }

    public IReadOnlyList<LspColorInformation> GetDocumentColors(string text)
    {
        var colors = new List<LspColorInformation>();
        foreach (Match match in Regex.Matches(text, "#[0-9a-fA-F]{3}(?:[0-9a-fA-F]{3})?\\b"))
        {
            var hex = match.Value[1..];
            var red = HexComponent(hex, 0);
            var green = HexComponent(hex, hex.Length == 3 ? 1 : 2);
            var blue = HexComponent(hex, hex.Length == 3 ? 2 : 4);
            var start = PositionAt(text, match.Index);
            var end = PositionAt(text, match.Index + match.Length);
            colors.Add(new LspColorInformation(new LspRange(start, end), new LspColor(red / 255d, green / 255d, blue / 255d)));
        }
        return colors;
    }

    public string? GetHover(string text, LspPosition position)
    {
        var scriptSymbol = ScriptSymbolAt(text, OffsetAt(text, position));
        if (scriptSymbol is not null)
        {
            return scriptSymbol.Value.Kind == ScriptSymbolKind.Event
                ? $"WXAML event handler `{scriptSymbol.Value.Name}`. Ctrl/Cmd-click to open the component JavaScript method."
                : $"WXAML binding `{scriptSymbol.Value.Name}`. Ctrl/Cmd-click to open its page/component JavaScript declaration.";
        }

        var styleReference = StyleReferenceAt(text, OffsetAt(text, position));
        if (styleReference is not null)
        {
            return styleReference.Value.Kind switch
            {
                StyleReferenceKind.Selector when styleReference.Value.Prefix == '.' => $"WXAML style selector `Class={styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to its class use.",
                StyleReferenceKind.Selector when styleReference.Value.Prefix == '#' => $"WXAML style selector `Id={styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to its ID use.",
                StyleReferenceKind.Selector => $"WXAML element selector `{styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to matching elements.",
                StyleReferenceKind.Class => $"WXAML class `{styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to its style selector.",
                StyleReferenceKind.Id => $"WXAML ID `{styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to its style selector.",
                StyleReferenceKind.Element => $"WXAML element `{styleReference.Value.Name}`. Ctrl/Cmd-click to navigate to its style selector.",
                _ => null
            };
        }

        var word = WordAt(text, position);
        if (word is null) return null;
        if (Documentation.TryGetValue(word, out var documentation)) return documentation;
        return word.StartsWith("on", StringComparison.Ordinal) && word.Length > 2
            ? "Component event callback. The component receives it as a callable prop."
            : null;
    }

    public LspLocation? GetDefinition(string uri, string text, LspPosition position)
    {
        var offset = OffsetAt(text, position);
        var scriptSymbol = ScriptSymbolAt(text, offset);
        if (scriptSymbol is not null)
        {
            var scriptTarget = FindScriptSymbol(uri, scriptSymbol.Value);
            if (scriptTarget is not null) return scriptTarget;
        }
        var styleTarget = FindStyleTarget(text, offset);
        if (styleTarget is not null) return new LspLocation(uri, RangeAt(text, styleTarget.Value.Start, styleTarget.Value.Length));
        var styleReference = StyleReferenceAt(text, offset);
        if (styleReference is not null)
        {
            var workspaceTarget = FindWorkspaceStyleTargets(uri, styleReference.Value).FirstOrDefault();
            if (workspaceTarget is not null) return workspaceTarget;
        }

        var word = WordAt(text, position);
        if (string.IsNullOrEmpty(word)) return null;

        foreach (Match match in Regex.Matches(text, "<Import\\s+[^>]*\\bName\\s*=\\s*[\\\"'](?<name>[A-Za-z_$][A-Za-z0-9_$]*)[\\\"'][^>]*\\b(?:Source|src)\\s*=\\s*[\\\"'](?<source>[^\\\"']+)[\\\"'][^>]*/?>", RegexOptions.IgnoreCase))
        {
            if (!string.Equals(match.Groups["name"].Value, word, StringComparison.OrdinalIgnoreCase)) continue;
            var source = match.Groups["source"].Value;
            var sourcePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(PathFromUri(uri))!, source));
            var targetUri = new Uri(sourcePath).AbsoluteUri;
            return new LspLocation(targetUri, new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)));
        }

        return null;
    }

    public IReadOnlyList<LspLocation> GetReferences(string uri, string text, LspPosition position)
    {
        var reference = StyleReferenceAt(text, OffsetAt(text, position));
        if (reference is null) return [];

        var locations = new List<LspLocation>();
        AddStyleTargets(locations, uri, text, reference.Value);
        foreach (var document in WorkspaceDocuments(uri))
            AddStyleTargets(locations, document.Uri, document.Text, reference.Value);
        return locations.Distinct().ToArray();
    }

    public LspRange? GetNavigationOrigin(string text, LspPosition position)
    {
        var offset = OffsetAt(text, position);
        var attribute = AttributeValueAt(text, offset);
        if (attribute is not null)
        {
            var token = SelectorTokenAt(attribute.Value.Text, attribute.Value.Start, offset,
                IsStyleTargetAttribute(text, attribute.Value.Start, attribute.Value.Name));
            if (token is not null) return RangeAt(text, token.Value.Start, token.Value.Length);
        }
        var element = ElementAt(text, offset);
        if (element is not null) return RangeAt(text, element.Value.Start, element.Value.Length);

        var start = offset;
        while (start > 0 && IsWordCharacter(text[start - 1])) start--;
        var end = offset;
        while (end < text.Length && IsWordCharacter(text[end])) end++;
        return start == end ? null : RangeAt(text, start, end - start);
    }

    private static (int Start, int Length)? FindStyleTarget(string text, int offset)
    {
        var reference = StyleReferenceAt(text, offset);
        return reference is null ? null : FindStyleTarget(text, reference.Value);
    }

    private static ScriptSymbol? ScriptSymbolAt(string text, int offset)
    {
        var attribute = AttributeAt(text, offset);
        if (attribute is null) return null;
        if (attribute.Value.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase)
            && IsIdentifier(attribute.Value.Value)
            && offset >= attribute.Value.ValueStart && offset <= attribute.Value.ValueStart + attribute.Value.Value.Length)
            return new ScriptSymbol(ScriptSymbolKind.Event, attribute.Value.Value);

        if (!TryMarkupExtension(attribute.Value.Value, out var kind, out var expression, out var expressionStart)) return null;
        if (offset < attribute.Value.ValueStart + expressionStart || offset > attribute.Value.ValueStart + expressionStart + expression.Length) return null;
        if (kind == "Binding")
        {
            var root = expression.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return root is not null && IsIdentifier(root) ? new ScriptSymbol(ScriptSymbolKind.Binding, root) : null;
        }
        var identifier = IdentifierAt(expression, offset - attribute.Value.ValueStart - expressionStart);
        return identifier is null ? null : new ScriptSymbol(ScriptSymbolKind.Expression, identifier);
    }

    private static AttributeSpan? AttributeAt(string text, int offset)
    {
        var index = 0;
        while (index < text.Length)
        {
            if (text[index++] != '<') continue;
            if (index < text.Length && (text[index] is '/' or '!' or '?')) continue;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            while (index < text.Length && IsMarkupNameCharacter(text[index])) index++;
            while (index < text.Length && text[index] != '>')
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length || text[index] == '>') break;
                var nameStart = index;
                while (index < text.Length && IsMarkupNameCharacter(text[index])) index++;
                if (index == nameStart) { index++; continue; }
                var name = text[nameStart..index];
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length || text[index] != '=') continue;
                index++;
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index >= text.Length || text[index] is not ('\'' or '\"')) continue;
                var quote = text[index++];
                var valueStart = index;
                while (index < text.Length && text[index] != quote) index++;
                var valueEnd = index;
                if (offset >= valueStart && offset <= valueEnd)
                    return new AttributeSpan(name, text[valueStart..valueEnd], valueStart);
                if (index < text.Length) index++;
            }
        }
        return null;
    }

    private static bool TryMarkupExtension(string value, out string kind, out string expression, out int expressionStart)
    {
        kind = ""; expression = ""; expressionStart = 0;
        if (value.Length < 2 || value[0] != '{' || value[^1] != '}') return false;
        var cursor = 1;
        while (cursor < value.Length - 1 && char.IsWhiteSpace(value[cursor])) cursor++;
        var kindStart = cursor;
        while (cursor < value.Length - 1 && char.IsLetter(value[cursor])) cursor++;
        kind = value[kindStart..cursor];
        if (!kind.Equals("Binding", StringComparison.OrdinalIgnoreCase) && !kind.Equals("Expr", StringComparison.OrdinalIgnoreCase)) return false;
        while (cursor < value.Length - 1 && char.IsWhiteSpace(value[cursor])) cursor++;
        expressionStart = cursor;
        var end = value.Length - 1;
        while (end > cursor && char.IsWhiteSpace(value[end - 1])) end--;
        expression = value[cursor..end];
        return expression.Length > 0;
    }

    private static bool IsIdentifier(string value)
        => value.Length > 0 && (char.IsLetter(value[0]) || value[0] is '_' or '$')
           && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '_' or '$');

    private static bool IsMarkupNameCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or ':' or '-' or '.';

    private static LspLocation? FindScriptSymbol(string wxamlUri, ScriptSymbol symbol)
    {
        var scriptPath = Path.ChangeExtension(PathFromUri(wxamlUri), ".js");
        if (!File.Exists(scriptPath)) return null;
        var script = File.ReadAllText(scriptPath);
        var astTarget = FindAstScriptSymbol(script, scriptPath, symbol.Name);
        if (astTarget is not null) return new LspLocation(new Uri(scriptPath).AbsoluteUri, astTarget);
        return null;
    }

    private static string? IdentifierAt(string expression, int offset)
    {
        try
        {
            var ast = JavaScriptSyntax.ParseScript(expression + ";", "wxaml-expr.js");
            foreach (var identifier in Identifiers(ast))
            {
                var start = OffsetFor(expression, identifier.Line, identifier.Column);
                if (offset >= start && offset <= start + identifier.Name.Length) return identifier.Name;
            }
        }
        catch (JavaScriptCompilationException) { }
        return null;
    }

    private static LspRange? FindAstScriptSymbol(string script, string fileName, string name)
    {
        try
        {
            var ast = JavaScriptSyntax.ParseModule(script, fileName);
            foreach (var statement in ast.Body)
            {
                if (statement is JsVariableStatement variable)
                {
                    var declaration = variable.Declarations.FirstOrDefault(item => item.Name == name);
                    if (declaration is not null) return NodeRange(script, declaration, name.Length);
                }
                if (statement is JsFunctionStatement function && function.Name == name)
                    return NodeRange(script, function, name.Length);
                if (statement is JsExportStatement { Declaration: JsFunctionStatement exportedFunction } && exportedFunction.Name == name)
                    return NodeRange(script, exportedFunction, name.Length);
                if (statement is JsExportStatement { IsDefault: true, Declaration: JsExpressionStatement { Expression: JsObjectExpression root } })
                {
                    var match = FindObjectMember(root, name);
                    if (match is not null) return NodeRange(script, match, name.Length);
                }
            }
        }
        catch (JavaScriptCompilationException) { }
        return null;
    }

    private static JsObjectProperty? FindObjectMember(JsObjectExpression root, string name)
    {
        foreach (var property in root.Properties)
        {
            if (property.Key == name) return property;
            if (property.Value is JsObjectExpression members)
            {
                var nested = FindObjectMember(members, name);
                if (nested is not null) return nested;
            }
            if (property.Key == "props" && property.Value is JsArrayExpression props)
            {
                var literal = props.Elements.OfType<JsLiteralExpression>().FirstOrDefault(item => item.Raw.Trim('\'', '\"') == name);
                if (literal is not null) return property;
            }
        }
        return null;
    }

    private static IEnumerable<JsIdentifierExpression> Identifiers(JsAstNode node) => node switch
    {
        JsAstProgram program => program.Body.SelectMany(Identifiers),
        JsExpressionStatement statement => Identifiers(statement.Expression),
        JsIdentifierExpression identifier => [identifier],
        JsUnaryExpression expression => Identifiers(expression.Argument),
        JsUpdateExpression expression => Identifiers(expression.Argument),
        JsBinaryExpression expression => Identifiers(expression.Left).Concat(Identifiers(expression.Right)),
        JsAssignmentExpression expression => Identifiers(expression.Left).Concat(Identifiers(expression.Right)),
        JsConditionalExpression expression => Identifiers(expression.Test).Concat(Identifiers(expression.Consequent)).Concat(Identifiers(expression.Alternate)),
        JsMemberExpression expression => Identifiers(expression.Object).Concat(Identifiers(expression.Property)),
        JsCallExpression expression => Identifiers(expression.Callee).Concat(expression.Arguments.SelectMany(Identifiers)),
        JsNewExpression expression => Identifiers(expression.Callee).Concat(expression.Arguments.SelectMany(Identifiers)),
        JsArrayExpression expression => expression.Elements.OfType<JsExpression>().SelectMany(Identifiers),
        JsObjectExpression expression => expression.Properties.SelectMany(property => Identifiers(property.Value)),
        JsSequenceExpression expression => expression.Expressions.SelectMany(Identifiers),
        JsAwaitExpression expression => Identifiers(expression.Argument),
        JsSpreadExpression expression => Identifiers(expression.Argument),
        _ => [],
    };

    private static LspRange NodeRange(string source, JsAstNode node, int length) =>
        RangeAt(source, OffsetFor(source, node.Line, node.Column), length);

    private static int OffsetFor(string source, int oneBasedLine, int oneBasedColumn)
    {
        var line = 1;
        var offset = 0;
        while (offset < source.Length && line < oneBasedLine) if (source[offset++] == '\n') line++;
        return Math.Min(source.Length, offset + Math.Max(0, oneBasedColumn - 1));
    }

    private static StyleReference? StyleReferenceAt(string text, int offset)
    {
        var attribute = AttributeValueAt(text, offset);
        if (attribute is not null)
        {
            var token = SelectorTokenAt(attribute.Value.Text, attribute.Value.Start, offset,
                IsStyleTargetAttribute(text, attribute.Value.Start, attribute.Value.Name));
            if (token is null) return null;

            if (IsStyleTargetAttribute(text, attribute.Value.Start, attribute.Value.Name))
            {
                var prefix = attribute.Value.Name.Equals("Class", StringComparison.OrdinalIgnoreCase) ? '.'
                    : attribute.Value.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) ? '#'
                    : '\0';
                return new StyleReference(StyleReferenceKind.Selector, prefix, token.Value.Name);
            }
            if (attribute.Value.Name.Equals("Class", StringComparison.OrdinalIgnoreCase))
                return new StyleReference(StyleReferenceKind.Class, '\0', token.Value.Name);
            if (attribute.Value.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
                return new StyleReference(StyleReferenceKind.Id, '\0', token.Value.Name);
        }

        var element = ElementAt(text, offset);
        return element is null ? null : new StyleReference(StyleReferenceKind.Element, '\0', element.Value.Name);
    }

    private static (int Start, int Length)? FindStyleTarget(string text, StyleReference reference) => reference.Kind switch
    {
        StyleReferenceKind.Selector when reference.Prefix == '.' => FirstTarget(FindComponentAttributeTokens(text, "Class", reference.Name)),
        StyleReferenceKind.Selector when reference.Prefix == '#' => FirstTarget(FindComponentAttributeTokens(text, "Id", reference.Name)),
        StyleReferenceKind.Selector => FindElementToken(text, reference.Name),
        StyleReferenceKind.Class => FindStyleTargetToken(text, "Class", reference.Name),
        StyleReferenceKind.Id => FindStyleTargetToken(text, "Id", reference.Name),
        StyleReferenceKind.Element => FindStyleTargetToken(text, "Tag", reference.Name),
        _ => null
    };

    private static IEnumerable<LspLocation> FindWorkspaceStyleTargets(string uri, StyleReference reference)
    {
        foreach (var document in WorkspaceDocuments(uri))
        {
            var target = FindStyleTarget(document.Text, reference);
            if (target is not null) yield return new LspLocation(document.Uri, RangeAt(document.Text, target.Value.Start, target.Value.Length));
        }
    }

    private static void AddStyleTargets(List<LspLocation> locations, string uri, string text, StyleReference reference)
    {
        IEnumerable<(int Start, int Length)> targets = reference.Kind switch
        {
            StyleReferenceKind.Selector when reference.Prefix == '.' => FindComponentAttributeTokens(text, "Class", reference.Name),
            StyleReferenceKind.Selector when reference.Prefix == '#' => FindComponentAttributeTokens(text, "Id", reference.Name),
            StyleReferenceKind.Selector => FindElementTokens(text, reference.Name),
            StyleReferenceKind.Class => FindStyleTargetTokens(text, "Class", reference.Name),
            StyleReferenceKind.Id => FindStyleTargetTokens(text, "Id", reference.Name),
            StyleReferenceKind.Element => FindStyleTargetTokens(text, "Tag", reference.Name),
            _ => []
        };
        locations.AddRange(targets.Select(target => new LspLocation(uri, RangeAt(text, target.Start, target.Length))));
    }

    private static IEnumerable<(string Uri, string Text)> WorkspaceDocuments(string uri)
    {
        var filePath = PathFromUri(uri);
        var directory = Path.GetDirectoryName(filePath);
        if (directory is null) yield break;
        var root = directory;
        var candidate = directory;
        while (true)
        {
            if (Directory.Exists(Path.Combine(candidate, ".idea")) || File.Exists(Path.Combine(candidate, "manifest.yaml")))
            {
                root = candidate;
                break;
            }
            var parent = Directory.GetParent(candidate);
            if (parent is null) break;
            candidate = parent.FullName;
        }
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFiles(root, "*.wxaml", SearchOption.AllDirectories).Take(500).ToArray(); }
        catch (IOException) { yield break; }
        foreach (var path in paths)
        {
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)) continue;
            string content;
            try { content = File.ReadAllText(path); }
            catch (IOException) { continue; }
            yield return (new Uri(path).AbsoluteUri, content);
        }
    }

    private static (string Name, string Text, int Start)? AttributeValueAt(string text, int offset)
    {
        foreach (Match match in Regex.Matches(text, "(?<name>Class|Id|Tag)\\s*=\\s*(?<quote>[\\\"'])(?<value>[^\\\"']*)\\k<quote>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var value = match.Groups["value"];
            if (offset >= value.Index && offset <= value.Index + value.Length)
                return (match.Groups["name"].Value, value.Value, value.Index);
        }
        return null;
    }

    private static (char Prefix, string Name, int Start, int Length)? SelectorTokenAt(string value, int valueStart, int offset, bool allowPrefix)
    {
        foreach (var token in SelectorTokens(value, valueStart))
        {
            if (offset < token.Start || offset > token.Start + token.Length) continue;
            if (!allowPrefix && token.Prefix != '\0') continue;
            return token;
        }
        return null;
    }

    private static (int Start, int Length)? FindAttributeToken(string text, string attributeName, string name)
        => FirstTarget(FindAttributeTokens(text, attributeName, name));

    private static IEnumerable<(int Start, int Length)> FindAttributeTokens(string text, string attributeName, string name)
    {
        foreach (Match match in Regex.Matches(text, $"\\b{attributeName}\\s*=\\s*([\\\"'])(?<value>[^\\\"']*)\\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var value = match.Groups["value"];
            foreach (Match token in Regex.Matches(value.Value, "[A-Za-z_][A-Za-z0-9_-]*", RegexOptions.CultureInvariant))
                if (token.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return (value.Index + token.Index, token.Length);
        }
    }

    private static (int Start, int Length)? FindStyleTargetToken(string text, string attribute, string name)
        => FirstTarget(FindStyleTargetTokens(text, attribute, name));

    private static IEnumerable<(int Start, int Length)> FindStyleTargetTokens(string text, string attribute, string name)
        => FindAttributeTokens(text, attribute, name).Where(token => IsStyleTargetAttribute(text, token.Start, attribute));

    private static IEnumerable<(int Start, int Length)> FindComponentAttributeTokens(string text, string attribute, string name)
        => FindAttributeTokens(text, attribute, name).Where(token => !IsStyleTargetAttribute(text, token.Start, attribute));

    private static IEnumerable<(int Start, int Length)> FindSelectorTokens(string text, char prefix, string name)
    {
        foreach (Match match in Regex.Matches(text, "\\bSelector\\s*=\\s*([\\\"'])(?<value>[^\\\"']*)\\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            var value = match.Groups["value"];
            foreach (var token in SelectorTokens(value.Value, value.Index))
                if (token.Prefix == prefix && token.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    yield return (token.Start, token.Length);
        }
    }

    private static bool IsStyleTargetAttribute(string text, int offset, string attributeName)
    {
        if (attributeName is not ("Class" or "Id" or "Tag")) return false;
        var tagStart = text.LastIndexOf('<', Math.Max(0, offset - 1));
        if (tagStart < 0 || tagStart + 1 >= text.Length || text[tagStart + 1] == '/') return false;
        var nameStart = tagStart + 1;
        while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart])) nameStart++;
        const string style = "Style";
        return nameStart + style.Length <= text.Length &&
               text.AsSpan(nameStart, style.Length).Equals(style, StringComparison.OrdinalIgnoreCase) &&
               (nameStart + style.Length >= text.Length || char.IsWhiteSpace(text[nameStart + style.Length]) || text[nameStart + style.Length] is '>' or '/');
    }

    private static IEnumerable<(char Prefix, string Name, int Start, int Length)> SelectorTokens(string value, int valueStart)
    {
        var cursor = 0;
        while (cursor < value.Length)
        {
            while (cursor < value.Length && (char.IsWhiteSpace(value[cursor]) || value[cursor] == ',')) cursor++;
            var partStart = cursor;
            while (cursor < value.Length && value[cursor] != ',') cursor++;
            var partEnd = cursor;
            while (partStart < partEnd && char.IsWhiteSpace(value[partStart])) partStart++;
            while (partEnd > partStart && char.IsWhiteSpace(value[partEnd - 1])) partEnd--;
            if (partStart == partEnd) continue;

            var equals = value.IndexOf('=', partStart);
            char prefix;
            int nameStart;
            if (equals < 0 || equals >= partEnd)
            {
                prefix = '\0';
                nameStart = partStart;
            }
            else
            {
                var kind = value[partStart..equals].Trim();
                prefix = kind.Equals("Class", StringComparison.OrdinalIgnoreCase) ? '.'
                    : kind.Equals("Id", StringComparison.OrdinalIgnoreCase) ? '#'
                    : kind.Equals("Tag", StringComparison.OrdinalIgnoreCase) ? '\0' : '\uffff';
                if (prefix == '\uffff') continue;
                nameStart = equals + 1;
                while (nameStart < partEnd && char.IsWhiteSpace(value[nameStart])) nameStart++;
            }
            var nameEnd = nameStart;
            while (nameEnd < partEnd && (char.IsLetterOrDigit(value[nameEnd]) || value[nameEnd] is '_' or '-')) nameEnd++;
            if (nameEnd > nameStart)
                yield return (prefix, value[nameStart..nameEnd], valueStart + nameStart, nameEnd - nameStart);
        }
    }

    private static (string Name, int Start, int Length)? ElementAt(string text, int offset)
    {
        foreach (Match match in Regex.Matches(text, "<\\s*(?!/)(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)", RegexOptions.CultureInvariant))
        {
            var name = match.Groups["name"];
            if (offset >= name.Index && offset <= name.Index + name.Length) return (name.Value, name.Index, name.Length);
        }
        return null;
    }

    private static (int Start, int Length)? FindElementToken(string text, string name)
        => FirstTarget(FindElementTokens(text, name));

    private static IEnumerable<(int Start, int Length)> FindElementTokens(string text, string name)
    {
        foreach (Match match in Regex.Matches(text, "<\\s*(?!/)(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)", RegexOptions.CultureInvariant))
        {
            var element = match.Groups["name"];
            if (element.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return (element.Index, element.Length);
        }
    }

    private static (int Start, int Length)? FirstTarget(IEnumerable<(int Start, int Length)> targets)
    {
        foreach (var target in targets) return target;
        return null;
    }

    private static LspRange RangeAt(string text, int start, int length) =>
        new(PositionAt(text, start), PositionAt(text, start + length));

    private enum StyleReferenceKind { Selector, Class, Id, Element }
    private readonly record struct StyleReference(StyleReferenceKind Kind, char Prefix, string Name);
    private enum ScriptSymbolKind { Binding, Event, Expression }
    private readonly record struct ScriptSymbol(ScriptSymbolKind Kind, string Name);
    private readonly record struct AttributeSpan(string Name, string Value, int ValueStart);

    private static LspDiagnostic ToDiagnostic(Diagnostic diagnostic)
    {
        var position = diagnostic.Position;
        var startLine = Math.Max(0, (position?.StartLine ?? 1) - 1);
        var startColumn = Math.Max(0, (position?.StartColumn ?? 1) - 1);
        var endLine = position?.EndLine > 0 ? position.EndLine - 1 : startLine;
        var endColumn = position?.EndColumn > 0 ? position.EndColumn - 1 : startColumn + 1;
        var severity = diagnostic.IsError ? 1 : diagnostic.IsWarning ? 2 : 3;
        return new LspDiagnostic(new LspRange(new LspPosition(startLine, startColumn), new LspPosition(endLine, Math.Max(startColumn + 1, endColumn))), severity, diagnostic.Message);
    }

    private static IReadOnlyList<LspCompletionItem> Complete(
        IEnumerable<string> candidates,
        string prefix,
        string detail,
        int kind,
        LspRange replacementRange,
        bool appendCloseBracket = false) =>
        candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => new LspCompletionItem(candidate, kind, detail, FilterText: prefix,
                TextEdit: new LspTextEdit(replacementRange, candidate + (appendCloseBracket ? ">" : ""))))
            .ToArray();

    private static LspRange ReplacementRange(string text, int startOffset, int endOffset) =>
        new(PositionAt(text, Math.Max(0, startOffset)), PositionAt(text, endOffset));

    private static IEnumerable<string> OpenElementNames(string text)
    {
        var stack = new Stack<string>();
        foreach (Match match in Regex.Matches(text, "<\\s*(?<closing>/)?\\s*(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)(?<body>[^<>]*)>", RegexOptions.CultureInvariant))
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["closing"].Success)
            {
                if (stack.Count > 0 && stack.Peek().Equals(name, StringComparison.OrdinalIgnoreCase)) stack.Pop();
            }
            else if (!match.Groups["body"].Value.TrimEnd().EndsWith('/')) stack.Push(name);
        }
        return stack;
    }

    private static string LastWord(string text)
    {
        var end = text.Length;
        while (end > 0 && !IsWordCharacter(text[end - 1]) && text[end - 1] != '-') end--;
        var start = end;
        while (start > 0 && (IsWordCharacter(text[start - 1]) || text[start - 1] is '-' or '.')) start--;
        return text[start..end];
    }

    private static IReadOnlyList<LspDiagnostic> GetXmlSyntaxDiagnostics(string text)
    {
        var stack = new Stack<(string Name, int Offset)>();
        for (var index = 0; index < text.Length;)
        {
            if (text[index] != '<') { index++; continue; }
            if (text.AsSpan(index).StartsWith("<!--"))
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (end < 0) return [XmlError(text, index, "unclosed XML comment; expected '-->'")];
                index = end + 3;
                continue;
            }
            if (index + 1 < text.Length && text[index + 1] is '!' or '?')
            {
                var end = text.IndexOf('>', index + 2);
                if (end < 0) return [XmlError(text, index, "unclosed XML declaration")];
                index = end + 1;
                continue;
            }

            var closing = index + 1 < text.Length && text[index + 1] == '/';
            var cursor = index + (closing ? 2 : 1);
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
            var nameStart = cursor;
            while (cursor < text.Length && (IsWordCharacter(text[cursor]) || text[cursor] is '.' or '-')) cursor++;
            if (cursor == nameStart) return [XmlError(text, index, "expected an XML element name after '<'")];
            var name = text[nameStart..cursor];

            char? quote = null;
            while (cursor < text.Length && (text[cursor] != '>' || quote is not null))
            {
                if (quote is null && text[cursor] is '\'' or '"') quote = text[cursor];
                else if (quote is not null && text[cursor] == quote) quote = null;
                cursor++;
            }
            if (cursor >= text.Length) return [XmlError(text, index, $"unclosed <{name}> tag; expected '>'")];
            if (quote is not null) return [XmlError(text, index, $"unclosed attribute quote in <{name}>")];

            var beforeClose = cursor - 1;
            while (beforeClose > nameStart && char.IsWhiteSpace(text[beforeClose])) beforeClose--;
            var selfClosing = !closing && text[beforeClose] == '/';
            if (closing)
            {
                if (stack.Count == 0) return [XmlError(text, index, $"unexpected closing tag </{name}>")];
                var open = stack.Pop();
                if (!open.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return [XmlError(text, index, $"mismatched closing tag </{name}>; expected </{open.Name}>")];
            }
            else if (!selfClosing) stack.Push((name, index));
            index = cursor + 1;
        }

        return stack.Count == 0 ? [] : [XmlError(text, stack.Peek().Offset, $"unclosed <{stack.Peek().Name}>; expected </{stack.Peek().Name}>")];
    }

    private static LspDiagnostic XmlError(string text, int offset, string message)
    {
        var before = text[..Math.Min(offset, text.Length)];
        var line = before.Count(character => character == '\n');
        var column = offset - (before.LastIndexOf('\n') + 1);
        return new LspDiagnostic(new LspRange(new LspPosition(line, column), new LspPosition(line, column + 1)), 1, $"XML: {message}");
    }

    private static int HexComponent(string hex, int index) =>
        Convert.ToInt32(hex.Length == 3 ? new string(hex[index], 2) : hex.Substring(index, 2), 16);

    private static LspPosition PositionAt(string text, int offset)
    {
        var prefix = text[..Math.Min(offset, text.Length)];
        var line = prefix.Count(character => character == '\n');
        return new LspPosition(line, offset - (prefix.LastIndexOf('\n') + 1));
    }

    private static int OffsetAt(string text, LspPosition position)
    {
        var line = 0;
        var offset = 0;
        while (offset < text.Length && line < position.Line)
        {
            if (text[offset++] == '\n') line++;
        }
        return Math.Min(text.Length, offset + Math.Max(0, position.Character));
    }

    private static string? WordAt(string text, LspPosition position)
    {
        var offset = OffsetAt(text, position);
        if (offset == text.Length && offset > 0) offset--;
        if (offset < 0 || offset >= text.Length || !IsWordCharacter(text[offset])) return null;
        var start = offset;
        var end = offset;
        while (start > 0 && IsWordCharacter(text[start - 1])) start--;
        while (end < text.Length && IsWordCharacter(text[end])) end++;
        return text[start..end];
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character is '_' or '$' or ':';
    private static string PathFromUri(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var value) && value.IsFile ? value.LocalPath : uri;
}
