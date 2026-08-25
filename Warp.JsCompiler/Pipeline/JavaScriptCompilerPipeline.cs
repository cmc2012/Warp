using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;

namespace Warp.JsCompiler.Pipeline;

/// <summary>
/// The single production compiler path. Each stage corresponds to the order in
/// js_create_function: parse lowering, child-first variable resolution, label
/// resolution, stack computation, then object serialization.
/// </summary>
internal static class JavaScriptCompilerPipeline
{
    internal static byte[] Compile(JavaScriptProgram program, bool stripDebugInfo)
    {
        ArgumentNullException.ThrowIfNull(program);

        // Module grammar is strict.  Keep the validation in the production
        // pipeline (rather than the reusable front-end parser) so tools that
        // parse solely to discover module imports can still invoke the
        // reference compiler and report its own early error.
        if (program.Kind == Api.JavaScriptSourceKind.Module)
            new JavaScriptStrictBindingValidator(program.FileName).ValidateModule(program.Ast);

        var unresolved = new ProgramIrLowerer().Run(program);
        var passes = ProductionPassFactory.Create(program, stripDebugInfo);
        CompilerPassPipeline.RunIrPasses(unresolved, passes);
        return BytecodeEmissionPass.Run(unresolved, passes);
    }
}
