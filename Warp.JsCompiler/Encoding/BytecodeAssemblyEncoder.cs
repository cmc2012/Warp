using System.Buffers.Binary;
using Warp.JsCompiler.Assembly;

namespace Warp.JsCompiler.Encoding;

internal sealed record EncodedAssemblyAtomRelocation(int OperandOffset, BytecodeAssemblyAtom Atom);

internal sealed record EncodedAssemblyFunction(
    BytecodeAssemblyFunctionId Id,
    BytecodeAssemblyAtom Name,
    IReadOnlyList<byte> Code,
    IReadOnlyList<EncodedAssemblyAtomRelocation> AtomRelocations,
    BytecodeAssemblyFunctionMetadata Metadata,
    IReadOnlyList<BytecodeAssemblyConstant> Constants);

internal sealed record EncodedAssemblyProgram(
    BytecodeAssemblyFunctionId Entry,
    IReadOnlyList<EncodedAssemblyFunction> Functions,
    BytecodeAssemblyModuleMetadata? Module);

/// <summary>ECMAScript label layout, compact opcode selection and byte encoding.</summary>
internal sealed class BytecodeAssemblyEncoder
{
    private static readonly IEqualityComparer<BytecodeAssemblyInstruction> InstructionIdentity =
        ReferenceEqualityComparer.Instance;

    internal EncodedAssemblyProgram Encode(BytecodeAssemblyProgram program)
    {
        BytecodeAssemblyVerifier.Verify(program);
        return new EncodedAssemblyProgram(program.Entry, program.Functions.Select(EncodeFunction).ToArray(), program.Module);
    }

    private static EncodedAssemblyFunction EncodeFunction(BytecodeAssemblyFunction function)
    {
        var instructions = function.Instructions;
        var relocatedAtoms = (function.AtomRelocations ?? []).ToDictionary(relocation =>
            function.Instructions[relocation.InstructionIndex], relocation => relocation.Atom,
            InstructionIdentity);
        var layout = Layout(instructions);
        var output = new List<byte>(layout.CodeSize);
        var relocations = new List<EncodedAssemblyAtomRelocation>();

        foreach (var instruction in instructions)
        {
            if (instruction.Opcode.Name == "label") continue;
            EncodeInstruction(output, instruction, layout, relocatedAtoms, relocations);
        }

        var stack = OperandStackAnalyzer.ComputeMaximumStack(instructions);
        var metadata = function.Metadata with { MaximumStackSize = stack };
        return new EncodedAssemblyFunction(function.Id, function.Name, output, relocations,
            metadata, function.Constants ?? []);
    }

    private sealed record LayoutResult(
        IReadOnlyDictionary<BytecodeAssemblyInstruction, int> Offsets,
        IReadOnlyDictionary<BytecodeAssemblyLabelId, int> Labels,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, TargetOpcodeDescriptor> Encodings,
        int CodeSize);

    private static LayoutResult Layout(IReadOnlyList<BytecodeAssemblyInstruction> instructions)
    {
        var selected = instructions.ToDictionary(instruction => instruction, instruction => instruction.Opcode,
            InstructionIdentity);
        Dictionary<BytecodeAssemblyInstruction, int> offsets;
        Dictionary<BytecodeAssemblyLabelId, int> labels;
        var codeSize = 0;
        for (var iteration = 0; iteration <= instructions.Count; iteration++)
        {
            (offsets, labels, codeSize) = Measure(instructions, selected);
            var changed = false;
            foreach (var instruction in instructions)
            {
                var shorter = SelectEncoding(instruction, offsets, labels);
                if (shorter != selected[instruction]) { selected[instruction] = shorter; changed = true; }
            }
            if (!changed) return new LayoutResult(offsets, labels, selected, codeSize);
        }
        throw new InvalidOperationException("ECMAScript instruction layout did not converge.");
    }

    private static (Dictionary<BytecodeAssemblyInstruction, int> Offsets,
        Dictionary<BytecodeAssemblyLabelId, int> Labels, int Size) Measure(
        IReadOnlyList<BytecodeAssemblyInstruction> instructions,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, TargetOpcodeDescriptor> selected)
    {
        var offsets = new Dictionary<BytecodeAssemblyInstruction, int>(InstructionIdentity);
        var labels = new Dictionary<BytecodeAssemblyLabelId, int>();
        var offset = 0;
        foreach (var instruction in instructions)
        {
            offsets.Add(instruction, offset);
            if (instruction.Opcode.Name == "label")
            {
                labels.Add(((BytecodeAssemblyLabelOperand)instruction.Operand!).Label, offset);
                continue;
            }
            offset = checked(offset + selected[instruction].Size);
        }
        return (offsets, labels, offset);
    }

    private static TargetOpcodeDescriptor SelectEncoding(BytecodeAssemblyInstruction instruction,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, int> offsets,
        IReadOnlyDictionary<BytecodeAssemblyLabelId, int> labels)
    {
        var name = instruction.Opcode.Name;
        if (name == "push_i32" && instruction.Operand is BytecodeAssemblySignedOperand integer)
        {
            if (integer.Value is >= -1 and <= 7) return TargetOpcodeCatalog.Get($"push_{(integer.Value == -1 ? "minus1" : integer.Value)}");
            if (integer.Value is >= sbyte.MinValue and <= sbyte.MaxValue) return TargetOpcodeCatalog.Get("push_i8");
            if (integer.Value is >= short.MinValue and <= short.MaxValue) return TargetOpcodeCatalog.Get("push_i16");
        }
        if (instruction.Operand is BytecodeAssemblyLocalOperand local)
        {
            if (local.Index <= 3 && name is "get_loc" or "put_loc" or "set_loc")
                return TargetOpcodeCatalog.Get($"{name}{local.Index}");
            if (local.Index <= byte.MaxValue && name is "get_loc" or "put_loc" or "set_loc")
                return TargetOpcodeCatalog.Get(name + "8");
        }
        if (instruction.Operand is BytecodeAssemblyArgumentOperand { ForceCanonical: false } argument && argument.Index <= 3 &&
            name is "get_arg" or "put_arg" or "set_arg")
            return TargetOpcodeCatalog.Get($"{name}{argument.Index}");
        if (instruction.Operand is BytecodeAssemblyVarReferenceOperand reference && reference.Index <= 3 &&
            name is "get_var_ref" or "put_var_ref" or "set_var_ref")
            return TargetOpcodeCatalog.Get($"{name}{reference.Index}");
        if (instruction.Operand is BytecodeAssemblyConstantOperand constant && constant.Constant.Value <= byte.MaxValue &&
            name is "push_const" or "fclosure")
            return TargetOpcodeCatalog.Get(name + "8");
        if (name == "call" && instruction.Operand is BytecodeAssemblyUnsignedOperand { Value: <= 3 } call)
            return TargetOpcodeCatalog.Get($"call{call.Value}");
        if (instruction.Operand is BytecodeAssemblyLabelOperand branch && name is "if_false" or "if_true" or "goto")
        {
            var operandStart = offsets[instruction] + 1;
            var delta = labels[branch.Label] - operandStart;
            if (delta is >= sbyte.MinValue and <= sbyte.MaxValue)
                return TargetOpcodeCatalog.Get(name + "8");
            if (name == "goto" && delta is >= short.MinValue and <= short.MaxValue)
                return TargetOpcodeCatalog.Get("goto16");
        }
        return instruction.Opcode;
    }

    private static void EncodeInstruction(List<byte> output, BytecodeAssemblyInstruction instruction,
        LayoutResult layout,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, BytecodeAssemblyAtom> atoms,
        ICollection<EncodedAssemblyAtomRelocation> relocations)
    {
        var encoding = layout.Encodings[instruction];
        output.Add(encoding.Code);
        var operand = instruction.Operand;
        switch (encoding.OperandFormat)
        {
            case TargetOpcodeOperandFormat.None:
            case TargetOpcodeOperandFormat.NoneInt:
            case TargetOpcodeOperandFormat.NoneLocal:
            case TargetOpcodeOperandFormat.NoneArgument:
            case TargetOpcodeOperandFormat.NoneVarReference:
            case TargetOpcodeOperandFormat.InlineVariablePop:
                return;
            case TargetOpcodeOperandFormat.I8:
                output.Add(unchecked((byte)((BytecodeAssemblySignedOperand)operand!).Value)); return;
            case TargetOpcodeOperandFormat.I16:
                WriteU16(output, unchecked((ushort)((BytecodeAssemblySignedOperand)operand!).Value)); return;
            case TargetOpcodeOperandFormat.I32:
                WriteU32(output, unchecked((uint)((BytecodeAssemblySignedOperand)operand!).Value)); return;
            case TargetOpcodeOperandFormat.U8:
            case TargetOpcodeOperandFormat.Constant8:
            case TargetOpcodeOperandFormat.Local8:
                output.Add(checked((byte)UnsignedValue(operand!))); return;
            case TargetOpcodeOperandFormat.U16:
            case TargetOpcodeOperandFormat.VariablePop:
            case TargetOpcodeOperandFormat.Local:
            case TargetOpcodeOperandFormat.Argument:
            case TargetOpcodeOperandFormat.VarReference:
                WriteU16(output, checked((ushort)UnsignedValue(operand!))); return;
            case TargetOpcodeOperandFormat.VariablePopU16:
                var eval = (BytecodeAssemblyEvalOperand)operand!;
                WriteU16(output, eval.ArgumentCount);
                WriteU16(output, eval.ScopeIndex);
                return;
            case TargetOpcodeOperandFormat.Constant:
                WriteU32(output, checked((uint)UnsignedValue(operand!))); return;
            case TargetOpcodeOperandFormat.Label8:
            case TargetOpcodeOperandFormat.Label16:
            case TargetOpcodeOperandFormat.Label:
                WriteLabel(output, encoding, (BytecodeAssemblyLabelOperand)operand!, layout); return;
            case TargetOpcodeOperandFormat.Atom:
            case TargetOpcodeOperandFormat.AtomU8:
            case TargetOpcodeOperandFormat.AtomU16:
                WriteAtom(output, instruction, atoms, relocations);
                if (encoding.OperandFormat == TargetOpcodeOperandFormat.AtomU8)
                    output.Add(checked((byte)((BytecodeAssemblyAtomReferenceOperand)operand!).Flags));
                else if (encoding.OperandFormat == TargetOpcodeOperandFormat.AtomU16)
                    WriteU16(output, ((BytecodeAssemblyAtomReferenceOperand)operand!).Flags);
                return;
            case TargetOpcodeOperandFormat.AtomLabelU8:
            case TargetOpcodeOperandFormat.AtomLabelU16:
                WriteAtom(output, instruction, atoms, relocations);
                var atomLabel = (BytecodeAssemblyAtomLabelOperand)operand!;
                WriteU32(output, unchecked((uint)(layout.Labels[atomLabel.Label] - (output.Count))));
                if (encoding.OperandFormat == TargetOpcodeOperandFormat.AtomLabelU8) output.Add(atomLabel.Flags);
                else WriteU16(output, atomLabel.Flags);
                return;
            default:
                throw new NotSupportedException($"Assembly encoding does not support {encoding.OperandFormat} yet.");
        }
    }

    private static void WriteAtom(List<byte> output, BytecodeAssemblyInstruction instruction,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, BytecodeAssemblyAtom> atoms,
        ICollection<EncodedAssemblyAtomRelocation> relocations)
    {
        if (!atoms.TryGetValue(instruction, out var atom))
            throw new InvalidOperationException("Atom instruction lost its relocation.");
        relocations.Add(new EncodedAssemblyAtomRelocation(output.Count, atom));
        WriteU32(output, 0);
    }

    private static void WriteLabel(List<byte> output, TargetOpcodeDescriptor encoding,
        BytecodeAssemblyLabelOperand operand, LayoutResult layout)
    {
        var delta = layout.Labels[operand.Label] - output.Count;
        if (encoding.OperandFormat == TargetOpcodeOperandFormat.Label8) output.Add(unchecked((byte)(sbyte)delta));
        else if (encoding.OperandFormat == TargetOpcodeOperandFormat.Label16) WriteU16(output, unchecked((ushort)(short)delta));
        else WriteU32(output, unchecked((uint)delta));
    }

    private static ulong UnsignedValue(BytecodeAssemblyOperand operand) => operand switch
    {
        BytecodeAssemblyUnsignedOperand value => value.Value,
        BytecodeAssemblyLocalOperand value => value.Index,
        BytecodeAssemblyArgumentOperand value => value.Index,
        BytecodeAssemblyVarReferenceOperand value => value.Index,
        BytecodeAssemblyConstantOperand value => checked((ulong)value.Constant.Value),
        _ => throw new InvalidOperationException("Operand is not unsigned."),
    };

    private static void WriteU16(List<byte> output, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value); output.AddRange(bytes);
    }

    private static void WriteU32(List<byte> output, uint value)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); output.AddRange(bytes);
    }
}
