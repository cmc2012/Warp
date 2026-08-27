using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class LocalBindingMinificationPassTests
{
    [Fact]
    public void Renames_local_bindings_and_their_closure_references_but_keeps_module_contracts()
    {
        var program = new JavaScriptFrontEnd("export function outer(longArgument) { const localValue = longArgument; return () => localValue; }", "entry.mjs", JavaScriptSourceKind.Module).Parse();
        var module = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);
        new PseudoBindingPass().Run(module);
        new LocalBindingMinificationPass().Run(module);

        var moduleFunction = Assert.Single(module.Functions, function => function.Options.Form == IrFunctionForm.Module);
        Assert.Contains(moduleFunction.Bindings, binding => binding.Name == "outer");
        Assert.DoesNotContain(module.Functions.Where(function => function != moduleFunction).SelectMany(function => function.Bindings), binding => binding.Name is "longArgument" or "localValue");
        Assert.Contains(module.Functions.Where(function => function != moduleFunction).SelectMany(function => function.Bindings), binding => binding.Name == "a");
        Assert.DoesNotContain(module.Functions.SelectMany(function => function.Blocks).SelectMany(block => block.Instructions)
            .Where(instruction => instruction.Operation.StartsWith("scope_", StringComparison.Ordinal))
            .Select(instruction => instruction.Operands.FirstOrDefault()).OfType<AtomOperand>(), atom => atom.Value is "longArgument" or "localValue");
        IrVerifier.Verify(module);
    }
}
