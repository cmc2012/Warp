namespace Warp.JsCompiler.Ir.Passes;

internal interface IIrPass
{
    void Run(IrModule module);
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
