using Warp.Diagnostics;
using Warp.ComponentCompiler.Scripting;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Translation;

/// <summary>Bundles relative ES modules into the component entry's private CommonJS table.</summary>
internal sealed class RelativeModuleBundler(string sourceRoot, DiagnosticSink sink)
{
    private readonly string _sourceRoot = Path.GetFullPath(sourceRoot);
    private readonly DiagnosticSink _sink = sink;
    private readonly Dictionary<string, Module> _modules = new(StringComparer.Ordinal);

    internal IReadOnlyList<JsStatement> Bundle(ComponentLogic entry, string entryPath)
    {
        foreach (var import in entry.Imports.Where(import => import.IsRelative))
            AddModule(Resolve(entryPath, import.Specifier), import.Position);
        if (_modules.Count == 0) return [];

        var factories = _modules.Values.Select(module =>
            new JsObjectProperty(module.Id, new JsFunctionExpression(null, ["module", "exports", "__warp_require__"],
                new JsBlockStatement(module.Body, 0, 0), false, false, 0, 0), false, 0, 0)).ToArray();
        var modules = Id("__warp_modules__");
        var cache = Id("__warp_cache__");
        var id = Id("id");
        var module = Id("module");
        var factory = Id("factory");
        var cached = Index(cache, id);
        var requireBody = new List<JsStatement>
        {
            Var("cached", cached),
            new JsIfStatement(Id("cached"), new JsReturnStatement(Member(Id("cached"), "exports"), 0, 0), null, 0, 0),
            Var("factory", Index(modules, id)),
            new JsIfStatement(new JsUnaryExpression("!", factory, 0, 0),
                new JsReturnStatement(Call(Id("$app_require$"), [id]), 0, 0), null, 0, 0),
            // Transformed relative files retain ES-module export semantics.
            // Default imports pass through _interopRequireDefault; without this
            // marker that helper wraps { default: value } again, leaving callers
            // with an extra `.default` layer.
            Var("module", Object([Property("exports", Object([Property("__esModule", Bool(true))]))])),
            Assign(Index(cache, id), module),
            new JsExpressionStatement(Call(factory, [module, Member(module, "exports"), Id("__warp_require__")]), 0, 0),
            new JsReturnStatement(Member(module, "exports"), 0, 0)
        };
        return [
            Var("__warp_modules__", Object(factories)),
            Var("__warp_cache__", Object([])),
            new JsFunctionStatement("__warp_require__", ["id"], new JsBlockStatement(requireBody, 0, 0), false, 0, 0)
        ];
    }

    internal string IdForImport(string importerPath, string specifier) => ModuleId(Resolve(importerPath, specifier));

    private void AddModule(string path, SourcePosition? position)
    {
        path = Path.GetFullPath(path);
        if (_modules.ContainsKey(path)) return;
        if (!File.Exists(path))
        {
            _sink.Error($"relative module not found: {path}", position);
            return;
        }
        JsAstProgram program;
        try { program = JavaScriptSyntax.ParseModule(File.ReadAllText(path), path); }
        catch (JavaScriptCompilationException ex)
        {
            _sink.Error($"relative module parse error: {ex.Message}", new SourcePosition(path, ex.Line, ex.Column));
            return;
        }
        // Insert the module before walking dependencies so cycles retain a stable factory identity.
        var module = new Module(ModuleId(path), []);
        _modules.Add(path, module);
        module.Body.AddRange(Transform(program, path));
    }

    private IEnumerable<JsStatement> Transform(JsAstProgram program, string path)
    {
        foreach (var statement in program.Body)
        {
            if (statement is JsImportStatement import)
            {
                var request = import.Specifier.StartsWith(".", StringComparison.Ordinal)
                    ? Call(Id("__warp_require__"), [String(IdForImport(path, import.Specifier))])
                    : Call(Id("$app_require$"), [String(import.Specifier.StartsWith("@system.", StringComparison.Ordinal)
                        ? "@app-module/" + import.Specifier[1..] : import.Specifier)]);
                foreach (var binding in import.Bindings)
                {
                    yield return binding.Kind switch
                    {
                        JsImportBindingKind.Default => Var(binding.LocalName, Member(Call(Id("_interopRequireDefault"), [request]), "default")),
                        JsImportBindingKind.Named => Var(binding.LocalName, Member(request, binding.ImportName)),
                        JsImportBindingKind.Namespace => Var(binding.LocalName, request),
                        _ => throw new InvalidOperationException("Unknown import binding."),
                    };
                }
                continue;
            }
            if (statement is JsExportAllStatement exportAll)
            {
                _sink.Error("export * is not supported in relative modules", new SourcePosition(path, exportAll.Line, exportAll.Column));
                continue;
            }
            if (statement is not JsExportStatement export)
            {
                yield return statement;
                continue;
            }
            if (export.Source is not null)
            {
                _sink.Error("re-export from another module is not supported", new SourcePosition(path, export.Line, export.Column));
                continue;
            }
            if (export.IsDefault)
            {
                if (export.Declaration is JsExpressionStatement expression)
                    yield return Assign(Member(Id("exports"), "default"), expression.Expression);
                else if (export.Declaration is JsFunctionStatement function)
                {
                    yield return function;
                    yield return Assign(Member(Id("exports"), "default"), Id(function.Name));
                }
                else if (export.Declaration is JsClassDeclaration @class)
                {
                    yield return @class;
                    yield return Assign(Member(Id("exports"), "default"), Id(@class.Name));
                }
                else _sink.Error("unsupported default export", new SourcePosition(path, export.Line, export.Column));
                continue;
            }
            if (export.Declaration is not null)
            {
                yield return export.Declaration;
                foreach (var name in DeclaredNames(export.Declaration))
                    yield return Assign(Member(Id("exports"), name), Id(name));
            }
            foreach (var binding in export.Bindings)
                yield return Assign(Member(Id("exports"), binding.ExportName), Id(binding.LocalName));
        }
    }

    private string Resolve(string importerPath, string specifier)
    {
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importerPath)!, specifier));
        foreach (var path in new[] { candidate, candidate + ".js", candidate + ".mjs", Path.Combine(candidate, "index.js") })
            if (File.Exists(path)) return path;
        return candidate + ".js";
    }

    private string ModuleId(string path) => Path.GetRelativePath(_sourceRoot, path).Replace(Path.DirectorySeparatorChar, '/');
    private static IEnumerable<string> DeclaredNames(JsStatement statement) => statement switch
    {
        JsVariableStatement variables => variables.Declarations.Where(item => item.Pattern is JsIdentifierPattern).Select(item => item.Name),
        JsFunctionStatement function => [function.Name],
        JsClassDeclaration @class => [@class.Name],
        _ => []
    };

    private sealed record Module(string Id, List<JsStatement> Body);
    private static JsIdentifierExpression Id(string name) => new(name, 0, 0);
    private static JsLiteralExpression String(string value) => new(value, JavaScriptTokenKind.String, 0, 0);
    private static JsMemberExpression Member(JsExpression value, string name) => new(value, Id(name), false, 0, 0);
    private static JsMemberExpression Index(JsExpression value, JsExpression key) => new(value, key, true, 0, 0);
    private static JsCallExpression Call(JsExpression callee, IReadOnlyList<JsExpression> args) => new(callee, args, 0, 0);
    private static JsObjectExpression Object(IReadOnlyList<JsObjectProperty> values) => new(values, 0, 0);
    private static JsObjectProperty Property(string name, JsExpression value) => new(name, value, false, 0, 0);
    private static JsLiteralExpression Bool(bool value) => new(value ? "true" : "false", JavaScriptTokenKind.Identifier, 0, 0);
    private static JsVariableStatement Var(string name, JsExpression value) => new("var", [new JsVariableDeclarator(name, value, 0, 0)], 0, 0);
    private static JsExpressionStatement Assign(JsExpression left, JsExpression right) => new(new JsAssignmentExpression("=", left, right, 0, 0), 0, 0);
}
