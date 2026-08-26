using Warp.ComponentSyntax.Ast;
using Warp.JsCompiler.Frontend;
namespace Warp.ComponentCompiler.Translation;

public static class StyleTranslator
{
    public static JsExpression Translate(UxStyleSheet? sheet)
    {
        if (sheet is null || sheet.Rules.Count == 0) return Array([]);

        var rules = new List<JsExpression>();
        foreach (var rule in sheet.Rules)
            rules.Add(TranslateRule(rule));
        foreach (var media in sheet.MediaRules ?? [])
            foreach (var rule in media.Rules)
            {
                var translated = TranslateRule(rule);
                rules.Add(Array([Object([Property("condition", String(media.Condition))]), translated.Elements[0]!, translated.Elements[1]!]));
            }
        return Array(rules);
    }

    private static JsArrayExpression TranslateRule(UxStyleRule rule)
    {
        var selectors = Array(rule.Selectors.Select(selector => Array([Number((int)selector.Kind), String(selector.Name)])).ToArray());
        var declarations = Object(rule.Declarations.Select(declaration => Property(declaration.Property, Value(declaration.Value))).ToArray());
        return Array([selectors, declarations]);
    }

    private static JsExpression Value(StyleValue v)
        => v switch
        {
            NumericStyleValue n => Number(n.Number),
            StringStyleValue s => String(s.Text),
            ColorStyleValue c => String(c.Normalized),
            _ => String("")
        };

    private static JsArrayExpression Array(IReadOnlyList<JsExpression> values) => new(values, 0, 0);
    private static JsObjectExpression Object(IReadOnlyList<JsObjectProperty> values) => new(values, 0, 0);
    private static JsObjectProperty Property(string name, JsExpression value) => new(name, value, false, 0, 0);
    private static JsLiteralExpression String(string value) => new(value, JavaScriptTokenKind.String, 0, 0);
    private static JsLiteralExpression Number(double value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), JavaScriptTokenKind.Number, 0, 0);
}
