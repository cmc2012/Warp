namespace Warp.JsCompiler.Ir;

// This is the pass-facing IR. It intentionally does not use serialized opcode
// numbers, atom indexes, byte offsets, or closure indexes. Those belong to the
// ECMAScript lowering IR produced by variable and label resolution.
internal readonly record struct IrFunctionId(int Value);
internal readonly record struct IrBlockId(int Value);
internal readonly record struct IrScopeId(int Value);
internal readonly record struct IrBindingId(int Value);
internal readonly record struct IrConstantId(int Value);

internal readonly record struct SourceLocation(int Line, int Column)
{
    internal static readonly SourceLocation None = new(0, 0);
}

internal enum IrFunctionKind : byte
{
    Normal,
    Generator,
    Async,
    AsyncGenerator,
}

internal enum IrFunctionForm : byte
{
    Declaration,
    Expression,
    Arrow,
    Getter,
    Setter,
    Method,
    ClassFieldInitializer,
    ClassConstructor,
    DerivedClassConstructor,
    Module,
    Script,
}

internal enum IrBindingKind : byte
{
    Normal,
    FunctionDeclaration,
    NewFunctionDeclaration,
    Catch,
    FunctionName,
    PrivateField,
    PrivateMethod,
    PrivateGetter,
    PrivateSetter,
    PrivateGetterSetter,
}

internal sealed record IrBinding(
    IrBindingId Id,
    string Name,
    IrScopeId Scope,
    IrBindingKind Kind,
    bool IsArgument = false,
    bool IsConst = false,
    bool IsLexical = false);

internal sealed record IrScope(IrScopeId Id, IrScopeId? Parent, IReadOnlyList<IrBindingId> Bindings);

internal abstract record IrConstant(IrConstantId Id);
internal sealed record IrNumberConstant(IrConstantId Id, double Value) : IrConstant(Id);
internal sealed record IrStringConstant(IrConstantId Id, string Value) : IrConstant(Id);
internal sealed record IrRegExpPatternConstant(IrConstantId Id, string Value) : IrConstant(Id);
/// <summary>A byte string produced by the regular-expression compiler. It is
/// deliberately distinct from source strings: it must stay in the constant
/// pool rather than being folded into a dynamic atom.</summary>
internal sealed record IrRegExpBytecodeConstant(IrConstantId Id, string Bytes) : IrConstant(Id);
internal sealed record IrFunctionConstant(IrConstantId Id, IrFunctionId Function) : IrConstant(Id);
internal sealed record IrTemplateConstant(IrConstantId Id, IReadOnlyList<string> Cooked,
    IReadOnlyList<string> Raw) : IrConstant(Id);

/// <summary>
/// An instruction in ECMAScript phase-one form. <see cref="Operation"/> includes
/// temporary scope operations such as scope_get_var and enter_scope. Variable
/// resolution replaces those operations only after walking the final IR in
/// bytecode order, which is what determines ECMAScript closure ordering.
/// </summary>
internal sealed record IrInstruction(
    string Operation,
    IReadOnlyList<IrOperand> Operands,
    SourceLocation Location);

internal abstract record IrOperand;
internal sealed record ImmediateOperand(long Value) : IrOperand;
/// <summary>
/// An atom operand.  The empty string is distinct from JS_ATOM_NULL in the
/// bytecode ABI, so anonymous classes retain that distinction explicitly.
/// </summary>
internal sealed record AtomOperand(string Value, bool IsEmptyStringAtom = false) : IrOperand
{
    internal static AtomOperand EmptyString { get; } = new(string.Empty, IsEmptyStringAtom: true);
}
internal sealed record IrScopeOperand(IrScopeId Scope) : IrOperand;
/// <summary>Symbolic IR edge used by instructions such as OP_catch.</summary>
internal sealed record IrBlockOperand(IrBlockId Block) : IrOperand;
internal sealed record IrBindingOperand(IrBindingId Binding) : IrOperand;
internal sealed record IrConstantOperand(IrConstantId Constant) : IrOperand;
internal sealed record IrFunctionOperand(IrFunctionId Function) : IrOperand;

internal abstract record IrTerminator(SourceLocation Location)
{
    internal abstract IEnumerable<IrBlockId> Successors { get; }
}

internal sealed record IrGotoTerminator(IrBlockId Target, SourceLocation Location,
    bool PreserveAfterResolution = false)
    : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [Target];
}

internal sealed record IrBranchTerminator(IrBlockId WhenTrue, IrBlockId WhenFalse,
    SourceLocation Location) : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [WhenTrue, WhenFalse];
}

internal sealed record IrReturnTerminator(bool HasValue, SourceLocation Location)
    : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [];
}

internal sealed record IrThrowTerminator(SourceLocation Location) : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [];
}

/// <summary>An instruction such as OP_throw_error is already terminal.</summary>
internal sealed record IrInstructionTerminal(SourceLocation Location) : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [];
}

internal sealed record IrGosubTerminator(IrBlockId Finally, IrBlockId Continuation,
    SourceLocation Location) : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [Finally, Continuation];
}

/// <summary>
/// Returns from a <see cref="IrGosubTerminator"/> finally subroutine.  This
/// is deliberately distinct from JavaScript return: the runtime resumes at
/// the instruction following the corresponding <c>gosub</c>.
/// </summary>
internal sealed record IrFinallyReturnTerminator(SourceLocation Location) : IrTerminator(Location)
{
    internal override IEnumerable<IrBlockId> Successors => [];
}

internal sealed class IrBlock(IrBlockId id)
{
    internal IrBlockId Id { get; } = id;
    internal List<IrInstruction> Instructions { get; } = [];
    internal IrTerminator? Terminator { get; set; }
    /// <summary>
    /// A parser label restored lexical liveness even though the runtime CFG
    /// has no ordinary predecessor (notably an inner try's label_end before
    /// an enclosing try emits its normal finally path).
    /// </summary>
    internal bool ParserContinuation { get; set; }
}

internal sealed record IrFunctionOptions(
    IrFunctionKind Kind,
    IrFunctionForm Form,
    bool Strict,
    bool HasPrototype,
    bool HasSimpleParameterList,
    bool HasParameterExpressions,
    bool HasThisBinding,
    bool HasArgumentsBinding,
    bool NewTargetAllowed,
    bool SuperCallAllowed,
    bool SuperAllowed,
    bool ArgumentsAllowed,
    bool HasHomeObject,
    bool IsEval,
    bool IsGlobalVariableEnvironment);

internal sealed class IrFunction(
    IrFunctionId id,
    string? name,
    IrFunctionOptions options,
    IrScopeId argumentScope,
    IrScopeId bodyScope,
    IrBlockId entry,
    IrFunctionId? parentFunction = null,
    IrScopeId? parentScope = null,
    IrConstantId? parentConstant = null,
    ushort definedArgumentCount = 0,
    SourceLocation declarationLocation = default)
{
    internal IrFunctionId Id { get; } = id;
    internal string? Name { get; } = name;
    internal IrFunctionOptions Options { get; } = options;
    internal IrScopeId ArgumentScope { get; } = argumentScope;
    internal IrScopeId BodyScope { get; } = bodyScope;
    internal IrBlockId Entry { get; } = entry;
    internal IrFunctionId? ParentFunction { get; } = parentFunction;
    internal IrScopeId? ParentScope { get; } = parentScope;
    internal IrConstantId? ParentConstant { get; private set; } = parentConstant;
    /// <summary>
    /// Source parsing may require a home object for runtime brand checks even
    /// where the body has no explicit <c>super</c> expression.
    /// </summary>
    internal bool RequiresHomeObject { get; set; }
    /// <summary>Completes the parent constant-pool link for a predeclared child.</summary>
    internal void LinkParentConstant(IrConstantId constant)
    {
        if (ParentConstant is not null)
            throw new InvalidOperationException("A child function can only be linked once.");
        ParentConstant = constant;
    }
    internal ushort DefinedArgumentCount { get; } = definedArgumentCount;
    internal SourceLocation DeclarationLocation { get; } = declarationLocation;
    internal List<IrScope> Scopes { get; } = [];
    internal List<IrBinding> Bindings { get; } = [];
    internal List<IrConstant> Constants { get; } = [];
    internal List<IrBlock> Blocks { get; } = [];
    // Block layout may temporarily remove forward targets and append them at
    // their parser-order position.  CFG identity is monotonic and must not
    // be recovered from the current layout list.
    internal int NextBlockId { get; set; }
}

internal sealed class IrModule
{
    internal List<IrFunction> Functions { get; } = [];
    internal List<string> RequiredModules { get; } = [];
    internal List<IrImport> Imports { get; } = [];
    internal List<IrExport> Exports { get; } = [];
    internal List<int> StarExports { get; } = [];
}

internal sealed record IrImport(IrBindingId Binding, string ImportName, int RequiredModuleIndex,
    bool IsNamespace = false);
internal abstract record IrExport(string ExportName);
internal sealed record IrLocalExport(string LocalName, string Name) : IrExport(Name);
internal sealed record IrIndirectExport(int RequiredModuleIndex, string LocalName, string Name) : IrExport(Name);

/// <summary>Structural validation shared by every middle-end pass boundary.</summary>
internal static class IrVerifier
{
    internal static void Verify(IrModule module)
    {
        var functionIds = new HashSet<IrFunctionId>();
        foreach (var function in module.Functions)
        {
            if (!functionIds.Add(function.Id))
                throw new InvalidOperationException($"Duplicate function id {function.Id.Value}.");
            Verify(function);
        }
        var functions = module.Functions.ToDictionary(function => function.Id);
        foreach (var child in module.Functions.Where(function => function.ParentFunction is not null))
        {
            if (child.ParentFunction is not { } parentId || !functions.TryGetValue(parentId, out var parent) ||
                child.ParentScope is not { } parentScope || !parent.Scopes.Any(scope => scope.Id == parentScope) ||
                child.ParentConstant is not { } constantId ||
                parent.Constants.SingleOrDefault(constant => constant.Id == constantId) is not IrFunctionConstant link ||
                link.Function != child.Id)
                throw new InvalidOperationException("Child function metadata does not match its parent constant reservation.");
        }
    }

    internal static void Verify(IrFunction function)
    {
        var scopes = function.Scopes.ToDictionary(scope => scope.Id);
        var bindings = function.Bindings.ToDictionary(binding => binding.Id);
        var constants = function.Constants.ToDictionary(constant => constant.Id);
        var blocks = function.Blocks.ToDictionary(block => block.Id);

        if (!scopes.ContainsKey(function.ArgumentScope) || !scopes.ContainsKey(function.BodyScope))
            throw new InvalidOperationException("Function argument and body scopes must exist.");
        if (!blocks.ContainsKey(function.Entry))
            throw new InvalidOperationException("Function entry block must exist.");
        if (function.DefinedArgumentCount > function.Bindings.Count(binding => binding.IsArgument))
            throw new InvalidOperationException("Defined argument count exceeds argument count.");

        foreach (var scope in scopes.Values)
        {
            if (scope.Parent is { } parent && !scopes.ContainsKey(parent))
                throw new InvalidOperationException($"Scope {scope.Id.Value} has an unknown parent.");
            foreach (var binding in scope.Bindings)
                if (!bindings.TryGetValue(binding, out var definition) || definition.Scope != scope.Id)
                    throw new InvalidOperationException($"Scope {scope.Id.Value} has an invalid binding.");
        }

        foreach (var block in blocks.Values)
        {
            if (block.Terminator is null)
                throw new InvalidOperationException($"Block {block.Id.Value} has no terminator.");
            foreach (var successor in block.Terminator.Successors)
                if (!blocks.ContainsKey(successor))
                    throw new InvalidOperationException($"Block {block.Id.Value} targets an unknown block.");
            foreach (var handler in block.Instructions.SelectMany(instruction => instruction.Operands)
                         .OfType<IrBlockOperand>())
                if (!blocks.ContainsKey(handler.Block))
                    throw new InvalidOperationException($"Block {block.Id.Value} has an unknown exceptional target.");
            foreach (var operand in block.Instructions.SelectMany(instruction => instruction.Operands))
                VerifyOperand(operand, scopes, bindings, constants);
        }
    }

    private static void VerifyOperand(IrOperand operand, IReadOnlyDictionary<IrScopeId, IrScope> scopes,
        IReadOnlyDictionary<IrBindingId, IrBinding> bindings,
        IReadOnlyDictionary<IrConstantId, IrConstant> constants)
    {
        if (operand is IrScopeOperand scope && !scopes.ContainsKey(scope.Scope))
            throw new InvalidOperationException("Instruction refers to an unknown scope.");
        if (operand is IrBindingOperand binding && !bindings.ContainsKey(binding.Binding))
            throw new InvalidOperationException("Instruction refers to an unknown binding.");
        if (operand is IrConstantOperand constant && !constants.ContainsKey(constant.Constant))
            throw new InvalidOperationException("Instruction refers to an unknown constant.");
    }
}
