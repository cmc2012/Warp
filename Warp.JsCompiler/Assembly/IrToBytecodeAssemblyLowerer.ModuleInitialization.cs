using Warp.JsCompiler.Ir;

namespace Warp.JsCompiler.Assembly;

internal sealed partial class IrToBytecodeAssemblyLowerer
{
    private static HashSet<IrInstruction> FindScriptInstantiationInstructions(FunctionLowering state,
        IReadOnlyList<IrBlock> blocks)
    {
        var result = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
        foreach (var block in blocks)
        {
            for (var index = 0; index + 1 < block.Instructions.Count; index++)
            {
                var closure = block.Instructions[index];
                var hasName = block.Instructions[index + 1].Operation == "set_name";
                if (hasName && index + 2 >= block.Instructions.Count) continue;
                var initialization = block.Instructions[index + (hasName ? 2 : 1)];
                if (closure.Operation != "fclosure" || initialization.Operation != "scope_put_var_init") continue;
                var (name, scope) = Symbol(initialization);
                var binding = FindLocal(state, name, scope);
                if (binding?.Kind is not (IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration) ||
                    !IsScriptGlobal(state, binding)) continue;
                result.Add(closure);
                if (hasName) result.Add(block.Instructions[index + 1]);
                result.Add(initialization);
                index += hasName ? 2 : 1;
            }
        }
        return result;
    }

    private static void EmitScriptInstantiation(FunctionLowering state, IReadOnlySet<IrInstruction> instantiation)
    {
        foreach (var binding in state.Source.Bindings.Where(binding => IsScriptGlobal(state, binding)))
        {
            var checkFlags = binding.Kind is IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration
                ? (ushort)0x40
                : (ushort)0;
            AddAtom(state, "check_define_var", binding.Name, checkFlags, BytecodeAssemblySourceLocation.None);
            if (binding.Kind is IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration)
            {
                foreach (var block in state.Source.Blocks)
                    for (var index = 0; index + 1 < block.Instructions.Count; index++)
                        if (instantiation.Contains(block.Instructions[index]) &&
                            block.Instructions[index].Operation == "fclosure" &&
                            Symbol(block.Instructions[index + 1]).Name == binding.Name)
                        {
                            LowerInstruction(state, block.Instructions[index]);
                            AddAtom(state, "define_func", binding.Name, location: BytecodeAssemblySourceLocation.None);
                            goto NextBinding;
                        }
            }
            else AddAtom(state, "define_var", binding.Name, location: BytecodeAssemblySourceLocation.None);
        NextBinding:;
        }
    }

    private static HashSet<IrInstruction> FindModuleInstantiationInstructions(FunctionLowering state,
        IReadOnlyList<IrBlock> blocks)
    {
        var result = new HashSet<IrInstruction>(ReferenceEqualityComparer.Instance);
        foreach (var block in blocks)
        {
            for (var index = 0; index + 1 < block.Instructions.Count; index++)
            {
                var closure = block.Instructions[index];
                var hasName = block.Instructions[index + 1].Operation == "set_name";
                if (hasName && index + 2 >= block.Instructions.Count) continue;
                var initialization = block.Instructions[index + (hasName ? 2 : 1)];
                if (closure.Operation != "fclosure" || initialization.Operation != "scope_put_var_init") continue;
                var (name, scope) = Symbol(initialization);
                var binding = FindLocal(state, name, scope);
                if (binding?.Kind is not (IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration) ||
                    binding.Scope != state.Source.BodyScope) continue;
                result.Add(closure);
                if (hasName) result.Add(block.Instructions[index + 1]);
                result.Add(initialization);
                index += hasName ? 2 : 1;
            }
        }
        return result;
    }

    private static void EmitModuleInstantiationBranch(FunctionLowering state, BytecodeAssemblyLabelId body,
        IReadOnlySet<IrInstruction> instantiation)
    {
        Add(state, "push_this", null, BytecodeAssemblySourceLocation.None);
        Add(state, "if_false", new BytecodeAssemblyLabelOperand(body), BytecodeAssemblySourceLocation.None);
        foreach (var block in state.Source.Blocks)
            foreach (var instruction in block.Instructions)
                if (instantiation.Contains(instruction)) LowerInstruction(state, instruction);
        Add(state, "return_undef", null, BytecodeAssemblySourceLocation.None);
    }

    private static void EmitPseudoVariablePreamble(FunctionLowering state)
    {
        var homeObject = state.Source.Bindings.SingleOrDefault(candidate => candidate.Name == "home_object");
        if (homeObject is not null)
        {
            Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(4), BytecodeAssemblySourceLocation.None);
            EmitAccess(state, "put", new SlotAccess(state.Slots[homeObject.Id]), BytecodeAssemblySourceLocation.None);
        }
        var activeFunction = state.Source.Bindings.SingleOrDefault(candidate => candidate.Name == "this_active_func");
        if (activeFunction is not null)
        {
            Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(2), BytecodeAssemblySourceLocation.None);
            EmitAccess(state, "put", new SlotAccess(state.Slots[activeFunction.Id]), BytecodeAssemblySourceLocation.None);
        }
        // A named function expression resolves its private self-name through
        // JS_VAR_FUNCTION_NAME. resolve_labels initializes that local from
        // the current-function special object before normal bytecode, even
        // when no explicit `this_active_func` pseudo binding exists.
        var expressionName = state.Source.Bindings.SingleOrDefault(candidate =>
            candidate.Kind == IrBindingKind.FunctionName);
        if (expressionName is not null)
        {
            Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(2), BytecodeAssemblySourceLocation.None);
            EmitAccess(state, "put", new SlotAccess(state.Slots[expressionName.Id]), BytecodeAssemblySourceLocation.None);
        }
        var binding = state.Source.Bindings.SingleOrDefault(candidate => candidate.Name == "new.target");
        if (binding is not null)
        {
            Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(3), BytecodeAssemblySourceLocation.None);
            EmitAccess(state, "put", new SlotAccess(state.Slots[binding.Id]), BytecodeAssemblySourceLocation.None);
        }
        var thisBinding = state.Source.Bindings.SingleOrDefault(candidate => candidate.Name == "this");
        if (thisBinding is not null)
        {
            if (state.Source.Options.Form == IrFunctionForm.DerivedClassConstructor)
                Add(state, "set_loc_uninitialized", new BytecodeAssemblyLocalOperand(state.Slots[thisBinding.Id]),
                    BytecodeAssemblySourceLocation.None);
            else
            {
                Add(state, "push_this", null, BytecodeAssemblySourceLocation.None);
                EmitAccess(state, "put", new SlotAccess(state.Slots[thisBinding.Id]), BytecodeAssemblySourceLocation.None);
            }
        }
        var argumentsBinding = state.Source.Bindings.SingleOrDefault(candidate => candidate.Name == "arguments");
        if (argumentsBinding is not null)
        {
            Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(0), BytecodeAssemblySourceLocation.None);
            EmitAccess(state, "put", new SlotAccess(state.Slots[argumentsBinding.Id]), BytecodeAssemblySourceLocation.None);
        }
    }
}
