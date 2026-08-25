using Warp.JsCompiler.Ir;

namespace Warp.JsCompiler.Assembly;

internal sealed partial class IrToBytecodeAssemblyLowerer
{
    private static SlotAccess? ResolveAccess(FunctionLowering state, string name, IrScopeId scope,
        CaptureOperation operation = CaptureOperation.GenericScopeRead)
    {
        if (FindLocal(state, name, scope) is { } local)
        {
            if (IsScriptGlobal(state, local)) return null;
            return new SlotAccess(state.Slots[local.Id], local.IsArgument,
                state.ClosureBindings.Contains(local.Id), local.IsLexical);
        }
        // The parameter-expression scope is deliberately parentless so a
        // default initializer cannot observe ordinary body/var bindings.
        // Frame pseudo variables are the exception: resolution falls back to
        // the activation record's var environment for arguments, new.target,
        // this and home-object cells.  This mirrors the ARG_SCOPE_END path in
        // resolve_variables without making those names lexical parameters.
        if (state.Source.Options.HasParameterExpressions && scope.Value == 2 && IsFramePseudoBinding(name) &&
            state.Source.Bindings.FirstOrDefault(binding => binding.Scope == state.Source.ArgumentScope &&
                binding.Name == name) is { } frame)
            return new SlotAccess(state.Slots[frame.Id], frame.IsArgument,
                state.ClosureBindings.Contains(frame.Id), frame.IsLexical);
        return ResolveCapture(state, name, operation, state.Source.Id) is { } capture
            ? new SlotAccess(capture.Index, IsClosure: true, IsLexical: capture.Binding.IsLexical)
            : null;
    }

    private static IrBinding? FindLocal(FunctionLowering state, string name, IrScopeId scope)
    {
        IrScopeId? current = scope;
        while (current is { } id)
        {
            var definition = state.Scopes[id];
            foreach (var bindingId in definition.Bindings)
                if (state.Bindings[bindingId].Name == name) return state.Bindings[bindingId];
            current = definition.Parent;
        }
        return null;
    }

    private static CaptureInfo? ResolveCapture(FunctionLowering state, string name,
        CaptureOperation operation = CaptureOperation.GenericScopeRead, IrFunctionId? consumer = null)
    {
        consumer ??= state.Source.Id;
        if (state.Parent is not { } parent || state.Source.ParentScope is not { } parentScope) return null;
        if (FindLocal(parent, name, parentScope) is { } binding)
        {
            if (IsScriptGlobal(parent, binding)) return null;
            return AddDirectCapture(state, parent, binding, operation, consumer.Value);
        }
        if (ResolveCapture(parent, name, operation, consumer) is not { } forwarded) return null;
        return AddForwardedCapture(state, parent, forwarded, operation, consumer.Value);
    }

    private static bool IsScriptGlobal(FunctionLowering state, IrBinding binding) =>
        state.Source.Options.Form == IrFunctionForm.Script &&
        !IsFramePseudoBinding(binding.Name) &&
        binding.Scope is var scope && (scope == state.Source.ArgumentScope || scope == state.Source.BodyScope);

    private static CaptureInfo AddDirectCapture(FunctionLowering child, FunctionLowering owner,
        IrBinding binding)
        => AddDirectCapture(child, owner, binding, CaptureOperation.GenericScopeRead, child.Source.Id);

    private static CaptureInfo AddDirectCapture(FunctionLowering child, FunctionLowering owner,
        IrBinding binding, CaptureOperation operation, IrFunctionId consumer)
    {
        var key = new CaptureKey(owner.Source.Id, binding.Id);
        if (child.CaptureByOrigin.TryGetValue(key, out var existing))
        {
            RecordCaptureSource(child, key, operation, consumer);
            return existing;
        }
        owner.CapturedBindings.Add(binding.Id);
        var index = checked((ushort)child.Closures.Count);
        child.Closures.Add(new BytecodeAssemblyClosure(Atom(binding.Name), owner.Slots[binding.Id], CaptureKind(binding),
            IsLocal: !owner.ClosureBindings.Contains(binding.Id), IsArgument: binding.IsArgument,
            binding.IsConst, binding.IsLexical));
        var result = new CaptureInfo(key, binding, index);
        child.CaptureByOrigin.Add(key, result);
        RecordCaptureSource(child, key, operation, consumer);
        return result;
    }

    private static CaptureInfo AddForwardedCapture(FunctionLowering child, FunctionLowering parent,
        CaptureInfo parentCapture)
        => AddForwardedCapture(child, parent, parentCapture, CaptureOperation.GenericScopeRead, child.Source.Id);

    private static CaptureInfo AddForwardedCapture(FunctionLowering child, FunctionLowering parent,
        CaptureInfo parentCapture, CaptureOperation operation, IrFunctionId consumer)
    {
        if (child.CaptureByOrigin.TryGetValue(parentCapture.Key, out var existing))
        {
            RecordCaptureSource(child, parentCapture.Key, operation, consumer);
            return existing;
        }
        var index = checked((ushort)child.Closures.Count);
        child.Closures.Add(new BytecodeAssemblyClosure(Atom(parentCapture.Binding.Name), parentCapture.Index,
            CaptureKind(parentCapture.Binding), IsLocal: false, IsArgument: parentCapture.Binding.IsArgument,
            parentCapture.Binding.IsConst, parentCapture.Binding.IsLexical));
        var result = new CaptureInfo(parentCapture.Key, parentCapture.Binding, index);
        child.CaptureByOrigin.Add(parentCapture.Key, result);
        RecordCaptureSource(child, parentCapture.Key, operation, consumer);
        return result;
    }

    private static void RecordCaptureSource(FunctionLowering state, CaptureKey key, CaptureOperation operation,
        IrFunctionId consumer)
    {
        if (!state.CaptureSources.TryGetValue(key, out var sources))
            state.CaptureSources.Add(key, sources = []);
        sources.Add(new CaptureSource(operation, consumer));
    }

    // resolve_scope_var copies a lexical block-function declaration kind into
    // its closure record. Ordinary body declarations (including their own
    // recursive reference) use the function var environment and become
    // normal closure cells instead.
    private static BytecodeAssemblyVariableKind CaptureKind(IrBinding binding) =>
        binding.Kind is IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration && !binding.IsLexical
            ? BytecodeAssemblyVariableKind.Normal
            // A destructured catch parameter is parsed through the lexical
            // destructuring path (TOK_LET), although its initial value comes
            // directly from OP_catch. It therefore resolves like a catch
            // binding but serializes its captured closure as JS_VAR_NORMAL.
            : binding.Kind == IrBindingKind.Catch && binding.IsLexical
                ? BytecodeAssemblyVariableKind.Normal
            : Kind(binding.Kind);

    private static void EmitAccess(FunctionLowering state, string operation, SlotAccess access,
        BytecodeAssemblySourceLocation location, bool checkInitialization = false)
    {
        var suffix = access.IsClosure ? "var_ref" : access.IsArgument ? "arg" : "loc";
        var opcode = operation switch
        {
            "get" when access.IsLexical => $"get_{suffix}_check",
            "put" when access.IsLexical => $"put_{suffix}_check",
            // Only derived-constructor `this` needs the initialize-once
            // opcode. Other lexical declarations initialize with put_loc.
            "init" when checkInitialization && access.IsLexical && !state.IsModule =>
                $"put_{suffix}_check_init",
            "init" => $"put_{suffix}",
            _ => $"{operation}_{suffix}",
        };
        BytecodeAssemblyOperand operand = access.IsClosure ? new BytecodeAssemblyVarReferenceOperand(access.Index) :
            access.IsArgument ? new BytecodeAssemblyArgumentOperand(access.Index) : new BytecodeAssemblyLocalOperand(access.Index);
        Add(state, opcode, operand, location);
    }

    private static IrBindingKind PrivateBindingKind(FunctionLowering state, string name, IrScopeId scope)
    {
        if (FindLocal(state, name, scope) is { } local) return local.Kind;
        if (ResolveCapture(state, name, CaptureOperation.PrivateResolver, state.Source.Id) is { } capture)
            return capture.Binding.Kind;
        throw new InvalidOperationException($"Private binding '{name}' was not resolved.");
    }

    private static void EmitPrivateGet(FunctionLowering state, SlotAccess access, IrBindingKind kind,
        bool keepReceiver, BytecodeAssemblySourceLocation location)
    {
        NormalizePrivateResolverClosure(state, access);
        switch (kind)
        {
            case IrBindingKind.PrivateField:
                if (keepReceiver) Add(state, "dup", null, location);
                // Private-name resolution uses get_loc/get_var_ref directly.
                // Its lexical declaration is a compile-time visibility
                // mechanism, not a TDZ-checked value access.
                EmitAccess(state, "get", access with { IsLexical = false }, location);
                Add(state, "get_private_field", null, location);
                return;
            case IrBindingKind.PrivateMethod:
                EmitAccess(state, "get", access with { IsLexical = false }, location);
                Add(state, "check_brand", null, location);
                if (!keepReceiver) Add(state, "nip", null, location);
                return;
            case IrBindingKind.PrivateGetter:
            case IrBindingKind.PrivateGetterSetter:
                if (keepReceiver) Add(state, "dup", null, location);
                EmitAccess(state, "get", access with { IsLexical = false }, location);
                Add(state, "check_brand", null, location);
                Add(state, "call_method", new BytecodeAssemblyUnsignedOperand(0), location);
                return;
            case IrBindingKind.PrivateSetter:
                throw new InvalidOperationException("Private setter cannot be read.");
            default:
                throw new InvalidOperationException($"Binding kind {kind} is not private.");
        }
    }

    /// <summary>
    /// Lowers the resolved form of OP_scope_put_private_field.  The parser
    /// leaves this symbolic opcode until resolve_scope_private_field has
    /// determined the declaration category.  An accessor write deliberately
    /// resolves both the public-facing private name and the parser-created
    /// <c>&lt;set&gt;</c> storage name, in that order.
    /// </summary>
    private static void EmitPrivatePut(FunctionLowering state, string name, IrScopeId scope,
        BytecodeAssemblySourceLocation location)
    {
        var access = ResolveAccess(state, name, scope, CaptureOperation.PrivateResolver) ??
                     throw new InvalidOperationException($"Private binding '{name}' was not resolved.");
        var kind = PrivateBindingKind(state, name, scope);
        NormalizePrivateResolverClosure(state, access);
        switch (kind)
        {
            case IrBindingKind.PrivateField:
                EmitAccess(state, "get", access with { IsLexical = false }, location);
                Add(state, "put_private_field", null, location);
                return;
            case IrBindingKind.PrivateMethod:
            case IrBindingKind.PrivateGetter:
                AddAtom(state, "throw_error", name, 0, location);
                return;
            case IrBindingKind.PrivateSetter:
            case IrBindingKind.PrivateGetterSetter:
            {
                // resolve_scope_private_field1 has already created the
                // principal closure above.  The setter lookup is separate,
                // mirroring get_private_setter_name + its second resolver
                // call in the source compiler.
                var setterName = name + "<set>";
                var setter = ResolveAccess(state, setterName, scope, CaptureOperation.PrivateResolver) ??
                             throw new InvalidOperationException($"Private setter '{setterName}' was not resolved.");
                NormalizePrivateResolverClosure(state, setter);
                EmitAccess(state, "get", setter with { IsLexical = false }, location);
                Add(state, "swap", null, location);
                Add(state, "rot3r", null, location);
                Add(state, "check_brand", null, location);
                Add(state, "rot3l", null, location);
                Add(state, "call_method", new BytecodeAssemblyUnsignedOperand(1), location);
                return;
            }
            default:
                throw new InvalidOperationException($"Binding kind {kind} is not private.");
        }
    }

    private static void NormalizePrivateResolverClosure(FunctionLowering state, SlotAccess access)
    {
        // resolve_scope_private_field explicitly requests JS_VAR_NORMAL when
        // it closes over a private declaration. Generic scope_get_var (used
        // by field-initializer code) retains the declaration kind instead.
        if (!access.IsClosure) return;
        state.Closures[access.Index] = state.Closures[access.Index] with
        {
            Kind = BytecodeAssemblyVariableKind.Normal,
        };
        // The dedicated private resolver obtains a closure cell without
        // setting JSVarDef.is_captured. Consequently its owner must not emit
        // close_loc merely because this access exists. A generic scope read
        // from a field initializer will add the owner back later if needed.
        // An arrow keeps the surrounding class environment alive as its
        // lexical closure.  Private access through that closure therefore
        // remains a real capture; only an ordinary private method's
        // resolver-local access uses the non-capturing normalization.
        if (state.Source.Options.Form == IrFunctionForm.Arrow) return;
        var capture = state.CaptureByOrigin.Values.SingleOrDefault(item => item.Index == access.Index);
        if (capture is not null && capture.Binding.Kind is IrBindingKind.PrivateField or IrBindingKind.PrivateMethod or
            IrBindingKind.PrivateGetter or IrBindingKind.PrivateSetter or IrBindingKind.PrivateGetterSetter)
        {
            // The resolver may have forwarded this private name through one
            // or more nested arrows. `state.Parent` is then merely an
            // intermediate closure, while CaptureKey.Function identifies the
            // declaration frame whose JSVarDef was marked by AddDirectCapture.
            // resolve_scope_private_field does not set that owner flag at
            // all, so clear it on the actual defining frame.
            FunctionLowering? owner = state;
            while (owner is not null && owner.Source.Id != capture.Key.Function)
                owner = owner.Parent;
            owner?.CapturedBindings.Remove(capture.Binding.Id);
        }
    }
}
