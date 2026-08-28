namespace Warp.JsCompiler.Ir.Passes;

/// <summary>
/// Shortens function-local bindings after pseudo bindings exist but before slot
/// and closure resolution.
/// </summary>
public sealed class LocalBindingMinificationPass(bool includeModuleBindings = false) : IIrPass
{
    private readonly bool _includeModuleBindings = includeModuleBindings;
    private static readonly HashSet<string> RuntimeNames = new(StringComparer.Ordinal)
    {
        "this", "arguments", "new.target", "home_object", "this_active_func", "class_fields_init",
    };

    public void Run(IrModule module)
    {
        var functions = module.Functions.ToDictionary(function => function.Id);
        var exportedLocals = module.Exports.OfType<IrLocalExport>().Select(export => export.LocalName)
            .ToHashSet(StringComparer.Ordinal);
        // Never introduce a name which already occurs in source IR. This also
        // prevents a renamed local from capturing an unresolved global access.
        var reserved = module.Functions.SelectMany(function => function.Blocks)
            .SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Operation.StartsWith("scope_", StringComparison.Ordinal))
            .Select(instruction => instruction.Operands.FirstOrDefault())
            .OfType<AtomOperand>().Select(atom => atom.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var binding in module.Functions.SelectMany(function => function.Bindings)) reserved.Add(binding.Name);

        var names = new Dictionary<(IrFunctionId Function, IrBindingId Binding), string>();
        foreach (var function in module.Functions.Where(function => _includeModuleBindings || IsLocalFunction(function)))
        {
            var next = 0;
            foreach (var binding in function.Bindings.Where(binding => IsMinifiable(binding) &&
                         (function.Options.Form is not (IrFunctionForm.Module or IrFunctionForm.Script) || !exportedLocals.Contains(binding.Name))))
            {
                string name;
                do { name = ShortName(next++); } while (!reserved.Add(name));
                names[(function.Id, binding.Id)] = name;
            }
        }
        if (names.Count == 0) return;

        foreach (var function in module.Functions)
            foreach (var block in function.Blocks)
                for (var index = 0; index < block.Instructions.Count; index++)
                    block.Instructions[index] = RewriteInstruction(function, block.Instructions[index], functions, names);

        foreach (var function in module.Functions)
            for (var index = 0; index < function.Bindings.Count; index++)
            {
                var binding = function.Bindings[index];
                if (names.TryGetValue((function.Id, binding.Id), out var name))
                    function.Bindings[index] = binding with { Name = name };
            }
    }

    private static bool IsLocalFunction(IrFunction function)
        => function.Options.Form is not (IrFunctionForm.Module or IrFunctionForm.Script);

    private static bool IsMinifiable(IrBinding binding)
        => binding.Kind is IrBindingKind.Normal or IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration or IrBindingKind.Catch or IrBindingKind.FunctionName
           && !RuntimeNames.Contains(binding.Name) && !binding.Name.Contains('<', StringComparison.Ordinal);

    private static IrInstruction RewriteInstruction(IrFunction function, IrInstruction instruction,
        IReadOnlyDictionary<IrFunctionId, IrFunction> functions,
        IReadOnlyDictionary<(IrFunctionId Function, IrBindingId Binding), string> names)
    {
        if (!instruction.Operation.StartsWith("scope_", StringComparison.Ordinal) ||
            instruction.Operands.FirstOrDefault() is not AtomOperand atom ||
            instruction.Operands.OfType<IrScopeOperand>().FirstOrDefault() is not { } scope ||
            Resolve(function, scope.Scope, atom.Value, functions) is not { } binding ||
            !names.TryGetValue((binding.Function, binding.Binding.Id), out var name)) return instruction;
        var operands = instruction.Operands.ToArray();
        operands[0] = new AtomOperand(name, atom.IsEmptyStringAtom);
        return instruction with { Operands = operands };
    }

    private static (IrFunctionId Function, IrBinding Binding)? Resolve(IrFunction function, IrScopeId scope, string name,
        IReadOnlyDictionary<IrFunctionId, IrFunction> functions)
    {
        for (var currentFunction = function; ;)
        {
            var scopes = currentFunction.Scopes.ToDictionary(item => item.Id);
            for (IrScopeId? currentScope = scope; currentScope is { } id; currentScope = scopes[id].Parent)
            {
                foreach (var bindingId in scopes[id].Bindings)
                {
                    var binding = currentFunction.Bindings.Single(item => item.Id == bindingId);
                    if (binding.Name == name) return (currentFunction.Id, binding);
                }
            }
            if (currentFunction.ParentFunction is not { } parent || currentFunction.ParentScope is not { } parentScope) return null;
            currentFunction = functions[parent];
            scope = parentScope;
        }
    }

    private static string ShortName(int index)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var result = "";
        do { result = alphabet[index % alphabet.Length] + result; index = index / alphabet.Length - 1; } while (index >= 0);
        return result;
    }
}
