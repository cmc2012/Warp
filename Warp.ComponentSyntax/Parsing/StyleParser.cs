using System.Text.RegularExpressions;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;

namespace Warp.ComponentSyntax.Parsing;

public sealed class StyleParser
{
    public UxStyleSheet Parse(string cssText, string filePath, DiagnosticSink sink)
    {
        if (string.IsNullOrWhiteSpace(cssText)) return new UxStyleSheet([]);

        var rules = new List<UxStyleRule>();
        var mediaRules = new List<UxMediaRule>();
        cssText = Regex.Replace(cssText, @"/\*.*?\*/", "", RegexOptions.Singleline);
        ParseRuleBlock(cssText, filePath, sink, rules, mediaRules, inMedia: false);
        return new UxStyleSheet(rules, mediaRules);
    }

    public IReadOnlyList<StyleSelector> ParseSelectors(string selText, string filePath, DiagnosticSink sink)
    {
        var list = new List<StyleSelector>();
        foreach (var part in selText.Split(','))
        {
            var s = part.Trim();
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Contains(' ') || s.Contains('>') || s.Contains('+') || s.Contains('~'))
            {
                sink.Warning($"descendant/combinator selector not supported: '{s}'", new SourcePosition(filePath, 1, 1));
                continue;
            }
            if (s == "*")
            {
                sink.Error("universal selector '*' not supported", new SourcePosition(filePath, 1, 1));
                continue;
            }
            if (s.StartsWith("."))
                list.Add(new StyleSelector(StyleSelectorKind.Class, s[1..]));
            else if (s.StartsWith("#"))
                list.Add(new StyleSelector(StyleSelectorKind.Id, s[1..]));
            else if (Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_-]*$"))
                list.Add(new StyleSelector(StyleSelectorKind.Tag, s));
            else
                sink.Error($"unsupported selector '{s}'", new SourcePosition(filePath, 1, 1));
        }
        return list;
    }

    public IReadOnlyList<StyleDeclaration> ParseDeclarations(IEnumerable<KeyValuePair<string, string>> declarations, string filePath, DiagnosticSink sink)
    {
        var list = new List<StyleDeclaration>();
        foreach (var declaration in declarations)
        {
            var property = ToCamelCase(declaration.Key.Trim());
            var value = declaration.Value.Trim();
            if (property.Length == 0 || value.Length == 0) continue;
            if (property == "boxSizing")
            {
                sink.Warning("style property 'box-sizing' is not supported by the target runtime", new SourcePosition(filePath, 1, 1));
                continue;
            }
            list.AddRange(TranslateDeclaration(property, value));
        }
        return list;
    }

    public string? NormalizeMediaCondition(string query, string filePath, DiagnosticSink sink)
    {
        var normalized = new List<string>();
        foreach (var part in SplitTopLevel(query, ','))
        {
            var item = part.Trim();
            if (item.Length == 0) continue;
            if (item.StartsWith('(')) item = "screen and " + item;
            if (!item.StartsWith("screen", StringComparison.OrdinalIgnoreCase))
            {
                sink.Error($"unsupported media type in '{part.Trim()}' (only screen is supported)", new SourcePosition(filePath, 1, 1));
                return null;
            }
            var features = Regex.Matches(item, @"\((?<name>[a-z-]+)\s*:\s*(?<value>[^)]+)\)");
            if (features.Count == 0)
            {
                sink.Error($"media query '{part.Trim()}' must contain a supported dimension feature", new SourcePosition(filePath, 1, 1));
                return null;
            }
            foreach (Match feature in features)
                if (feature.Groups["name"].Value is not ("min-width" or "max-width" or "min-height" or "max-height"))
                {
                    sink.Error($"unsupported media feature '{feature.Groups["name"].Value}'", new SourcePosition(filePath, 1, 1));
                    return null;
                }
            normalized.Add(Regex.Replace(item, @"\s+", " "));
        }
        return normalized.Count == 0 ? null : string.Join(',', normalized);
    }

    private void ParseRuleBlock(string css, string filePath, DiagnosticSink sink, List<UxStyleRule> rules, List<UxMediaRule> mediaRules, bool inMedia)
    {
        var index = 0;
        while (index < css.Length)
        {
            while (index < css.Length && char.IsWhiteSpace(css[index])) index++;
            if (index >= css.Length) break;
            var open = css.IndexOf('{', index);
            if (open < 0) { sink.Warning($"invalid style syntax '{css[index..].Trim()}'", new SourcePosition(filePath, 1, 1)); break; }
            var header = css[index..open].Trim();
            var close = FindClosingBrace(css, open);
            if (close < 0) { sink.Error($"unclosed style block '{header}'", new SourcePosition(filePath, 1, 1)); break; }
            var body = css[(open + 1)..close];
            index = close + 1;
            if (header.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
            {
                if (inMedia) { sink.Error("nested @media is not supported", new SourcePosition(filePath, 1, 1)); continue; }
                var condition = NormalizeMediaCondition(header[6..].Trim(), filePath, sink);
                if (condition is null) continue;
                var nestedRules = new List<UxStyleRule>();
                ParseRuleBlock(body, filePath, sink, nestedRules, [], inMedia: true);
                mediaRules.Add(new UxMediaRule(condition, nestedRules));
                continue;
            }
            if (header.StartsWith('@')) { sink.Warning($"unsupported style at-rule '{header}'", new SourcePosition(filePath, 1, 1)); continue; }
            var selectors = ParseSelectors(header, filePath, sink);
            var declarations = ParseCssDeclarations(body, filePath, sink);
            foreach (var selector in selectors) rules.Add(new UxStyleRule([selector], declarations));
        }
    }

    private IReadOnlyList<StyleDeclaration> ParseCssDeclarations(string body, string filePath, DiagnosticSink sink)
    {
        var declarations = new List<KeyValuePair<string, string>>();
        foreach (var item in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.IndexOf(':');
            if (colon < 0) { sink.Warning($"invalid declaration '{item.Trim()}'", new SourcePosition(filePath, 1, 1)); continue; }
            declarations.Add(new(item[..colon], item[(colon + 1)..]));
        }
        return ParseDeclarations(declarations, filePath, sink);
    }

    private static int FindClosingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string text, char delimiter)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == delimiter && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }

    // The device style ABI accepts individual box-edge properties.  Expand CSS
    // shorthands before emission, matching the reference transformer's output.
    private static IReadOnlyList<StyleDeclaration> TranslateDeclaration(string property, string value)
    {
        if (property is "margin" or "padding" or "borderWidth" or "borderColor")
            return ExpandEdges(property, value);
        if (property == "border") return ExpandBorder(value);
        return [new StyleDeclaration(property, ParseValue(value))];
    }

    private static IReadOnlyList<StyleDeclaration> ExpandEdges(string property, string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return [];
        string[] values = tokens.Length switch
        {
            1 => [tokens[0], tokens[0], tokens[0], tokens[0]],
            2 => [tokens[0], tokens[1], tokens[0], tokens[1]],
            3 => [tokens[0], tokens[1], tokens[2], tokens[1]],
            _ => [tokens[0], tokens[1], tokens[2], tokens[3]]
        };
        var parts = SplitCamel(property);
        var prefix = parts[0];
        var suffix = parts.Length == 1 ? "" : parts[1];
        var edges = new[] { "Top", "Right", "Bottom", "Left" };
        return edges.Select((edge, index) => new StyleDeclaration(prefix + edge + suffix, ParseValue(values[index]))).ToArray();
    }

    private static IReadOnlyList<StyleDeclaration> ExpandBorder(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string? width = null;
        string? style = null;
        string? color = null;
        foreach (var token in tokens)
        {
            if (token is "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset") style = token;
            else if (token is "thin" or "medium" or "thick" || Regex.IsMatch(token, @"^-?(\d+(\.\d+)?)(px|dp|%)$")) width = token;
            else color = token;
        }
        var output = new List<StyleDeclaration>();
        if (width is not null) output.AddRange(ExpandEdges("borderWidth", width));
        if (style is not null) output.Add(new StyleDeclaration("borderStyle", ParseValue(style)));
        if (color is not null) output.AddRange(ExpandEdges("borderColor", color));
        return output;
    }

    private static string[] SplitCamel(string value) => Regex.Split(value, "(?=[A-Z])");

    private static StyleValue ParseValue(string raw)
    {
        if (raw.Contains(' ') || raw.Contains(','))
            return new StringStyleValue(raw);
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && Regex.IsMatch(raw, @"^-?\d+(\.\d+)?$"))
            return new NumericStyleValue(n);
        if (raw.StartsWith("#"))
        {
            var hex = raw[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            return new ColorStyleValue($"#{hex.ToLowerInvariant()}");
        }
        if (raw.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            return new ColorStyleValue(raw);
        return new StringStyleValue(raw);
    }

    private static string ToCamelCase(string kebab)
        => Regex.Replace(kebab, @"-([a-z])", m => m.Groups[1].Value.ToUpperInvariant());
}
