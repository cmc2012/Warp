using Warp.JsCompiler.Api;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;

namespace Warp.JsCompiler.Tests;

/// <summary>Opt-in structural diagnostics for closure ownership investigations.</summary>
internal static class IrClosureDiagnostic
{
    internal static string Describe(string source, string fileName)
    {
        var program = new JavaScriptFrontEnd(source, fileName, JavaScriptSourceKind.Module).Parse();
        var module = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);
        new PseudoBindingPass().Run(module);
        var assembly = new IrToBytecodeAssemblyLowerer().Run(module);
        var assemblyById = assembly.Functions.ToDictionary(function => function.Id.Value);
        var lines = new List<string>();
        foreach (var function in module.Functions)
        {
            var parent = function.ParentFunction?.Value.ToString() ?? "-";
            lines.Add($"IR f{function.Id.Value} parent={parent} parentScope={function.ParentScope?.Value.ToString() ?? "-"} form={function.Options.Form}");
            lines.Add("  bindings " + string.Join("; ", function.Bindings.Select(binding =>
                $"{binding.Id.Value}:{binding.Name}@s{binding.Scope.Value}/{binding.Kind}/arg={binding.IsArgument}/const={binding.IsConst}/lex={binding.IsLexical}")));
            lines.Add("  scopes " + string.Join("; ", function.Scopes.Select(scope =>
                $"s{scope.Id.Value}->s{scope.Parent?.Value.ToString() ?? "-"}[{string.Join(',', scope.Bindings.Select(id => id.Value))}]")));
            var lowered = assemblyById[function.Id.Value];
            lines.Add("  locals " + string.Join("; ", (lowered.Metadata.Locals ?? []).Select((local, index) =>
                $"{index}:{local.Name?.Symbol ?? "<predef>"}/{local.Kind}/cap={local.IsCaptured}/const={local.IsConst}/lex={local.IsLexical}")));
            lines.Add("  closures " + string.Join("; ", (lowered.Metadata.Closures ?? []).Select((closure, index) =>
                $"{index}:{closure.Name.Symbol ?? "<predef>"}->#{closure.ParentIndex}/{closure.Kind}/local={closure.IsLocal}/arg={closure.IsArgument}/const={closure.IsConst}/lex={closure.IsLexical}")));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
