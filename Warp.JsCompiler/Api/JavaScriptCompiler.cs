using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Pipeline;

namespace Warp.JsCompiler.Api;

/// <summary>
/// Managed compiler for the target ECMAScript 2021-03-27 bytecode wire format.
/// The emitted object is intended exclusively for that runtime version.
/// </summary>
public sealed class JavaScriptCompiler
{
    public JavaScriptBytecode Compile(JavaScriptCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        var kind = DetectKind(request);
        var frontend = new JavaScriptFrontEnd(request.Source, request.FileName, kind);
        var program = frontend.Parse();
        if (program.StaticImports.Count != 0)
        {
            var import = program.StaticImports[0];
            throw new JavaScriptCompilationException(
                "The module contains static imports. Use CompileModuleGraph with an IJavaScriptModuleResolver.",
                request.FileName, import.Line, import.Column, "ECMA2001");
        }

        return new JavaScriptBytecode(JavaScriptCompilerPipeline.Compile(program, request.StripDebugInfo), request.FileName, kind);
    }

    public JavaScriptModuleGraph CompileModuleGraph(JavaScriptCompilationRequest entry, IJavaScriptModuleResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(resolver);
        entry.Validate();

        var modules = new Dictionary<string, JavaScriptBytecode>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        // Static specifiers identify requests in the driver-facing module
        // graph.  Cache the resolved source before walking it so a diamond
        // dependency does not call an otherwise stateful resolver twice.
        var resolvedModules = new Dictionary<string, JavaScriptModuleSource>(StringComparer.Ordinal);
        CompileModule(entry, resolver, modules, visiting, resolvedModules);
        return new JavaScriptModuleGraph(modules);
    }

    private static void CompileModule(JavaScriptCompilationRequest request, IJavaScriptModuleResolver resolver,
        Dictionary<string, JavaScriptBytecode> modules, HashSet<string> visiting,
        Dictionary<string, JavaScriptModuleSource> resolvedModules)
    {
        if (modules.ContainsKey(request.FileName)) return;
        if (!visiting.Add(request.FileName)) return; // ES modules permit cycles.

        var kind = DetectKind(request);
        var program = new JavaScriptFrontEnd(request.Source, request.FileName, kind).Parse();
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
            if (dependency.IsExternal) continue;
            CompileModule(new JavaScriptCompilationRequest(dependency.Source, dependency.CanonicalName, JavaScriptSourceKind.Module)
            {
                StripDebugInfo = request.StripDebugInfo,
            }, resolver, modules, visiting, resolvedModules);
        }

        modules.Add(request.FileName, new JavaScriptBytecode(JavaScriptCompilerPipeline.Compile(program, request.StripDebugInfo), request.FileName, kind));
        visiting.Remove(request.FileName);
    }

    private static JavaScriptSourceKind DetectKind(JavaScriptCompilationRequest request)
    {
        if (request.Kind != JavaScriptSourceKind.Auto) return request.Kind;
        if (request.FileName.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)) return JavaScriptSourceKind.Module;
        var tokens = new JavaScriptScanner(request.Source, request.FileName).Scan();
        return JavaScriptScanner.HasModuleSyntax(tokens) ? JavaScriptSourceKind.Module : JavaScriptSourceKind.Script;
    }
}
