using Warp.ComponentCompiler.Analysis;
using Warp.ComponentCompiler.Scripting;
using Warp.ComponentCompiler.Translation;
using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.ComponentCompiler.Tests;

public sealed class TemplateTranslationTests
{
    [Fact]
    public void Emits_parseable_module_for_xaml_list_and_model()
    {
        const string markup = """
            <Page x:Class="Sample"><Input model="{Binding name}" /><List ItemsSource="{Binding items}"><ItemTemplate><Text Text="{Binding name}" /></ItemTemplate></List></Page>
            """;
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse(markup, "sample.ux", sink);
        var logic = new ComponentLogic([], [], [], null, []);
        var output = JavaScriptAstWriter.Write(new JsAstProgram([
            new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(ConstantTable.Build(logic, sink)).TranslateAst(document.Children), 0, 0), 0, 0)
        ]));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("model", output, StringComparison.Ordinal);
        Assert.Contains("__cf__", output, StringComparison.Ordinal);
        Assert.NotEmpty(JavaScriptSyntax.ParseScript(output, "generated.js").Body);
    }
}
