using Warp.JsCompiler.Encoding;

namespace Warp.JsCompiler.Assembly;

internal readonly record struct BytecodeAssemblyFunctionId(int Value);
internal readonly record struct BytecodeAssemblyLabelId(int Value);
internal readonly record struct BytecodeAssemblyConstantId(int Value);

internal enum BytecodeAssemblyAtomKind : byte
{
    Predefined,
    Symbol,
    TaggedInteger,
}

/// <summary>An atom before object-stream atom table allocation.</summary>
internal readonly record struct BytecodeAssemblyAtom
{
    private BytecodeAssemblyAtom(BytecodeAssemblyAtomKind kind, uint predefinedId, string? symbol)
    {
        Kind = kind;
        PredefinedId = predefinedId;
        Symbol = symbol;
    }

    internal BytecodeAssemblyAtomKind Kind { get; }
    internal uint PredefinedId { get; }
    internal string? Symbol { get; }

    internal static BytecodeAssemblyAtom Predefined(uint id) => new(BytecodeAssemblyAtomKind.Predefined, id, null);

    internal static BytecodeAssemblyAtom TaggedInteger(uint value) =>
        new(BytecodeAssemblyAtomKind.TaggedInteger, value, null);

    internal static BytecodeAssemblyAtom Named(string symbol)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);
        return new BytecodeAssemblyAtom(BytecodeAssemblyAtomKind.Symbol, 0, symbol);
    }
}

internal readonly record struct BytecodeAssemblySourceLocation(int Line, int Column)
{
    internal static readonly BytecodeAssemblySourceLocation None = new(0, 0);
}

internal abstract record BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblySignedOperand(long Value) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyUnsignedOperand(ulong Value) : BytecodeAssemblyOperand;
/// <summary>OP_eval carries both its argument count and the active lexical scope.</summary>
internal sealed record BytecodeAssemblyEvalOperand(ushort ArgumentCount, ushort ScopeIndex) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyLocalOperand(ushort Index) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyArgumentOperand(ushort Index, bool ForceCanonical = false) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyVarReferenceOperand(ushort Index) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyConstantOperand(BytecodeAssemblyConstantId Constant) : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyLabelOperand(BytecodeAssemblyLabelId Label) : BytecodeAssemblyOperand;

/// <summary>
/// Placeholder for an atom-bearing opcode. The atom itself is held by the
/// corresponding relocation so instruction layout remains atom-table agnostic.
/// </summary>
internal abstract record BytecodeAssemblyAtomOperand : BytecodeAssemblyOperand;
internal sealed record BytecodeAssemblyAtomReferenceOperand(ushort Flags = 0) : BytecodeAssemblyAtomOperand;
internal sealed record BytecodeAssemblyAtomLabelOperand(BytecodeAssemblyLabelId Label, byte Flags = 0)
    : BytecodeAssemblyAtomOperand;

internal sealed record BytecodeAssemblyInstruction(
    TargetOpcodeDescriptor Opcode,
    BytecodeAssemblyOperand? Operand = null,
    BytecodeAssemblySourceLocation Location = default,
    bool PreserveAfterResolution = false);

/// <summary>An atom use anchored to a stable instruction, not a byte offset.</summary>
internal sealed record BytecodeAssemblyAtomRelocation(
    int InstructionIndex,
    BytecodeAssemblyAtom Atom);

internal abstract record BytecodeAssemblyConstant(BytecodeAssemblyConstantId Id);
internal sealed record BytecodeAssemblyNumberConstant(BytecodeAssemblyConstantId Id, double Value) : BytecodeAssemblyConstant(Id);
internal sealed record BytecodeAssemblyStringConstant(BytecodeAssemblyConstantId Id, string Value) : BytecodeAssemblyConstant(Id);
internal sealed record BytecodeAssemblyRegExpPatternConstant(BytecodeAssemblyConstantId Id, string Value) : BytecodeAssemblyConstant(Id);
internal sealed record BytecodeAssemblyRegExpBytecodeConstant(BytecodeAssemblyConstantId Id, string Bytes) : BytecodeAssemblyConstant(Id);
internal sealed record BytecodeAssemblyFunctionConstant(BytecodeAssemblyConstantId Id, BytecodeAssemblyFunctionId Function)
    : BytecodeAssemblyConstant(Id);
internal sealed record BytecodeAssemblyTemplateConstant(BytecodeAssemblyConstantId Id,
    IReadOnlyList<string> Cooked, IReadOnlyList<string> Raw) : BytecodeAssemblyConstant(Id);

internal enum BytecodeAssemblyVariableKind : byte
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

internal enum BytecodeAssemblyFunctionKind : byte { Normal, Generator, Async, AsyncGenerator }

internal sealed record BytecodeAssemblyDebugInfo(BytecodeAssemblyAtom FileName, uint LineNumber,
    IReadOnlyList<byte> PcToLine);

internal sealed record BytecodeAssemblyLocal(
    BytecodeAssemblyAtom? Name,
    BytecodeAssemblyVariableKind Kind = BytecodeAssemblyVariableKind.Normal,
    bool IsConst = false,
    bool IsLexical = false,
    bool IsCaptured = false,
    uint ScopeLevel = 0,
    int ScopeNext = -1);

internal sealed record BytecodeAssemblyClosure(
    BytecodeAssemblyAtom Name,
    uint ParentIndex,
    BytecodeAssemblyVariableKind Kind = BytecodeAssemblyVariableKind.Normal,
    bool IsLocal = true,
    bool IsArgument = false,
    bool IsConst = false,
    bool IsLexical = false);

/// <summary>Function flags and slot tables needed after opcode lowering.</summary>
internal sealed record BytecodeAssemblyFunctionMetadata(
    ushort ArgumentCount = 0,
    ushort DefinedArgumentCount = 0,
    ushort MaximumStackSize = 1,
    byte JsMode = 0,
    bool HasPrototype = false,
    bool HasSimpleParameterList = true,
    bool IsDerivedConstructor = false,
    bool NeedsHomeObject = false,
    BytecodeAssemblyFunctionKind Kind = BytecodeAssemblyFunctionKind.Normal,
    bool NewTargetAllowed = false,
    bool SuperCallAllowed = false,
    bool SuperAllowed = false,
    bool ArgumentsAllowed = true,
    BytecodeAssemblyDebugInfo? DebugInfo = null,
    IReadOnlyList<BytecodeAssemblyLocal>? Locals = null,
    IReadOnlyList<BytecodeAssemblyClosure>? Closures = null,
    bool SerializeVariableDefinitions = true,
    ushort? VariableCount = null);

internal sealed record BytecodeAssemblyFunction(
    BytecodeAssemblyFunctionId Id,
    BytecodeAssemblyAtom Name,
    IReadOnlyList<BytecodeAssemblyInstruction> Instructions,
    BytecodeAssemblyFunctionMetadata Metadata,
    IReadOnlyList<BytecodeAssemblyConstant>? Constants = null,
    IReadOnlyList<BytecodeAssemblyAtomRelocation>? AtomRelocations = null);

internal abstract record BytecodeAssemblyExport(BytecodeAssemblyAtom ExportName);
internal sealed record BytecodeAssemblyLocalExport(uint VariableIndex, BytecodeAssemblyAtom Name)
    : BytecodeAssemblyExport(Name);
internal sealed record BytecodeAssemblyIndirectExport(uint RequiredModuleIndex, BytecodeAssemblyAtom LocalName,
    BytecodeAssemblyAtom Name) : BytecodeAssemblyExport(Name);
internal readonly record struct BytecodeAssemblyStarExport(uint RequiredModuleIndex);
internal sealed record BytecodeAssemblyImport(uint VariableIndex, BytecodeAssemblyAtom ImportName, uint RequiredModuleIndex);
internal sealed record BytecodeAssemblyModuleMetadata(
    BytecodeAssemblyAtom Name,
    IReadOnlyList<BytecodeAssemblyAtom>? RequiredModules = null,
    IReadOnlyList<BytecodeAssemblyExport>? Exports = null,
    IReadOnlyList<BytecodeAssemblyStarExport>? StarExports = null,
    IReadOnlyList<BytecodeAssemblyImport>? Imports = null);

internal sealed record BytecodeAssemblyProgram(
    BytecodeAssemblyFunctionId Entry,
    IReadOnlyList<BytecodeAssemblyFunction> Functions,
    BytecodeAssemblyModuleMetadata? Module = null);

/// <summary>Structural checks at the assembly/object-writer boundary.</summary>
internal static class BytecodeAssemblyVerifier
{
    internal static void Verify(BytecodeAssemblyProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(program.Functions);
        var functions = Unique(program.Functions, function => function.Id, "function");
        if (!functions.ContainsKey(program.Entry))
            throw new InvalidOperationException("Assembly entry function does not exist.");
        foreach (var function in program.Functions) Verify(function, functions);
        if (program.Module is { } module) VerifyModule(module);
    }

    internal static void Verify(BytecodeAssemblyFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        Verify(function, new Dictionary<BytecodeAssemblyFunctionId, BytecodeAssemblyFunction> { [function.Id] = function },
            allowExternalFunctionConstants: true);
    }

    private static void Verify(BytecodeAssemblyFunction function,
        IReadOnlyDictionary<BytecodeAssemblyFunctionId, BytecodeAssemblyFunction> functions,
        bool allowExternalFunctionConstants = false)
    {
        if (function.Metadata.DefinedArgumentCount > function.Metadata.ArgumentCount)
            throw new InvalidOperationException("Defined argument count exceeds argument count.");

        var constants = Unique(function.Constants ?? [], constant => constant.Id, "constant");
        foreach (var constant in constants.Values)
            if (constant is BytecodeAssemblyFunctionConstant child && !allowExternalFunctionConstants &&
                !functions.ContainsKey(child.Function))
                throw new InvalidOperationException("Function constant targets an unknown function.");

        var labels = new HashSet<BytecodeAssemblyLabelId>();
        for (var index = 0; index < function.Instructions.Count; index++)
        {
            var instruction = function.Instructions[index];
            if (instruction.Opcode.Name.StartsWith("scope_", StringComparison.Ordinal) ||
                instruction.Opcode.Name is "enter_scope" or "leave_scope")
                throw new InvalidOperationException("Unresolved scope opcode crossed the assembly boundary.");
            // ECMAScript has no canonical encoding for the optimized empty-string literal.
            if (instruction.Opcode.EncodingKind == TargetOpcodeEncodingKind.Short &&
                instruction.Opcode.Name is not ("push_empty_string" or "get_length"))
                throw new InvalidOperationException("Assembly instructions use canonical or temporary opcodes; layout selects short forms.");
            VerifyOperand(instruction, constants);
            if (instruction.Opcode.Name == "label" && instruction.Operand is BytecodeAssemblyLabelOperand label &&
                !labels.Add(label.Label))
                throw new InvalidOperationException("Duplicate assembly label.");
        }
        foreach (var instruction in function.Instructions)
            if ((instruction.Operand is BytecodeAssemblyLabelOperand label && instruction.Opcode.Name != "label" &&
                 !labels.Contains(label.Label)) ||
                (instruction.Operand is BytecodeAssemblyAtomLabelOperand atomLabel && !labels.Contains(atomLabel.Label)))
                throw new InvalidOperationException("Instruction targets an unknown assembly label.");

        var relocations = function.AtomRelocations ?? [];
        var relocatedInstructions = new HashSet<int>();
        foreach (var relocation in relocations)
        {
            if ((uint)relocation.InstructionIndex >= (uint)function.Instructions.Count)
                throw new InvalidOperationException("Atom relocation targets an unknown instruction.");
            if (!relocatedInstructions.Add(relocation.InstructionIndex))
                throw new InvalidOperationException("Instruction has more than one atom relocation.");
            var instruction = function.Instructions[relocation.InstructionIndex];
            if (instruction.Operand is not BytecodeAssemblyAtomOperand || !IsAtomFormat(instruction.Opcode.OperandFormat))
                throw new InvalidOperationException("Atom relocation targets a non-atom operand.");
            VerifyAtom(relocation.Atom);
        }
        for (var index = 0; index < function.Instructions.Count; index++)
            if (function.Instructions[index].Operand is BytecodeAssemblyAtomOperand && !relocatedInstructions.Contains(index))
                throw new InvalidOperationException("Atom operand has no relocation.");

        foreach (var local in function.Metadata.Locals ?? [])
        {
            if (local.ScopeNext < -2) throw new InvalidOperationException("Local scope-next index is invalid.");
            if (local.Name is { } atom) VerifyAtom(atom);
        }
        foreach (var closure in function.Metadata.Closures ?? []) VerifyAtom(closure.Name);
        VerifyAtom(function.Name);
        if (function.Metadata.DebugInfo is { } debug) VerifyAtom(debug.FileName);
    }

    private static void VerifyModule(BytecodeAssemblyModuleMetadata module)
    {
        VerifyAtom(module.Name);
        var required = module.RequiredModules ?? [];
        foreach (var atom in required) VerifyAtom(atom);
        void Index(uint index)
        {
            if (index >= (uint)required.Count) throw new InvalidOperationException("Module table index is out of range.");
        }
        foreach (var export in module.Exports ?? [])
        {
            VerifyAtom(export.ExportName);
            if (export is BytecodeAssemblyIndirectExport indirect)
            {
                Index(indirect.RequiredModuleIndex);
                VerifyAtom(indirect.LocalName);
            }
        }
        foreach (var star in module.StarExports ?? []) Index(star.RequiredModuleIndex);
        foreach (var import in module.Imports ?? [])
        {
            Index(import.RequiredModuleIndex);
            VerifyAtom(import.ImportName);
        }
    }

    private static void VerifyOperand(BytecodeAssemblyInstruction instruction,
        IReadOnlyDictionary<BytecodeAssemblyConstantId, BytecodeAssemblyConstant> constants)
    {
        var format = instruction.Opcode.OperandFormat;
        if (format is TargetOpcodeOperandFormat.None or TargetOpcodeOperandFormat.NoneInt or
            TargetOpcodeOperandFormat.NoneLocal or TargetOpcodeOperandFormat.NoneArgument or
            TargetOpcodeOperandFormat.NoneVarReference)
        {
            if (instruction.Operand is not null) throw new InvalidOperationException("Operand supplied to operand-free opcode.");
            return;
        }
        if (instruction.Operand is null) throw new InvalidOperationException("Opcode requires an operand.");
        if (instruction.Operand is BytecodeAssemblyConstantOperand constant && !constants.ContainsKey(constant.Constant))
            throw new InvalidOperationException("Instruction refers to an unknown constant.");
        if (IsAtomFormat(format) != (instruction.Operand is BytecodeAssemblyAtomOperand))
            throw new InvalidOperationException("Atom opcode and operand disagree.");
        if (format is (TargetOpcodeOperandFormat.Label or TargetOpcodeOperandFormat.Label8 or
            TargetOpcodeOperandFormat.Label16) && instruction.Operand is not BytecodeAssemblyLabelOperand)
            throw new InvalidOperationException("Label opcode requires a label operand.");
        if (format is TargetOpcodeOperandFormat.AtomLabelU8 or TargetOpcodeOperandFormat.AtomLabelU16 &&
            instruction.Operand is not BytecodeAssemblyAtomLabelOperand)
            throw new InvalidOperationException("Atom-label opcode requires an atom-label operand.");
    }

    private static bool IsAtomFormat(TargetOpcodeOperandFormat format) => format is
        TargetOpcodeOperandFormat.Atom or TargetOpcodeOperandFormat.AtomU8 or
        TargetOpcodeOperandFormat.AtomU16 or TargetOpcodeOperandFormat.AtomLabelU8 or
        TargetOpcodeOperandFormat.AtomLabelU16;

    private static void VerifyAtom(BytecodeAssemblyAtom atom)
    {
        if (atom.Kind == BytecodeAssemblyAtomKind.Symbol && string.IsNullOrEmpty(atom.Symbol))
            throw new InvalidOperationException("Symbol atom is empty.");
        if (atom.Kind == BytecodeAssemblyAtomKind.Predefined && atom.Symbol is not null)
            throw new InvalidOperationException("Predefined atom carries a symbol.");
    }

    private static Dictionary<TKey, TValue> Unique<TKey, TValue>(IEnumerable<TValue> values,
        Func<TValue, TKey> key, string kind) where TKey : notnull
    {
        var result = new Dictionary<TKey, TValue>();
        foreach (var value in values)
            if (!result.TryAdd(key(value), value))
                throw new InvalidOperationException($"Duplicate assembly {kind} id.");
        return result;
    }
}
