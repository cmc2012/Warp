using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;

namespace Warp.JsCompiler.Assembly.Passes;

/// <summary>A pass that transforms resolved ECMAScript assembly without observing frontend IR.</summary>
public interface IBytecodeAssemblyPass
{
    BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program);
}

internal sealed class BytecodeAssemblyPassManager(IEnumerable<IBytecodeAssemblyPass> passes)
{
    private readonly IReadOnlyList<IBytecodeAssemblyPass> _passes = passes.ToArray();

    internal BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program)
    {
        BytecodeAssemblyVerifier.Verify(program);
        foreach (var pass in _passes)
        {
            program = pass.Run(program) ??
                      throw new InvalidOperationException($"Assembly pass {pass.GetType().Name} returned null.");
            BytecodeAssemblyVerifier.Verify(program);
        }
        return program;
    }
}

/// <summary>The fixed resolve_variables boundary after which unresolved names are forbidden.</summary>
internal interface IIrToAssemblyLoweringPass
{
    BytecodeAssemblyProgram Run(IrModule module);
}

internal sealed class CompilerPasses(
    IEnumerable<IIrPass>? ir = null,
    IEnumerable<IBytecodeAssemblyPass>? assembly = null)
{
    internal IrPassManager Ir { get; } = new(ir ?? []);
    internal BytecodeAssemblyPassManager Assembly { get; } = new(assembly ?? []);
}
