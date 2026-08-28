using Warp.JsCompiler.Encoding;

namespace Warp.JsCompiler.Assembly;

/// <summary>Control-flow facts exposed to external bytecode assembly passes.</summary>
public static class BytecodeAssemblyControlFlow
{
    /// <summary>
    /// Computes the unique incoming operand-stack depth for each reachable
    /// assembly label. Returns false for malformed CFGs, stack underflow, or
    /// joins whose incoming depths disagree.
    /// </summary>
    public static bool TryGetLabelEntryStackDepths(IReadOnlyList<BytecodeAssemblyInstruction> instructions,
        out IReadOnlyDictionary<BytecodeAssemblyLabelId, int> labelDepths)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        var labels = new Dictionary<BytecodeAssemblyLabelId, int>();
        foreach (var (instruction, index) in instructions.Select((instruction, index) => (instruction, index)))
            if (instruction.Opcode.Name == "label" &&
                !labels.TryAdd(((BytecodeAssemblyLabelOperand)instruction.Operand!).Label, index))
                goto Failed;
        var depths = new Dictionary<int, int>();
        var result = new Dictionary<BytecodeAssemblyLabelId, int>();
        var pending = new Stack<(int Index, int Depth)>();
        pending.Push((0, 0));
        while (pending.Count != 0)
        {
            var (index, depth) = pending.Pop();
            if ((uint)index >= (uint)instructions.Count || depth < 0) goto Failed;
            if (depths.TryGetValue(index, out var known))
            {
                if (known != depth) goto Failed;
                continue;
            }
            depths[index] = depth;
            var instruction = instructions[index];
            if (instruction.Opcode.Name == "label")
            {
                var label = ((BytecodeAssemblyLabelOperand)instruction.Operand!).Label;
                result[label] = depth;
                if (index + 1 < instructions.Count) pending.Push((index + 1, depth));
                continue;
            }
            var variable = instruction.Operand switch
            {
                BytecodeAssemblyUnsignedOperand count => checked((int)count.Value),
                BytecodeAssemblyEvalOperand eval => eval.ArgumentCount,
                _ => 0,
            };
            var next = checked(depth + instruction.Opcode.StackDelta(variable));
            if (next < 0) goto Failed;
            foreach (var edge in instruction.Opcode.Successors)
            {
                var target = edge.Kind == TargetOpcodeSuccessorKind.Fallthrough ? index + 1 :
                    labels[((BytecodeAssemblyLabelOperand)instruction.Operand!).Label];
                if ((uint)target >= (uint)instructions.Count) goto Failed;
                pending.Push((target, checked(next + edge.StackAdjustment)));
            }
        }
        labelDepths = result;
        return true;

    Failed:
        labelDepths = new Dictionary<BytecodeAssemblyLabelId, int>();
        return false;
    }

    /// <summary>
    /// Returns whether every reachable labelled block entry is reached with an
    /// empty operand stack. This is the safe precondition for transformations
    /// that replace edges without transporting expression values.
    /// </summary>
    public static bool HasEmptyStackAtLabelBoundaries(IReadOnlyList<BytecodeAssemblyInstruction> instructions)
    {
        return TryGetLabelEntryStackDepths(instructions, out var depths) && depths.Values.All(depth => depth == 0);
    }
}
