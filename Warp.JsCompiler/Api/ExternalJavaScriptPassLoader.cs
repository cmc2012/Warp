using System.Reflection;
using System.Runtime.Loader;
using ReflectionAssembly = System.Reflection.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir.Passes;

namespace Warp.JsCompiler.Api;

internal static class ExternalJavaScriptPassLoader
{
    internal static ExternalPasses Load(IEnumerable<string> assemblyPaths)
    {
        var astPasses = new List<IJavaScriptAstPass>();
        var contextualAstPasses = new List<IJavaScriptAstPassWithContext>();
        var irPasses = new List<IIrPass>();
        var postPseudoIrPasses = new List<IPostPseudoIrPass>();
        var moduleGraphPasses = new List<IModuleGraphPass>();
        var assemblyPasses = new List<IBytecodeAssemblyPass>();
        foreach (var assemblyPath in assemblyPaths)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("An external pass assembly path cannot be empty.", nameof(assemblyPaths));

            var fullPath = Path.GetFullPath(assemblyPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("External pass assembly was not found.", fullPath);

            var assembly = new ExternalPassLoadContext(fullPath).LoadFromAssemblyPath(fullPath);
            foreach (var type in GetLoadableTypes(assembly)
                         .Where(type => type is { IsAbstract: false, IsInterface: false, IsPublic: true }
                             && (typeof(IJavaScriptAstPass).IsAssignableFrom(type) || typeof(IJavaScriptAstPassWithContext).IsAssignableFrom(type) || typeof(IIrPass).IsAssignableFrom(type) || typeof(IPostPseudoIrPass).IsAssignableFrom(type) || typeof(IModuleGraphPass).IsAssignableFrom(type) || typeof(IBytecodeAssemblyPass).IsAssignableFrom(type))))
            {
                if (Activator.CreateInstance(type) is not { } pass)
                    throw new InvalidOperationException($"Could not create external pass '{type.FullName}'.");
                if (pass is IJavaScriptAstPass astPass) astPasses.Add(astPass);
                if (pass is IJavaScriptAstPassWithContext contextualAstPass) contextualAstPasses.Add(contextualAstPass);
                if (pass is IIrPass irPass) irPasses.Add(irPass);
                if (pass is IPostPseudoIrPass postPseudoIrPass) postPseudoIrPasses.Add(postPseudoIrPass);
                if (pass is IModuleGraphPass graphPass) moduleGraphPasses.Add(graphPass);
                if (pass is IBytecodeAssemblyPass assemblyPass) assemblyPasses.Add(assemblyPass);
            }
        }
        return new ExternalPasses(astPasses, contextualAstPasses, irPasses, postPseudoIrPasses, moduleGraphPasses, assemblyPasses);
    }

    private static IEnumerable<Type> GetLoadableTypes(ReflectionAssembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException exception)
        {
            var details = string.Join(Environment.NewLine, exception.LoaderExceptions
                .Where(error => error is not null).Select(error => error!.Message));
            throw new InvalidOperationException($"Unable to load external passes from '{assembly.Location}'. {details}", exception);
        }
    }

    private sealed class ExternalPassLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override ReflectionAssembly? Load(AssemblyName assemblyName)
        {
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, typeof(IIrPass).Assembly.GetName()))
                return typeof(IIrPass).Assembly;

            var dependencyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return dependencyPath is null ? null : LoadFromAssemblyPath(dependencyPath);
        }
    }
}

internal sealed record ExternalPasses(IReadOnlyList<IJavaScriptAstPass> Ast, IReadOnlyList<IJavaScriptAstPassWithContext> ContextualAst,
    IReadOnlyList<IIrPass> Ir,
    IReadOnlyList<IPostPseudoIrPass> PostPseudoIr,
    IReadOnlyList<IModuleGraphPass> ModuleGraph,
    IReadOnlyList<IBytecodeAssemblyPass> Assembly);
