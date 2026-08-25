using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class CompilerPassPipelineTests
{
    [Fact]
    public void Ir_passes_run_in_registration_order()
    {
        var events = new List<int>();
        var module = EmptyIr();
        var passes = new CompilerPasses([new RecordingIrPass(events, 1), new RecordingIrPass(events, 2)]);

        CompilerPassPipeline.RunIrPasses(module, passes);

        Assert.Equal([1, 2], events);
    }

    [Fact]
    public void Assembly_passes_run_after_the_fixed_lowering_boundary()
    {
        var events = new List<int>();
        var passes = new CompilerPasses(assembly:
            [new RecordingAssemblyPass(events, 2), new RecordingAssemblyPass(events, 3)]);

        CompilerPassPipeline.LowerAndRunAssemblyPasses(EmptyIr(), new RecordingLowering(events), passes);

        Assert.Equal([1, 2, 3], events);
    }

    private static IrModule EmptyIr()
    {
        var function = new IrFunction(new IrFunctionId(0), null,
            new(IrFunctionKind.Normal, IrFunctionForm.Module, true, false, false, false,
                false, false, false, false, false, true, false, false, true),
            new IrScopeId(0), new IrScopeId(1), new IrBlockId(0));
        function.Scopes.Add(new(new IrScopeId(0), null, []));
        function.Scopes.Add(new(new IrScopeId(1), new IrScopeId(0), []));
        function.Blocks.Add(new(new IrBlockId(0)) { Terminator = new IrReturnTerminator(false, SourceLocation.None) });
        var module = new IrModule();
        module.Functions.Add(function);
        return module;
    }

    private sealed class RecordingIrPass(List<int> events, int value) : IIrPass
    {
        public void Run(IrModule module) => events.Add(value);
    }

    private sealed class RecordingLowering(List<int> events) : IIrToAssemblyLoweringPass
    {
        public BytecodeAssemblyProgram Run(IrModule module)
        {
            events.Add(1);
            return Assembly();
        }
    }

    private sealed class RecordingAssemblyPass(List<int> events, int value) : IBytecodeAssemblyPass
    {
        public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program)
        {
            events.Add(value);
            return program;
        }
    }

    private static BytecodeAssemblyProgram Assembly()
    {
        var id = new BytecodeAssemblyFunctionId(0);
        return new(id, [new(id, BytecodeAssemblyAtom.Predefined(1),
            [new(TargetOpcodeCatalog.Get("return_undef"))], new())]);
    }
}
