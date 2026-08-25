namespace Warp.JsCompiler.Ir.Passes;

/// <summary>Folds literal conditional edges and removes blocks no longer reachable from function entry.</summary>
internal sealed class ConstantControlFlowPass : IIrPass
{
    public void Run(IrModule module)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.Blocks)
            {
                if (block.Terminator is not IrBranchTerminator branch || block.Instructions.Count == 0) continue;
                var last = block.Instructions[^1];
                if (last.Operation is not ("push_true" or "push_false")) continue;
                block.Instructions.RemoveAt(block.Instructions.Count - 1);
                block.Terminator = new IrGotoTerminator(
                    last.Operation == "push_true" ? branch.WhenTrue : branch.WhenFalse, branch.Location,
                    // resolve_labels rewrites a false if_false into a goto
                    // before it removes the skipped source range.  The target
                    // label then becomes adjacent only after resolution, so
                    // the emitted goto remains observable in the bytecode.
                    PreserveAfterResolution: last.Operation == "push_false");
            }

            var blocks = function.Blocks.ToDictionary(block => block.Id);
            var reachable = new HashSet<IrBlockId>();
            var pending = new Stack<IrBlockId>();
            pending.Push(function.Entry);
            // An asynchronous finally ret resumes through the coroutine
            // continuation rather than a statically known CFG edge.  The
            // parser's label_end after such a subroutine is therefore live
            // even when the protected statement itself terminated.  Preserve
            // only that parser continuation; ordinary ret-ended finally
            // regions remain dead, as resolve_labels emits them.
            foreach (var parserContinuation in function.Blocks.Where(block => block.ParserContinuation))
                pending.Push(parserContinuation.Id);
            while (pending.Count != 0)
            {
                var id = pending.Pop();
                if (!reachable.Add(id)) continue;
                foreach (var successor in blocks[id].Terminator!.Successors) pending.Push(successor);
                foreach (var target in blocks[id].Instructions
                             .Where(instruction => instruction.Operation is "catch" or "gosub")
                             .SelectMany(instruction => instruction.Operands.OfType<IrBlockOperand>()))
                    pending.Push(target.Block);
            }
            function.Blocks.RemoveAll(block => !reachable.Contains(block.Id));
        }
    }
}
