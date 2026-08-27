using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Warp.ComponentCompiler.Analysis;
using Warp.ComponentCompiler.Scripting;
using Warp.ComponentCompiler.Translation;
using Warp.ComponentSyntax.Ast;
using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Ast = Warp.ComponentSyntax.Ast;
using Warp.JsCompiler.Frontend;

namespace Warp.ComponentCompiler.Pipeline;

/// <summary>Builds a WXAML project into the runtime's JavaScript module layout.</summary>
public sealed class WarpPipeline
{
    private readonly BuildOptions _opts;
    private readonly DiagnosticSink _sink;
    private readonly ILogger _logger;
    private readonly HashSet<string> _builtComponents = new(StringComparer.OrdinalIgnoreCase);
    private bool _minifyIdentifiers = true;

    public WarpPipeline(BuildOptions opts, ILogger? logger = null)
    {
        _opts = opts;
        _logger = logger ?? NullLogger.Instance;
        _sink = new DiagnosticSink(_logger);
    }

    private static string ManifestPath(string projectPath) => Path.Combine(projectPath, "manifest.yaml");

    public async Task<BuildResult> BuildAsync(CancellationToken ct = default)
    {
        if (!PrepareOutputDirectory())
            return new BuildResult([], _sink.Diagnostics, false);

        var manifestPath = ManifestPath(_opts.ProjectPath);
        Manifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            var text = await File.ReadAllTextAsync(manifestPath, ct);
            manifest = new ManifestParser().Parse(text, manifestPath, _sink);
            _minifyIdentifiers = manifest.Config.MinifyIdentifiers;
        }
        else
        {
            _sink.Error($"missing project manifest: expected {manifestPath}");
        }

        var pages = DiscoverPages(manifest);
        var pageResults = new List<PageBuildResult>();

        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            var result = await BuildPageAsync(page, ct);
            pageResults.Add(result);
        }

        // app.js / app.wxaml
        await BuildAppAsync(manifest, ct);
        if (manifest is not null)
        {
            var outManifest = Path.Combine(_opts.ProjectPath, _opts.OutputDir, "manifest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outManifest)!);
            var json = DeviceManifestEmitter.Emit(manifest, writeIndented: true);
            await File.WriteAllTextAsync(outManifest, json, ct);
            await WriteDeviceManifestsAsync(manifest, ct);
        }
        CopyResources();
        CopyRuntimeConfiguration();
        if (!_sink.HasErrors && _opts.CompileJavaScript)
        {
            var bytecodeSucceeded = await CompileBytecodeAsync(ct);
            if (bytecodeSucceeded)
                pageResults = pageResults.Select(page => page with { OutputPath = Path.ChangeExtension(page.OutputPath, ".jsc") }).ToList();
        }

        var success = !_sink.HasErrors;
        return new BuildResult(pageResults, _sink.Diagnostics, success, BytecodeCompiled: _opts.CompileJavaScript && success);
    }

    /// <summary>
    /// The bytecode compiler compiles every JavaScript file below the output root.
    /// Recreate that generated-only directory so deleted pages, components, and
    /// previous JavaScript output cannot leave unreachable .jsc artifacts behind.
    /// </summary>
    private bool PrepareOutputDirectory()
    {
        var projectRoot = Path.GetFullPath(_opts.ProjectPath);
        var sourceRoot = Path.GetFullPath(Path.Combine(projectRoot, _opts.SourceRoot));
        var outputRoot = Path.GetFullPath(Path.Combine(projectRoot, _opts.OutputDir));
        var relative = Path.GetRelativePath(projectRoot, outputRoot);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            outputRoot.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) || sourceRoot.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _sink.Error($"refusing to clean unsafe output directory '{outputRoot}'");
            return false;
        }

        if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        Directory.CreateDirectory(outputRoot);
        return true;
    }

    private IReadOnlyList<string> DiscoverPages(Manifest? manifest)
    {
        if (manifest is not null && manifest.Router.Pages.Count > 0)
            return manifest.Router.Pages.Keys.Select(k => k.Split('/').Last()).ToList();

        var pagesDir = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "pages");
        if (!Directory.Exists(pagesDir)) return [];
        return Directory.GetDirectories(pagesDir).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList();
    }

    private async Task<PageBuildResult> BuildPageAsync(string pageName, CancellationToken ct)
    {
        var sink = new DiagnosticSink(_logger);
        var wxamlPath = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "pages", pageName, $"{pageName}.wxaml");
        var jsPath = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "pages", pageName, $"{pageName}.js");

        var wxamlText = File.Exists(wxamlPath) ? await File.ReadAllTextAsync(wxamlPath, ct) : "";
        var jsText = File.Exists(jsPath) ? await File.ReadAllTextAsync(jsPath, ct) : "export default { data: {} }";

        if (!File.Exists(wxamlPath)) sink.Error($"missing {wxamlPath}");
        if (!File.Exists(jsPath)) sink.Error($"missing {jsPath}");

        // 1. Parse
        var wxamlDoc = new WxamlParser().Parse(wxamlText, wxamlPath, sink);
        var logic = new ComponentScriptParser().Parse(jsText, jsPath, sink);
        var inlineComponents = await ResolveInlineComponentsAsync(wxamlDoc, wxamlPath, sink, ct);
        var inlineMerge = InlineComponentScriptMerger.Merge(logic, inlineComponents, wxamlDoc);
        logic = inlineMerge.Logic;
        inlineComponents = inlineMerge.Components;
        var nameMinification = MinifyComponentMethodNames(logic, inlineComponents);
        logic = nameMinification.Logic;
        inlineComponents = nameMinification.Components;
        var inlineInvocationPlans = RewriteInlineInvocationPlans(inlineMerge.InvocationPlans, nameMinification.Names);

        var constTable = ConstantTable.Build(logic, sink);

        var budget = StaticAnalysis.Analyze(wxamlDoc, constTable, sink);

        var transparentConsts = constTable.Entries.Values.ToList();
        var mergedStyles = MergeInlineStyles(wxamlDoc.Styles, inlineComponents.Values);
        var styleSelectorTransform = StyleSelectorTransform.Create(mergedStyles, wxamlDoc.Imports.Concat(FlattenInlineComponents(inlineComponents.Values).SelectMany(component => component.Document.Imports)));
        var templateCode = new TemplateTranslator(
            constTable,
            logic.Functions.Select(function => function.Name),
            wxamlPath,
            Path.Combine(_opts.ProjectPath, _opts.SourceRoot),
            sink,
            inlineComponents: inlineComponents,
            styleSelectorTransform: styleSelectorTransform,
            methodNames: nameMinification.Names,
            inlineInvocationPlans: inlineInvocationPlans).TranslateAst(wxamlDoc.Children, generatedClasses: GeneratedClasses(styleSelectorTransform, "page"));
        var styleCode = StyleTranslator.Translate(mergedStyles, styleSelectorTransform);
        var bundler = new RelativeModuleBundler(Path.Combine(_opts.ProjectPath, _opts.SourceRoot), sink);
        var bundledModules = bundler.Bundle(logic, jsPath);
        var scriptCode = ScriptTranslator.Translate(logic, isPage: true,
            import => import.IsRelative ? bundler.IdForImport(jsPath, import.Specifier) : null);

        var moduleCode = Translation.ModuleEmitter.EmitPage(wxamlDoc, styleCode, scriptCode, templateCode, null, isPage: true, transparentConsts, bundledModules);

        foreach (var import in wxamlDoc.Imports.Where(import => !import.IsInline))
            await BuildComponentAsync(import, wxamlPath, ct);

        // 5. Emit
        var outPath = Path.Combine(_opts.ProjectPath, _opts.OutputDir, "pages", pageName, $"{pageName}.js");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, moduleCode, ct);

        CrossCheck(wxamlDoc, logic, sink, nameMinification.Names);

        _sink.Merge(sink.Diagnostics);
        return new PageBuildResult(pageName, outPath, budget, sink.Diagnostics);
    }

    private async Task<IReadOnlyDictionary<string, InlineComponentDefinition>> ResolveInlineComponentsAsync(UxDocument host, string hostPath, DiagnosticSink sink, CancellationToken ct)
    {
        var result = new Dictionary<string, InlineComponentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var import in host.Imports.Where(import => import.IsInline))
        {
            var component = await ResolveInlineComponentAsync(import, hostPath, sink, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (component is not null) result[import.Name] = component;
        }
        return result;
    }

    private async Task<InlineComponentDefinition?> ResolveInlineComponentAsync(UxImportRef import, string hostPath, DiagnosticSink sink, CancellationToken ct, ISet<string> ancestry)
    {
        var componentPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(hostPath)!, import.Src));
        if (!ancestry.Add(componentPath))
        {
            sink.Error($"inline component import cycle detected at '{import.Name}'", import.Position);
            return null;
        }
        if (!File.Exists(componentPath))
        {
            sink.Error($"missing inline component {componentPath}", import.Position);
            return null;
        }
        var component = new WxamlParser().Parse(await File.ReadAllTextAsync(componentPath, ct), componentPath, sink);
        if (component.Component is null)
        {
            sink.Error($"inline import '{import.Name}' must reference a <Component>", import.Position);
            return null;
        }
        if (component.Imports.Any(nested => !nested.IsInline))
        {
            sink.Error($"inline component '{import.Name}' may only import inline components", import.Position);
            return null;
        }
        var scriptPath = Path.ChangeExtension(componentPath, ".js");
        var componentLogic = new ComponentScriptParser().Parse(File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath, ct) : "export default { data: {} }", scriptPath, sink);
        if (!IsInlineSafe(componentLogic))
        {
            sink.Error($"inline component '{import.Name}' may only declare props, an empty data object, lifecycle hooks, and methods", import.Position);
            return null;
        }
        var nested = new Dictionary<string, InlineComponentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var nestedImport in component.Imports)
        {
            var nestedComponent = await ResolveInlineComponentAsync(nestedImport, componentPath, sink, ct, new HashSet<string>(ancestry, StringComparer.OrdinalIgnoreCase));
            if (nestedComponent is not null) nested[nestedImport.Name] = nestedComponent;
        }
        return new InlineComponentDefinition(component, componentPath, componentLogic, nested);
    }

    private static bool IsInlineSafe(ComponentLogic logic)
    {
        if (logic.Imports.Count > 0 || logic.Consts.Count > 0 || logic.Functions.Count > 0 || logic.NamedExports.Count > 0) return false;
        return logic.ExportDefault?.Properties.All(property => property.Kind is JsPropertyKind.Props or JsPropertyKind.Method or JsPropertyKind.Lifecycle ||
            (property.Kind == JsPropertyKind.Data && property.RawValue == "{}")) ?? true;
    }

    private static UxStyleSheet? MergeInlineStyles(UxStyleSheet? host, IEnumerable<InlineComponentDefinition> components)
    {
        var sheets = FlattenInlineComponents(components).Select(component => component.Document.Styles).Append(host).Where(sheet => sheet is not null).Cast<UxStyleSheet>().ToArray();
        if (sheets.Length == 0) return null;
        return new UxStyleSheet(sheets.SelectMany(sheet => sheet.Rules).ToArray(), sheets.SelectMany(sheet => sheet.MediaRules ?? []).ToArray());
    }

    private static IEnumerable<InlineComponentDefinition> FlattenInlineComponents(IEnumerable<InlineComponentDefinition> components)
    {
        foreach (var component in components)
        {
            yield return component;
            foreach (var nested in FlattenInlineComponents(component.InlineComponents.Values)) yield return nested;
        }
    }

    private static IReadOnlyList<string> GeneratedClasses(StyleSelectorTransform transform, string tag)
        => transform.GeneratedClassFor(tag) is { } generatedClass ? [generatedClass] : [];

    private async Task BuildComponentAsync(UxImportRef import, string importerPath, CancellationToken ct)
    {
        var wxamlPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importerPath)!, import.Src));
        if (!_builtComponents.Add(wxamlPath)) return;
        var sink = new DiagnosticSink(_logger);
        if (!File.Exists(wxamlPath)) { sink.Error($"missing component {wxamlPath}", import.Position); _sink.Merge(sink.Diagnostics); return; }
        var doc = new WxamlParser().Parse(await File.ReadAllTextAsync(wxamlPath, ct), wxamlPath, sink);
        if (doc.Component is null) sink.Error($"import '{import.Name}' must reference a <Component>", import.Position);
        foreach (var nested in doc.Imports.Where(nested => !nested.IsInline)) await BuildComponentAsync(nested, wxamlPath, ct);
        var jsPath = Path.ChangeExtension(wxamlPath, ".js");
        var logic = new ComponentScriptParser().Parse(File.Exists(jsPath) ? await File.ReadAllTextAsync(jsPath, ct) : "export default { data: {} }", jsPath, sink);
        var inlineComponents = await ResolveInlineComponentsAsync(doc, wxamlPath, sink, ct);
        var inlineMerge = InlineComponentScriptMerger.Merge(logic, inlineComponents, doc);
        logic = inlineMerge.Logic;
        inlineComponents = inlineMerge.Components;
        var nameMinification = MinifyComponentMethodNames(logic, inlineComponents);
        logic = nameMinification.Logic;
        inlineComponents = nameMinification.Components;
        var inlineInvocationPlans = RewriteInlineInvocationPlans(inlineMerge.InvocationPlans, nameMinification.Names);
        var constants = ConstantTable.Build(logic, sink);
        var mergedStyles = MergeInlineStyles(doc.Styles, inlineComponents.Values);
        var styleSelectorTransform = StyleSelectorTransform.Create(mergedStyles, doc.Imports.Concat(FlattenInlineComponents(inlineComponents.Values).SelectMany(component => component.Document.Imports)));
        var template = new TemplateTranslator(constants, logic.Functions.Select(function => function.Name), wxamlPath, Path.Combine(_opts.ProjectPath, _opts.SourceRoot), sink, inlineComponents: inlineComponents, styleSelectorTransform: styleSelectorTransform, methodNames: nameMinification.Names, inlineInvocationPlans: inlineInvocationPlans).TranslateAst(doc.Children, generatedClasses: GeneratedClasses(styleSelectorTransform, "component"));
        var bundler = new RelativeModuleBundler(Path.Combine(_opts.ProjectPath, _opts.SourceRoot), sink);
        var output = Translation.ModuleEmitter.EmitComponent(doc, StyleTranslator.Translate(mergedStyles, styleSelectorTransform), ScriptTranslator.Translate(logic, isPage: false, item => item.IsRelative ? bundler.IdForImport(jsPath, item.Specifier) : null), template, constants.Entries.Values.ToList(), bundler.Bundle(logic, jsPath));
        var relative = Path.ChangeExtension(Path.GetRelativePath(Path.Combine(_opts.ProjectPath, _opts.SourceRoot), wxamlPath), ".js");
        var outPath = Path.Combine(_opts.ProjectPath, _opts.OutputDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, output, ct);
        CrossCheck(doc, logic, sink, nameMinification.Names);
        _sink.Merge(sink.Diagnostics);
    }

    private async Task BuildAppAsync(Manifest? manifest, CancellationToken ct)
    {
        var appJsPath = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "app.js");
        var appWxamlPath = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "app.wxaml");
        if (!File.Exists(appJsPath) && !File.Exists(appWxamlPath)) return;

        var sink = new DiagnosticSink(_logger);
        var jsText = File.Exists(appJsPath) ? await File.ReadAllTextAsync(appJsPath, ct) : "export default { data: {} }";
        var logic = new ComponentScriptParser().Parse(jsText, appJsPath, sink);
        var constTable = ConstantTable.Build(logic, sink);

        JsExpression styleCode = StyleTranslator.Translate(null);
        if (File.Exists(appWxamlPath))
        {
            var wxamlText = await File.ReadAllTextAsync(appWxamlPath, ct);
            var doc = new WxamlParser().Parse(wxamlText, appWxamlPath, sink);
            styleCode = StyleTranslator.Translate(doc.Styles, StyleSelectorTransform.Create(doc.Styles, doc.Imports));
        }

        var bundler = new RelativeModuleBundler(Path.Combine(_opts.ProjectPath, _opts.SourceRoot), sink);
        var bundledModules = bundler.Bundle(logic, appJsPath);
        var scriptCode = ScriptTranslator.Translate(logic, isPage: false,
            import => import.IsRelative ? bundler.IdForImport(appJsPath, import.Specifier) : null);
        var docForEmit = new UxDocument(null, null, new SourcePosition(appJsPath, 1, 1));
        var manifestJson = manifest is not null ? DeviceManifestEmitter.Emit(manifest) : null;
        var moduleCode = Translation.ModuleEmitter.EmitPage(docForEmit, styleCode, scriptCode, [], manifestJson, isPage: false, constTable.Entries.Values.ToList(), bundledModules);

        var outPath = Path.Combine(_opts.ProjectPath, _opts.OutputDir, "app.js");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, moduleCode, ct);
        _sink.Merge(sink.Diagnostics);
    }

    private void CopyResources()
    {
        var srcCommon = Path.Combine(_opts.ProjectPath, _opts.SourceRoot, "common");
        var dstCommon = Path.Combine(_opts.ProjectPath, _opts.OutputDir, "common");
        if (!Directory.Exists(srcCommon)) return;
        foreach (var file in Directory.GetFiles(srcCommon, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcCommon, file);
            var dst = Path.Combine(dstCommon, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    private async Task WriteDeviceManifestsAsync(Manifest manifest, CancellationToken ct)
    {
        // The runtime selects a device-specific manifest before it starts the
        // page router.  The reference build emits one for each declared
        // device type, even when no device-specific fields are present.
        var outputRoot = Path.Combine(_opts.ProjectPath, _opts.OutputDir);
        var root = JsonNode.Parse(DeviceManifestEmitter.Emit(manifest))?.AsObject()
            ?? throw new InvalidOperationException("manifest serialization produced no object");
        root.Remove("minAPILevel");
        root.Remove("packageInfo");
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        foreach (var deviceType in manifest.DeviceTypeList.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(deviceType)) continue;
            await File.WriteAllTextAsync(Path.Combine(outputRoot, $"manifest-{deviceType}.json"), json, ct);
        }
    }

    private void CopyRuntimeConfiguration()
    {
        var sourceRoot = Path.Combine(_opts.ProjectPath, _opts.SourceRoot);
        var outputRoot = Path.Combine(_opts.ProjectPath, _opts.OutputDir);
        if (!Directory.Exists(sourceRoot)) return;
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "config-*.json", SearchOption.TopDirectoryOnly))
            File.Copy(source, Path.Combine(outputRoot, Path.GetFileName(source)), overwrite: true);
    }

    private async Task<bool> CompileBytecodeAsync(CancellationToken ct)
    {
        var outputRoot = Path.Combine(_opts.ProjectPath, _opts.OutputDir);
        try
        {
            var bytecode = await new JavaScriptDirectoryCompiler().CompileAsync(
                new JavaScriptDirectoryCompilationRequest(outputRoot, outputRoot)
                {
                    // Match the target compiler's `-m -s` invocation: every
                    // generated file is emitted as stripped module bytecode.
                    CompileAsModules = true,
                    MinifyLocalBindings = _minifyIdentifiers
                }, ct);
            if (!_opts.KeepJavaScript)
            {
                foreach (var output in bytecode)
                {
                    var source = Path.ChangeExtension(output, ".js");
                    if (File.Exists(source)) File.Delete(source);
                }
            }
            _sink.Info("I-BLD-002", $"compiled {bytecode.Count} JavaScript module(s) to bytecode");
            return true;
        }
        catch (JavaScriptCompilationException exception)
        {
            _sink.Error($"bytecode compilation failed for {exception.FileName}: {exception.Message}");
            return false;
        }
        catch (Exception exception)
        {
            _sink.Error($"bytecode compilation failed: {exception.Message}");
            return false;
        }
    }

    private (ComponentLogic Logic, IReadOnlyDictionary<string, string> Names,
        IReadOnlyDictionary<string, InlineComponentDefinition> Components) MinifyComponentMethodNames(
            ComponentLogic logic, IReadOnlyDictionary<string, InlineComponentDefinition> inlineComponents)
    {
        if (!_minifyIdentifiers)
            return (logic, new Dictionary<string, string>(StringComparer.Ordinal), inlineComponents);

        return ComponentMethodNameMinifier.Minify(logic, inlineComponents);
    }

    private static IReadOnlyDictionary<SourcePosition, InlineInvocationPlan> RewriteInlineInvocationPlans(
        IReadOnlyDictionary<SourcePosition, InlineInvocationPlan> plans, IReadOnlyDictionary<string, string> names)
        => plans.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                MethodNames = pair.Value.MethodNames.ToDictionary(
                    method => method.Key,
                    method => names.TryGetValue(method.Value, out var renamed) ? renamed : method.Value,
                    StringComparer.Ordinal)
            });

    private static void CrossCheck(UxDocument doc, ComponentLogic logic, DiagnosticSink sink, IReadOnlyDictionary<string, string>? originalMethodNames = null)
    {
        var methods = new HashSet<string>(
            logic.ExportDefault?.Properties.Where(p => p.Kind is JsPropertyKind.Method or JsPropertyKind.Lifecycle).Select(p => p.Name) ?? [],
            StringComparer.Ordinal);
        if (originalMethodNames is not null) methods.UnionWith(originalMethodNames.Keys);
        var dataKeys = new HashSet<string>(StringComparer.Ordinal);
        var propNames = new HashSet<string>(StringComparer.Ordinal);
        var props = logic.ExportDefault?.Properties.FirstOrDefault(p => p.Kind == JsPropertyKind.Props);
        if (props is not null)
            foreach (Match match in Regex.Matches(props.RawValue, "[\"'](\\w+)[\"']"))
                propNames.Add(match.Groups[1].Value);
        dataKeys.UnionWith(propNames);
        if (doc.Component is not null) methods.UnionWith(propNames);
        foreach (var key in new[] { "data", "protected", "private", "public" })
        {
            var prop = logic.ExportDefault?.Properties.FirstOrDefault(p => p.Name == key);
            if (prop is not null)
                foreach (Match m in Regex.Matches(prop.RawValue, @"(\w+)\s*:"))
                    dataKeys.Add(m.Groups[1].Value);
        }

        void CheckNode(UxNode node, bool itemScope)
        {
            if (node is UxElement el)
            {
                foreach (var a in el.Attrs)
                {
                    bool isItemProp = itemScope && a.Value is Ast.BindingValue bv0 && bv0.ItemScope;
                    if (a.Kind == Ast.AttrKind.Event)
                    {
                        string raw = a.Value is Ast.LiteralValue lit ? lit.Text : a.Value is Ast.ExprValue ev ? ev.Expr : "";
                        var trimmed = raw.Trim();
                        if (trimmed.Contains("=>", StringComparison.Ordinal) || trimmed.StartsWith("function", StringComparison.Ordinal))
                            continue;
                        // Only direct method references/calls belong to the export-default
                        // method table. A member expression such as vmObj.handler is a
                        // runtime value and must not be rejected as a missing method.
                        var m = Regex.Match(trimmed, @"^([A-Za-z_$][A-Za-z0-9_$]*)(?:\s*\([^)]*\))?\s*$");
                        if (m.Success && !methods.Contains(m.Groups[1].Value))
                            sink.Error($"event method '{m.Groups[1].Value}' not found in export default", a.Position);
                    }
                    else if (!isItemProp && a.Value is Ast.BindingValue bv && !string.IsNullOrEmpty(bv.Path) && !bv.Path.StartsWith("$"))
                    {
                        var first = bv.Path.Split('.')[0];
                        if (!dataKeys.Contains(first) && !string.IsNullOrEmpty(first))
                            sink.Error($"binding '{bv.Path}' first segment '{first}' not in data", a.Position);
                    }
                }
                foreach (var c in el.Children) CheckNode(c, itemScope);
            }
            else if (node is UxListNode l)
            {
                if (l.ItemsSource is Ast.BindingValue bv && !string.IsNullOrEmpty(bv.Path) && !bv.Path.StartsWith("$"))
                {
                    var first = bv.Path.Split('.')[0];
                    if (!dataKeys.Contains(first))
                        sink.Error($"ItemsSource binding '{bv.Path}' not in data", l.Position);
                }
                CheckNode(l.ItemTemplateRoot, true);
            }
            else if (node is UxIfChain ch)
            {
                foreach (var br in ch.Branches)
                {
                    if (br.Test is Ast.BindingValue bv2 && !string.IsNullOrEmpty(bv2.Path))
                    {
                        var first = bv2.Path.Split('.')[0];
                        if (!dataKeys.Contains(first) && !bv2.ItemScope)
                            sink.Error($"If Test binding '{bv2.Path}' not in data", br.Position);
                    }
                    foreach (var c in br.Children) CheckNode(c, itemScope);
                }
            }
        }

        foreach (var n in doc.Children) CheckNode(n, false);

    }
}
