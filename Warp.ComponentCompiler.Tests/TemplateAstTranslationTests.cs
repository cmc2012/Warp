using Warp.ComponentCompiler.Analysis;
using Warp.ComponentCompiler.Scripting;
using Warp.ComponentCompiler.Translation;
using Warp.ComponentSyntax.Ast;
using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Warp.Testing;
using Xunit;

namespace Warp.ComponentCompiler.Tests;

public sealed class TemplateAstTranslationTests
{
    [Fact]
    public void Emits_native_style_rules_as_selector_chain_and_declarations()
    {
        var stylesheet = new UxStyleSheet([
            new UxStyleRule([new StyleSelector(StyleSelectorKind.Class, "page")], [
                new StyleDeclaration("color", new ColorStyleValue("#ffffff"))
            ])
        ]);

        var output = JavaScriptAstWriter.Write(StyleTranslator.Translate(stylesheet));

        Assert.Equal("[[[[0, \"page\"]], {color: \"#ffffff\"}]]", output);
    }

    [Fact]
    public void Expands_box_and_border_shorthands_for_the_native_style_abi()
    {
        var sink = new DiagnosticSink();
        var stylesheet = new StyleParser().Parse(".box { margin: 10px 6px; padding: 1px 2px 3px; border: 3px solid #fff; }", "sample.css", sink);

        var output = JavaScriptAstWriter.Write(StyleTranslator.Translate(stylesheet));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("marginTop: \"10px\"", output, StringComparison.Ordinal);
        Assert.Contains("marginLeft: \"6px\"", output, StringComparison.Ordinal);
        Assert.Contains("paddingBottom: \"3px\"", output, StringComparison.Ordinal);
        Assert.Contains("borderTopWidth: \"3px\"", output, StringComparison.Ordinal);
        Assert.Contains("borderStyle: \"solid\"", output, StringComparison.Ordinal);
        Assert.Contains("borderLeftColor: \"#ffffff\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Supports_xaml_styles_and_media_queries_while_preserving_css()
    {
        const string markup = """
            <Page x:Class="Styled">
              <Page.Styles>
                <Style Class="page"><Setter Property="margin" Value="10px 6px" /><Setter Property="color" Value="#fff" /></Style>
                <Media Query="(min-width: 320px), screen and (max-height: 480px)">
                  <Style Tag="Text"><Setter Property="font-size" Value="18px" /></Style>
                </Media>
              </Page.Styles>
              <Stack Class="page" />
            </Page>
            """;
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse(markup, "styled.wxaml", sink);
        var output = JavaScriptAstWriter.Write(StyleTranslator.Translate(document.Styles));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("marginTop: \"10px\"", output, StringComparison.Ordinal);
        Assert.Contains("condition: \"screen and (min-width: 320px),screen and (max-height: 480px)\"", output, StringComparison.Ordinal);
        Assert.Contains("fontSize: \"18px\"", output, StringComparison.Ordinal);
        Assert.NotEmpty(JavaScriptSyntax.ParseScript("const styles = " + output + ";", "generated.js").Body);
    }

    [Fact]
    public void Emits_tag_selectors_in_regular_and_media_only_style_sheets()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("""
            <Page x:Class="Styled">
              <Page.Styles>
                <Media Query="(min-width: 320px)">
                  <Style Tag="Text"><Setter Property="color" Value="#fff" /></Style>
                </Media>
              </Page.Styles>
              <Text Text="Hello" />
            </Page>
            """, "styled.wxaml", sink);

        var output = JavaScriptAstWriter.Write(StyleTranslator.Translate(document.Styles));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("[[2, \"text\"]]", output, StringComparison.Ordinal);
        Assert.Contains("condition: \"screen and (min-width: 320px)\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Supports_css_media_queries_with_target_condition_encoding()
    {
        var sink = new DiagnosticSink();
        var stylesheet = new StyleParser().Parse(".page { color: red; } @media (min-width: 320px) { .page { color: blue; } }", "sample.css", sink);
        var output = JavaScriptAstWriter.Write(StyleTranslator.Translate(stylesheet));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("condition: \"screen and (min-width: 320px)\"", output, StringComparison.Ordinal);
        Assert.Contains("color: \"blue\"", output, StringComparison.Ordinal);
        Assert.NotEmpty(JavaScriptSyntax.ParseScript("const styles = " + output + ";", "generated.js").Body);
    }

    [Fact]
    public void Rejects_css_text_in_wxaml_styles_without_removing_the_css_parser()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("<Page x:Class=\"Styled\"><Page.Styles>.page { color: red; }</Page.Styles></Page>", "styled.wxaml", sink);

        Assert.True(sink.HasErrors);
        Assert.Empty(document.Styles!.Rules);

        var cssSink = new DiagnosticSink();
        Assert.NotEmpty(new StyleParser().Parse(".page { color: red; }", "compat.css", cssSink).Rules);
        Assert.False(cssSink.HasErrors);
    }

    [Fact]
    public void Imports_xaml_style_resources_and_reports_cycles()
    {
        var directory = TestWorkspace.CreateDirectory("warp-style-import");
        var pagePath = Path.Combine(directory, "page.wxaml");
        var resourcePath = Path.Combine(directory, "shared.wxaml");
        try
        {
            File.WriteAllText(resourcePath, "<ResourceDictionary><Style Class=\"shared\"><Setter Property=\"color\" Value=\"#fff\" /></Style><Media Query=\"(min-width: 320px)\"><Style Tag=\"Text\"><Setter Property=\"font-size\" Value=\"18px\" /></Style></Media></ResourceDictionary>");
            var sink = new DiagnosticSink();
            var document = new WxamlParser().Parse("<Page x:Class=\"Styled\"><Page.Styles><Import Source=\"./shared.wxaml\" /></Page.Styles><Stack /></Page>", pagePath, sink);

            Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
            Assert.Single(document.Styles!.Rules);
            Assert.Single(document.Styles.MediaRules!);

            File.WriteAllText(resourcePath, "<ResourceDictionary><ResourceDictionary Source=\"./shared.wxaml\" /></ResourceDictionary>");
            sink = new DiagnosticSink();
            new WxamlParser().Parse("<Page x:Class=\"Styled\"><Page.Styles><Import Source=\"./shared.wxaml\" /></Page.Styles><Stack /></Page>", pagePath, sink);
            Assert.True(sink.HasErrors);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Supports_pascal_case_component_import_with_source_attribute()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("<Page x:Class=\"Host\"><Import Name=\"Card\" Source=\"./Card.wxaml\" /><Card /></Page>", "host.wxaml", sink);

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        var import = Assert.Single(document.Imports);
        Assert.Equal("Card", import.Name);
        Assert.Equal("./Card.wxaml", import.Src);
    }

    [Fact]
    public void Lowers_xaml_lists_conditionals_models_and_events_to_a_parseable_module()
    {
        const string markup = """
            <Page x:Class="Sample">
              <Input Model="{Binding name}" Change="save" />
              <List ItemsSource="{Binding items}" Key="id"><ItemTemplate><Text Text="{Binding name}" /></ItemTemplate></List>
              <If Test="{Binding visible}"><Text Text="shown" /></If><Else><Image Source="/hidden.png" /></Else>
            </Page>
            """;
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse(markup, "sample.wxaml", sink);
        var logic = new ComponentLogic([], [], [], null, []);
        var output = JavaScriptAstWriter.Write(new JsAstProgram([
            new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(ConstantTable.Build(logic, sink)).TranslateAst(document.Children), 0, 0), 0, 0)
        ]));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("model", output, StringComparison.Ordinal);
        Assert.Contains("events: {change", output, StringComparison.Ordinal);
        Assert.Contains("__cf__", output, StringComparison.Ordinal);
        Assert.Contains("__ci__", output, StringComparison.Ordinal);
        AssertCanonicalJavaScript(output);
    }

    [Fact]
    public void Resolves_static_media_resources_relative_to_the_source_root()
    {
        const string markup = "<Page x:Class=\"Media\"><Video src=\"../../assets/movie.mp4\" alt=\"../../assets/poster.png\" /><Lottie Source=\"../../assets/animation.json\" /></Page>";
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse(markup, "/project/src/pages/home/home.wxaml", sink);
        var output = JavaScriptAstWriter.Write(new JsAstProgram([
            new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(ConstantTable.Build(new ComponentLogic([], [], [], null, []), sink), templatePath: "/project/src/pages/home/home.wxaml", sourceRootPath: "/project/src").TranslateAst(document.Children), 0, 0), 0, 0)
        ]));

        Assert.Contains("/assets/movie.mp4", output, StringComparison.Ordinal);
        Assert.Contains("/assets/poster.png", output, StringComparison.Ordinal);
        Assert.Contains("/assets/animation.json", output, StringComparison.Ordinal);
        AssertCanonicalJavaScript(output);
    }

    [Fact]
    public void Supports_dynamic_components_and_static_interactive_elements()
    {
        const string markup = "<Page x:Class=\"Dynamic\"><component is=\"{Binding currentView}\" remotewidget=\"{Binding remoteWidget}\" /><Map static=\"\" Click=\"select\" /></Page>";
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse(markup, "dynamic.wxaml", sink);
        var output = JavaScriptAstWriter.Write(new JsAstProgram([
            new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(ConstantTable.Build(new ComponentLogic([], [], [], null, []), sink)).TranslateAst(document.Children), 0, 0), 0, 0)
        ]));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("__cdc__", output, StringComparison.Ordinal);
        Assert.Contains("static: true", output, StringComparison.Ordinal);
        Assert.Contains("interactive: true", output, StringComparison.Ordinal);
        AssertCanonicalJavaScript(output);
    }

    [Fact]
    public void Validates_and_lowers_const_subtrees_to_static_nodes()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("<Page x:Class=\"Const\"><Stack Const=\"true\"><Text Text=\"fixed\" /></Stack></Page>", "const.wxaml", sink);
        var output = JavaScriptAstWriter.Write(new JsAstProgram([new JsExpressionStatement(new JsArrayExpression(new TemplateTranslator(ConstantTable.Build(new ComponentLogic([], [], [], null, []), sink)).TranslateAst(document.Children), 0, 0), 0, 0)]));

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        Assert.Contains("static: true", output, StringComparison.Ordinal);

        sink = new DiagnosticSink();
        new WxamlParser().Parse("<Page x:Class=\"Const\"><Text Const=\"true\" Text=\"{Binding title}\" /></Page>", "const.wxaml", sink);
        Assert.True(sink.HasErrors);
    }

    private static void AssertCanonicalJavaScript(string source)
    {
        var reparsed = JavaScriptSyntax.ParseScript(source, "generated.js");
        Assert.NotEmpty(reparsed.Body);
        Assert.Equal(source, JavaScriptAstWriter.Write(reparsed));
    }
}
