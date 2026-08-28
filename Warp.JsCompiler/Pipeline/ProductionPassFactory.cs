using Warp.JsCompiler.Api;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir.Passes;
using Warp.JsCompiler.ObjectFormat;

namespace Warp.JsCompiler.Pipeline;

/// <summary>Defines the ordered pass pipeline used by production compilation.</summary>
internal static class ProductionPassFactory
{
    internal static CompilerPasses Create(JavaScriptProgram program, bool stripDebugInfo, bool minifyLocalBindings,
        IEnumerable<IIrPass>? externalIrPasses = null,
        IEnumerable<IPostPseudoIrPass>? externalPostPseudoIrPasses = null,
        IEnumerable<IBytecodeAssemblyPass>? externalAssemblyPasses = null)
    {
        var assembly = new List<IBytecodeAssemblyPass>();
        if (program.Kind == JavaScriptSourceKind.Module)
            assembly.Add(new ModuleMetadataPass(BytecodeAssemblyAtom.Named(
                BytecodeTargetAbi.ToTargetModuleName(program.FileName))));
        if (stripDebugInfo)
            assembly.Add(new StripDebugMetadataPass());
        else
            assembly.Add(new DebugMetadataPass(BytecodeAssemblyAtom.Named(program.FileName)));
        assembly.Add(new BytecodePeepholePass());
        assembly.AddRange(externalAssemblyPasses ?? []);
        return new CompilerPasses(
            ir: (externalIrPasses ?? []).Concat([new PseudoBindingPass()])
                .Concat(externalPostPseudoIrPasses?.Select(pass => new PostPseudoAdapter(pass)) ?? [])
                .Concat(minifyLocalBindings ? [new LocalBindingMinificationPass(), new ConstantControlFlowPass()] : [new ConstantControlFlowPass()]),
            assembly: assembly);
    }

    private sealed class PostPseudoAdapter(IPostPseudoIrPass pass) : IIrPass
    {
        public void Run(Warp.JsCompiler.Ir.IrModule module) => pass.Run(module);
    }
}
