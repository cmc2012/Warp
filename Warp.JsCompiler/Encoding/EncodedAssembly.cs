using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.ObjectFormat;

namespace Warp.JsCompiler.Encoding;

/// <summary>Maps encoded assembly to the object writer DTO without observing frontend AST or IR.</summary>
internal sealed class EncodedAssemblyObjectPass
{
    internal BytecodeObjectValue Run(EncodedAssemblyProgram assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var functions = assembly.Functions.ToDictionary(function => function.Id);
        if (!functions.ContainsKey(assembly.Entry)) throw new InvalidOperationException("Encoded entry function does not exist.");
        var active = new HashSet<BytecodeAssemblyFunctionId>();
        IrFunctionObject ConvertFunction(BytecodeAssemblyFunctionId id)
        {
            if (!functions.TryGetValue(id, out var encoded)) throw new InvalidOperationException("Missing encoded function.");
            if (!active.Add(id)) throw new InvalidOperationException("Function constants form a cycle.");
            var metadata = encoded.Metadata;
            var locals = metadata.Locals ?? [];
            var constants = encoded.Constants.OrderBy(constant => constant.Id.Value).ToArray();
            for (var index = 0; index < constants.Length; index++)
                if (constants[index].Id.Value != index) throw new InvalidOperationException("Constant ids must be contiguous.");
            var value = new IrFunctionObject(Atom(encoded.Name), encoded.Code,
                encoded.AtomRelocations.Select(item => new BytecodeObjectAtomRelocation(item.OperandOffset, Atom(item.Atom))).ToArray(),
                metadata.ArgumentCount,
                metadata.VariableCount ?? checked((ushort)(locals.Count - metadata.ArgumentCount)),
                metadata.DefinedArgumentCount,
                metadata.MaximumStackSize,
                metadata.SerializeVariableDefinitions
                    ? locals.Select(local => new BytecodeObjectVariable(
                        local.Name is { } name ? Atom(name) : BytecodeObjectAtom.Predefined(0),
                        local.ScopeLevel, local.ScopeNext, VariableKind(local.Kind), local.IsConst,
                        local.IsLexical, local.IsCaptured)).ToArray()
                    : null,
                (metadata.Closures ?? []).Select(closure => new BytecodeObjectClosure(Atom(closure.Name), closure.ParentIndex,
                    VariableKind(closure.Kind), closure.IsLocal, closure.IsArgument, closure.IsConst, closure.IsLexical)).ToArray(),
                constants.Select(constant => Constant(constant, ConvertFunction)).ToArray(),
                metadata.DebugInfo is { } debug ? new BytecodeObjectDebugInfo(Atom(debug.FileName), debug.LineNumber, debug.PcToLine) : null,
                metadata.JsMode, metadata.HasPrototype, metadata.HasSimpleParameterList, metadata.IsDerivedConstructor,
                metadata.NeedsHomeObject, (BytecodeObjectFunctionKind)metadata.Kind, metadata.NewTargetAllowed,
                metadata.SuperCallAllowed, metadata.SuperAllowed, metadata.ArgumentsAllowed);
            active.Remove(id);
            return value;
        }

        var entry = ConvertFunction(assembly.Entry);
        if (assembly.Module is not { } module) return entry;
        return new BytecodeModuleValue(Atom(module.Name), entry,
            (module.RequiredModules ?? []).Select(Atom).ToArray(),
            (module.Exports ?? []).Select(Export).ToArray(),
            (module.StarExports ?? []).Select(item => new BytecodeObjectStarExport(item.RequiredModuleIndex)).ToArray(),
            (module.Imports ?? []).Select(item => new BytecodeObjectImport(item.VariableIndex, Atom(item.ImportName), item.RequiredModuleIndex)).ToArray());
    }

    private static BytecodeObjectValue Constant(BytecodeAssemblyConstant constant,
        Func<BytecodeAssemblyFunctionId, IrFunctionObject> function) => constant switch
    {
        BytecodeAssemblyNumberConstant number => new BytecodeFloatValue(number.Value),
        BytecodeAssemblyStringConstant text => new BytecodeStringValue(text.Value),
        BytecodeAssemblyRegExpPatternConstant pattern => new BytecodeStringValue(pattern.Value),
        BytecodeAssemblyRegExpBytecodeConstant bytecode => new BytecodeStringValue(bytecode.Bytes),
        BytecodeAssemblyFunctionConstant child => function(child.Function),
        BytecodeAssemblyTemplateConstant template => new BytecodeTemplateValue(template.Cooked, template.Raw),
        _ => throw new NotSupportedException($"Unsupported assembly constant {constant.GetType().Name}.")
    };

    private static BytecodeObjectExport Export(BytecodeAssemblyExport export) => export switch
    {
        BytecodeAssemblyLocalExport local => new BytecodeObjectLocalExport(local.VariableIndex, Atom(local.ExportName)),
        BytecodeAssemblyIndirectExport indirect => new BytecodeObjectIndirectExport(indirect.RequiredModuleIndex,
            Atom(indirect.LocalName), Atom(indirect.ExportName)),
        _ => throw new NotSupportedException($"Unsupported assembly export {export.GetType().Name}.")
    };

    private static BytecodeObjectVariableKind VariableKind(BytecodeAssemblyVariableKind kind) => (BytecodeObjectVariableKind)kind;
    private static BytecodeObjectAtom Atom(BytecodeAssemblyAtom atom) => atom.Kind switch
    {
        BytecodeAssemblyAtomKind.Predefined => BytecodeObjectAtom.Predefined(atom.PredefinedId),
        BytecodeAssemblyAtomKind.Symbol => BytecodeObjectAtom.Dynamic(atom.Symbol!),
        BytecodeAssemblyAtomKind.TaggedInteger => BytecodeObjectAtom.TaggedInteger(atom.PredefinedId),
        _ => throw new InvalidOperationException("Unknown assembly atom kind.")
    };
}
