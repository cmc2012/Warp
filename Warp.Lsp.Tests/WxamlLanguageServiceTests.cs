using Xunit;

namespace Warp.Lsp.Tests;

public sealed class WxamlLanguageServiceTests
{
    private readonly WxamlLanguageService _service = new();

    [Fact]
    public void Publishes_parser_errors_as_lsp_diagnostics()
    {
        var diagnostics = _service.GetDiagnostics("file:///workspace/home.wxaml", "<Page><Unknown /></Page>");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(1, diagnostic.Severity);
        Assert.Contains("unknown element", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void Accepts_resource_dictionary_as_a_valid_wxaml_style_resource()
    {
        var diagnostics = _service.GetDiagnostics("file:///workspace/styles/common.wxaml",
            "<ResourceDictionary><Style Selector=\".card\"><Setter Property=\"padding\" Value=\"4px\" /></Style></ResourceDictionary>");

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Message.Contains("root element", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Completes_elements_after_an_open_angle_bracket()
    {
        var completions = _service.GetCompletions("<", new LspPosition(0, 1));

        Assert.Contains(completions, item => item.Label == "Page");
        Assert.Contains(completions, item => item.Label == "Component");
    }

    [Fact]
    public void Explains_const_attribute()
    {
        var hover = _service.GetHover("<Div Const=\"true\" />", new LspPosition(0, 7));

        Assert.NotNull(hover);
        Assert.Contains("runtime", hover, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolves_component_use_to_import_source()
    {
        const string source = "<Page><Import Name=\"Card\" Source=\"./components/Card.wxaml\" /><Card /></Page>";
        var definition = _service.GetDefinition("file:///workspace/pages/home.wxaml", source, new LspPosition(0, source.LastIndexOf("Card", StringComparison.Ordinal) + 1));

        Assert.NotNull(definition);
        Assert.Equal("file:///workspace/pages/components/Card.wxaml", definition!.Uri);
    }

    [Fact]
    public void Produces_semantic_tokens_for_wxaml_markup()
    {
        var tokens = WxamlSemanticTokens.Encode("<Text Text=\"{Binding title}\" Hint=\"Hello\" />");

        Assert.Equal(0, tokens[0]); // first token starts on the first line
        Assert.Contains(1, tokens); // element/type token
        Assert.Contains(2, tokens); // attribute/property token
        Assert.Contains(3, tokens); // literal value/string token
        Assert.Contains(4, tokens); // binding/expression token
        Assert.Contains(7, tokens); // XML tag delimiters
        Assert.Contains(8, tokens); // binding braces
    }

    [Fact]
    public void Explains_mismatched_xml_closing_tag()
    {
        var diagnostics = _service.GetDiagnostics("file:///workspace/home.wxaml", "<Page><Text></Page>");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("mismatched closing tag </Page>; expected </Text>", diagnostic.Message);
        Assert.Equal(0, diagnostic.Range.Start.Line);
        Assert.Equal(12, diagnostic.Range.Start.Character);
    }

    [Fact]
    public void Completes_attributes_inside_an_open_tag()
    {
        var completions = _service.GetCompletions("<Text Te", new LspPosition(0, 8));

        Assert.Contains(completions, item => item.Label == "Text");
        Assert.DoesNotContain(completions, item => item.Label == "Page");
    }

    [Fact]
    public void Completes_binding_forms_inside_an_attribute_value()
    {
        var completions = _service.GetCompletions("<Text Text=\"{\" />", new LspPosition(0, 14));

        Assert.Contains(completions, item => item.Label == "Binding");
        Assert.Contains(completions, item => item.Label == "Expr");
    }

    [Fact]
    public void Completion_matching_is_case_insensitive_for_the_editor_client()
    {
        var completion = Assert.Single(_service.GetCompletions("<pa", new LspPosition(0, 3)), item => item.Label == "Page");

        Assert.Equal("Page", completion.Label);
        Assert.Equal("pa", completion.FilterText);
    }

    [Fact]
    public void Completes_the_innermost_unclosed_element_after_a_closing_angle_bracket()
    {
        var completion = Assert.Single(_service.GetCompletions("<Page><Text></te", new LspPosition(0, 16)));

        Assert.Equal("Text", completion.Label);
        Assert.Equal("Text>", completion.TextEdit!.NewText);
        Assert.Equal("te", completion.FilterText);
    }

    [Fact]
    public void Element_completion_replaces_a_qualified_name_instead_of_appending_to_its_last_segment()
    {
        const string source = "<Page.St";
        var completion = Assert.Single(_service.GetCompletions(source, new LspPosition(0, source.Length)), item => item.Label == "Page.Styles");

        Assert.Equal(new LspPosition(0, 1), completion.TextEdit!.Range.Start);
        Assert.Equal(new LspPosition(0, source.Length), completion.TextEdit.Range.End);
        Assert.Equal("Page.Styles>", completion.TextEdit.NewText);
    }

    [Fact]
    public void Navigates_between_style_selectors_and_class_uses()
    {
        const string source = "<Page><Page.Styles><Style Selector=\".card\" /></Page.Styles><Stack Class=\"card\" /></Page>";
        var selector = source.IndexOf("card\"", StringComparison.Ordinal);
        var classUse = source.LastIndexOf("card\"", StringComparison.Ordinal);

        var selectorDefinition = _service.GetDefinition("file:///workspace/home.wxaml", source, new LspPosition(0, selector + 1));
        var classDefinition = _service.GetDefinition("file:///workspace/home.wxaml", source, new LspPosition(0, classUse + 1));

        Assert.Equal(classUse, selectorDefinition!.Range.Start.Character);
        Assert.Equal(selector, classDefinition!.Range.Start.Character);
    }

    [Fact]
    public void Finds_all_class_usages_for_a_selector()
    {
        const string source = "<Page><Page.Styles><Style Selector=\".card\" /></Page.Styles><Stack Class=\"card\" /><Text Class=\"card\" /></Page>";
        var selector = source.IndexOf("card\"", StringComparison.Ordinal);

        var references = _service.GetReferences("file:///workspace/home.wxaml", source, new LspPosition(0, selector + 1));

        Assert.Equal(2, references.Count);
        Assert.All(references, reference => Assert.Equal("file:///workspace/home.wxaml", reference.Uri));
    }

    [Fact]
    public void Explains_style_references_on_hover()
    {
        const string source = "<Style Selector=\".card\" />";
        var hover = _service.GetHover(source, new LspPosition(0, source.IndexOf("card", StringComparison.Ordinal) + 1));

        Assert.Contains("style selector", hover, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".card", hover, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_the_precise_origin_range_for_style_navigation()
    {
        const string source = "<Style Selector=\".card\" />";
        var origin = _service.GetNavigationOrigin(source, new LspPosition(0, source.IndexOf("card", StringComparison.Ordinal) + 1));

        Assert.Equal(source.IndexOf("card", StringComparison.Ordinal), origin!.Start.Character);
        Assert.Equal(4, origin.End.Character - origin.Start.Character);
    }
}
