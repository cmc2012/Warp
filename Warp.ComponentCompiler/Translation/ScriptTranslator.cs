using Warp.ComponentCompiler.Scripting;
using Warp.JsCompiler.Frontend;
namespace Warp.ComponentCompiler.Translation;

public static class ScriptTranslator
{
    public static JsFunctionStatement Translate(ComponentLogic logic, bool isPage, Func<JsImport, string?>? relativeModuleId = null)
    {
        var lines = new List<JsStatement>();

        // Match the device toolchain's CommonJS-to-ES-module bridge.  The
        // page runtime observes this export object while it wires lifecycle
        // hooks, so a plain assignment-only object is not equivalent.
        lines.Add(new JsExpressionStatement(String("use strict"), 0, 0));
        lines.Add(new JsExpressionStatement(Call(Member(Id("Object"), "defineProperty"), [
            Id("exports"), String("__esModule"), Object([Property("value", new JsLiteralExpression("true", JavaScriptTokenKind.Identifier, 0, 0))])
        ]), 0, 0));
        lines.Add(Assign(Member(Id("exports"), "default"), new JsUnaryExpression("void", new JsLiteralExpression("0", JavaScriptTokenKind.Number, 0, 0), 0, 0)));

        foreach (var imp in logic.Imports)
        {
            var bundledId = imp.IsRelative ? relativeModuleId?.Invoke(imp) : null;
            var req = Call(Id(bundledId is null ? "$app_require$" : "__warp_require__"),
                [String(bundledId ?? (imp.IsSystem ? "@app-module/" + imp.Specifier[1..] : imp.Specifier))]);
            if (imp.DefaultName is not null)
                lines.Add(Var(imp.DefaultName, Member(Call(Id("_interopRequireDefault"), [req]), "default")));
            foreach (var (imported, local) in imp.Named)
                lines.Add(Var(local, Member(req, imported)));
        }

        foreach (var fn in logic.Functions)
            lines.Add(fn.Node);

        // 3. export default
        if (logic.ExportDefault is null)
        {
            lines.Add(new JsExpressionStatement(new JsAssignmentExpression("=", Member(Id("exports"), "default"), Object([]), 0, 0), 0, 0));
        }
        else
        {
            var props = logic.ExportDefault.Properties;
            var parts = new List<JsObjectProperty>();
            foreach (var p in props)
                parts.Add(p.Node);
            foreach (var ne in logic.NamedExports)
                parts.Add(new JsObjectProperty(ne.Exported, Id(ne.Local), false, 0, 0));

            lines.Add(new JsExpressionStatement(new JsAssignmentExpression("=", Member(Id("exports"), "default"), Object(parts), 0, 0), 0, 0));
            if (isPage)
                lines.AddRange(PageStateNormalizationStatements());
            foreach (var ne in logic.NamedExports)
                lines.Add(new JsExpressionStatement(new JsAssignmentExpression("=", Member(Id("exports"), ne.Exported), Id(ne.Local), 0, 0), 0, 0));

        }

        return new JsFunctionStatement("$app_script$", ["module", "exports", "$app_require$"], new JsBlockStatement(lines, 0, 0), false, 0, 0);
    }

    private static JsIdentifierExpression Id(string name) => new(name, 0, 0);
    private static JsLiteralExpression String(string value) => new(value, JavaScriptTokenKind.String, 0, 0);
    private static JsLiteralExpression Number(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), JavaScriptTokenKind.Number, 0, 0);
    private static JsMemberExpression Member(JsExpression target, string property) => new(target, Id(property), false, 0, 0);
    private static JsMemberExpression Index(JsExpression target, JsExpression index) => new(target, index, true, 0, 0);
    private static JsCallExpression Call(JsExpression callee, IReadOnlyList<JsExpression> arguments) => new(callee, arguments, 0, 0);
    private static JsVariableStatement Var(string name, JsExpression? value) => new("var", [new JsVariableDeclarator(name, value, 0, 0)], 0, 0);
    private static JsObjectExpression Object(IReadOnlyList<JsObjectProperty> values) => new(values, 0, 0);
    private static JsArrayExpression Array(IReadOnlyList<JsExpression> values) => new(values, 0, 0);
    private static JsObjectProperty Property(string name, JsExpression value) => new(name, value, false, 0, 0);
    private static JsLiteralExpression Null() => new("null", JavaScriptTokenKind.Identifier, 0, 0);
    private static JsExpressionStatement Assign(JsExpression left, JsExpression right) => new(new JsAssignmentExpression("=", left, right, 0, 0), 0, 0);

    private static IReadOnlyList<JsStatement> PageStateNormalizationStatements()
    {
        var moduleOwn = Id("moduleOwn");
        var accessors = Id("accessors");
        var index = Id("index");
        var access = Id("access");
        var group = Id("group");
        var name = Id("name");
        var merge = new JsForStatement(new JsVariableStatement("var", [new JsVariableDeclarator("index", Number(0), 0, 0)], 0, 0),
            new JsBinaryExpression("<", index, Member(accessors, "length"), 0, 0), new JsUpdateExpression("++", index, false, 0, 0),
            new JsBlockStatement([
                new JsVariableStatement("var", [new JsVariableDeclarator("access", Index(accessors, index), 0, 0)], 0, 0),
                new JsVariableStatement("var", [new JsVariableDeclarator("group", Index(moduleOwn, access), 0, 0)], 0, 0),
                new JsIfStatement(new JsBinaryExpression("===", new JsUnaryExpression("typeof", group, 0, 0), String("object"), 0, 0), new JsBlockStatement([
                    new JsExpressionStatement(Call(Member(Id("Object"), "assign"), [Member(moduleOwn, "data"), group]), 0, 0),
                    new JsForInOfStatement(new JsVariableStatement("var", [new JsVariableDeclarator("name", null, 0, 0)], 0, 0), null, group, false,
                        Assign(Index(Member(moduleOwn, "_descriptor"), name), Object([Property("access", access)])), 0, 0)
                ], 0, 0), null, 0, 0)
            ], 0, 0), 0, 0);
        return [
            new JsVariableStatement("var", [new JsVariableDeclarator("moduleOwn", new JsBinaryExpression("||", Member(Id("exports"), "default"), Member(Id("module"), "exports"), 0, 0), 0, 0)], 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator("accessors", Array([String("public"), String("protected"), String("private")]), 0, 0)], 0, 0),
            new JsIfStatement(new JsUnaryExpression("!", Member(moduleOwn, "data"), 0, 0), new JsBlockStatement([
                Assign(Member(moduleOwn, "data"), Object([])), Assign(Member(moduleOwn, "_descriptor"), Object([])), merge
            ], 0, 0), null, 0, 0)
        ];
    }


    public static JsFunctionStatement InteropHelper() => new("_interopRequireDefault", ["e"], new JsBlockStatement([
        new JsIfStatement(new JsUnaryExpression("!", Id("e"), 0, 0),
            new JsReturnStatement(Object([Property("default", Id("e"))]), 0, 0), null, 0, 0),
        new JsIfStatement(Member(Id("e"), "__esModule"), new JsReturnStatement(Id("e"), 0, 0), null, 0, 0),
        new JsReturnStatement(Object([Property("default", Id("e"))]), 0, 0)
    ], 0, 0), false, 0, 0);
}
