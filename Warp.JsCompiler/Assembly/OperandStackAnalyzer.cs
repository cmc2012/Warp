using Warp.JsCompiler.Encoding;

namespace Warp.JsCompiler.Assembly;

/// <summary>Computes operand-stack requirements from the final assembly control-flow graph.</summary>
internal static class OperandStackAnalyzer
{
    internal static ushort ComputeMaximumStack(IReadOnlyList<BytecodeAssemblyInstruction> instructions)
    {
        var labels = instructions.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Opcode.Name == "label")
            .ToDictionary(item => ((BytecodeAssemblyLabelOperand)item.instruction.Operand!).Label, item => item.index);
        var depths = new Dictionary<int, int>();
        var work = new Stack<(int Index, int Depth)>();
        work.Push((0, 0));
        var maximum = 0;
        while (work.Count != 0)
        {
            var (index, depth) = work.Pop();
            if ((uint)index >= (uint)instructions.Count)
                throw new InvalidOperationException("Assembly falls through its end.");
            if (depths.TryGetValue(index, out var prior))
            {
                if (prior != depth)
                {
                    var context = string.Join(", ", instructions
                        .Skip(Math.Max(0, index - 3)).Take(7)
                        .Select((item, offset) => $"{Math.Max(0, index - 3) + offset}:{item.Opcode.Name}"));
                    throw new InvalidOperationException(
                        $"Inconsistent assembly stack depth at instruction {index} ('{instructions[index].Opcode.Name}'): expected {prior}, received {depth}. Nearby: {context}.");
                }
                continue;
            }
            depths.Add(index, depth);
            var instruction = instructions[index];
            if (instruction.Opcode.Name == "label")
            {
                work.Push((index + 1, depth));
                continue;
            }
            var variable = instruction.Operand switch
            {
                BytecodeAssemblyUnsignedOperand count => checked((int)count.Value),
                BytecodeAssemblyEvalOperand eval => eval.ArgumentCount,
                _ => 0,
            };
            var next = checked(depth + instruction.Opcode.StackDelta(variable));
            if (next < 0)
            {
                var context = string.Join(", ", instructions
                    .Skip(Math.Max(0, index - 6)).Take(13)
                    .Select((item, offset) => $"{Math.Max(0, index - 6) + offset}:{item.Opcode.Name} {item.Operand}"));
                throw new InvalidOperationException(
                    $"Assembly operand stack underflow at instruction {index} ('{instruction.Opcode.Name}') with depth {depth}. Nearby: {context}.");
            }
            maximum = Math.Max(maximum, next);
            foreach (var edge in instruction.Opcode.Successors)
            {
                var target = edge.Kind == TargetOpcodeSuccessorKind.Fallthrough
                    ? index + 1
                    : labels[GetTarget(instruction.Operand!)];
                work.Push((target, checked(next + edge.StackAdjustment)));
            }
        }
        return checked((ushort)maximum);
    }

    private static BytecodeAssemblyLabelId GetTarget(BytecodeAssemblyOperand operand) => operand switch
    {
        BytecodeAssemblyLabelOperand target => target.Label,
        BytecodeAssemblyAtomLabelOperand target => target.Label,
        _ => throw new InvalidOperationException("Control opcode has no target."),
    };
}
