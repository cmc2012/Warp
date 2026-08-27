using Warp.ComponentCompiler.Analysis;
using Warp.ComponentCompiler.Ir;
using Warp.ComponentCompiler.Scripting;
using Warp.ComponentCompiler.Translation;
using Warp.ComponentSyntax.Parsing;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.ComponentCompiler.Tests;

public sealed class TemplateTranslationTests
{
    [Fact]
    public void Preserves_lifecycle_keys_while_rewriting_their_self_calls_after_method_minification()
    {
        var sink = new DiagnosticSink();
        var logic = new ComponentScriptParser().Parse("export default { onShow() { return this.refresh(); }, refresh() { return 1; } };", "page.js", sink);

        var (minified, names, _) = ComponentMethodNameMinifier.Minify(logic, new Dictionary<string, InlineComponentDefinition>());
        var output = JavaScriptAstWriter.Write(ScriptTranslator.Translate(minified, isPage: true));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Equal("a", names["refresh"]);
        Assert.Contains("onShow(){return this.a();}", output, StringComparison.Ordinal);
        Assert.DoesNotContain("this.refresh()", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrites_self_calls_in_lifecycle_callbacks_wrapped_with_call_this()
    {
        var sink = new DiagnosticSink();
        var logic = new ComponentScriptParser().Parse("export default { onInit() { return (function () { return this.refresh(); }).call(this); }, refresh() { return 1; } };", "page.js", sink);

        var (minified, names, _) = ComponentMethodNameMinifier.Minify(logic, new Dictionary<string, InlineComponentDefinition>());
        var output = JavaScriptAstWriter.Write(ScriptTranslator.Translate(minified, isPage: true));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Equal("a", names["refresh"]);
        Assert.Contains("this.a()", output, StringComparison.Ordinal);
        Assert.DoesNotContain("this.refresh()", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lowers_wxaml_to_render_ir_without_erasing_bindings_or_control_flow()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("""
            <Page x:Class="Sample">
              <Text Text="{Binding title}" />
              <List ItemsSource="{Binding items}" Key="id"><ItemTemplate><Text Text="{Binding name}" /></ItemTemplate></List>
            </Page>
            """, "sample.wxaml", sink);

        var program = new ComponentRenderIrLowerer(new Dictionary<string, InlineComponentDefinition>(), null).Lower(document.Children);

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        var element = Assert.IsType<ComponentRenderElement>(program.Nodes[0]);
        Assert.Equal("text", element.RuntimeTag);
        var list = Assert.IsType<ComponentRenderList>(program.Nodes[1]);
        Assert.IsType<BindingValue>(list.ItemsSource);
        Assert.IsType<ComponentRenderElement>(list.ItemTemplateRoot);
    }

    [Fact]
    public void Emits_parseable_module_for_xaml_list_and_model()
    {
        const string markup = """
            <Page x:Class="Sample"><Input Model="{Binding name}" /><List ItemsSource="{Binding items}"><ItemTemplate><Text Text="{Binding name}" /></ItemTemplate></List></Page>
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

    [Fact]
    public void Lowers_structural_tag_selectors_to_classes_on_their_rendered_roots()
    {
        var parser = new WxamlParser();
        var sink = new DiagnosticSink();
        var host = parser.Parse("""
            <Page x:Class="Host">
              <Page.Styles>
                <Style Tag="List"><Setter Property="color" Value="#fff" /></Style>
                <Style Tag="ItemTemplate"><Setter Property="font-size" Value="14px" /></Style>
                <Style Tag="ItemCard"><Setter Property="padding" Value="4px" /></Style>
                <Style Tag="Page"><Setter Property="background-color" Value="#000" /></Style>
                <Style Tag="If"><Setter Property="margin" Value="2px" /></Style>
                <Style Tag="component"><Setter Property="opacity" Value="0.5" /></Style>
              </Page.Styles>
              <Import Name="ItemCard" Source="./ItemCard.wxaml" Inline="true" />
              <List ItemsSource="{Binding items}" Key="id"><ItemTemplate><Text Text="{Binding name}" /></ItemTemplate></List>
              <ItemCard />
              <If Test="{Binding visible}"><Text Text="shown" /></If>
              <component is="{Binding current}" />
            </Page>
            """, "host.wxaml", sink);
        var inline = parser.Parse("<Component x:Class=\"ItemCard\"><Text Text=\"card\" /></Component>", "ItemCard.wxaml", sink);
        var logic = new ComponentLogic([], [], [], null, []);
        var transform = StyleSelectorTransform.Create(host.Styles, host.Imports);

        var template = JavaScriptAstWriter.Write(new JsAstProgram([
            new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(
                ConstantTable.Build(logic, sink),
                inlineComponents: new Dictionary<string, InlineComponentDefinition>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ItemCard"] = new InlineComponentDefinition(inline, "ItemCard.wxaml")
                },
                styleSelectorTransform: transform).TranslateAst(host.Children, generatedClasses: transform.GeneratedClassFor("page") is { } pageClass ? [pageClass] : []), 0, 0), 0, 0)
        ]));
        var styles = JavaScriptAstWriter.Write(StyleTranslator.Translate(host.Styles, transform));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("[[0, \"__warp_tag_list\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("[[0, \"__warp_tag_itemtemplate\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("[[0, \"__warp_tag_itemcard\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("[[0, \"__warp_tag_page\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("[[0, \"__warp_tag_if\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("[[0, \"__warp_tag_component\"]]", styles, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_list", template, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_itemtemplate", template, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_itemcard", template, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_page", template, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_if", template, StringComparison.Ordinal);
        Assert.Contains("__warp_tag_component", template, StringComparison.Ordinal);
    }
}
