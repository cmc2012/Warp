namespace Warp.JsCompiler.Ir.Passes;

/// <summary>
/// Graph-level minification of module-private bindings. Export names remain
/// untouched, so importers retain their ES-module contract even across cycles.
/// </summary>
internal sealed class ModuleGraphBindingMinificationPass : IModuleGraphPass
{
    public void Run(IrModuleGraph graph)
    {
        foreach (var module in graph.Modules.Values)
            new LocalBindingMinificationPass(includeModuleBindings: true).Run(module);
        new InternalModuleExportMinificationPass().Run(graph);
    }
}

/// <summary>Renames exports that never cross the resolved graph boundary.</summary>
internal sealed class InternalModuleExportMinificationPass : IModuleGraphPass
{
    public void Run(IrModuleGraph graph)
    {
        var starContractTargets = RuntimeNameContractTargets(graph);
        var directNames = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (moduleName, module) in graph.Modules)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var next = 0;
            foreach (var export in module.Exports)
            {
                if (map.ContainsKey(export.ExportName)) continue;
                map[export.ExportName] = moduleName == graph.EntryModule || starContractTargets.Contains(moduleName)
                    ? export.ExportName : ShortName(next++);
            }
            directNames[moduleName] = map;
        }
        var names = EffectiveExportNames(graph, directNames);

        foreach (var (moduleName, module) in graph.Modules)
        {
            if (directNames.TryGetValue(moduleName, out var ownNames))
                for (var index = 0; index < module.Exports.Count; index++)
                    module.Exports[index] = RenameExport(module.Exports[index], ownNames);

            var dependencies = graph.Dependencies.TryGetValue(moduleName, out var edges) ? edges : null;
            if (dependencies is null) continue;
            for (var index = 0; index < module.Imports.Count; index++)
            {
                var import = module.Imports[index];
                if (dependencies.TryGetValue(import.RequiredModuleIndex, out var target) && names.TryGetValue(target, out var targetNames) &&
                    targetNames.TryGetValue(import.ImportName, out var renamed))
                    module.Imports[index] = import with { ImportName = renamed };
            }
            for (var index = 0; index < module.Exports.Count; index++)
            {
                if (module.Exports[index] is not IrIndirectExport indirect || !dependencies.TryGetValue(indirect.RequiredModuleIndex, out var target) ||
                    !names.TryGetValue(target, out var targetNames) || !targetNames.TryGetValue(indirect.LocalName, out var renamed)) continue;
                module.Exports[index] = indirect with { LocalName = renamed };
            }
        }
    }

    private static HashSet<string> RuntimeNameContractTargets(IrModuleGraph graph)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string moduleName)
        {
            if (!graph.Modules.TryGetValue(moduleName, out var module) || !graph.Dependencies.TryGetValue(moduleName, out var edges)) return;
            foreach (var index in module.StarExports)
                if (edges.TryGetValue(index, out var target) && result.Add(target)) Visit(target);
        }
        Visit(graph.EntryModule);
        foreach (var (moduleName, module) in graph.Modules)
            if (graph.Dependencies.TryGetValue(moduleName, out var edges))
                foreach (var import in module.Imports.Where(import => import.IsNamespace))
                    if (edges.TryGetValue(import.RequiredModuleIndex, out var target) && result.Add(target)) Visit(target);
        return result;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EffectiveExportNames(IrModuleGraph graph,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> directNames)
    {
        var current = directNames.ToDictionary(pair => pair.Key, pair => new Dictionary<string, string>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        for (var iteration = 0; iteration < graph.Modules.Count; iteration++)
        {
            var next = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var (moduleName, module) in graph.Modules)
            {
                var map = new Dictionary<string, string>(directNames[moduleName], StringComparer.Ordinal);
                var direct = new HashSet<string>(map.Keys, StringComparer.Ordinal);
                var blocked = new HashSet<string>(StringComparer.Ordinal);
                if (graph.Dependencies.TryGetValue(moduleName, out var edges))
                    foreach (var index in module.StarExports)
                        if (edges.TryGetValue(index, out var target) && current.TryGetValue(target, out var targetNames))
                            foreach (var (original, renamed) in targetNames)
                            {
                                if (direct.Contains(original) || blocked.Contains(original)) continue;
                                if (map.TryGetValue(original, out var previous) && previous != renamed)
                                {
                                    map.Remove(original);
                                    blocked.Add(original);
                                }
                                else map[original] = renamed;
                            }
                next[moduleName] = map;
            }
            if (Same(current, next)) return next.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal);
            current = next;
        }
        return current.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal);
    }

    private static bool Same(IReadOnlyDictionary<string, Dictionary<string, string>> left,
        IReadOnlyDictionary<string, Dictionary<string, string>> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Count == value.Count && pair.Value.All(item => value.TryGetValue(item.Key, out var current) && current == item.Value));

    private static IrExport RenameExport(IrExport export, IReadOnlyDictionary<string, string> names)
        => names.TryGetValue(export.ExportName, out var renamed) ? export switch
        {
            // ExportName belongs to IrExport's primary constructor. Updating
            // only the derived record property via `with` leaves that base
            // value unchanged, which in turn makes bytecode lowering retain
            // the original runtime export name.
            IrLocalExport local => new IrLocalExport(local.LocalName, renamed),
            IrIndirectExport indirect => new IrIndirectExport(indirect.RequiredModuleIndex, indirect.LocalName, renamed),
            _ => export
        } : export;

    private static string ShortName(int index)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var value = "";
        do { value = alphabet[index % alphabet.Length] + value; index = index / alphabet.Length - 1; } while (index >= 0);
        return value;
    }
}
