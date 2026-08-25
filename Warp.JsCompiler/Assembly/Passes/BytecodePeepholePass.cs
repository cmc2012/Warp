using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.ObjectFormat;

namespace Warp.JsCompiler.Assembly.Passes;

/// <summary>ECMAScript-compatible instruction rewrites performed after variable resolution.</summary>
internal sealed class BytecodePeepholePass : IBytecodeAssemblyPass
{
    private static readonly IEqualityComparer<BytecodeAssemblyInstruction> InstructionIdentity =
        ReferenceEqualityComparer.Instance;

    public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program) =>
        program with { Functions = program.Functions.Select(Run).ToArray() };

    private static BytecodeAssemblyFunction Run(BytecodeAssemblyFunction function)
    {
        var source = function.Instructions;
        var output = new List<BytecodeAssemblyInstruction>(source.Count);
        var labels = source.Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.Opcode.Name == "label")
            .ToDictionary(item => ((BytecodeAssemblyLabelOperand)item.instruction.Operand!).Label, item => item.index);
        // resolve_labels redirects references to a label whose first live
        // operation is an unconditional jump.  Keep the label itself (it may
        // still be reached by fallthrough), but canonicalize explicit edges.
        var labelAliases = labels.Keys.ToDictionary(label => label, label => label);
        foreach (var label in labels.Keys)
        {
            var target = label;
            var seen = new HashSet<BytecodeAssemblyLabelId>();
            while (seen.Add(target) && labels.TryGetValue(target, out var labelIndex) &&
                   FirstOpcodeAfterLabel(source, labelIndex) is
                       { Opcode.Name: "goto", Operand: BytecodeAssemblyLabelOperand jump,
                           PreserveAfterResolution: false })
                target = jump.Label;
            labelAliases[label] = target;
        }
        // LabelSlot.ref_count is mutable: resolve_labels decrements it as a
        // jump is folded or skipped, then skip_dead_code uses the evolving
        // value to decide where lexical emission becomes live again.
        var labelReferences = labels.Keys.ToDictionary(label => label, _ => 0);
        foreach (var instruction in source)
            if (instruction.Opcode.Name != "label" && instruction.Operand is BytecodeAssemblyLabelOperand edge &&
                labelReferences.ContainsKey(edge.Label))
                labelReferences[edge.Label]++;
        // A catch instruction protects its fallthrough range until the
        // handler label.  Resolver optimizations must not turn an abrupt
        // completion within that range into an unprotected tail sequence:
        // doing so changes which exception handler observes a throw while
        // evaluating the discarded completion value.
        var protectedByCatch = new bool[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            if (source[index] is not { Opcode.Name: "catch", Operand: BytecodeAssemblyLabelOperand handler } ||
                !labels.TryGetValue(handler.Label, out var handlerIndex)) continue;
            for (var protectedIndex = index + 1; protectedIndex < handlerIndex; protectedIndex++)
                protectedByCatch[protectedIndex] = true;
        }
        var atomRelocations = (function.AtomRelocations ?? []).ToDictionary(
            relocation => source[relocation.InstructionIndex], relocation => relocation,
            InstructionIdentity);
        var consumedRelocations = new HashSet<BytecodeAssemblyInstruction>(InstructionIdentity);
        var invertedBranchGotos = new HashSet<int>();

        for (var index = 0; index < source.Count; index++)
        {
            if (invertedBranchGotos.Contains(index)) continue;
            var instruction = source[index];
            // find_jump_target() moves the edge's mutable reference while it
            // follows a label/goto chain.  The conditional inversion form is
            // handled below as one combined rewrite: its following goto owns
            // the replacement reference, so defer the transfer in that case.
            // It first removes the old label reference, follows any label /
            // goto chain, then adds the reference at the final destination.
            // The mutable counts are observable to skip_dead_code, so merely
            // rewriting the operand is not equivalent.
            if (instruction.Opcode.Name is "goto" or "if_false" or "if_true" &&
                instruction.Operand is BytecodeAssemblyLabelOperand edge &&
                labelAliases.TryGetValue(edge.Label, out var alias) && alias != edge.Label &&
                !TryInvertBranchAcrossLabels(source, index, out _, out _))
            {
                labelReferences[edge.Label]--;
                labelReferences[alias]++;
                instruction = instruction with { Operand = edge with { Label = alias } };
            }
            // Assignment expressions preserve their value for an enclosing
            // expression.  When an expression statement immediately drops
            // that value, emitting the lvalue keep permutation is redundant:
            // `object, value, insert2, put_field, drop` is exactly the plain
            // statement form `object, value, put_field`.  The same applies to
            // computed members, whose lvalue has one additional key slot.
            // Perform this after name resolution so it works for every kind
            // of receiver rather than teaching expression construction about
            // statement-only special cases.
            if (instruction.Opcode.Name == "insert2" && index + 2 < source.Count &&
                source[index + 1].Opcode.Name == "put_field" && source[index + 2].Opcode.Name == "drop")
            {
                output.Add(source[index + 1]);
                index += 2;
                continue;
            }
            if (instruction.Opcode.Name == "insert3" && index + 2 < source.Count &&
                source[index + 1].Opcode.Name == "put_array_el" && source[index + 2].Opcode.Name == "drop")
            {
                output.Add(source[index + 1]);
                index += 2;
                continue;
            }
            if (instruction.Opcode.Name is "call" or "call_method" && index + 1 < source.Count &&
                source[index + 1].Opcode.Name == "return" && !protectedByCatch[index])
            {
                output.Add(instruction with
                {
                    Opcode = TargetOpcodeCatalog.Get(
                        instruction.Opcode.Name == "call" ? "tail_call" : "tail_call_method"),
                });
                // resolve_labels treats the folded tail call as terminal and
                // immediately invokes skip_dead_code from the matched
                // return.  This matters for a terminal case/default followed
                // by an unreferenced labelled-statement exit: the source
                // parser did append its implicit return, but resolution
                // removes that dead tail rather than serializing it.
                index = SkipDeadCode(source, index + 2, labelReferences, atomRelocations,
                    consumedRelocations) - 1;
                continue;
            }
            // resolve_labels folds the common typeof comparison immediately
            // after parsing the expression.  This is a semantic opcode (it
            // preserves the host-defined typeof behavior), not a textual
            // shortcut, so apply it to every atom relocation.
            if (instruction.Opcode.Name == "typeof" && index + 2 < source.Count &&
                source[index + 1].Opcode.Name == "push_atom_value" &&
                (source[index + 2].Opcode.Name is "strict_eq" or "eq") &&
                atomRelocations.TryGetValue(source[index + 1], out var typeofAtom) &&
                TypeofTestOpcode(typeofAtom.Atom) is { } typeofOpcode)
            {
                output.Add(instruction with { Opcode = TargetOpcodeCatalog.Get(typeofOpcode) });
                consumedRelocations.Add(source[index + 1]);
                index += 2;
                continue;
            }
            if (instruction.Opcode.Name == "undefined" && index + 1 < source.Count &&
                source[index + 1].Opcode.Name == "strict_eq")
            {
                output.Add(instruction with
                {
                    Opcode = TargetOpcodeCatalog.Get("is_undefined") with
                        { EncodingKind = TargetOpcodeEncodingKind.Canonical },
                });
                index++;
                continue;
            }
            if (instruction.Opcode.Name == "dup" && index + 1 < source.Count &&
                source[index + 1] is { Operand: not null } put &&
                put.Opcode.Name is "put_loc" or "put_arg" or "put_var_ref")
            {
                if (index + 2 < source.Count && source[index + 2].Opcode.Name == "drop")
                {
                    output.Add(put);
                    index += 2;
                    continue;
                }
                output.Add(put with
                {
                    Opcode = TargetOpcodeCatalog.Get(
                        put.Opcode.Name.Replace("put_", "set_", StringComparison.Ordinal)),
                });
                index++;
                continue;
            }
            if (instruction.Opcode.Name == "get_field" &&
                atomRelocations.TryGetValue(instruction, out var fieldRelocation) &&
                IsLengthAtom(fieldRelocation.Atom))
            {
                output.Add(instruction with
                    { Opcode = TargetOpcodeCatalog.Get("get_length"), Operand = null });
                consumedRelocations.Add(instruction);
                continue;
            }
            if (TryInvertBranchAcrossLabels(source, index, out var inverseTarget, out var gotoIndex))
            {
                var inverse = instruction.Opcode.Name == "if_false" ? "if_true" : "if_false";
                output.Add(instruction with
                {
                    Opcode = TargetOpcodeCatalog.Get(inverse),
                    Operand = inverseTarget,
                });
                invertedBranchGotos.Add(gotoIndex);
                continue;
            }
            if (instruction.Opcode.Name == "goto" && instruction.Operand is BytecodeAssemblyLabelOperand jump &&
                index + 1 < source.Count && source[index + 1].Opcode.Name == "label" &&
                source[index + 1].Operand is BytecodeAssemblyLabelOperand next && next.Label == jump.Label &&
                !instruction.PreserveAfterResolution)
            {
                labelReferences[jump.Label]--;
                continue;
            }
            if (instruction.Opcode.Name == "goto" && instruction.Operand is BytecodeAssemblyLabelOperand terminalJump &&
                TerminalOpcodeAfterLabel(source, labels[terminalJump.Label]) is { } terminal)
            {
                // ecma.c resolve_labels: replace goto with the terminal,
                // decrement the old label edge, then call skip_dead_code from
                // the original bytecode position following the goto.
                // terminalJump is the resolved destination.  The alias
                // transfer above has already moved the edge here when
                // needed; this is the resolver's final removal of it.
                labelReferences[terminalJump.Label]--;
                output.Add(terminal with { Location = instruction.Location });
                index = SkipDeadCode(source, index + 1, labelReferences, atomRelocations,
                    consumedRelocations) - 1;
                continue;
            }
            if (StartsDeadCodeScan(instruction))
            {
                output.Add(instruction);
                index = SkipDeadCode(source, index + 1, labelReferences, atomRelocations,
                    consumedRelocations) - 1;
                continue;
            }
            if (instruction.Opcode.Name == "drop" && index + 1 < source.Count &&
                source[index + 1].Opcode.Name == "return_undef" && !protectedByCatch[index] &&
                !instruction.PreserveAfterResolution)
                continue;
            if (instruction.Opcode.Name == "push_i32" && instruction.Operand is BytecodeAssemblySignedOperand value &&
                value.Value is not 0 and not int.MinValue && index + 1 < source.Count &&
                source[index + 1].Opcode.Name == "neg")
            {
                output.Add(instruction with { Operand = new BytecodeAssemblySignedOperand(-value.Value) });
                index++;
                continue;
            }
            if (instruction.Opcode.Name is "post_inc" or "post_dec" && index + 2 < source.Count &&
                source[index + 1].Opcode.Name is "put_loc" or "put_arg" or "put_var_ref" &&
                source[index + 2].Opcode.Name == "drop")
            {
                output.Add(instruction with
                {
                    Opcode = TargetOpcodeCatalog.Get(
                        instruction.Opcode.Name == "post_inc" ? "inc" : "dec"),
                });
                output.Add(source[index + 1]);
                index += 2;
                continue;
            }
            if (index + 1 < source.Count &&
                !(index + 2 < source.Count && source[index + 2].Opcode.Name == "drop" &&
                  protectedByCatch[index + 2]) &&
                TrySetOpcode(instruction, source[index + 1], out var set))
            {
                output.Add(set);
                index++;
                continue;
            }
            output.Add(instruction);
        }

        var outputIndices = output.Select((instruction, index) => (instruction, index))
            .ToDictionary(item => item.instruction, item => item.index, InstructionIdentity);
        var relocations = (function.AtomRelocations ?? [])
            .Where(relocation => !consumedRelocations.Contains(source[relocation.InstructionIndex]))
            .Select(relocation =>
        {
            var instruction = source[relocation.InstructionIndex];
            if (!outputIndices.TryGetValue(instruction, out var newIndex))
                throw new InvalidOperationException("An assembly pass removed an instruction with an atom relocation.");
            return relocation with { InstructionIndex = newIndex };
        }).ToArray();
        return function with { Instructions = output, AtomRelocations = relocations };
    }

    private static bool IsLengthAtom(BytecodeAssemblyAtom atom) =>
        atom.Kind == BytecodeAssemblyAtomKind.Predefined
            ? atom.PredefinedId == PredefinedAtomTable.TryGet("length")
            : atom.Symbol == "length";

    // resolve_labels invokes skip_dead_code for these cases. OP_ret is not
    // included: it resumes a dynamic gosub continuation rather than ending
    // the parser's lexical continuation.
    private static bool StartsDeadCodeScan(BytecodeAssemblyInstruction instruction) => instruction.Opcode.Name is
        "goto" or "return" or "return_undef" or "return_async" or "throw" or "throw_error";

    private static int SkipDeadCode(IReadOnlyList<BytecodeAssemblyInstruction> source, int index,
        IDictionary<BytecodeAssemblyLabelId, int> references,
        IReadOnlyDictionary<BytecodeAssemblyInstruction, BytecodeAssemblyAtomRelocation> atomRelocations,
        ISet<BytecodeAssemblyInstruction> consumedRelocations)
    {
        while (index < source.Count)
        {
            var instruction = source[index];
            if (instruction.Opcode.Name == "label")
            {
                var label = ((BytecodeAssemblyLabelOperand)instruction.Operand!).Label;
                if (references[label] > 0) return index;
                index++;
                continue;
            }
            if (instruction.Operand is BytecodeAssemblyLabelOperand edge && references.ContainsKey(edge.Label))
                references[edge.Label]--;
            if (atomRelocations.ContainsKey(instruction)) consumedRelocations.Add(instruction);
            index++;
        }
        return source.Count;
    }

    private static string? TypeofTestOpcode(BytecodeAssemblyAtom atom) => atom.Kind switch
    {
        BytecodeAssemblyAtomKind.Predefined when atom.PredefinedId == PredefinedAtomTable.TryGet("undefined") =>
            "typeof_is_undefined",
        BytecodeAssemblyAtomKind.Predefined when atom.PredefinedId == PredefinedAtomTable.TryGet("function") =>
            "typeof_is_function",
        BytecodeAssemblyAtomKind.Symbol when atom.Symbol == "undefined" => "typeof_is_undefined",
        BytecodeAssemblyAtomKind.Symbol when atom.Symbol == "function" => "typeof_is_function",
        _ => null,
    };

    private static BytecodeAssemblyInstruction? FirstOpcodeAfterLabel(IReadOnlyList<BytecodeAssemblyInstruction> source,
        int labelIndex)
    {
        for (var index = labelIndex + 1; index < source.Count; index++)
            if (source[index].Opcode.Name != "label") return source[index];
        return null;
    }

    private static bool TryInvertBranchAcrossLabels(IReadOnlyList<BytecodeAssemblyInstruction> source, int index,
        out BytecodeAssemblyLabelOperand target, out int gotoIndex)
    {
        target = null!;
        gotoIndex = -1;
        if (source[index] is not { Opcode.Name: "if_false" or "if_true", Operand: BytecodeAssemblyLabelOperand falseEdge })
            return false;
        var cursor = index + 1;
        while (cursor < source.Count && source[cursor].Opcode.Name == "label") cursor++;
        if (cursor >= source.Count || source[cursor] is not
                { Opcode.Name: "goto", Operand: BytecodeAssemblyLabelOperand taken, PreserveAfterResolution: false })
            return false;
        var foundFalseLabel = false;
        for (cursor++; cursor < source.Count && source[cursor].Opcode.Name == "label"; cursor++)
            foundFalseLabel |= ((BytecodeAssemblyLabelOperand)source[cursor].Operand!).Label == falseEdge.Label;
        if (!foundFalseLabel) return false;
        target = taken;
        gotoIndex = index + 1;
        while (source[gotoIndex].Opcode.Name == "label") gotoIndex++;
        return true;
    }

    private static BytecodeAssemblyInstruction? TerminalOpcodeAfterLabel(
        IReadOnlyList<BytecodeAssemblyInstruction> source, int labelIndex)
    {
        var first = FirstOpcodeAfterLabel(source, labelIndex);
        if (first?.Opcode.Name is "return" or "return_undef" or "throw") return first;
        if (first?.Opcode.Name != "drop") return null;
        var dropIndex = IndexOfIdentity(source, first);
        for (var index = dropIndex + 1; index < source.Count; index++)
        {
            if (source[index].Opcode.Name == "label") continue;
            return source[index].Opcode.Name == "return_undef" ? source[index] : null;
        }
        return null;
    }

    private static int IndexOfIdentity(IReadOnlyList<BytecodeAssemblyInstruction> source,
        BytecodeAssemblyInstruction instruction)
    {
        for (var index = 0; index < source.Count; index++)
            if (ReferenceEquals(source[index], instruction)) return index;
        throw new InvalidOperationException("Assembly instruction identity was lost.");
    }

    private static bool TrySetOpcode(BytecodeAssemblyInstruction put, BytecodeAssemblyInstruction get,
        out BytecodeAssemblyInstruction set)
    {
        set = null!;
        var setName = (put.Opcode.Name, get.Opcode.Name, put.Operand, get.Operand) switch
        {
            ("put_loc", "get_loc", BytecodeAssemblyLocalOperand left, BytecodeAssemblyLocalOperand right) when left == right => "set_loc",
            ("put_arg", "get_arg", BytecodeAssemblyArgumentOperand left, BytecodeAssemblyArgumentOperand right) when left == right => "set_arg",
            ("put_var_ref", "get_var_ref", BytecodeAssemblyVarReferenceOperand left, BytecodeAssemblyVarReferenceOperand right) when left == right => "set_var_ref",
            _ => null,
        };
        if (setName is null) return false;
        set = new BytecodeAssemblyInstruction(TargetOpcodeCatalog.Get(setName), put.Operand, put.Location);
        return true;
    }
}
