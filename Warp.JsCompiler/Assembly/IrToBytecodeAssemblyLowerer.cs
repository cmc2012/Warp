using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.ObjectFormat;

namespace Warp.JsCompiler.Assembly;

/// <summary>
/// Resolves phase-one module names into assembly-level var references and
/// global atom operations. This is the resolve_variables boundary: no bytes,
/// object-writer records, scope operands, or frontend IR escape the pass.
/// </summary>
internal sealed partial class IrToBytecodeAssemblyLowerer : IIrToAssemblyLoweringPass
{
    private readonly record struct CaptureKey(IrFunctionId Function, IrBindingId Binding);
    private sealed record CaptureInfo(CaptureKey Key, IrBinding Binding, ushort Index);
    private enum CaptureOperation : byte { GenericScopeRead, PrivateResolver }
    private readonly record struct CaptureSource(CaptureOperation Operation, IrFunctionId Consumer);
    private readonly record struct SlotAccess(ushort Index, bool IsArgument = false, bool IsClosure = false,
        bool IsLexical = false);

    private sealed class FunctionLowering(IrFunction source, bool module)
    {
        internal readonly IrFunction Source = source;
        internal readonly List<BytecodeAssemblyInstruction> Instructions = [];
        internal readonly List<BytecodeAssemblyAtomRelocation> Relocations = [];
        internal readonly Dictionary<IrBindingId, ushort> Slots = [];
        internal readonly HashSet<IrBindingId> ClosureBindings = [];
        internal readonly HashSet<IrBindingId> ImportBindings = [];
        internal readonly IReadOnlyDictionary<IrScopeId, IrScope> Scopes = source.Scopes.ToDictionary(item => item.Id);
        internal readonly IReadOnlyDictionary<IrBindingId, IrBinding> Bindings = source.Bindings.ToDictionary(item => item.Id);
        internal readonly bool IsModule = module;
        internal readonly IReadOnlyDictionary<IrConstantId, IrConstant> Constants =
            source.Constants.ToDictionary(item => item.Id);
        internal readonly Dictionary<IrConstantId, BytecodeAssemblyConstantId> ConstantSlots = [];
        internal readonly List<BytecodeAssemblyClosure> Closures = [];
        internal readonly Dictionary<CaptureKey, CaptureInfo> CaptureByOrigin = [];
        internal readonly Dictionary<CaptureKey, HashSet<CaptureSource>> CaptureSources = [];
        internal readonly HashSet<IrBindingId> CapturedBindings = [];
        internal readonly HashSet<IrBindingId> EvalCapturedBindings = [];
        // `mark_eval_captured_variables()` executes while resolve_variables
        // scans the bytecode, rather than while parsing declarations.  Keep
        // that temporal distinction for OP_leave_scope: a leave emitted
        // before its first direct eval cannot yet produce close_loc.
        internal readonly HashSet<IrBindingId> EvalActivatedBindings = [];
        internal FunctionLowering? Parent;
        internal FunctionLowering? ModuleRoot;
        internal SlotAccess? PendingReference;
        internal bool PendingGlobalReference;
        internal bool PendingOptimizedGlobalReference;
        internal string? PendingGlobalName;
        internal bool NextScopeMakeReferenceReadsValue;
        internal bool PendingPersistentReference;
        internal bool PendingReferenceUpdated;
        internal bool PendingReferenceIsBodyLexical;
    }

    public BytecodeAssemblyProgram Run(IrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        IrVerifier.Verify(module);
        if (module.Functions.Count == 0) throw new InvalidOperationException("IR module has no entry function.");
        var functionIds = module.Functions.ToDictionary(function => function.Id,
            function => new BytecodeAssemblyFunctionId(function.Id.Value));
        var states = module.Functions.ToDictionary(function => function.Id,
            function => new FunctionLowering(function, function.Options.Form == IrFunctionForm.Module));
        foreach (var import in module.Imports.Where(import => !import.IsNamespace))
            states[module.Functions[0].Id].ImportBindings.Add(import.Binding);
        foreach (var state in states.Values) AllocateSlots(state.Source, state);
        foreach (var state in states.Values)
            if (state.Source.ParentFunction is { } parent) state.Parent = states[parent];
        var moduleRoot = states[module.Functions[0].Id];
        foreach (var state in states.Values) state.ModuleRoot = moduleRoot;
        foreach (var state in states.Values) MarkEvalVisibleBindings(state);
        foreach (var state in states.Values) AddEvalParentCaptures(state);
        var lowered = new Dictionary<IrFunctionId, BytecodeAssemblyFunction>();
        BytecodeAssemblyFunction LowerChildFirst(FunctionLowering state)
        {
            if (lowered.TryGetValue(state.Source.Id, out var existing)) return existing;
            foreach (var child in state.Source.Constants.OfType<IrFunctionConstant>())
                LowerChildFirst(states[child.Function]);
            var result = LowerFunction(state, functionIds);
            lowered.Add(state.Source.Id, result);
            return result;
        }
        LowerChildFirst(states[module.Functions[0].Id]);
        foreach (var state in states.Values) LowerChildFirst(state);
        NormalizeFieldInitializerArrowPrivateClosures(states.Values, lowered);
        var functions = module.Functions.Select(function => lowered[function.Id]).ToArray();
        var entryState = states[module.Functions[0].Id];
        var moduleMetadata = entryState.IsModule ? LowerModuleMetadata(module, entryState) : null;
        var result = new BytecodeAssemblyProgram(functionIds[module.Functions[0].Id], functions, moduleMetadata);
        BytecodeAssemblyVerifier.Verify(result);
        return result;
    }

    /// <summary>
    /// Applies the closure-record rule that depends on the completed resolver
    /// tree. When a field initializer generically closes a private cell and a
    /// nested arrow resolves that same origin through the private resolver,
    /// the arrow's intermediate forwarding cells are normal closures. The
    /// field-initializer and the arrow retain their own resolver kinds.
    /// </summary>
    private static void NormalizeFieldInitializerArrowPrivateClosures(IEnumerable<FunctionLowering> states,
        IDictionary<IrFunctionId, BytecodeAssemblyFunction> lowered)
    {
        var all = states.ToArray();
        foreach (var initializer in all.Where(state => state.Source.Options.Form == IrFunctionForm.ClassFieldInitializer))
        {
            var origins = initializer.CaptureByOrigin.Values.Where(capture =>
                IsPrivateBinding(capture.Binding) &&
                initializer.CaptureSources.TryGetValue(capture.Key, out var initializerSources) &&
                initializerSources.Contains(new CaptureSource(CaptureOperation.GenericScopeRead, initializer.Source.Id)));
            foreach (var origin in origins)
            foreach (var arrow in all.Where(candidate => candidate.Source.Options.Form == IrFunctionForm.Arrow &&
                                                          IsDescendantOf(candidate, initializer.Parent) &&
                                                          candidate.CaptureSources.TryGetValue(origin.Key, out var sources) &&
                                                          sources.Contains(new CaptureSource(CaptureOperation.PrivateResolver,
                                                              candidate.Source.Id))))
            {
                for (var current = arrow.Parent; current is not null && !ReferenceEquals(current, initializer.Parent);
                     current = current.Parent)
                    if (current.CaptureByOrigin.TryGetValue(origin.Key, out var forwarding))
                        NormalizeClosureKind(current, forwarding.Index, lowered);
            }
        }
    }

    private static void NormalizeClosureKind(FunctionLowering state, ushort index,
        IDictionary<IrFunctionId, BytecodeAssemblyFunction> lowered)
    {
        var function = lowered[state.Source.Id];
        var closures = (function.Metadata.Closures ?? []).Select((closure, closureIndex) =>
            closureIndex == index ? closure with { Kind = BytecodeAssemblyVariableKind.Normal } : closure).ToArray();
        lowered[state.Source.Id] = function with { Metadata = function.Metadata with { Closures = closures } };
    }

    private static bool IsDescendantOf(FunctionLowering state, FunctionLowering? ancestor)
    {
        if (ancestor is null) return false;
        for (var current = state.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static bool IsPrivateBinding(IrBinding binding) => binding.Kind is IrBindingKind.PrivateField or
        IrBindingKind.PrivateMethod or IrBindingKind.PrivateGetter or IrBindingKind.PrivateSetter or
        IrBindingKind.PrivateGetterSetter;

    private static BytecodeAssemblyModuleMetadata LowerModuleMetadata(IrModule module, FunctionLowering entry)
    {
        var exports = module.Exports.Select<IrExport, BytecodeAssemblyExport>(export => export switch
        {
            IrLocalExport local => new BytecodeAssemblyLocalExport(
                checked((uint)SlotForName(entry, local.LocalName)), Atom(local.ExportName)),
            IrIndirectExport indirect => new BytecodeAssemblyIndirectExport(
                checked((uint)indirect.RequiredModuleIndex), Atom(indirect.LocalName), Atom(indirect.ExportName)),
            _ => throw new NotSupportedException($"Unknown IR export {export.GetType().Name}."),
        }).ToArray();
        var imports = module.Imports.Select(import => new BytecodeAssemblyImport(
            entry.Slots[import.Binding], Atom(import.ImportName), checked((uint)import.RequiredModuleIndex))).ToArray();
        return new BytecodeAssemblyModuleMetadata(BytecodeAssemblyAtom.Predefined(0),
            module.RequiredModules.Select(Atom).ToArray(), exports,
            module.StarExports.Select(index => new BytecodeAssemblyStarExport(checked((uint)index))).ToArray(), imports);
    }

    private static ushort SlotForName(FunctionLowering state, string name)
    {
        var binding = state.Source.Bindings.FirstOrDefault(candidate =>
            candidate.Name == name && state.ClosureBindings.Contains(candidate.Id));
        return binding is null
            ? throw new InvalidOperationException($"Module export '{name}' has no local binding.")
            : state.Slots[binding.Id];
    }

    private static BytecodeAssemblyFunction LowerFunction(FunctionLowering state,
        IReadOnlyDictionary<IrFunctionId, BytecodeAssemblyFunctionId> functionIds)
    {
        var source = state.Source;
        var blocks = source.Blocks.Where(block => block.Id == source.Entry)
            .Concat(source.Blocks.Where(block => block.Id != source.Entry)).ToArray();
        var labels = blocks.Select((block, index) => (block.Id, Label: new BytecodeAssemblyLabelId(index)))
            .ToDictionary(item => item.Id, item => item.Label);
        var instantiation = state.IsModule ? FindModuleInstantiationInstructions(state, blocks) :
            source.Options.Form == IrFunctionForm.Script ? FindScriptInstantiationInstructions(state, blocks) : [];
        // A generator is entered only to its initial suspension point.  The
        // parser emits OP_initial_yield before local-instantiation code so
        // declarations are initialized on the first resume, not while the
        // generator object is being created.
        var initialSuspend = source.Options.Kind is IrFunctionKind.Generator or IrFunctionKind.AsyncGenerator
            ? blocks.SelectMany(candidate => candidate.Instructions).FirstOrDefault(
                instruction => instruction.Operation == "initial_yield")
            : null;
        var retainedConstants = source.Constants.Where(RequiresConstantPool).ToArray();
        for (var index = 0; index < retainedConstants.Length; index++)
            state.ConstantSlots.Add(retainedConstants[index].Id, new BytecodeAssemblyConstantId(index));
        var constants = retainedConstants.Select(constant =>
            LowerConstant(constant, state.ConstantSlots[constant.Id], functionIds)).ToArray();
        if (state.IsModule) EmitPseudoVariablePreamble(state);
        if (state.IsModule) EmitModuleInstantiationBranch(state, labels[source.Entry], instantiation);
        else if (source.Options.Form == IrFunctionForm.Script) EmitScriptInstantiation(state, instantiation);
        if (!state.IsModule) EmitPseudoVariablePreamble(state);
        if (initialSuspend is not null) LowerInstruction(state, initialSuspend, labels);
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            var block = blocks[blockIndex];
            Add(state, "label", new BytecodeAssemblyLabelOperand(labels[block.Id]), BytecodeAssemblySourceLocation.None);
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                state.NextScopeMakeReferenceReadsValue = instruction.Operation == "scope_make_ref" &&
                    instructionIndex + 1 < block.Instructions.Count &&
                    block.Instructions[instructionIndex + 1].Operation == "get_ref_value";
                if (!instantiation.Contains(instruction) && !ReferenceEquals(instruction, initialSuspend))
                    LowerInstruction(state, instruction, labels);
            }
            var fallthrough = blockIndex + 1 < blocks.Length ? blocks[blockIndex + 1].Id : (IrBlockId?)null;
            LowerTerminator(state, block.Terminator!, labels, fallthrough);
        }

        // OP_ret resumes a gosub continuation; it is not a function return.
        // A finalizer may be the physically last block even though the parser
        // still serializes the function's implicit completion immediately
        // after it. Preserve that lexical tail when no later block supplied
        // one of its own.
        var lastOpcode = state.Instructions.LastOrDefault(instruction => instruction.Opcode.Name != "label");
        if (lastOpcode?.Opcode.Name == "ret" && source.Options.Kind is not IrFunctionKind.Normal)
        {
            Add(state, "undefined", null, BytecodeAssemblySourceLocation.None);
            Add(state, "return_async", null, BytecodeAssemblySourceLocation.None);
        }

        var locals = BuildLocals(state);
        var closures = state.IsModule ? source.Bindings.Where(binding => state.ClosureBindings.Contains(binding.Id))
            .Select(binding => new BytecodeAssemblyClosure(
            // Closure references are runtime cells. Private declaration
            // kinds are retained only on their owning function's vardefs.
            Atom(binding.Name), checked((uint)state.Slots[binding.Id]),
            BytecodeAssemblyVariableKind.Normal,
            IsLocal: !state.ImportBindings.Contains(binding.Id), IsArgument: false,
            IsConst: binding.Kind is not (IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration) &&
                     binding.IsConst,
            IsLexical: binding.Kind is not (IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration) &&
                       binding.IsLexical)).ToArray() : state.Closures.ToArray();
        var metadata = new BytecodeAssemblyFunctionMetadata(
            ArgumentCount: checked((ushort)source.Bindings.Count(binding => binding.IsArgument)),
            DefinedArgumentCount: source.DefinedArgumentCount,
            MaximumStackSize: 1,
            JsMode: source.Options.Strict ? (byte)1 : (byte)0,
            HasPrototype: source.Options.HasPrototype,
            HasSimpleParameterList: source.Options.Form != IrFunctionForm.Script &&
                                    !state.IsModule && source.Options.HasSimpleParameterList,
            IsDerivedConstructor: source.Options.Form == IrFunctionForm.DerivedClassConstructor,
            // `has_home_object` determines whether syntax may refer to
            // `super`; the serialized bit is `need_home_object`, which is
            // set only after resolving an actual home-object reference.
            NeedsHomeObject: source.RequiresHomeObject || source.Options.Form == IrFunctionForm.ClassFieldInitializer ||
                source.Bindings.Any(binding => binding.Name == "home_object"),
            Kind: (BytecodeAssemblyFunctionKind)source.Options.Kind,
            NewTargetAllowed: source.Options.NewTargetAllowed,
            SuperCallAllowed: source.Options.SuperCallAllowed,
            SuperAllowed: source.Options.SuperAllowed,
            ArgumentsAllowed: source.Options.ArgumentsAllowed,
            Locals: locals, Closures: closures,
            SerializeVariableDefinitions: source.Options.Form != IrFunctionForm.Script,
            VariableCount: source.Options.Form == IrFunctionForm.Script ? (ushort)1 : null);
        return new BytecodeAssemblyFunction(new BytecodeAssemblyFunctionId(source.Id.Value),
            source.Name is { Length: > 0 } name ? Atom(name) :
            source.Options.Form is IrFunctionForm.Module or IrFunctionForm.Script
                ? BytecodeAssemblyAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom)
                : BytecodeAssemblyAtom.Predefined(0),
            state.Instructions, metadata, constants, state.Relocations);
    }

    private static void AllocateSlots(IrFunction source, FunctionLowering state)
    {
        ushort argument = 0, local = source.Options.Form == IrFunctionForm.Script ? (ushort)1 : (ushort)0, closure = 0;
        // resolve_variables adds activation pseudo variables after parsing
        // the ordinary declarations.  The IR introduces them as soon as an
        // expression needs one, so recover the source allocation phase here
        // before assigning runtime local indices.
        foreach (var binding in source.Bindings.OrderBy(binding => IsFramePseudoBinding(binding.Name)))
        {
            if (state.Source.Options.Form == IrFunctionForm.Script &&
                !IsFramePseudoBinding(binding.Name) &&
                (binding.Scope == source.ArgumentScope || binding.Scope == source.BodyScope))
                continue;
            // Module declarations live in closure cells so that the module
            // record can expose them.  Frame pseudo variables are different:
            // they are implementation locals initialized from the current
            // activation record and may subsequently be captured by nested
            // functions.  Treating them as module cells changes both the
            // parent slot kind and the child closure layout.
            var moduleBinding = state.IsModule && !IsFramePseudoBinding(binding.Name) &&
                                (binding.Scope == source.ArgumentScope || binding.Scope == source.BodyScope);
            if (moduleBinding) state.ClosureBindings.Add(binding.Id);
            state.Slots.Add(binding.Id, moduleBinding ? closure++ : binding.IsArgument ? argument++ : local++);
        }
        // Module closure metadata is serialized by source binding order. Keep
        // every bytecode var-ref operand on that same compact index space;
        // otherwise a skipped frame pseudo binding can leave a hole between
        // the slot map and the emitted closure table.
        if (state.IsModule)
        {
            ushort index = 0;
            foreach (var binding in source.Bindings.Where(binding => state.ClosureBindings.Contains(binding.Id)))
                state.Slots[binding.Id] = index++;
        }
    }

    /// <summary>
    /// OP_eval receives a lexical-scope head, not merely a numeric nesting
    /// depth. resolve_variables marks every variable reachable through that
    /// scope's inherited chain as captured so the runtime can construct the
    /// direct-evaluation environment. Do the same before local metadata and
    /// leave-scope instructions are emitted.
    /// </summary>
    private static void MarkEvalVisibleBindings(FunctionLowering state)
    {
        foreach (var eval in state.Source.Blocks.SelectMany(block => block.Instructions)
                     .Where(instruction => instruction.Operation == "eval"))
        {
            var operands = eval.Operands.OfType<ImmediateOperand>().ToArray();
            if (operands.Length != 2) continue;
            var scope = new IrScopeId(checked((int)operands[1].Value));
            for (IrScopeId? current = scope; current is { } id; current = state.Scopes[id].Parent)
                foreach (var bindingId in state.Scopes[id].Bindings)
                    // mark_eval_captured_variables walks lexical scope
                    // entries. The var/argument environment remains visible
                    // to direct eval through its vardef chain and is not
                    // marked as a closed cell merely because eval exists.
                    if (IsScopedBinding(state.Bindings[bindingId]) && !IsScriptGlobal(state, state.Bindings[bindingId]))
                    {
                        state.CapturedBindings.Add(bindingId);
                        state.EvalCapturedBindings.Add(bindingId);
                    }
        }
    }

    private static void ActivateEvalCaptureChain(FunctionLowering state, IrScopeId scope)
    {
        // resolve_variables reaches OP_eval in bytecode order and only then
        // calls mark_eval_captured_variables().  Do not infer activation from
        // the final captured bit: earlier OP_leave_scope instructions have
        // already been copied to the resolved bytecode at that point.
        for (IrScopeId? current = scope; current is { } id; current = state.Scopes[id].Parent)
            foreach (var bindingId in state.Scopes[id].Bindings)
                if (IsScopedBinding(state.Bindings[bindingId]) &&
                    !IsScriptGlobal(state, state.Bindings[bindingId]))
                    state.EvalActivatedBindings.Add(bindingId);
    }

    /// <summary>
    /// add_eval_variables() creates closure entries eagerly, in lexical scope
    /// order, for every outer environment a direct eval can observe. Normal
    /// name resolution only creates a capture when a name is used in source,
    /// so direct eval needs this explicit counterpart.
    /// </summary>
    private static void AddEvalParentCaptures(FunctionLowering state)
    {
        if (!state.Source.Blocks.SelectMany(block => block.Instructions)
                .Any(instruction => instruction.Operation == "eval")) return;
        var parentScope = state.Source.ParentScope;
        for (var parent = state.Parent; parent is not null;)
        {
            // A top-level declaration has no enclosing lexical scope on its
            // function record, yet a direct eval in one of its descendants
            // still reaches the module/body environment.  The body is the
            // parser's fallback scope for that boundary.
            var scope = parentScope ?? parent.Source.BodyScope;
            var seen = new HashSet<IrBindingId>();
            // Lexical bindings are linked from the evaluation point outward.
            for (IrScopeId? current = scope; current is { } id; current = parent.Scopes[id].Parent)
            {
                // Module-body declarations are closure cells, not ordinary
                // lexical-frame entries for this walk.  Their capture order
                // is the module's vardef order below; taking them from the
                // scope chain here would instead use add_scope_var's
                // newest-first linkage and reverse sibling declarations.
                if (parent.IsModule && id == parent.Source.BodyScope) continue;
                foreach (var bindingId in parent.Scopes[id].Bindings)
                {
                    var binding = parent.Bindings[bindingId];
                    if (binding.IsLexical && seen.Add(bindingId)) AddEvalCapture(state, parent, binding);
                }
            }
            // Then the argument and unscoped var environments.  The
            // parameter-expression scope has the ARG_SCOPE_END sentinel: it
            // intentionally exposes only its lexical bindings plus the
            // special frame cells, not the ordinary argument/var namespace.
            var parameterEnvironment = parent.Source.Options.HasParameterExpressions && scope.Value == 2;
            foreach (var binding in parent.Source.Bindings)
                if (!binding.IsLexical && seen.Add(binding.Id) && !IsScriptGlobal(parent, binding) &&
                    (!parameterEnvironment || IsParameterEnvironmentFrameBinding(binding)))
                    AddEvalCapture(state, parent, binding);
            // Module declarations already reside in closure cells.  They are
            // visible to a nested direct eval even when their declaration
            // scope is not on the function body's lexical chain (for
            // example, a hoisted module function).  Preserve their existing
            // closure-slot identity and append them after the ordinary
            // scope-ordered entries, as add_eval_variables() does while it
            // walks the enclosing function chain.
            if (parent.IsModule)
                foreach (var binding in parent.Source.Bindings)
                    if (parent.ClosureBindings.Contains(binding.Id) &&
                        !state.CaptureByOrigin.ContainsKey(new CaptureKey(parent.Source.Id, binding.Id)))
                        AddEvalCapture(state, parent, binding);
            parentScope = parent.Source.ParentScope;
            parent = parent.Parent;
        }
        // Parent links are intentionally absent at a few declaration
        // instantiation boundaries.  The root module environment is still
        // observable from direct eval, and is appended only after the normal
        // enclosing-function walk so closure indices retain source order.
        var root = state.ModuleRoot;
        if (root is not null && root.IsModule && !ReferenceEquals(root, state))
            foreach (var binding in root.Source.Bindings)
                if (root.ClosureBindings.Contains(binding.Id) &&
                        !state.CaptureByOrigin.ContainsKey(new CaptureKey(root.Source.Id, binding.Id)))
                        AddDirectCapture(state, root, binding);
    }

    /// <summary>
    /// get_closure_var() recursively installs an outer eval-visible cell in
    /// every intervening closure.  A direct eval nested in arrows therefore
    /// cannot capture a grandparent slot directly: its immediate parent first
    /// owns a forwarded entry, and the eval closure forwards that entry again.
    /// </summary>
    private static void AddEvalCapture(FunctionLowering leaf, FunctionLowering owner, IrBinding binding)
    {
        var descendants = new List<FunctionLowering>();
        for (var current = leaf; current.Parent is { } parent; current = parent)
        {
            descendants.Add(current);
            if (!ReferenceEquals(parent, owner)) continue;
            var capture = AddDirectCapture(current, owner, binding);
            for (var index = descendants.Count - 2; index >= 0; index--)
                capture = AddForwardedCapture(descendants[index], descendants[index + 1], capture);
            return;
        }
        // Declaration-instantiation boundaries without a parent link are
        // already represented as direct runtime captures in the IR.
        AddDirectCapture(leaf, owner, binding);
    }

    private static bool IsParameterEnvironmentFrameBinding(IrBinding binding) =>
        binding.Kind == IrBindingKind.FunctionName ||
        binding.Name is "this" or "home_object" or "this_active_func" or "new.target" or "__arg_var";

    private static BytecodeAssemblyLocal[] BuildLocals(FunctionLowering state)
    {
        var bindings = state.Source.Bindings.Where(binding => !IsScriptGlobal(state, binding) && binding.IsArgument)
            .Concat(state.Source.Bindings.Where(binding => !binding.IsArgument &&
                                                           !IsScriptGlobal(state, binding) &&
                                                           !state.ClosureBindings.Contains(binding.Id) &&
                                                           !IsFramePseudoBinding(binding.Name)))
            .Concat(state.Source.Bindings.Where(binding => !binding.IsArgument &&
                                                           !IsScriptGlobal(state, binding) &&
                                                           !state.ClosureBindings.Contains(binding.Id) &&
                                                           IsFramePseudoBinding(binding.Name)))
            .ToArray();
        var localIndices = bindings.Select((binding, index) => (binding.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        return bindings.Select(binding =>
        {
            // `var` declarations reside in the unscoped var environment;
            // lexical declarations retain their parser scope number.
            // The IR reserves id 2 for the temporary parameter environment,
            // whereas the parser allocates that environment before it enters
            // the body.  Consequently a function with parameter expressions
            // has source levels: var=0, parameters=1, body=2.  Keep this
            // translation at the IR/assembly boundary rather than leaking
            // parser allocation order into every scope-building pass.
            var scopeLevel = IsScopedBinding(binding) ? SourceScopeLevel(state, binding.Scope) : 0u;
            var next = ScopeNext(state, binding, localIndices);
            return new BytecodeAssemblyLocal(Atom(binding.Name), VariableKind(binding), binding.IsConst,
                binding.IsLexical, state.CapturedBindings.Contains(binding.Id), scopeLevel, next);
        }).ToArray();
    }

    private static uint SourceScopeLevel(FunctionLowering state, IrScopeId scope) =>
        state.Source.Options.HasParameterExpressions && scope.Value == 2
            ? 1u
            : state.Source.Options.HasParameterExpressions && scope == state.Source.BodyScope
                ? 2u
                : checked((uint)scope.Value);

    private static int ScopeNext(FunctionLowering state, IrBinding binding,
        IReadOnlyDictionary<IrBindingId, int> localIndices)
    {
        // Formal arguments are a separate allocation in the source compiler.
        // Their JSVarDef records are zero-initialized and are not rebuilt by
        // the lexical scope-linkage pass, so the serialized scope-next is
        // zero (rather than the normal end-of-chain -1).
        if (binding.IsArgument) return 0;
        // add_eval_variables() runs after ECMAScript has rebuilt the ordinary
        // scope linkage. Its activation pseudo variables are appended with
        // the var-scope head as their common next link, rather than chaining
        // through one another.
        if (IsFramePseudoBinding(binding.Name) && localIndices.TryGetValue(new IrBindingId(0), out _))
            return 0;
        // create_function() rebuilds scope_first by prepending vardefs. The
        // IR scope list already has that newest-first order. A lexical scope
        // whose own list is exhausted continues through its parent's head;
        // ordinary var declarations all belong to the level-zero chain.
        var level = IsScopedBinding(binding) ? binding.Scope : new IrScopeId(0);
        var siblings = state.Scopes[binding.Scope].Bindings;
        var position = -1;
        for (var index = 0; index < siblings.Count; index++)
            if (siblings[index] == binding.Id) { position = index; break; }
        for (var index = position + 1; index < siblings.Count; index++)
            if (localIndices.TryGetValue(siblings[index], out var next))
                return VarDefIndex(state, next);
        // The parameter-expression environment is source scope 1.  Its
        // terminal link is ARG_SCOPE_END (-2), which prevents lookup from
        // falling through to the function var scope.
        if (state.Source.Options.HasParameterExpressions && binding.Scope.Value == 2)
            return -2;
        // Catch declarations occupy a scoped slot but their source vardef is
        // not linked into the enclosing lexical chain when that scope is
        // rebuilt; the catch cell terminates its own chain.
        if (binding.Kind == IrBindingKind.Catch) return -1;
        if (!IsScopedBinding(binding))
        {
            // Non-lexical body declarations are represented in the body
            // scope in IR but serialized in the var scope. Find the next
            // older non-lexical vardef across all source scopes.
            return localIndices.Where(item => item.Key != binding.Id &&
                                              !state.Bindings[item.Key].IsLexical &&
                                              item.Value < localIndices[binding.Id])
                .Select(item => (int?)VarDefIndex(state, item.Value)).Max() ?? -1;
        }
        // The parser only links a lexical vardef to its parent after source
        // scope 1.  Scope 1 is the ordinary function body (and source scope
        // 2 is the body when a parameter environment was allocated).  In
        // particular, body-level `let` must terminate its chain rather than
        // pointing at eval's later-added frame pseudo variables.
        if (SourceScopeLevel(state, binding.Scope) <= 1) return -1;
        var parent = IsParameterExpressionBody(state, binding.Scope)
            ? (IrScopeId?)new IrScopeId(0)
            : state.Scopes[level].Parent;
        return ScopeHead(state, parent, localIndices);
    }

    private static bool IsParameterExpressionBody(FunctionLowering state, IrScopeId scope) =>
        state.Source.Options.HasParameterExpressions && scope == state.Source.BodyScope;

    private static int ScopeHead(FunctionLowering state, IrScopeId? scope,
        IReadOnlyDictionary<IrBindingId, int> localIndices)
    {
        for (var current = scope; current is { } id; current = state.Scopes[id].Parent)
            foreach (var bindingId in state.Scopes[id].Bindings)
                if (localIndices.TryGetValue(bindingId, out var index) &&
                    !IsFramePseudoBinding(state.Bindings[bindingId].Name))
                    return VarDefIndex(state, index);
        return -1;
    }

    // JSFunctionDef keeps formal arguments in `args` and every scope-linked
    // declaration in its separate `vars` array.  The object stream serializes
    // args followed by vars, but JSVarDef.scope_next retains its `vars`-array
    // index (js_create_function rebuilds this chain before the concatenation).
    // `localIndices` deliberately describes the serialized, combined table,
    // therefore scope links must translate back to the latter array here.
    private static int VarDefIndex(FunctionLowering state, int serializedIndex) =>
        serializedIndex - state.Source.Bindings.Count(binding => binding.IsArgument);

    private static ushort EvalScopeHead(FunctionLowering state, IrScopeId scope)
    {
        for (IrScopeId? current = scope; current is { } id; current = state.Scopes[id].Parent)
        {
            var bindings = state.Scopes[id].Bindings;
            for (var index = bindings.Count - 1; index >= 0; index--)
            {
                var binding = state.Bindings[bindings[index]];
                // OP_eval's scope operand names the lexical scope head.
                // `var`/pseudo bindings are resolved separately through the
                // function var environment and must not shift this index.
                if (!IsScopedBinding(binding) || binding.IsArgument || state.ClosureBindings.Contains(binding.Id)) continue;
                return checked((ushort)(state.Slots[binding.Id] + 1));
            }
        }
        return 0;
    }

    private static bool IsFramePseudoBinding(string name) => name is
        "this" or "home_object" or "this_active_func" or "new.target" or "arguments";

    private static bool IsScopedBinding(IrBinding binding) =>
        binding.IsLexical || binding.Kind == IrBindingKind.Catch;

    private static IrConstantId? FindBlockFunctionInitializer(FunctionLowering state, IrBinding binding) =>
        state.Source.Blocks.SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Operation == "block_function_initializer" &&
                                  instruction.Operands.OfType<AtomOperand>().SingleOrDefault()?.Value == binding.Name &&
                                  instruction.Operands.OfType<IrScopeOperand>().SingleOrDefault()?.Scope == binding.Scope)
            .Select(instruction => instruction.Operands.OfType<IrConstantOperand>().Single().Constant)
            .Cast<IrConstantId?>()
            .FirstOrDefault();

    // JS_VAR_FUNCTION_DECL is a lexical block-declaration kind.  A normal
    // function-body declaration is hoisted through the var environment and
    // serialize_variables records it as JS_VAR_NORMAL; the IR keeps its
    // declaration kind separately so module/script instantiation can still
    // locate its function-pool constant.
    private static BytecodeAssemblyVariableKind VariableKind(IrBinding binding) =>
        binding.Kind is IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration && !binding.IsLexical
            ? BytecodeAssemblyVariableKind.Normal
            : Kind(binding.Kind);

    private static void LowerInstruction(FunctionLowering state, IrInstruction instruction,
        IReadOnlyDictionary<IrBlockId, BytecodeAssemblyLabelId>? labels = null)
    {
        var location = Location(instruction.Location);
        switch (instruction.Operation)
        {
            case "enter_scope":
            {
                var scope = One<IrScopeOperand>(instruction).Scope;
                foreach (var bindingId in state.Scopes[scope].Bindings)
                {
                    var binding = state.Bindings[bindingId];
                    if (binding.Kind is IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration &&
                        binding.IsLexical && FindBlockFunctionInitializer(state, binding) is { } initializer)
                    {
                        Add(state, "fclosure", new BytecodeAssemblyConstantOperand(state.ConstantSlots[initializer]), location);
                        var access = ResolveAccess(state, binding.Name, scope) ??
                                     throw new InvalidOperationException($"Block function '{binding.Name}' was not resolved.");
                        EmitAccess(state, "init", access, location);
                    }
                    // Block function declarations are lexical for scope
                    // closing/capture purposes, but their function-pool
                    // initializer is installed on scope entry; the parser
                    // does not emit a TDZ set_loc_uninitialized for them.
                    if (binding.IsLexical && binding.Kind is not
                        (IrBindingKind.FunctionDeclaration or IrBindingKind.NewFunctionDeclaration or IrBindingKind.Catch) &&
                        !state.ClosureBindings.Contains(bindingId))
                        Add(state, "set_loc_uninitialized",
                            new BytecodeAssemblyLocalOperand(state.Slots[bindingId]), location);
                }
                return;
            }
            case "block_function_initializer":
                // The matching enter_scope materializes this parser-side
                // function-pool initializer before the block body.
                return;
            case "leave_scope":
            {
                var scope = One<IrScopeOperand>(instruction).Scope;
                // Variable resolution closes only cells that escaped their
                // lexical scope.  Keeping this in the assembly lowering
                // mirrors OP_leave_scope after ECMAScript resolves captures.
                foreach (var bindingId in state.Scopes[scope].Bindings)
                {
                    var binding = state.Bindings[bindingId];
                    if (IsScopedBinding(binding) && state.CapturedBindings.Contains(bindingId) &&
                        (!state.EvalCapturedBindings.Contains(bindingId) ||
                         state.EvalActivatedBindings.Contains(bindingId)))
                        Add(state, "close_loc", new BytecodeAssemblyLocalOperand(state.Slots[bindingId]), location);
                }
                return;
            }
            case "push_const":
            {
                var id = One<IrConstantOperand>(instruction).Constant;
                var constant = state.Constants[id];
                if (constant is IrNumberConstant { Value: var number } && number == Math.Truncate(number) &&
                    number is >= int.MinValue and <= int.MaxValue)
                    Add(state, "push_i32", new BytecodeAssemblySignedOperand((long)number), location);
                else if (constant is IrStringConstant { Value.Length: 0 })
                    Add(state, "push_empty_string", null, location);
                else if (constant is IrStringConstant numericText && IsTaggedIntegerAtom(numericText.Value))
                    Add(state, "push_const", new BytecodeAssemblyConstantOperand(state.ConstantSlots[id]), location);
                else if (constant is IrStringConstant text)
                    AddAtom(state, "push_atom_value", text.Value, location);
                else if (constant is IrRegExpPatternConstant)
                    Add(state, "push_const", new BytecodeAssemblyConstantOperand(state.ConstantSlots[id]), location);
                else if (constant is IrRegExpBytecodeConstant)
                    Add(state, "push_const", new BytecodeAssemblyConstantOperand(state.ConstantSlots[id]), location);
                else Add(state, "push_const", new BytecodeAssemblyConstantOperand(state.ConstantSlots[id]), location);
                return;
            }
            case "fclosure":
            {
                var id = One<IrConstantOperand>(instruction).Constant;
                Add(state, "fclosure", new BytecodeAssemblyConstantOperand(state.ConstantSlots[id]), location);
                return;
            }
            case "define_class" or "define_class_computed" or "define_method":
            {
                if (instruction.Operands is not [AtomOperand atom, ImmediateOperand flags])
                    throw new InvalidOperationException($"{instruction.Operation} requires an atom and flags.");
                AddAtom(state, instruction.Operation, Atom(atom), checked((ushort)flags.Value), location);
                return;
            }
            case "define_method_computed":
                Add(state, "define_method_computed", new BytecodeAssemblyUnsignedOperand(
                    checked((ulong)One<ImmediateOperand>(instruction).Value)), location);
                return;
            case "push_true": Add(state, "push_true", null, location); return;
            case "push_false": Add(state, "push_false", null, location); return;
            case "push_null": Add(state, "null", null, location); return;
            case "push_undefined": Add(state, "undefined", null, location); return;
            case "push_i32": Add(state, "push_i32", new BytecodeAssemblySignedOperand(One<ImmediateOperand>(instruction).Value), location); return;
            case "special_object": Add(state, "special_object", new BytecodeAssemblyUnsignedOperand(
                checked((ulong)One<ImmediateOperand>(instruction).Value)), location); return;
            case "catch" or "gosub":
                if (labels is null) throw new InvalidOperationException("Control-flow lowering requires a function CFG label map.");
                Add(state, instruction.Operation,
                    new BytecodeAssemblyLabelOperand(labels[One<IrBlockOperand>(instruction).Block]), location);
                return;
            case "rest": Add(state, "rest", new BytecodeAssemblyUnsignedOperand(
                checked((ulong)One<ImmediateOperand>(instruction).Value)), location); return;
            case "get_arg_direct": Add(state, "get_arg", new BytecodeAssemblyArgumentOperand(
                checked((ushort)One<ImmediateOperand>(instruction).Value)), location); return;
            case "get_arg_slot": Add(state, "get_arg", new BytecodeAssemblyArgumentOperand(
                checked((ushort)One<ImmediateOperand>(instruction).Value)), location); return;
            case "put_arg_direct": Add(state, "put_arg", new BytecodeAssemblyArgumentOperand(
                checked((ushort)One<ImmediateOperand>(instruction).Value)), location); return;
            case "scope_get_var" or "scope_get_var_undef":
            {
                var (name, scope) = Symbol(instruction);
                if (ResolveAccess(state, name, scope) is { } access) EmitAccess(state, "get", access, location);
                else AddAtom(state, instruction.Operation == "scope_get_var" ? "get_var" : "get_var_undef", name, location);
                return;
            }
            case "scope_get_private_field":
            {
                var (name, scope) = Symbol(instruction);
                var access = ResolveAccess(state, name, scope, CaptureOperation.PrivateResolver) ??
                             throw new InvalidOperationException($"Private binding '{name}' was not resolved.");
                EmitPrivateGet(state, access, PrivateBindingKind(state, name, scope), keepReceiver: false, location);
                return;
            }
            case "scope_get_private_field2":
            {
                var (name, scope) = Symbol(instruction);
                var access = ResolveAccess(state, name, scope, CaptureOperation.PrivateResolver) ??
                             throw new InvalidOperationException($"Private binding '{name}' was not resolved.");
                EmitPrivateGet(state, access, PrivateBindingKind(state, name, scope), keepReceiver: true, location);
                return;
            }
            case "scope_get_private_binding":
            {
                var (name, scope) = Symbol(instruction);
                var access = ResolveAccess(state, name, scope, CaptureOperation.PrivateResolver) ??
                             throw new InvalidOperationException($"Private binding '{name}' was not resolved.");
                NormalizePrivateResolverClosure(state, access);
                EmitAccess(state, "get", access with { IsLexical = false }, location);
                return;
            }
            case "scope_put_private_field":
            {
                var (name, scope) = Symbol(instruction);
                EmitPrivatePut(state, name, scope, location);
                return;
            }
            case "scope_put_var_init":
            {
                var (name, scope) = Symbol(instruction);
                if (ResolveAccess(state, name, scope) is { } access)
                    EmitAccess(state, "init", access, location,
                        // An arrow writes the derived constructor's lexical
                        // `this` through a closure cell.  The initialize-once
                        // rule belongs to that resolved binding, not to the
                        // function currently being lowered.
                        checkInitialization: name == "this");
                else if (FindLocal(state, name, scope) is { } global && IsScriptGlobal(state, global))
                    AddAtom(state, global.IsLexical ? "put_var_init" : "put_var", name, location);
                else AddAtom(state, "put_var_init", name, location);
                return;
            }
            case "scope_put_var":
            {
                var (name, scope) = Symbol(instruction);
                if (ResolveAccess(state, name, scope) is { } access)
                    EmitAccess(state, "put", access, location);
                else AddAtom(state, "put_var", name, location);
                return;
            }
            case "scope_set_uninitialized":
            {
                var (name, scope) = Symbol(instruction);
                var access = ResolveAccess(state, name, scope) ??
                             throw new InvalidOperationException($"Lexical binding '{name}' was not resolved.");
                if (access.IsClosure || access.IsArgument)
                    throw new InvalidOperationException("A parameter lexical binding must lower to a local slot.");
                Add(state, "set_loc_uninitialized", new BytecodeAssemblyLocalOperand(access.Index), location);
                return;
            }
            case "scope_make_ref":
            {
                var (name, scope) = Symbol(instruction);
                if (ResolveAccess(state, name, scope) is { } access)
                {
                    state.PendingReference = access;
                    state.PendingGlobalReference = false;
                    // The optimizer contracts ordinary function-body lexical
                    // bindings to a direct local get/put sequence. Block,
                    // catch and per-iteration bindings retain their own
                    // scope-closing protocol, so they keep the generic
                    // reference stack layout.
                    state.PendingReferenceIsBodyLexical = FindLocal(state, name, scope) is { } binding &&
                        binding.IsLexical && binding.Scope == state.Source.BodyScope;
                }
                else
                {
                    state.PendingReference = null;
                    state.PendingGlobalReference = true;
                    state.PendingReferenceIsBodyLexical = false;
                    state.PendingOptimizedGlobalReference = state.NextScopeMakeReferenceReadsValue;
                    state.PendingGlobalName = name;
                    if (!state.PendingOptimizedGlobalReference)
                        AddAtom(state, "make_var_ref", name, location);
                }
                return;
            }
            case "scope_make_direct_ref":
            {
                var (name, scope) = Symbol(instruction);
                var access = ResolveAccess(state, name, scope) ??
                             throw new InvalidOperationException($"Pattern binding '{name}' was not resolved.");
                var opcode = access.IsClosure ? "make_var_ref_ref" :
                    access.IsArgument ? "make_arg_ref" : "make_loc_ref";
                AddAtom(state, opcode, name, access.Index, location);
                return;
            }
            case "scope_make_persistent_ref":
            {
                var (name, scope) = Symbol(instruction);
                if (ResolveAccess(state, name, scope) is { } access)
                {
                    var opcode = access.IsClosure ? "make_var_ref_ref" :
                        access.IsArgument ? "make_arg_ref" : "make_loc_ref";
                    AddAtom(state, opcode, name, access.Index, location);
                }
                else AddAtom(state, "make_var_ref", name, location);
                state.PendingReference = null;
                state.PendingGlobalReference = false;
                state.PendingReferenceIsBodyLexical = false;
                state.PendingPersistentReference = true;
                return;
            }
            case "get_ref_value":
                if (state.PendingReference is { } get) EmitAccess(state, "get", get, location);
                else if (state.PendingOptimizedGlobalReference)
                {
                    var name = state.PendingGlobalName ?? throw new InvalidOperationException("Global reference lost its name.");
                    // The source resolver emits this strict existence check
                    // before reading a global RMW operand, then patches the
                    // later reference store into put_var_strict.
                    if (state.Source.Options.Strict) AddAtom(state, "check_var", name, location);
                    AddAtom(state, "get_var", name, location);
                }
                else if (state.PendingPersistentReference) Add(state, "get_ref_value", null, location);
                else if (state.PendingGlobalReference) Add(state, "get_ref_value", null, location);
                else throw new InvalidOperationException("get_ref_value has no pending reference.");
                return;
            case "put_ref_value":
                if (state.PendingReference is { } put)
                    EmitAccess(state, state.PendingReferenceUpdated ? "put" : "set", put, location);
                else if (state.PendingPersistentReference) Add(state, "put_ref_value", null, location);
                else if (state.PendingOptimizedGlobalReference)
                    AddAtom(state, state.Source.Options.Strict ? "put_var_strict" : "put_var",
                        state.PendingGlobalName ?? throw new InvalidOperationException("Global reference lost its name."), location);
                else if (state.PendingGlobalReference) Add(state, "put_ref_value", null, location);
                else throw new InvalidOperationException("put_ref_value has no pending reference.");
                state.PendingReference = null;
                state.PendingGlobalReference = false;
                state.PendingOptimizedGlobalReference = false;
                state.PendingGlobalName = null;
                state.PendingPersistentReference = false;
                state.PendingReferenceUpdated = false;
                state.PendingReferenceIsBodyLexical = false;
                return;
            case "put_ref_value_copy":
                if (state.PendingReference is { } copied) EmitAccess(state, "put", copied, location);
                else if (state.PendingPersistentReference || state.PendingGlobalReference)
                    Add(state, "put_ref_value", null, location);
                else throw new InvalidOperationException("put_ref_value_copy has no pending reference.");
                state.PendingReference = null;
                state.PendingGlobalReference = false;
                state.PendingOptimizedGlobalReference = false;
                state.PendingGlobalName = null;
                state.PendingPersistentReference = false;
                state.PendingReferenceUpdated = false;
                state.PendingReferenceIsBodyLexical = false;
                return;
            case "put_ref_value_direct": Add(state, "put_ref_value", null, location); return;
            case "get_field" or "get_field2" or "put_field" or "define_field" or "set_name" or
                "define_class_computed":
                AddAtom(state, instruction.Operation, One<AtomOperand>(instruction).Value, location); return;
            case "throw_error":
                AddAtom(state, "throw_error", instruction.Operands.OfType<AtomOperand>().Single().Value,
                    checked((ushort)instruction.Operands.OfType<ImmediateOperand>().Single().Value), location);
                return;
            case "private_symbol":
                AddAtom(state, "private_symbol", One<AtomOperand>(instruction).Value, location); return;
            case "call" or "call_method" or "call_constructor" or "apply" or "array_from" or "iterator_call":
                Add(state, instruction.Operation, new BytecodeAssemblyUnsignedOperand(
                    checked((ulong)One<ImmediateOperand>(instruction).Value)), location); return;
            case "eval":
                var evalOperands = instruction.Operands.OfType<ImmediateOperand>().ToArray();
                if (evalOperands.Length != 2)
                    throw new InvalidOperationException("eval requires argument-count and scope-index operands.");
                Add(state, "eval", new BytecodeAssemblyEvalOperand(
                    checked((ushort)evalOperands[0].Value), EvalScopeHead(state,
                        new IrScopeId(checked((int)evalOperands[1].Value)))), location);
                ActivateEvalCaptureChain(state, new IrScopeId(checked((int)evalOperands[1].Value)));
                return;
            case "inc" or "dec" or "post_inc" or "post_dec":
                state.PendingReferenceUpdated = state.PendingReference is not null || state.PendingGlobalReference;
                Add(state, instruction.Operation, null, location); return;
            case "insert3" when state.PendingReferenceIsBodyLexical:
                // get_lvalue emits INSERT3 for PUT_LVALUE_KEEP_TOP on a
                // scope reference. resolve_labels subsequently contracts a
                // resolved reference into get_loc/get_arg/get_var_ref plus a
                // direct put. At that point the reference record no longer
                // occupies the two lower stack slots, so retaining the new
                // prefix value is simply DUP.
                Add(state, "dup", null, location); return;
            case "perm4" when state.PendingReferenceIsBodyLexical:
                // PUT_LVALUE_KEEP_SECOND normally moves the old postfix
                // value below an on-stack reference. The contracted direct
                // get/put sequence already has `old new`; put_* consumes
                // only `new` and naturally leaves `old`, requiring no
                // physical permutation.
                return;
            case "set_eval_ret":
                Add(state, "put_loc", new BytecodeAssemblyLocalOperand(0), location); return;
            case "drop" or "nip" or "object" or "check_ctor" or "check_ctor_return" or "add_brand" or "get_private_field" or "put_private_field" or "define_private_field" or "get_array_el" or "get_array_el2" or "put_array_el" or
                "define_array_el" or "append" or "set_name_computed" or "check_brand" or "get_super_value" or "put_super_value" or "dup" or "dup1" or "dup2" or "dup3" or "insert2" or "insert3" or "insert4" or "perm3" or "perm4" or "perm5" or
                "rot3l" or "swap" or "get_super" or "set_home_object" or "set_proto" or "to_object" or "to_propkey" or "for_in_start" or "for_in_next" or "for_of_start" or "for_await_of_start" or "iterator_get_value_done" or "iterator_check_object" or "iterator_next" or "iterator_close" or "iterator_close_return" or "initial_yield" or "yield" or "yield_star" or "async_yield_star" or "regexp" or
                "to_propkey2" or "await" or "import" or "is_undefined_or_null" or "plus" or "neg" or "lnot" or "not" or
                "typeof" or "delete" or "add" or "sub" or "mul" or "div" or "mod" or "pow" or
                "shl" or "sar" or "shr" or "and" or "or" or "xor" or "eq" or "neq" or
                "strict_eq" or "strict_neq" or "lt" or "lte" or "gt" or "gte" or "in" or "instanceof":
                // resolve_variables rewrites a global reference update into a
                // direct get_var/put_var sequence.  That removes the retained
                // reference from the operand stack, so its stack shuffles use
                // the corresponding three-value forms.
                var operation = state.PendingOptimizedGlobalReference ? instruction.Operation switch
                {
                    "insert3" => "insert2",
                    "perm4" => "perm3",
                    "rot3l" => "swap",
                    _ => instruction.Operation,
                } : instruction.Operation;
                Add(state, operation, null, location); return;
            case "for_of_next" or "copy_data_properties": Add(state, instruction.Operation, new BytecodeAssemblyUnsignedOperand(
                checked((ulong)One<ImmediateOperand>(instruction).Value)), location); return;
            case "for_in_end":
                // The parser emits a second drop at the loop break label after
                // discarding the completion value.  During the final label
                // resolution pass that terminal drop is removed when followed
                // by the implicit return.  Do not protect it from the same
                // normalisation here: preserving it changes the bytecode
                // length for a loop at end of function.
                Add(state, "drop", null, location);
                return;
            default: throw new NotSupportedException($"Module assembly lowering does not support '{instruction.Operation}'.");
        }
    }

    private static void LowerTerminator(FunctionLowering state, IrTerminator terminator,
        IReadOnlyDictionary<IrBlockId, BytecodeAssemblyLabelId> labels, IrBlockId? fallthrough = null)
    {
        switch (terminator)
        {
            case IrGotoTerminator jump:
                state.Instructions.Add(new BytecodeAssemblyInstruction(TargetOpcodeCatalog.Get("goto"),
                    new BytecodeAssemblyLabelOperand(labels[jump.Target]), Location(jump.Location),
                    jump.PreserveAfterResolution));
                break;
            case IrBranchTerminator branch:
                // A conditional has one implicit fallthrough edge.  Emit the
                // complementary branch when the false edge is physically
                // next; this is how post-test loops become `if_true body`
                // instead of an `if_false exit; goto body` pair.
                if (branch.WhenFalse == fallthrough)
                    Add(state, "if_true", new BytecodeAssemblyLabelOperand(labels[branch.WhenTrue]),
                        Location(branch.Location));
                else
                {
                    Add(state, "if_false", new BytecodeAssemblyLabelOperand(labels[branch.WhenFalse]),
                        Location(branch.Location));
                    if (branch.WhenTrue != fallthrough)
                        Add(state, "goto", new BytecodeAssemblyLabelOperand(labels[branch.WhenTrue]),
                            Location(branch.Location));
                }
                break;
            case IrReturnTerminator returned:
                if (state.Source.Options.Kind is not IrFunctionKind.Normal)
                {
                    if (!returned.HasValue) Add(state, "undefined", null, Location(returned.Location));
                    // Async generators resolve a returned value through the
                    // await protocol before completing their iterator result.
                    // This applies equally to an explicit return and to the
                    // terminal value produced by a delegated yield*.
                    else if (state.Source.Options.Kind == IrFunctionKind.AsyncGenerator)
                        Add(state, "await", null, Location(returned.Location));
                    Add(state, "return_async", null, Location(returned.Location));
                }
                else if (state.Source.Options.Form == IrFunctionForm.Script && !returned.HasValue)
                {
                    Add(state, "get_loc", new BytecodeAssemblyLocalOperand(0), Location(returned.Location));
                    Add(state, "return", null, Location(returned.Location));
                }
                else Add(state, returned.HasValue ? "return" : "return_undef", null, Location(returned.Location));
                break;
            case IrThrowTerminator thrown: Add(state, "throw", null, Location(thrown.Location)); break;
            case IrInstructionTerminal: break;
            case IrGosubTerminator gosub:
                Add(state, "gosub", new BytecodeAssemblyLabelOperand(labels[gosub.Finally]), Location(gosub.Location));
                break;
            case IrFinallyReturnTerminator returned:
                Add(state, "ret", null, Location(returned.Location));
                break;
            default: throw new NotSupportedException($"Module assembly lowering does not support {terminator.GetType().Name}.");
        }
    }


    private static void AddAtom(FunctionLowering state, string opcode, string atom, BytecodeAssemblySourceLocation location)
        => AddAtom(state, opcode, atom, 0, location);

    private static void AddAtom(FunctionLowering state, string opcode, string atom, ushort flags,
        BytecodeAssemblySourceLocation location)
        => AddAtom(state, opcode, Atom(atom), flags, location);

    private static void AddAtom(FunctionLowering state, string opcode, BytecodeAssemblyAtom atom, ushort flags,
        BytecodeAssemblySourceLocation location)
    {
        Add(state, opcode, new BytecodeAssemblyAtomReferenceOperand(flags), location);
        state.Relocations.Add(new BytecodeAssemblyAtomRelocation(state.Instructions.Count - 1, atom));
    }

    private static void Add(FunctionLowering state, string opcode, BytecodeAssemblyOperand? operand,
        BytecodeAssemblySourceLocation location) => state.Instructions.Add(new(
        TargetOpcodeCatalog.Get(opcode), operand, location));

    private static (string Name, IrScopeId Scope) Symbol(IrInstruction instruction)
    {
        if (instruction.Operands is not [AtomOperand atom, IrScopeOperand scope])
            throw new InvalidOperationException($"'{instruction.Operation}' requires atom and scope operands.");
        return (atom.Value, scope.Scope);
    }

    private static T One<T>(IrInstruction instruction) where T : IrOperand =>
        instruction.Operands is [T operand] ? operand : throw new InvalidOperationException(
            $"'{instruction.Operation}' requires one {typeof(T).Name} operand.");

    private static BytecodeAssemblyConstant LowerConstant(IrConstant constant, BytecodeAssemblyConstantId id,
        IReadOnlyDictionary<IrFunctionId, BytecodeAssemblyFunctionId> functions) => constant switch
    {
        IrNumberConstant number => new BytecodeAssemblyNumberConstant(id, number.Value),
        IrStringConstant text => new BytecodeAssemblyStringConstant(id, text.Value),
        IrRegExpPatternConstant pattern => new BytecodeAssemblyRegExpPatternConstant(id, pattern.Value),
        IrRegExpBytecodeConstant bytecode => new BytecodeAssemblyRegExpBytecodeConstant(id, bytecode.Bytes),
        IrFunctionConstant function => new BytecodeAssemblyFunctionConstant(id, functions[function.Function]),
        IrTemplateConstant template => new BytecodeAssemblyTemplateConstant(id, template.Cooked, template.Raw),
        _ => throw new NotSupportedException($"Unknown IR constant {constant.GetType().Name}.")
    };

    private static bool RequiresConstantPool(IrConstant constant) => constant switch
    {
        IrNumberConstant { Value: var number } => number != Math.Truncate(number) ||
                                                     number < int.MinValue || number > int.MaxValue,
        IrStringConstant text => IsTaggedIntegerAtom(text.Value),
        IrRegExpPatternConstant => true,
        IrRegExpBytecodeConstant => true,
        _ => true,
    };

    // JS_NewAtomStr represents canonical non-negative integer strings up to
    // JS_ATOM_MAX_INT as tagged atoms. emit_push_const must keep those values
    // in the constant pool because push_atom_value cannot encode tagged atoms.
    private static bool IsTaggedIntegerAtom(string value)
    {
        if (value.Length is 0 or > 10 || value[0] is < '0' or > '9') return false;
        if (value.Length > 1 && value[0] == '0') return false;
        uint number = 0;
        foreach (var character in value)
        {
            if (character is < '0' or > '9') return false;
            var next = (ulong)number * 10 + (uint)(character - '0');
            if (next > uint.MaxValue) return false;
            number = (uint)next;
        }
        return number <= 0x7fff_ffff;
    }

    private static BytecodeAssemblyVariableKind Kind(IrBindingKind kind) => (BytecodeAssemblyVariableKind)kind;
    private static BytecodeAssemblyAtom Atom(string name)
    {
        // Internal IR spellings intentionally remain identifier-like.  The
        // object ABI uses the predefined pseudo-variable spellings below.
        name = name switch
        {
            "home_object" => "<home_object>",
            "this_active_func" => "this.active_func",
            "class_fields_init" => "<class_fields_init>",
            _ => name,
        };
        return string.IsNullOrEmpty(name)
        ? BytecodeAssemblyAtom.Predefined(0)
        : PredefinedAtomTable.TryGet(name) is { } predefined
        ? BytecodeAssemblyAtom.Predefined(predefined)
        : IsTaggedIntegerAtom(name) && uint.TryParse(name, out var integer)
        ? BytecodeAssemblyAtom.TaggedInteger(integer)
        : BytecodeAssemblyAtom.Named(name);
    }

    private static BytecodeAssemblyAtom Atom(AtomOperand atom) => atom.IsEmptyStringAtom
        ? BytecodeAssemblyAtom.Predefined(BytecodeTargetAbi.EmptyStringAtom)
        : Atom(atom.Value);
    private static BytecodeAssemblySourceLocation Location(SourceLocation location) => new(location.Line, location.Column);
}

internal static class BytecodeAssemblyIdExtensions
{
    internal static BytecodeAssemblyConstantId ToAssembly(this IrConstantId id) => new(id.Value);
}
