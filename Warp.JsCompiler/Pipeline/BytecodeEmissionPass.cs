using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.ObjectFormat;

namespace Warp.JsCompiler.Pipeline;

/// <summary>Lowers resolved IR through assembly and serializes a ECMAScript object.</summary>
internal static class BytecodeEmissionPass
{
    internal static byte[] Run(IrModule module, CompilerPasses passes)
    {
        var assembly = CompilerPassPipeline.LowerAndRunAssemblyPasses(
            module, new IrToBytecodeAssemblyLowerer(), passes);
        var encoded = new BytecodeAssemblyEncoder().Encode(assembly);
        var objectValue = new EncodedAssemblyObjectPass().Run(encoded);
        return new BytecodeObjectWriter().Write(objectValue);
    }
}
