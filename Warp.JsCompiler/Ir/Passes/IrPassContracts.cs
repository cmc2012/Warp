namespace Warp.JsCompiler.Ir.Passes;

public interface IIrPass
{
    void Run(IrModule module);
}

/// <summary>
/// Runs after pseudo bindings are materialized but before variable/closure slot
/// resolution. External passes can safely rename or regroup lexical bindings
/// at this boundary without depending on compiler-internal pass ordering.
/// </summary>
public interface IPostPseudoIrPass
{
    void Run(IrModule module);
}

/// <summary>Extension point for transformations requiring visibility of every module in a resolved graph.</summary>
public interface IModuleGraphPass
{
    void Run(IrModuleGraph graph);
}

internal sealed class IrPassManager(IEnumerable<IIrPass> passes)
{
    private readonly IReadOnlyList<IIrPass> _passes = passes.ToArray();

    internal void Run(IrModule module)
    {
        IrVerifier.Verify(module);
        foreach (var pass in _passes)
        {
            pass.Run(module);
            IrVerifier.Verify(module);
        }
    }
}
