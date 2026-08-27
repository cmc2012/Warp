namespace Warp.JsCompiler.Ir;

// This is the pass-facing IR. It intentionally does not use serialized opcode
// numbers, atom indexes, byte offsets, or closure indexes. Those belong to the
// ECMAScript lowering IR produced by variable and label resolution.
public readonly record struct IrFunctionId(int Value);
public readonly record struct IrBlockId(int Value);
public readonly record struct IrScopeId(int Value);
public readonly record struct IrBindingId(int Value);
public readonly record struct IrConstantId(int Value);

public readonly record struct SourceLocation(int Line, int Column)
{
    public static readonly SourceLocation None = new(0, 0);
}

public enum IrFunctionKind : byte
{
    Normal,
    Generator,
    Async,
    AsyncGenerator,
}

public enum IrFunctionForm : byte
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

public enum IrBindingKind : byte
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

public sealed record IrBinding(
    IrBindingId Id,
    string Name,
    IrScopeId Scope,
    IrBindingKind Kind,
    bool IsArgument = false,
    bool IsConst = false,
    bool IsLexical = false);

public sealed record IrScope(IrScopeId Id, IrScopeId? Parent, IReadOnlyList<IrBindingId> Bindings);

public abstract record IrConstant(IrConstantId Id);
public sealed record IrNumberConstant(IrConstantId Id, double Value) : IrConstant(Id);
public sealed record IrStringConstant(IrConstantId Id, string Value) : IrConstant(Id);
public sealed record IrRegExpPatternConstant(IrConstantId Id, string Value) : IrConstant(Id);
/// <summary>A byte string produced by the regular-expression compiler. It is
/// deliberately distinct from source strings: it must stay in the constant
/// pool rather than being folded into a dynamic atom.</summary>
public sealed record IrRegExpBytecodeConstant(IrConstantId Id, string Bytes) : IrConstant(Id);
public sealed record IrFunctionConstant(IrConstantId Id, IrFunctionId Function) : IrConstant(Id);
public sealed record IrTemplateConstant(IrConstantId Id, IReadOnlyList<string> Cooked,
    IReadOnlyList<string> Raw) : IrConstant(Id);

/// <summary>
/// An instruction in ECMAScript phase-one form. <see cref="Operation"/> includes
/// temporary scope operations such as scope_get_var and enter_scope. Variable
/// resolution replaces those operations only after walking the final IR in
/// bytecode order, which is what determines ECMAScript closure ordering.
/// </summary>
public sealed record IrInstruction(
    string Operation,
    IReadOnlyList<IrOperand> Operands,
    SourceLocation Location);

public abstract record IrOperand;
public sealed record ImmediateOperand(long Value) : IrOperand;
/// <summary>
/// An atom operand.  The empty string is distinct from JS_ATOM_NULL in the
/// bytecode ABI, so anonymous classes retain that distinction explicitly.
/// </summary>
public sealed record AtomOperand(string Value, bool IsEmptyStringAtom = false) : IrOperand
{
    public static AtomOperand EmptyString { get; } = new(string.Empty, IsEmptyStringAtom: true);
}
public sealed record IrScopeOperand(IrScopeId Scope) : IrOperand;
/// <summary>Symbolic IR edge used by instructions such as OP_catch.</summary>
public sealed record IrBlockOperand(IrBlockId Block) : IrOperand;
public sealed record IrBindingOperand(IrBindingId Binding) : IrOperand;
public sealed record IrConstantOperand(IrConstantId Constant) : IrOperand;
public sealed record IrFunctionOperand(IrFunctionId Function) : IrOperand;

public abstract record IrTerminator(SourceLocation Location)
{
    public abstract IEnumerable<IrBlockId> Successors { get; }
}

public sealed record IrGotoTerminator(IrBlockId Target, SourceLocation Location,
    bool PreserveAfterResolution = false)
    : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [Target];
}

public sealed record IrBranchTerminator(IrBlockId WhenTrue, IrBlockId WhenFalse,
    SourceLocation Location) : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [WhenTrue, WhenFalse];
}

public sealed record IrReturnTerminator(bool HasValue, SourceLocation Location)
    : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [];
}

public sealed record IrThrowTerminator(SourceLocation Location) : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [];
}

/// <summary>An instruction such as OP_throw_error is already terminal.</summary>
public sealed record IrInstructionTerminal(SourceLocation Location) : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [];
}

public sealed record IrGosubTerminator(IrBlockId Finally, IrBlockId Continuation,
    SourceLocation Location) : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [Finally, Continuation];
}

/// <summary>
/// Returns from a <see cref="IrGosubTerminator"/> finally subroutine.  This
/// is deliberately distinct from JavaScript return: the runtime resumes at
/// the instruction following the corresponding <c>gosub</c>.
/// </summary>
public sealed record IrFinallyReturnTerminator(SourceLocation Location) : IrTerminator(Location)
{
    public override IEnumerable<IrBlockId> Successors => [];
}

public sealed class IrBlock(IrBlockId id)
{
    public IrBlockId Id { get; } = id;
    public List<IrInstruction> Instructions { get; } = [];
    public IrTerminator? Terminator { get; set; }
    /// <summary>
    /// A parser label restored lexical liveness even though the runtime CFG
    /// has no ordinary predecessor (notably an inner try's label_end before
    /// an enclosing try emits its normal finally path).
    /// </summary>
    public bool ParserContinuation { get; set; }
}

public sealed record IrFunctionOptions(
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

public sealed class IrFunction(
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
    public IrFunctionId Id { get; } = id;
    public string? Name { get; } = name;
    public IrFunctionOptions Options { get; } = options;
    public IrScopeId ArgumentScope { get; } = argumentScope;
    public IrScopeId BodyScope { get; } = bodyScope;
    public IrBlockId Entry { get; } = entry;
    public IrFunctionId? ParentFunction { get; } = parentFunction;
    public IrScopeId? ParentScope { get; } = parentScope;
    public IrConstantId? ParentConstant { get; private set; } = parentConstant;
    /// <summary>
    /// Source parsing may require a home object for runtime brand checks even
    /// where the body has no explicit <c>super</c> expression.
    /// </summary>
    public bool RequiresHomeObject { get; set; }
    /// <summary>Completes the parent constant-pool link for a predeclared child.</summary>
    public void LinkParentConstant(IrConstantId constant)
    {
        if (ParentConstant is not null)
            throw new InvalidOperationException("A child function can only be linked once.");
        ParentConstant = constant;
    }
    public ushort DefinedArgumentCount { get; } = definedArgumentCount;
    public SourceLocation DeclarationLocation { get; } = declarationLocation;
    public List<IrScope> Scopes { get; } = [];
    public List<IrBinding> Bindings { get; } = [];
    public List<IrConstant> Constants { get; } = [];
    public List<IrBlock> Blocks { get; } = [];
    // Block layout may temporarily remove forward targets and append them at
    // their parser-order position.  CFG identity is monotonic and must not
    // be recovered from the current layout list.
    public int NextBlockId { get; set; }
}

public sealed class IrModule
{
    public List<IrFunction> Functions { get; } = [];
    public List<string> RequiredModules { get; } = [];
    public List<IrImport> Imports { get; } = [];
    public List<IrExport> Exports { get; } = [];
    public List<int> StarExports { get; } = [];
}

/// <summary>
/// The complete lowered module graph, keyed by canonical module name. Graph
/// passes may inspect or transform IR across module boundaries before each
/// module enters the ordinary per-module pass pipeline.
/// </summary>
public sealed class IrModuleGraph(
    IReadOnlyDictionary<string, IrModule> modules,
    string entryModule,
    IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> dependencies)
{
    public IReadOnlyDictionary<string, IrModule> Modules { get; } = modules;
    public string EntryModule { get; } = entryModule;
    /// <summary>Resolved internal dependency module names by importer and required-module index.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<int, string>> Dependencies { get; } = dependencies;
}

public sealed record IrImport(IrBindingId Binding, string ImportName, int RequiredModuleIndex,
    bool IsNamespace = false);
public abstract record IrExport(string ExportName);
public sealed record IrLocalExport(string LocalName, string Name) : IrExport(Name);
public sealed record IrIndirectExport(int RequiredModuleIndex, string LocalName, string Name) : IrExport(Name);

/// <summary>Structural validation shared by every middle-end pass boundary.</summary>
public static class IrVerifier
{
    public static void Verify(IrModule module)
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

    public static void Verify(IrFunction function)
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
