using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;

namespace Warp.JsCompiler.TestPass;

public sealed class AppendMarkerPass : IIrPass, IBytecodeAssemblyPass
{
    public void Run(IrModule module) => module.RequiredModules.Add("@pass-marker");

    public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program) => program;
}
