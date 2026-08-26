using Warp.ComponentSyntax.Parsing;
using Warp.Diagnostics;
using Xunit;

namespace Warp.ComponentSyntax.Tests;

public sealed class WxamlParserTests
{
    [Fact]
    public void Parses_catalog_elements_and_reports_their_constraints()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("<Page x:Class=\"Sample\"><Map /><Image-Animator FillMode=\"forwards\" /><Lottie /></Page>", "sample.ux", sink);

        Assert.Equal(3, document.Children.Count);
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("<Lottie> requires attribute 'source'", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown element", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_legacy_template_syntax()
    {
        var sink = new DiagnosticSink();
        _ = new WxamlParser().Parse("<Page x:Class=\"Sample\"><block for=\"item in {{items}}\"><Text Text=\"{{item.name}}\" /></block></Page>", "sample.wxaml", sink);

        Assert.True(sink.HasErrors);
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("unknown element <block>", StringComparison.Ordinal));
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("legacy directive", StringComparison.Ordinal));
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("interpolation is not supported", StringComparison.Ordinal));
    }

    [Fact]
    public void Classifies_element_specific_events_without_event_prefixes()
    {
        var sink = new DiagnosticSink();
        var document = new WxamlParser().Parse("<Page x:Class=\"Sample\"><Input Change=\"save\" /><Video TimeUpdate=\"tick\" /><Map RegionChange=\"move\" /></Page>", "sample.wxaml", sink);

        Assert.False(sink.HasErrors, string.Join("\n", sink.Diagnostics));
        foreach (var element in document.Children.Cast<Warp.ComponentSyntax.Ast.UxElement>())
            Assert.Contains(element.Attrs, attribute => attribute.Kind == Warp.ComponentSyntax.Ast.AttrKind.Event);
    }

    [Fact]
    public void Rejects_at_prefixed_event_shorthand()
    {
        var sink = new DiagnosticSink();
        _ = new WxamlParser().Parse("<Page x:Class=\"Sample\"><Text @click=\"save\" /></Page>", "sample.wxaml", sink);

        Assert.True(sink.HasErrors);
        Assert.Contains(sink.Diagnostics, diagnostic => diagnostic.Message.Contains("unsupported event shorthand", StringComparison.Ordinal) || diagnostic.Message.Contains("XML parse error", StringComparison.Ordinal));
    }
}
