using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;

namespace Warp.JsCompiler.Pipeline;

/// <summary>
/// The single production compiler path. Each stage corresponds to the order in
/// js_create_function: parse lowering, child-first variable resolution, label
/// resolution, stack computation, then object serialization.
/// </summary>
internal static class JavaScriptCompilerPipeline
{
    internal static byte[] Compile(JavaScriptProgram program, bool stripDebugInfo, bool minifyLocalBindings,
        IEnumerable<IIrPass>? externalIrPasses = null,
        IEnumerable<IPostPseudoIrPass>? externalPostPseudoIrPasses = null,
        IEnumerable<IBytecodeAssemblyPass>? externalAssemblyPasses = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var unresolved = new ProgramIrLowerer().Run(program);
        return CompileIr(program, unresolved, stripDebugInfo, minifyLocalBindings, externalIrPasses, externalPostPseudoIrPasses, externalAssemblyPasses);
    }

    internal static byte[] CompileIr(JavaScriptProgram program, IrModule unresolved, bool stripDebugInfo, bool minifyLocalBindings,
        IEnumerable<IIrPass>? externalIrPasses = null,
        IEnumerable<IPostPseudoIrPass>? externalPostPseudoIrPasses = null,
        IEnumerable<IBytecodeAssemblyPass>? externalAssemblyPasses = null)
    {
        // Module grammar is strict.  Keep the validation in the production
        // pipeline (rather than the reusable front-end parser) so tools that
        // parse solely to discover module imports can still invoke the
        // reference compiler and report its own early error.
        if (program.Kind == Api.JavaScriptSourceKind.Module)
            new JavaScriptStrictBindingValidator(program.FileName).ValidateModule(program.Ast);

        var passes = ProductionPassFactory.Create(program, stripDebugInfo, minifyLocalBindings, externalIrPasses, externalPostPseudoIrPasses, externalAssemblyPasses);
        CompilerPassPipeline.RunIrPasses(unresolved, passes);
        return BytecodeEmissionPass.Run(unresolved, passes);
    }
}
