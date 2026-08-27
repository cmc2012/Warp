using Warp.ComponentCompiler.Scripting;
using Warp.ComponentSyntax.Ast;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using System.Text.Json;

namespace Warp.ComponentCompiler.Translation;

public static class ModuleEmitter
{
    public static string EmitPage(UxDocument doc, JsExpression style, JsFunctionStatement script, IReadOnlyList<JsExpression> template, string? manifestJson, bool isPage, IReadOnlyList<ConstDecl> transparentConsts, IReadOnlyList<JsStatement>? bundledModules = null)
    {
        var body = new List<JsStatement>();
        foreach (var import in doc.Imports.Where(import => !import.IsInline)) body.Add(Assign(Index(Id("$app_exports$"), String(import.Name.ToLowerInvariant())), Call(Id("require"), [String(RuntimeModuleSpecifier(import.Src))])));
        foreach (var constant in transparentConsts) body.Add(new JsVariableStatement("const", [new JsVariableDeclarator(constant.Name, constant.Expression, 0, 0)], 0, 0));
        body.Add(ScriptTranslator.InteropHelper());
        if (bundledModules is not null) body.AddRange(bundledModules);
        body.Add(new JsVariableStatement("var", [new JsVariableDeclarator("$app_style$", style, 0, 0)], 0, 0));
        body.Add(script);
        if (isPage)
        {
            var templateBody = new JsBlockStatement([new JsVariableStatement("const", [new JsVariableDeclarator("_vm_", new JsBinaryExpression("||", Id("vm"), Id("this"), 0, 0), 0, 0)], 0, 0), new JsReturnStatement(new JsArrayExpression(template, 0, 0), 0, 0)], 0, 0);
            body.Add(new JsVariableStatement("var", [new JsVariableDeclarator("$app_template$", new JsFunctionExpression(null, ["vm"], templateBody, false, false, 0, 0), 0, 0)], 0, 0));
            var entry = new JsBlockStatement([new JsExpressionStatement(Call(Id("$app_script$"), [Object([]), Id("$app_exports$"), Id("$app_require$1")]), 0, 0), Assign(Member(Member(Id("$app_exports$"), "default"), "template"), Id("$app_template$")), Assign(Member(Member(Id("$app_exports$"), "default"), "style"), Id("$app_style$"))], 0, 0);
            body.Add(Assign(Index(Id("$app_exports$"), String("entry")), new JsFunctionExpression(null, ["$app_exports$"], entry, false, false, 0, 0)));
        }
        else
        {
            body.Add(new JsExpressionStatement(Call(Id("$app_script$"), [Object([]), Id("$app_exports$"), Id("$app_require$1")]), 0, 0));
            body.Add(Assign(Member(Member(Id("$app_exports$"), "default"), "style"), Id("$app_style$")));
            body.Add(Assign(Member(Member(Id("$app_exports$"), "default"), "manifest"), manifestJson is null ? Call(Id("require"), [String("./manifest.json")]) : JsonExpression(manifestJson)));
            body.AddRange(StyleRuntimeStatements());
        }
        return JavaScriptAstWriter.Write(new JsAstProgram([WrapRuntimeModule(body, isPage)]));
    }

    public static string EmitComponent(UxDocument doc, JsExpression style, JsFunctionStatement script, IReadOnlyList<JsExpression> template, IReadOnlyList<ConstDecl> transparentConsts, IReadOnlyList<JsStatement>? bundledModules = null)
    {
        var body = new List<JsStatement>();
        foreach (var import in doc.Imports.Where(import => !import.IsInline)) body.Add(Assign(Index(Id("$app_exports$"), String(import.Name.ToLowerInvariant())), Call(Id("require"), [String(RuntimeModuleSpecifier(import.Src))])));
        foreach (var constant in transparentConsts) body.Add(new JsVariableStatement("const", [new JsVariableDeclarator(constant.Name, constant.Expression, 0, 0)], 0, 0));
        body.Add(ScriptTranslator.InteropHelper());
        if (bundledModules is not null) body.AddRange(bundledModules);
        body.Add(new JsVariableStatement("var", [new JsVariableDeclarator("$app_style$", style, 0, 0)], 0, 0));
        body.Add(script);
        var templateBody = new JsBlockStatement([new JsVariableStatement("const", [new JsVariableDeclarator("_vm_", new JsBinaryExpression("||", Id("vm"), Id("this"), 0, 0), 0, 0)], 0, 0), new JsReturnStatement(new JsArrayExpression(template, 0, 0), 0, 0)], 0, 0);
        body.Add(new JsVariableStatement("var", [new JsVariableDeclarator("$app_template$", new JsFunctionExpression(null, ["vm"], templateBody, false, false, 0, 0), 0, 0)], 0, 0));
        body.Add(new JsExpressionStatement(Call(Id("$app_script$"), [Object([]), Id("$app_exports$"), Id("$app_require$1")]), 0, 0));
        body.Add(Assign(Member(Member(Id("$app_exports$"), "default"), "template"), Id("$app_template$")));
        body.Add(Assign(Member(Member(Id("$app_exports$"), "default"), "style"), Id("$app_style$")));
        return JavaScriptAstWriter.Write(new JsAstProgram([WrapRuntimeModule(body, isPage: true)]));
    }

    private static string RuntimeModuleSpecifier(string source)
        => source.EndsWith(".wxaml", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(source, ".js") : source;

    private static JsFunctionStatement PageStateNormalizer()
    {
        var moduleOwn = Id("moduleOwn");
        var accessors = Id("accessors");
        var access = Id("access");
        var group = Id("group");
        var name = Id("name");
        var index = Id("index");
        var hasAccessorState = new JsBinaryExpression("||", Member(moduleOwn, "public"), new JsBinaryExpression("||", Member(moduleOwn, "protected"), Member(moduleOwn, "private"), 0, 0), 0, 0);
        var addDescriptors = new JsForInOfStatement(
            new JsVariableStatement("var", [new JsVariableDeclarator("name", null, 0, 0)], 0, 0), null, group, false,
            Assign(Index(Member(moduleOwn, "_descriptor"), name), Object([new JsObjectProperty("access", access, false, 0, 0)])), 0, 0);
        var mergeGroup = new JsIfStatement(
            new JsBinaryExpression("&&", new JsBinaryExpression("===", new JsUnaryExpression("typeof", group, 0, 0), String("object"), 0, 0), group, 0, 0),
            new JsBlockStatement([
                new JsExpressionStatement(Call(Member(Id("Object"), "assign"), [Member(moduleOwn, "data"), group]), 0, 0),
                addDescriptors
            ], 0, 0), null, 0, 0);
        var mergeAccessors = new JsForStatement(
            new JsVariableStatement("var", [new JsVariableDeclarator("index", Number(0), 0, 0)], 0, 0),
            new JsBinaryExpression("<", index, Member(accessors, "length"), 0, 0),
            new JsUpdateExpression("++", index, false, 0, 0),
            new JsBlockStatement([
                new JsVariableStatement("var", [new JsVariableDeclarator("access", Index(accessors, index), 0, 0)], 0, 0),
                new JsVariableStatement("var", [new JsVariableDeclarator("group", Index(moduleOwn, access), 0, 0)], 0, 0),
                mergeGroup
            ], 0, 0), 0, 0);
        return new JsFunctionStatement("$app_normalize_page_state$", [], new JsBlockStatement([
            new JsVariableStatement("var", [new JsVariableDeclarator("moduleOwn", Member(Id("$app_exports$"), "default"), 0, 0)], 0, 0),
            new JsIfStatement(new JsUnaryExpression("!", moduleOwn, 0, 0), new JsReturnStatement(null, 0, 0), null, 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator("accessors", Array([String("public"), String("protected"), String("private")]), 0, 0)], 0, 0),
            new JsIfStatement(new JsBinaryExpression("&&", Member(moduleOwn, "data"), hasAccessorState, 0, 0), new JsThrowStatement(Call(Id("Error"), [String("page state cannot declare data together with public, protected, or private")]), 0, 0), null, 0, 0),
            new JsIfStatement(new JsUnaryExpression("!", Member(moduleOwn, "data"), 0, 0), new JsBlockStatement([
                Assign(Member(moduleOwn, "data"), Object([])),
                Assign(Member(moduleOwn, "_descriptor"), Object([])),
                mergeAccessors
            ], 0, 0), null, 0, 0)
        ], 0, 0), false, 0, 0);
    }

    /// <summary>
    /// The device runtime loads every bytecode file as an ES module and invokes
    /// its default export.  Generated component code therefore cannot run at
    /// module top level: it must execute inside the handler after the runtime
    /// has supplied its globals and module exports object.
    /// </summary>
    private static JsExportStatement WrapRuntimeModule(IReadOnlyList<JsStatement> moduleBody, bool isPage)
    {
        var parameters = new[] { "global", "globalThis", "window", "$app_exports$", "$app_evaluate$" };
        var runtimeParameters = parameters;
        var handlerName = isPage ? "createPageHandler" : "createAppHandler";
        // Keep this nesting identical to the device toolchain.  In
        // particular, the page body is an immediately-invoked arrow inside
        // the handler function rather than the handler body itself.
        var pageBody = new JsFunctionExpression(null, [], new JsBlockStatement(moduleBody, 0, 0), false, true, 0, 0);
        var handler = new JsFunctionExpression(null, [], new JsBlockStatement([
            new JsReturnStatement(Call(pageBody, []), 0, 0)
        ], 0, 0), false, false, 0, 0);
        var initializeRuntime = new List<JsStatement>
        {
            new JsVariableStatement("var", [new JsVariableDeclarator("setTimeout", Member(Id("global"), "setTimeout"), 0, 0)], 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator("setInterval", Member(Id("global"), "setInterval"), 0, 0)], 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator("clearTimeout", Member(Id("global"), "clearTimeout"), 0, 0)], 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator("clearInterval", Member(Id("global"), "clearInterval"), 0, 0)], 0, 0),
            // Rspack renames the wrapper-local require to $app_require$1 because
            // the generated script module has a $app_require$ parameter.  Preserve
            // that exact lexical layout: the Vela HMR evaluator reuses this scope.
            new JsVariableStatement("var", [new JsVariableDeclarator("$app_require$1", new JsBinaryExpression("||", Member(Id("global"), "$app_require$"), Id("org_app_require"), 0, 0), 0, 0)], 0, 0),
            new JsVariableStatement("var", [new JsVariableDeclarator(handlerName, handler, 0, 0)], 0, 0),
            new JsReturnStatement(Call(Id(handlerName), []), 0, 0)
        };
        var runtimeClosure = new JsFunctionExpression(null, runtimeParameters, new JsBlockStatement(initializeRuntime, 0, 0), false, false, 0, 0);
        var outerBody = new JsBlockStatement([
            new JsVariableStatement("var", [new JsVariableDeclarator("org_app_require", Id("$app_require$"), 0, 0)], 0, 0),
            new JsExpressionStatement(Call(runtimeClosure, parameters.Select(Id).Cast<JsExpression>().ToArray()), 0, 0)
        ], 0, 0);
        var exportedHandler = new JsFunctionStatement(null!, parameters, outerBody, false, 0, 0);
        return new JsExportStatement(exportedHandler, [], true, 0, 0);
    }
    private static IReadOnlyList<JsStatement> StyleRuntimeStatements()
    {
        var v = Id("v");
        var i = Id("i");
        var m = Id("m");
        var camel = Function(["_", "c"], [new JsReturnStatement(Call(Member(Id("c"), "toUpperCase"), []), 0, 0)]);
        var key = Call(Member(Call(Member(Index(m, Number(1)), "trim"), []), "replace"), [Regex("/-([a-z])/g"), camel]);
        var pair = Array([key, Call(Member(Index(m, Number(2)), "trim"), [])]);
        var mapper = Function(["i"], [
            new JsVariableStatement("var", [new JsVariableDeclarator("m", Call(Member(i, "match"), [Regex("/([^:]+):(.*)/")]), 0, 0)], 0, 0),
            new JsReturnStatement(new JsConditionalExpression(m, pair, Array([]), 0, 0), 0, 0)
        ]);
        var values = Call(Member(Call(Member(Call(Member(v, "split"), [String(";")]), "filter"), [Id("Boolean")]), "map"), [mapper]);
        var convert = Call(Member(Id("Object"), "fromEntries"), [values]);
        var function = Function(["v"], [
            new JsIfStatement(new JsBinaryExpression("===", new JsUnaryExpression("typeof", v, 0, 0), String("string"), 0, 0), new JsReturnStatement(convert, 0, 0), null, 0, 0),
            new JsReturnStatement(v, 0, 0)
        ]);
        return [
            new JsVariableStatement("var", [new JsVariableDeclarator("$translateStyle$", function, 0, 0)], 0, 0),
            Assign(Member(Id("global"), "$translateStyle$"), Id("$translateStyle$"))
        ];
    }
    private static JsExpression JsonExpression(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonExpression(document.RootElement);
    }
    private static JsExpression JsonExpression(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => Object(value.EnumerateObject().Select(property => new JsObjectProperty(property.Name, JsonExpression(property.Value), false, 0, 0)).ToArray()),
        JsonValueKind.Array => new JsArrayExpression(value.EnumerateArray().Select(JsonExpression).ToArray(), 0, 0),
        JsonValueKind.String => String(value.GetString() ?? ""),
        JsonValueKind.Number => new JsLiteralExpression(value.GetRawText(), JavaScriptTokenKind.Number, 0, 0),
        JsonValueKind.True => new JsLiteralExpression("true", JavaScriptTokenKind.Identifier, 0, 0),
        JsonValueKind.False => new JsLiteralExpression("false", JavaScriptTokenKind.Identifier, 0, 0),
        _ => new JsLiteralExpression("null", JavaScriptTokenKind.Identifier, 0, 0)
    };
    private static JsIdentifierExpression Id(string name) => new(name, 0, 0);
    private static JsLiteralExpression String(string value) => new(value, JavaScriptTokenKind.String, 0, 0);
    private static JsLiteralExpression Number(int value) => new(value.ToString(System.Globalization.CultureInfo.InvariantCulture), JavaScriptTokenKind.Number, 0, 0);
    private static JsLiteralExpression Regex(string value) => new(value, JavaScriptTokenKind.Regex, 0, 0);
    private static JsMemberExpression Member(JsExpression target, string name) => new(target, Id(name), false, 0, 0);
    private static JsMemberExpression Index(JsExpression target, JsExpression index) => new(target, index, true, 0, 0);
    private static JsCallExpression Call(JsExpression callee, IReadOnlyList<JsExpression> arguments) => new(callee, arguments, 0, 0);
    private static JsArrayExpression Array(IReadOnlyList<JsExpression> elements) => new(elements, 0, 0);
    private static JsObjectExpression Object(IReadOnlyList<JsObjectProperty> properties) => new(properties, 0, 0);
    private static JsExpressionStatement Assign(JsExpression left, JsExpression right) => new(new JsAssignmentExpression("=", left, right, 0, 0), 0, 0);
    private static JsFunctionExpression Function(IReadOnlyList<string> parameters, IReadOnlyList<JsStatement> body) => new(null, parameters, new JsBlockStatement(body, 0, 0), false, false, 0, 0);
}
