using Warp.JsCompiler.Ir;

namespace Warp.JsCompiler.Assembly.Passes;

/// <summary>Named insertion points in the production JS -> IR -> ASM -> bytecode pipeline.</summary>
internal static class CompilerPassPipeline
{
    internal static IrModule RunIrPasses(IrModule module, CompilerPasses passes)
    {
        passes.Ir.Run(module);
        return module;
    }

    internal static BytecodeAssemblyProgram LowerAndRunAssemblyPasses(IrModule module,
        IIrToAssemblyLoweringPass lowering, CompilerPasses passes)
    {
        IrVerifier.Verify(module);
        var assembly = lowering.Run(module);
        BytecodeAssemblyVerifier.Verify(assembly);
        return passes.Assembly.Run(assembly);
    }
}
