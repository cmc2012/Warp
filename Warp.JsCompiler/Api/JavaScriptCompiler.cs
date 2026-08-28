using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;
using Warp.JsCompiler.Pipeline;

namespace Warp.JsCompiler.Api;

/// <summary>
/// Managed compiler for the target ECMAScript 2021-03-27 bytecode wire format.
/// The emitted object is intended exclusively for that runtime version.
/// </summary>
public sealed class JavaScriptCompiler
{
    private readonly ExternalPasses _externalPasses;
    private readonly Action<JavaScriptCompilerWarning>? _warningSink;

    public JavaScriptCompiler(JavaScriptCompilerOptions? options = null)
    {
        _externalPasses = ExternalJavaScriptPassLoader.Load(options?.ExternalPassAssemblyPaths ?? []);
        _warningSink = options?.WarningSink;
    }

    public JavaScriptBytecode Compile(JavaScriptCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var kind = DetectKind(request);
        var program = ApplyAstPasses(new JavaScriptFrontEnd(request.Source, request.FileName, kind).Parse());
        if (program.StaticImports.Count != 0)
        {
            var import = program.StaticImports[0];
            throw new JavaScriptCompilationException(
                "The module contains static imports. Use CompileModuleGraph with an IJavaScriptModuleResolver.",
                request.FileName, import.Line, import.Column, "ECMA2001");
        }

        return new JavaScriptBytecode(JavaScriptCompilerPipeline.Compile(
            program, request.StripDebugInfo, request.MinifyLocalBindings,
            _externalPasses.Ir, _externalPasses.PostPseudoIr, _externalPasses.Assembly), request.FileName, kind);
    }

    public JavaScriptModuleGraph CompileModuleGraph(JavaScriptCompilationRequest entry, IJavaScriptModuleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(resolver);
        entry.Validate();

        var modules = new Dictionary<string, LoadedModule>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        // Static specifiers identify requests in the driver-facing module
        // graph.  Cache the resolved source before walking it so a diamond
        // dependency does not call an otherwise stateful resolver twice.
        var resolvedModules = new Dictionary<string, JavaScriptModuleSource>(StringComparer.Ordinal);
        LoadModule(entry, resolver, modules, visiting, resolvedModules);
        var warnings = CollectModuleGraphWarnings(modules);
        var ir = modules.ToDictionary(pair => pair.Key, pair => new ProgramIrLowerer().Run(pair.Value.Program), StringComparer.Ordinal);
        var dependencies = modules.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<int, string>)ir[pair.Key].RequiredModules.Select((specifier, index) => (specifier, index))
                .Where(item => pair.Value.Dependencies.TryGetValue(item.specifier, out _))
                .ToDictionary(item => item.index, item => pair.Value.Dependencies[item.specifier]), StringComparer.Ordinal);
        var graph = new IrModuleGraph(ir, entry.FileName, dependencies);
        if (entry.MinifyLocalBindings) new ModuleGraphBindingMinificationPass().Run(graph);
        foreach (var pass in _externalPasses.ModuleGraph) pass.Run(graph);
        var bytecode = modules.ToDictionary(pair => pair.Key, pair => new JavaScriptBytecode(JavaScriptCompilerPipeline.CompileIr(
            pair.Value.Program, graph.Modules[pair.Key], pair.Value.Request.StripDebugInfo, pair.Value.Request.MinifyLocalBindings,
            _externalPasses.Ir, _externalPasses.PostPseudoIr, _externalPasses.Assembly), pair.Key, pair.Value.Kind), StringComparer.Ordinal);
        return new JavaScriptModuleGraph(bytecode, warnings);
    }

    private void LoadModule(JavaScriptCompilationRequest request, IJavaScriptModuleResolver resolver,
        Dictionary<string, LoadedModule> modules, HashSet<string> visiting,
        Dictionary<string, JavaScriptModuleSource> resolvedModules)
    {
        if (modules.ContainsKey(request.FileName)) return;
        if (!visiting.Add(request.FileName)) return; // ES modules permit cycles.

        var kind = DetectKind(request);
        var program = ApplyAstPasses(new JavaScriptFrontEnd(request.Source, request.FileName, kind).Parse());
        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var import in program.StaticImports)
        {
            if (!resolvedModules.TryGetValue(import.Specifier, out var dependency))
            {
                try { dependency = resolver.Resolve(import.Specifier, request.FileName); }
                catch (JavaScriptCompilationException) { throw; }
                catch (Exception e)
                {
                    throw new JavaScriptCompilationException($"Unable to resolve module '{import.Specifier}'.", request.FileName,
                        import.Line, import.Column, "ECMA2002", e);
                }
                if (string.IsNullOrWhiteSpace(dependency.CanonicalName))
                    throw new JavaScriptCompilationException("A module resolver must return a canonical module name.", request.FileName,
                        import.Line, import.Column, "ECMA2003");
                resolvedModules.Add(import.Specifier, dependency);
            }
            if (!dependency.IsExternal) dependencies[import.Specifier] = dependency.CanonicalName;
            if (dependency.IsExternal) continue;
            LoadModule(new JavaScriptCompilationRequest(dependency.Source, dependency.CanonicalName, JavaScriptSourceKind.Module)
            {
                StripDebugInfo = request.StripDebugInfo,
                MinifyLocalBindings = request.MinifyLocalBindings,
            }, resolver, modules, visiting, resolvedModules);
        }

        modules.Add(request.FileName, new LoadedModule(request, program, kind, dependencies));
        visiting.Remove(request.FileName);
    }

    private JavaScriptProgram ApplyAstPasses(JavaScriptProgram program)
    {
        foreach (var pass in _externalPasses.Ast)
            program = program with { Ast = pass.Run(program.Ast) ??
                throw new InvalidOperationException($"AST pass {pass.GetType().Name} returned null.") };
        var context = new JavaScriptAstPassContext(program.Source, program.FileName, program.Kind);
        foreach (var pass in _externalPasses.ContextualAst)
            program = program with { Ast = pass.Run(program.Ast, context) ??
                throw new InvalidOperationException($"AST pass {pass.GetType().Name} returned null.") };
        // External AST passes may introduce lexical bindings.  Their output is
        // canonical AST, so rebuild scope metadata before lowering rather than
        // leaving the parser's pre-transform analysis attached to it.
        return program with { Scopes = new JavaScriptScopeAnalyzer(program.FileName).Analyze(program.Ast) };
    }

    private sealed record LoadedModule(JavaScriptCompilationRequest Request, JavaScriptProgram Program, JavaScriptSourceKind Kind,
        IReadOnlyDictionary<string, string> Dependencies);

    private List<JavaScriptCompilerWarning> CollectModuleGraphWarnings(IReadOnlyDictionary<string, LoadedModule> modules)
    {
        var warnings = new List<JavaScriptCompilerWarning>();
        foreach (var module in modules.Values)
        foreach (var statement in module.Program.Ast.Body)
        {
            switch (statement)
            {
                case JsExportAllStatement exportAll:
                    ReportWarning(warnings, new JavaScriptCompilerWarning(
                        "Avoid 'export *': it can force exported property names to remain stable across a public module boundary. Prefer explicit named re-exports.",
                        module.Request.FileName, exportAll.Line, exportAll.Column, "WARP3001"));
                    break;
                case JsImportStatement import:
                    foreach (var binding in import.Bindings.Where(binding => binding.Kind == JsImportBindingKind.Namespace))
                        ReportWarning(warnings, new JavaScriptCompilerWarning(
                            "Avoid namespace imports ('import * as ...'): dynamically accessed properties require exported names to remain stable. Prefer explicit named imports.",
                            module.Request.FileName, binding.Line, binding.Column, "WARP3002"));
                    break;
            }
        }
        return warnings;
    }

    private void ReportWarning(List<JavaScriptCompilerWarning> warnings, JavaScriptCompilerWarning warning)
    {
        warnings.Add(warning);
        _warningSink?.Invoke(warning);
    }

    private static JavaScriptSourceKind DetectKind(JavaScriptCompilationRequest request)
    {
        if (request.Kind != JavaScriptSourceKind.Auto) return request.Kind;
        if (request.FileName.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)) return JavaScriptSourceKind.Module;
        var tokens = new JavaScriptScanner(request.Source, request.FileName).Scan();
        return JavaScriptScanner.HasModuleSyntax(tokens) ? JavaScriptSourceKind.Module : JavaScriptSourceKind.Script;
    }
}
