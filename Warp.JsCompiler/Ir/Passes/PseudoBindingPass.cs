namespace Warp.JsCompiler.Ir.Passes;

/// <summary>
/// Materializes frame pseudo variables after AST construction.
///
/// The parser records pseudo-variable uses as ordinary symbolic accesses.  A
/// dedicated pass then creates a local only when a use actually survives in a
/// function (and forwards an arrow use to its nearest non-arrow owner).  This
/// preserves the source compiler's lazy pseudo-variable allocation and keeps
/// function metadata independent of syntactic class membership.
/// </summary>
internal sealed class PseudoBindingPass : IIrPass
{
    private static readonly HashSet<string> PseudoNames = new(StringComparer.Ordinal)
    {
        "this", "home_object", "this_active_func", "new.target", "arguments",
    };

    public void Run(IrModule module)
    {
        var functions = module.Functions.ToDictionary(function => function.Id);
        foreach (var function in module.Functions)
        {
            foreach (var name in ReferencedPseudoNames(function))
            {
                var owner = FindOwner(functions, function, name);
                if (owner is not null) EnsureBinding(owner, name);
            }
        }
        // A direct eval has an implicit lexical use of the enclosing frame's
        // special bindings.  It is not represented by a scope_get_var in the
        // arrow's IR, so materialize it explicitly here, after every parent
        // function and scope table is available.  This mirrors
        // add_eval_variables(): walk outward until the first owner of each
        // binding category, rather than assigning the bindings to the arrow
        // activation itself.
        foreach (var evaluator in module.Functions.Where(HasDirectEval))
            EnsureEvalEnvironment(functions, evaluator);
    }

    private static bool HasDirectEval(IrFunction function) =>
        function.Blocks.SelectMany(block => block.Instructions)
            .Any(instruction => instruction.Operation == "eval");

    private static void EnsureEvalEnvironment(IReadOnlyDictionary<IrFunctionId, IrFunction> functions,
        IrFunction evaluator)
    {
        var needsThis = true;
        var needsArguments = true;
        for (var current = evaluator; ;)
        {
            if (needsThis && current.Options.HasThisBinding)
            {
                EnsureBinding(current, "this");
                EnsureBinding(current, "new.target");
                if (current.Options.Form == IrFunctionForm.DerivedClassConstructor)
                    EnsureBinding(current, "this_active_func");
                if (current.Options.HasHomeObject) EnsureBinding(current, "home_object");
                needsThis = false;
            }
            if (needsArguments && current.Options.HasArgumentsBinding)
            {
                EnsureBinding(current, "arguments");
                needsArguments = false;
            }
            if (!needsThis && !needsArguments) return;
            if (current.ParentFunction is not { } parent) return;
            current = functions[parent];
        }
    }

    private static IEnumerable<string> ReferencedPseudoNames(IrFunction function) =>
        function.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Operation.StartsWith("scope_", StringComparison.Ordinal))
            .Select(instruction => instruction.Operands.FirstOrDefault())
            .OfType<AtomOperand>()
            .Select(operand => operand.Value)
            .Where(PseudoNames.Contains)
            .Distinct(StringComparer.Ordinal);

    private static IrFunction? FindOwner(IReadOnlyDictionary<IrFunctionId, IrFunction> functions,
        IrFunction function, string name)
    {
        var owner = function;
        while (!OwnsPseudoBinding(owner, name))
        {
            if (owner.ParentFunction is not { } parent)
            {
                // At module or script top level `arguments` is an ordinary
                // unresolved global name, not a function-frame pseudo
                // variable.  Direct eval and global lookups therefore keep
                // the usual get_var path.
                if (name == "arguments") return null;
                throw new InvalidOperationException($"Pseudo variable '{name}' has no owning function.");
            }
            owner = functions[parent];
        }
        return owner;
    }

    private static bool OwnsPseudoBinding(IrFunction function, string name) => name == "arguments"
        ? function.Options.HasArgumentsBinding
        : function.Options.HasThisBinding;

    private static void EnsureBinding(IrFunction function, string name)
    {
        if (function.Bindings.Any(binding => binding.Name == name)) return;
        var id = new IrBindingId(function.Bindings.Count == 0
            ? 0
            : function.Bindings.Max(binding => binding.Id.Value) + 1);
        var isDerivedThis = name == "this" && function.Options.Form == IrFunctionForm.DerivedClassConstructor;
        function.Bindings.Add(new IrBinding(id, name, function.ArgumentScope, IrBindingKind.Normal,
            IsLexical: isDerivedThis));
        var scopeIndex = function.Scopes.FindIndex(scope => scope.Id == function.ArgumentScope);
        var scope = function.Scopes[scopeIndex];
        function.Scopes[scopeIndex] = scope with { Bindings = scope.Bindings.Prepend(id).ToArray() };
    }
}
