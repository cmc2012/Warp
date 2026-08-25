using System.Buffers.Binary;

namespace Warp.JsCompiler.ObjectFormat;

internal abstract record BytecodeObjectValue;
internal sealed record BytecodeUndefinedValue : BytecodeObjectValue;
internal sealed record BytecodeNullValue : BytecodeObjectValue;
internal sealed record BytecodeBooleanValue(bool Value) : BytecodeObjectValue;
internal sealed record BytecodeIntegerValue(int Value) : BytecodeObjectValue;
internal sealed record BytecodeFloatValue(double Value) : BytecodeObjectValue;
internal sealed record BytecodeStringValue(string Value) : BytecodeObjectValue;
internal sealed record BytecodeTemplateValue(IReadOnlyList<string> Cooked, IReadOnlyList<string> Raw)
    : BytecodeObjectValue;
internal abstract record BytecodeObjectExport(BytecodeObjectAtom ExportName);
internal sealed record BytecodeObjectLocalExport(uint VariableIndex, BytecodeObjectAtom Name)
    : BytecodeObjectExport(Name);
internal sealed record BytecodeObjectIndirectExport(uint RequiredModuleIndex, BytecodeObjectAtom LocalName,
    BytecodeObjectAtom Name) : BytecodeObjectExport(Name);
internal readonly record struct BytecodeObjectStarExport(uint RequiredModuleIndex);
internal sealed record BytecodeObjectImport(uint VariableIndex, BytecodeObjectAtom ImportName,
    uint RequiredModuleIndex);
internal sealed record BytecodeModuleValue(
    BytecodeObjectAtom Name,
    IrFunctionObject Function,
    IReadOnlyList<BytecodeObjectAtom>? RequiredModules = null,
    IReadOnlyList<BytecodeObjectExport>? Exports = null,
    IReadOnlyList<BytecodeObjectStarExport>? StarExports = null,
    IReadOnlyList<BytecodeObjectImport>? Imports = null) : BytecodeObjectValue;

internal readonly record struct BytecodeObjectAtom(uint Id, string? DynamicName = null, bool IsTaggedInteger = false)
{
    internal static BytecodeObjectAtom Predefined(uint id)
    {
        if (id >= BytecodeTargetAbi.FirstDynamicAtom)
            throw new ArgumentOutOfRangeException(nameof(id), "A predefined atom must precede the dynamic atom range.");
        return new BytecodeObjectAtom(id);
    }

    internal static BytecodeObjectAtom Dynamic(string name) =>
        new(BytecodeTargetAbi.FirstDynamicAtom, name ?? throw new ArgumentNullException(nameof(name)));

    internal static BytecodeObjectAtom TaggedInteger(uint value) => new(value, null, true);
}

internal enum BytecodeObjectFunctionKind : byte
{
    Normal,
    Generator,
    Async,
    AsyncGenerator,
}

internal enum BytecodeObjectVariableKind : byte
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

internal sealed record BytecodeObjectVariable(
    BytecodeObjectAtom Name,
    uint ScopeLevel,
    int ScopeNext,
    BytecodeObjectVariableKind Kind = BytecodeObjectVariableKind.Normal,
    bool IsConst = false,
    bool IsLexical = false,
    bool IsCaptured = false);

internal sealed record BytecodeObjectClosure(
    BytecodeObjectAtom Name,
    uint VariableIndex,
    BytecodeObjectVariableKind Kind = BytecodeObjectVariableKind.Normal,
    bool IsLocal = false,
    bool IsArgument = false,
    bool IsConst = false,
    bool IsLexical = false);

internal sealed record BytecodeObjectDebugInfo(
    BytecodeObjectAtom FileName,
    uint LineNumber,
    IReadOnlyList<byte> PcToLine);

internal readonly record struct BytecodeObjectAtomRelocation(int OperandOffset, BytecodeObjectAtom Atom);

internal sealed record IrFunctionObject(
    BytecodeObjectAtom Name,
    IReadOnlyList<byte> Bytecode,
    IReadOnlyList<BytecodeObjectAtomRelocation>? AtomRelocations = null,
    uint ArgumentCount = 0,
    uint VariableCount = 0,
    uint DefinedArgumentCount = 0,
    uint StackSize = 0,
    IReadOnlyList<BytecodeObjectVariable>? Variables = null,
    IReadOnlyList<BytecodeObjectClosure>? Closures = null,
    IReadOnlyList<BytecodeObjectValue>? Constants = null,
    BytecodeObjectDebugInfo? Debug = null,
    byte JsMode = 0,
    bool HasPrototype = false,
    bool HasSimpleParameterList = true,
    bool IsDerivedClassConstructor = false,
    bool NeedsHomeObject = false,
    BytecodeObjectFunctionKind Kind = BytecodeObjectFunctionKind.Normal,
    bool NewTargetAllowed = true,
    bool SuperCallAllowed = false,
    bool SuperAllowed = false,
    bool ArgumentsAllowed = true,
    bool BacktraceBarrier = false) : BytecodeObjectValue;

/// <summary>Writes the ECMAScript 2021-03-27 binary object format without invoking a native compiler.</summary>
internal sealed class BytecodeObjectWriter
{
    private const byte NullTag = 1;
    private const byte UndefinedTag = 2;
    private const byte FalseTag = 3;
    private const byte TrueTag = 4;
    private const byte Int32Tag = 5;
    private const byte Float64Tag = 6;
    private const byte StringTag = 7;
    private const byte TemplateTag = 13;
    private const byte FunctionBytecodeTag = 14;
    private const byte ModuleTag = 15;

    private readonly List<byte> _body = [];
    private DynamicAtomTable _dynamicAtoms = new();

    internal byte[] Write(BytecodeObjectValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _body.Clear();
        _dynamicAtoms = new DynamicAtomTable();
        WriteValue(value);

        var result = new List<byte>();
        result.Add(BytecodeTargetAbi.BytecodeVersion);
        WriteUnsigned(result, checked((uint)_dynamicAtoms.Names.Count));
        foreach (var atom in _dynamicAtoms.Names) WriteString(result, atom);
        result.AddRange(_body);
        return result.ToArray();
    }

    private void WriteValue(BytecodeObjectValue value)
    {
        switch (value)
        {
            case BytecodeUndefinedValue: _body.Add(UndefinedTag); break;
            case BytecodeNullValue: _body.Add(NullTag); break;
            case BytecodeBooleanValue boolean: _body.Add(boolean.Value ? TrueTag : FalseTag); break;
            case BytecodeIntegerValue integer:
                _body.Add(Int32Tag);
                WriteSigned(_body, integer.Value);
                break;
            case BytecodeFloatValue floating:
                _body.Add(Float64Tag);
                Span<byte> bytes = stackalloc byte[sizeof(double)];
                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(floating.Value));
                _body.AddRange(bytes);
                break;
            case BytecodeStringValue text:
                _body.Add(StringTag);
                WriteString(_body, text.Value);
                break;
            case BytecodeTemplateValue template:
                if (template.Cooked.Count != template.Raw.Count)
                    throw new ArgumentException("Cooked and raw template segments must have equal lengths.", nameof(value));
                _body.Add(TemplateTag);
                WriteUnsigned(_body, checked((uint)template.Cooked.Count));
                foreach (var segment in template.Cooked) WriteValue(new BytecodeStringValue(segment));
                _body.Add(TemplateTag);
                WriteUnsigned(_body, checked((uint)template.Raw.Count));
                foreach (var segment in template.Raw) WriteValue(new BytecodeStringValue(segment));
                WriteValue(new BytecodeUndefinedValue());
                break;
            case IrFunctionObject function:
                WriteFunction(function);
                break;
            case BytecodeModuleValue module:
                WriteModule(module);
                break;
            default:
                throw new NotSupportedException($"Unsupported ECMAScript object value '{value.GetType().Name}'.");
        }
    }

    private void WriteModule(BytecodeModuleValue module)
    {
        var requiredModules = module.RequiredModules ?? [];
        var exports = module.Exports ?? [];
        var starExports = module.StarExports ?? [];
        var imports = module.Imports ?? [];
        _body.Add(ModuleTag);
        WriteAtom(module.Name);
        WriteUnsigned(_body, checked((uint)requiredModules.Count));
        foreach (var required in requiredModules) WriteAtom(required);

        WriteUnsigned(_body, checked((uint)exports.Count));
        foreach (var export in exports)
        {
            switch (export)
            {
                case BytecodeObjectLocalExport local:
                    _body.Add(0);
                    WriteUnsigned(_body, local.VariableIndex);
                    WriteAtom(local.ExportName);
                    break;
                case BytecodeObjectIndirectExport indirect:
                    ValidateRequiredModuleIndex(indirect.RequiredModuleIndex, requiredModules.Count);
                    _body.Add(1);
                    WriteUnsigned(_body, indirect.RequiredModuleIndex);
                    WriteAtom(indirect.LocalName);
                    WriteAtom(indirect.ExportName);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported module export '{export.GetType().Name}'.");
            }
        }

        WriteUnsigned(_body, checked((uint)starExports.Count));
        foreach (var star in starExports)
        {
            ValidateRequiredModuleIndex(star.RequiredModuleIndex, requiredModules.Count);
            WriteUnsigned(_body, star.RequiredModuleIndex);
        }

        WriteUnsigned(_body, checked((uint)imports.Count));
        foreach (var import in imports)
        {
            ValidateRequiredModuleIndex(import.RequiredModuleIndex, requiredModules.Count);
            WriteUnsigned(_body, import.VariableIndex);
            WriteAtom(import.ImportName);
            WriteUnsigned(_body, import.RequiredModuleIndex);
        }
        WriteFunction(module.Function);
    }

    private static void ValidateRequiredModuleIndex(uint index, int count)
    {
        if (index >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index), "A module table entry refers to a missing required module.");
    }

    private void WriteFunction(IrFunctionObject function)
    {
        var variables = function.Variables ?? [];
        var closures = function.Closures ?? [];
        var constants = function.Constants ?? [];
        if (variables.Count != 0 && variables.Count != checked((int)(function.ArgumentCount + function.VariableCount)))
            throw new ArgumentException("The vardef count must equal argument count plus variable count.", nameof(function));

        _body.Add(FunctionBytecodeTag);
        var flags = PackFunctionFlags(function);
        _body.Add((byte)flags);
        _body.Add((byte)(flags >> 8));
        _body.Add(function.JsMode);
        WriteAtom(function.Name);
        WriteUnsigned(_body, function.ArgumentCount);
        WriteUnsigned(_body, function.VariableCount);
        WriteUnsigned(_body, function.DefinedArgumentCount);
        WriteUnsigned(_body, function.StackSize);
        WriteUnsigned(_body, checked((uint)closures.Count));
        WriteUnsigned(_body, checked((uint)constants.Count));
        WriteUnsigned(_body, checked((uint)function.Bytecode.Count));
        WriteUnsigned(_body, checked((uint)variables.Count));
        foreach (var variable in variables) WriteVariable(variable);
        foreach (var closure in closures) WriteClosure(closure);
        WriteFunctionBytecode(function.Bytecode, function.AtomRelocations ?? []);
        if (function.Debug is { } debug)
        {
            WriteAtom(debug.FileName);
            WriteUnsigned(_body, debug.LineNumber);
            WriteUnsigned(_body, checked((uint)debug.PcToLine.Count));
            _body.AddRange(debug.PcToLine);
        }
        foreach (var constant in constants) WriteValue(constant);
    }

    private void WriteVariable(BytecodeObjectVariable variable)
    {
        if (variable.ScopeNext < -2) throw new ArgumentOutOfRangeException(nameof(variable.ScopeNext));
        WriteAtom(variable.Name);
        WriteUnsigned(_body, variable.ScopeLevel);
        // ARG_SCOPE_END is -2. The reference format stores scope_next + 1
        // as an unsigned LEB128 value, so this sentinel intentionally wraps
        // to uint.MaxValue rather than being rejected as an ordinary index.
        WriteUnsigned(_body, unchecked((uint)(variable.ScopeNext + 1)));
        _body.Add((byte)((byte)variable.Kind |
                         (variable.IsConst ? 1 << 4 : 0) |
                         (variable.IsLexical ? 1 << 5 : 0) |
                         (variable.IsCaptured ? 1 << 6 : 0)));
    }

    private void WriteClosure(BytecodeObjectClosure closure)
    {
        WriteAtom(closure.Name);
        WriteUnsigned(_body, closure.VariableIndex);
        _body.Add((byte)((closure.IsLocal ? 1 : 0) |
                         (closure.IsArgument ? 1 << 1 : 0) |
                         (closure.IsConst ? 1 << 2 : 0) |
                         (closure.IsLexical ? 1 << 3 : 0) |
                         ((byte)closure.Kind << 4)));
    }

    private void WriteAtom(BytecodeObjectAtom atom)
    {
        WriteUnsigned(_body, atom.IsTaggedInteger
            ? (atom.Id << 1) | 1
            : ResolveAtom(atom) << 1);
    }

    private uint ResolveAtom(BytecodeObjectAtom atom)
    {
        if (atom.IsTaggedInteger) return checked(atom.Id | 0x8000_0000u);
        if (atom.DynamicName is null)
        {
            return atom.Id >= BytecodeTargetAbi.FirstDynamicAtom ? throw new ArgumentOutOfRangeException(nameof(atom), "Dynamic atoms require a name.") : atom.Id;
        }
        var index = _dynamicAtoms.Register(atom.DynamicName);
        return checked(BytecodeTargetAbi.FirstDynamicAtom + (uint)index);
    }

    private void WriteFunctionBytecode(IReadOnlyList<byte> bytecode,
        IReadOnlyList<BytecodeObjectAtomRelocation> relocations)
    {
        if (relocations.Count == 0)
        {
            _body.AddRange(bytecode);
            return;
        }

        var copy = bytecode.ToArray();
        foreach (var relocation in relocations)
        {
            if (relocation.OperandOffset < 0 || relocation.OperandOffset > copy.Length - sizeof(uint))
                throw new ArgumentOutOfRangeException(nameof(relocations), "An atom relocation lies outside the bytecode buffer.");
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(relocation.OperandOffset), ResolveAtom(relocation.Atom));
        }
        _body.AddRange(copy);
    }

    private static ushort PackFunctionFlags(IrFunctionObject function)
    {
        ushort flags = 0;
        if (function.HasPrototype) flags |= 1 << 0;
        if (function.HasSimpleParameterList) flags |= 1 << 1;
        if (function.IsDerivedClassConstructor) flags |= 1 << 2;
        if (function.NeedsHomeObject) flags |= 1 << 3;
        flags |= (ushort)((byte)function.Kind << 4);
        if (function.NewTargetAllowed) flags |= 1 << 6;
        if (function.SuperCallAllowed) flags |= 1 << 7;
        if (function.SuperAllowed) flags |= 1 << 8;
        if (function.ArgumentsAllowed) flags |= 1 << 9;
        if (function.Debug is not null) flags |= 1 << 10;
        if (function.BacktraceBarrier) flags |= 1 << 11;
        return flags;
    }

    internal static void WriteUnsigned(List<byte> output, uint value)
    {
        do
        {
            var next = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0) next |= 0x80;
            output.Add(next);
        } while (value != 0);
    }

    internal static void WriteSigned(List<byte> output, int value)
    {
        var remaining = value;
        while (true)
        {
            var next = (byte)(remaining & 0x7f);
            remaining >>= 7;
            var done = (remaining == 0 && (next & 0x40) == 0) ||
                       (remaining == -1 && (next & 0x40) != 0);
            if (!done) next |= 0x80;
            output.Add(next);
            if (done) return;
        }
    }

    private static void WriteString(List<byte> output, string value)
    {
        var wide = value.Any(character => character > byte.MaxValue);
        WriteUnsigned(output, checked(((uint)value.Length << 1) | (wide ? 1u : 0u)));
        if (wide)
        {
            foreach (var character in value)
            {
                output.Add((byte)character);
                output.Add((byte)(character >> 8));
            }
        }
        else
        {
            foreach (var character in value) output.Add((byte)character);
        }
    }
}
