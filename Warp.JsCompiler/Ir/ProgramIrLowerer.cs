using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Ir;

internal sealed class ProgramIrLowerer
{
    internal IrModule Run(JavaScriptProgram program) =>
        new AstToIrLowerer().Run(program.Ast, program.Kind);
}
