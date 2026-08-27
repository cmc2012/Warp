using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Scripting;

/// <summary>
/// Projects the canonical JavaScript compiler AST into the small semantic
/// Model needed by WXAML translation. Parsing itself remains the sole
/// responsibility of <c>Warp.JsCompiler</c>.
/// </summary>
public sealed class ComponentScriptParser
{
    public ComponentLogic Parse(string source, string filePath, DiagnosticSink sink)
    {
        if (string.IsNullOrWhiteSpace(source)) return new ComponentLogic([], [], [], null, []);

        JsAstProgram program;
        try { program = JavaScriptSyntax.ParseModule(source, filePath); }
        catch (JavaScriptCompilationException ex)
        {
            sink.Error($"JS parse error: {ex.Message} ({ex.Line}:{ex.Column})", new SourcePosition(filePath, ex.Line, ex.Column));
            return new ComponentLogic([], [], [], null, []);
        }

        var imports = new List<JsImport>();
        var constants = new List<ConstDecl>();
        var functions = new List<JsFunction>();
        var namedExports = new List<JsNamedExport>();
        JsExportDefault? exportDefault = null;
        var seenConstants = new HashSet<string>(StringComparer.Ordinal);

        foreach (var statement in program.Body)
        {
            switch (statement)
            {
                case JsImportStatement import:
                    ReadImport(import, imports, sink, filePath);
                    break;
                case JsVariableStatement { Kind: "const" } declaration:
                    AddConstants(declaration, constants, seenConstants, filePath, sink);
                    break;
                case JsFunctionStatement function:
                    functions.Add(ToFunction(function, filePath));
                    break;
                case JsExportStatement { IsDefault: true } export:
                    if (exportDefault is not null) sink.Error("multiple export default", Position(filePath, export));
                    exportDefault = ReadDefaultExport(export, filePath, sink);
                    break;
                case JsExportStatement { Declaration: JsVariableStatement { Kind: "const" } declaration } export:
                    AddConstants(declaration, constants, seenConstants, filePath, sink);
                    foreach (var item in declaration.Declarations.Where(d => d.Pattern is JsIdentifierPattern))
                        namedExports.Add(new JsNamedExport(item.Name, item.Name, Position(filePath, item)));
                    break;
                case JsExportStatement { Declaration: JsFunctionStatement function } export:
                    functions.Add(ToFunction(function, filePath));
                    namedExports.Add(new JsNamedExport(function.Name, function.Name, Position(filePath, function)));
                    break;
                case JsExportStatement { Bindings.Count: > 0, Source: null } export:
                    namedExports.AddRange(export.Bindings.Select(binding => new JsNamedExport(binding.ExportName, binding.LocalName, Position(filePath, binding))));
                    break;
                case JsExportAllStatement export:
                    sink.Error("export * from not supported", Position(filePath, export));
                    break;
                case JsExportStatement { Source: not null } export:
                    sink.Error("re-export from another module is not supported", Position(filePath, export));
                    break;
            }
        }

        return new ComponentLogic(imports, constants, functions, exportDefault, namedExports);
    }

    private static void ReadImport(JsImportStatement import, List<JsImport> imports, DiagnosticSink sink, string filePath)
    {
        if (import.Bindings.Any(binding => binding.Kind == JsImportBindingKind.Namespace))
        {
            sink.Error("namespace imports are not supported", Position(filePath, import));
            return;
        }

        var defaultName = import.Bindings.FirstOrDefault(binding => binding.Kind == JsImportBindingKind.Default)?.LocalName;
        var named = import.Bindings.Where(binding => binding.Kind == JsImportBindingKind.Named)
            .Select(binding => (binding.ImportName, binding.LocalName)).ToArray();
        imports.Add(new JsImport(defaultName, named, import.Specifier, Position(filePath, import)));
    }

    private static void AddConstants(JsVariableStatement statement, List<ConstDecl> constants, HashSet<string> seen,
        string filePath, DiagnosticSink sink)
    {
        foreach (var declaration in statement.Declarations)
        {
            if (declaration.Pattern is not JsIdentifierPattern || declaration.Initializer is null) continue;
            if (!seen.Add(declaration.Name))
            {
                sink.Error($"const '{declaration.Name}' redeclared", Position(filePath, declaration));
                continue;
            }
            constants.Add(new ConstDecl(declaration.Name, declaration.Initializer, null, false, Position(filePath, declaration)));
        }
    }

    private static JsExportDefault ReadDefaultExport(JsExportStatement export, string filePath, DiagnosticSink sink)
    {
        if (export.Declaration is not JsExpressionStatement { Expression: JsObjectExpression value })
        {
            sink.Error("export default must be object literal { ... }", Position(filePath, export));
            return new JsExportDefault([], Position(filePath, export));
        }

        var properties = new List<JsProperty>();
        var dataKeys = new HashSet<string>(StringComparer.Ordinal);
        string? propsRaw = null;
        for (var index = 0; index < value.Properties.Count; index++)
        {
            var property = value.Properties[index];
            var kind = PropertyKind(property);
            if (property.Key is "data" or "protected" or "private" or "public" && property.Value is JsObjectExpression data)
            {
                foreach (var item in data.Properties) dataKeys.Add(item.Key);
            }
            if (property.Key == "props") propsRaw = JavaScriptAstWriter.Write(property.Value);
            properties.Add(new JsProperty(property, kind, Position(filePath, property)));
        }

        if (propsRaw is not null)
        {
            foreach (var name in System.Text.RegularExpressions.Regex.Matches(propsRaw, "[\"'](\\w+)[\"']").Select(match => match.Groups[1].Value))
                if (dataKeys.Contains(name)) sink.Error($"props '{name}' shadows data key", Position(filePath, export));
        }
        return new JsExportDefault(properties, Position(filePath, export));
    }

    private static JsFunction ToFunction(JsFunctionStatement function, string filePath)
        => new(function, Position(filePath, function));

    private static JsPropertyKind PropertyKind(JsObjectProperty property) => property.Key switch
    {
        "data" => JsPropertyKind.Data,
        "protected" => JsPropertyKind.Protected,
        "private" => JsPropertyKind.Private,
        "public" => JsPropertyKind.Public,
        "props" => JsPropertyKind.Props,
        // These names are invoked by the Vela runtime through the exported
        // ViewModel/App object, rather than by a statically visible call in
        // user code.  They are therefore part of the runtime ABI and must not
        // be treated as ordinary minifiable methods.
        "onInit" or "onReady" or "onShow" or "onHide" or "onDestroy" or "onBackPress" or
        "onRefresh" or "onConfigurationChanged" or "onCreate" or "onError" => JsPropertyKind.Lifecycle,
        _ when property.Kind is JsObjectPropertyKind.Method or JsObjectPropertyKind.Getter or JsObjectPropertyKind.Setter => JsPropertyKind.Method,
        _ when property.Value is JsFunctionExpression => JsPropertyKind.Method,
        _ => JsPropertyKind.Unknown,
    };

    private static SourcePosition Position(string filePath, JsAstNode node) => new(filePath, node.Line, node.Column);
}
