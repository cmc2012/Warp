using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Xml.Parser;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;

namespace Warp.ComponentSyntax.Parsing;

public sealed class WxamlParser
{
    private static readonly HashSet<string> NativeTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "img", "image", "div", "a", "text", "span", "label", "maml", "slider", "web", "list", "list-item", "slot", "input",
            "refresh", "refresh2", "refresh-header", "refresh-footer", "ad", "swiper", "progress", "picker", "switch", "textarea", "video", "camera", "map", "custommarker", "canvas",
            "stack", "richtext", "tabs", "tab-content", "tab-bar", "popup", "rating", "marquee", "scrollview", "drawer", "drawer-navigation", "slide-view", "lottie",
            "section-list", "section-group", "section-header", "section-item", "share-button", "shortcut-button", "qrcode", "scroll", "frame-image", "number-image", "chars-image", "time", "svg", "rect",
            "circle", "ellipse", "line", "polyline", "polygon", "path", "barcode", "chart", "image-animator", "arc-text"
        };

    private static readonly HashSet<string> EventMap = new(StringComparer.OrdinalIgnoreCase)
        { "Click","LongPress","Swipe","Focus","Blur","Key","Appear","Disappear",
          "TouchStart","TouchMove","TouchEnd","TouchCancel","Resize",
          "AnimationStart","AnimationIteration","AnimationEnd",
          "bounce", "buttonclick", "callouttap", "cameraframe", "camerainitdone", "cancel", "change", "close", "columnchange", "complete", "controltap", "enterkeyclick", "error", "fail", "finish", "fullscreenchange", "linechange", "load", "loaded", "markertap", "message", "move", "open", "pagefinish", "pagestart", "pause", "playing", "poitap", "prepared", "progress", "pulldownrefresh", "pulluprefresh", "refresh", "regionchange", "scroll", "scrollbegindrag", "scrollbottom", "scrollend", "scrollenddrag", "scrollstart", "scrollstop", "scrolltop", "scrolltouchup", "seeked", "seeking", "selected", "selectionchange", "slide", "start", "success", "swipeend", "swipestart", "tap", "timeupdate", "titlereceive", "visibilitychange" };

    private static readonly IReadOnlyDictionary<string, HashSet<string>> EnumAttributes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["*.disabled"] = ["true", "false", ""],
        ["*.forcedark"] = ["true", "false"],
        ["*.focusable"] = ["false", "true"],
        ["*.autofocus"] = ["false", "true"],
        ["*.aria-unfocusable"] = ["true", "false", ""],
        ["*.descendantfocusability"] = ["before", "after", "block"],
        ["text.type"] = ["text", "html"],
        ["input.type"] = ["text", "button", "checkbox", "radio", "email", "date", "time", "number", "password", "tel", "eventbutton"],
        ["input.autocomplete"] = ["on", "off"],
        ["input.enterkeytype"] = ["default", "next", "go", "done", "send", "search"],
        ["input.eventtype"] = ["shortcut"],
        ["progress.type"] = ["horizontal", "circular", "arc"],
        ["picker.type"] = ["text", "date", "time", "multi-text"],
        ["video.orientation"] = ["landscape", "portrait"],
        ["camera.deviceposition"] = ["back", "front"],
        ["camera.flash"] = ["auto", "on", "off", "torch"],
        ["lottie.rendermode"] = ["AUTOMATIC", "HARDWARE", "SOFTWARE"],
        ["tabs.mode"] = ["fixed", "scrollable"],
        ["drawer-navigation.direction"] = ["start", "end"],
        ["scroll.scroll-x"] = ["true", "false", ""],
        ["scroll.scroll-y"] = ["true", "false", ""],
        ["img.enablenightmode"] = ["true", "false", ""], ["img.autoplay"] = ["true", "false", ""],
        ["div.enablevideofullscreencontainer"] = ["false", "true"], ["a.visited"] = ["false", "true"],
        ["arc-text.direction"] = ["clockwise", "counterclockwise"], ["image.enablenightmode"] = ["true", "false", ""], ["image.autoplay"] = ["true", "false", ""],
        ["maml.enablenightmode"] = ["true", "false", ""], ["maml.autoplay"] = ["true", "false", ""], ["slider.enabled"] = ["true", "false", ""],
        ["web.allowthirdpartycookies"] = ["true", "false", ""], ["web.enablenightmode"] = ["true", "false", ""], ["web.showloadingdialog"] = ["true", "false", ""], ["web.supportzoom"] = ["true", "false", ""],
        ["list.scrollpage"] = ["true", "false", ""], ["list.focusbehavior"] = ["aligned", "edged", "reverseedged", "leadingedged", "trailingedged"], ["input.checked"] = ["false", "true", ""],
        ["refresh.refreshing"] = ["false", "true"], ["refresh.type"] = ["auto", "pulldown"], ["refresh.enable-refresh"] = ["true", "false"],
        ["refresh2.pulldownrefreshing"] = ["false", "true", ""], ["refresh2.pulluprefreshing"] = ["false", "true", ""], ["refresh2.enablepulldown"] = ["false", "true", ""], ["refresh2.enablepullup"] = ["false", "true", ""], ["refresh2.reboundable"] = ["false", "true", ""], ["refresh2.gesture"] = ["false", "true", ""], ["refresh2.refreshing"] = ["false", "true", ""], ["refresh2.type"] = ["auto", "pulldown"],
        ["refresh-header.spinnerstyle"] = ["translation", "front", "behind"], ["refresh-header.autorefresh"] = ["false", "true", ""], ["refresh-header.translationwithcontent"] = ["false", "true", ""],
        ["refresh-footer.spinnerstyle"] = ["translation", "front", "behind"], ["refresh-footer.autorefresh"] = ["false", "true", ""], ["refresh-footer.translationwithcontent"] = ["false", "true", ""],
        ["swiper.autoplay"] = ["false", "true", ""], ["swiper.indicator"] = ["false", "true", ""], ["swiper.loop"] = ["false", "true", ""], ["swiper.vertical"] = ["false", "true", ""], ["swiper.enableswipe"] = ["false", "true", ""], ["switch.checked"] = ["false", "true", ""],
        ["video.muted"] = ["true", "false", ""], ["video.autoplay"] = ["true", "false", ""], ["video.controls"] = ["true", "false", ""], ["video.titlebar"] = ["true", "false", ""],
        ["camera.framesize"] = ["low", "normal", "high"], ["camera.autoexposurelock"] = ["true", "false", ""], ["camera.autowhitebalancelock"] = ["true", "false", ""],
        ["map.showmylocation"] = ["true", "false", ""], ["map.showcompass"] = ["true", "false", ""], ["map.enableoverlooking"] = ["true", "false", ""], ["map.enablezoom"] = ["true", "false", ""], ["map.enablescroll"] = ["true", "false", ""], ["map.enablerotate"] = ["true", "false", ""], ["map.showscale"] = ["true", "false", ""], ["map.showzoom"] = ["true", "false", ""],
        ["richtext.type"] = ["html", "ux", "mix"], ["tab-content.scrollable"] = ["true", "false", ""], ["tab-bar.mode"] = ["fixed", "scrollable"],
        ["popup.placement"] = ["left", "top", "right", "bottom", "topLeft", "topRight", "bottomLeft", "bottomRight"], ["rating.indicator"] = ["false", "true"], ["marquee.direction"] = ["left", "right"],
        ["scrollview.scroll-direction"] = ["vertical", "horizontal", "vertical_horizontal"], ["scrollview.show-scrollbar"] = ["true", "false", ""], ["drawer.enableswipe"] = ["true", "false", ""],
        ["lottie.loop"] = ["false", "true", ""], ["lottie.autoplay"] = ["false", "true", ""], ["slide-view.edge"] = ["right", "left"], ["slide-view.enableslide"] = ["true", "false", ""], ["slide-view.isopen"] = ["false", "true", ""], ["slide-view.layer"] = ["above", "same"],
        ["section-group.expand"] = ["false", "true", ""], ["share-button.usepageparams"] = ["true", "false", ""], ["scroll.bounces"] = ["true", "false", ""], ["frame-image.cache"] = ["false", "true", ""], ["image-animator.fillmode"] = ["none", "forwards"]
    };

    private static readonly IReadOnlyDictionary<string, string[]> RequiredAttributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["list-item"] = ["type"], ["picker"] = ["type"], ["richtext"] = ["type"], ["popup"] = ["target"], ["lottie"] = ["source"]
    };

    public UxDocument Parse(string text, string filePath, DiagnosticSink sink)
    {
        var filePos = new SourcePosition(filePath, 1, 1);
        if (string.IsNullOrWhiteSpace(text))
        {
            sink.Error("empty .wxaml", filePos);
            return EmptyDoc(filePath, filePos);
        }

        var parser = new XmlParser(new XmlParserOptions { IsKeepingSourceReferences = true });
        IDocument doc;
        try
        {
            doc = parser.ParseDocument(text);
        }
        catch (Exception ex)
        {
            sink.Critical($"XML parse error: {ex.Message}", filePos);
            return EmptyDoc(filePath, filePos);
        }

        var root = FindRoot(doc, sink);
        if (root is null)
        {
            sink.Error("missing root element Page or Component", filePos);
            return EmptyDoc(filePath, filePos);
        }

        var isPage = root.TagName.Equals("Page", StringComparison.OrdinalIgnoreCase);
        var isComponent = root.TagName.Equals("Component", StringComparison.OrdinalIgnoreCase);
        var isStyleResource = root.TagName.Equals("ResourceDictionary", StringComparison.OrdinalIgnoreCase)
                              || root.TagName.Equals("Styles", StringComparison.OrdinalIgnoreCase);
        if (isStyleResource)
        {
            // Style resource files are valid .wxaml documents when imported from
            // Page.Styles/Component.Styles. Parse their contents through the same
            // style grammar so diagnostics remain precise instead of reporting a
            // misleading missing Page/Component root.
            _ = ParseXamlStyles(root.Children.OfType<IElement>(), new StyleParser(), filePath, sink,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return new UxDocument(null, null, filePos);
        }
        if (!isPage && !isComponent)
        {
            sink.Error($"root must be Page or Component, got <{root.TagName}>", Pos(root));
            return EmptyDoc(filePath, filePos);
        }

        var className = GetAttr(root, "x:Class") ?? GetAttr(root, "XClass") ?? GetAttr(root, "xclass") ?? InferClassName(filePath);
        if (!IsValidIdentifier(className))
            sink.Error($"x:Class '{className}' is not a valid identifier", Pos(root));

        UxStyleSheet? styles = null;
        var stylesElName = isPage ? "page.styles" : "component.styles";
        var children = new List<UxNode>();
        var imports = new List<UxImportRef>();
        var seenContent = false;

        foreach (var child in root.Children)
        {
            if (child is not IElement el) continue;
            var name = el.TagName.ToLowerInvariant();
            if (name == stylesElName)
            {
                if (seenContent)
                    sink.Error($"<{stylesElName}> must be before content nodes", Pos(el));
                if (styles is not null)
                    sink.Error($"duplicate <{stylesElName}>", Pos(el));
                styles = ParseStyles(el, filePath, sink);
            }
            else if (name == "import")
            {
                if (seenContent)
                    sink.Warning("<import> should be before content", Pos(el));
                var imp = ParseImport(el, sink);
                if (imp is not null)
                {
                    if (imports.Any(x => x.Name.Equals(imp.Name, StringComparison.OrdinalIgnoreCase)))
                        sink.Error($"duplicate import name '{imp.Name}'", Pos(el));
                    else if (NativeTags.Contains(imp.Name) || IsDirectiveTag(imp.Name))
                        sink.Error($"import name '{imp.Name}' conflicts with native/directive", Pos(el));
                    else imports.Add(imp);
                }
            }
            else
            {
                seenContent = true;
                children.Add(ParseElement(el, filePath, sink, imports, itemScope: false));
            }
        }

        children = CoalesceIfChains(children, sink);

        if (isPage)
        {
            var page = new UxPage(className, imports, styles, children);
            return new UxDocument(page, null, filePos);
        }
        else
        {
            var comp = new UxComponent(className, imports, styles, children);
            return new UxDocument(null, comp, filePos);
        }
    }

    private static IElement? FindRoot(IDocument doc, DiagnosticSink sink)
    {
        var el = doc.DocumentElement;
        if (el != null && (el.TagName.Equals("Page", StringComparison.OrdinalIgnoreCase)
                           || el.TagName.Equals("Component", StringComparison.OrdinalIgnoreCase)
                           || el.TagName.Equals("ResourceDictionary", StringComparison.OrdinalIgnoreCase)
                           || el.TagName.Equals("Styles", StringComparison.OrdinalIgnoreCase)))
            return el;
        foreach (var e in doc.All.OfType<IElement>())
            if (e.TagName.Equals("Page", StringComparison.OrdinalIgnoreCase) || e.TagName.Equals("Component", StringComparison.OrdinalIgnoreCase))
                return e;
        return null;
    }

    private static UxDocument EmptyDoc(string filePath, SourcePosition pos)
    {
        var page = new UxPage(InferClassName(filePath), [], null, []);
        return new UxDocument(page, null, pos);
    }

    private static UxImportRef? ParseImport(IElement el, DiagnosticSink sink)
    {
        var name = GetAttr(el, "name");
        var src = GetAttr(el, "source") ?? GetAttr(el, "src");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(src))
        {
            sink.Error("<Import> requires Name and Source", Pos(el));
            return null;
        }
        if (name!.Contains('.'))
            sink.Error($"import name '{name}' must not contain '.'", Pos(el));
        var inlineValue = GetAttr(el, "inline");
        var isInline = inlineValue is not null && inlineValue.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (inlineValue is not null && !isInline)
            sink.Error("Import Inline must be true when specified", Pos(el));
        return new UxImportRef(name, src!, isInline, Pos(el));
    }

    private UxStyleSheet? ParseStyles(IElement el, string filePath, DiagnosticSink sink)
    {
        var styleParser = new StyleParser();
        var children = el.Children.OfType<IElement>().ToArray();
        if (children.Length > 0)
            return ParseXamlStyles(children, styleParser, filePath, sink, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(el.TextContent))
            sink.Error("CSS text in <Page.Styles>/<Component.Styles> is temporarily disabled; use <Style><Setter ... /></Style>", Pos(el));
        return new UxStyleSheet([]);
    }

    private UxStyleSheet ParseXamlStyles(IEnumerable<IElement> elements, StyleParser styleParser, string filePath, DiagnosticSink sink, HashSet<string> importedStyleFiles)
    {
        var rules = new List<UxStyleRule>();
        var mediaRules = new List<UxMediaRule>();
        foreach (var element in elements)
        {
            if (element.TagName.Equals("Import", StringComparison.OrdinalIgnoreCase)
                || element.TagName.Equals("ResourceDictionary", StringComparison.OrdinalIgnoreCase)
                || element.TagName.Equals("StyleImport", StringComparison.OrdinalIgnoreCase))
            {
                var source = GetAttr(element, "Source") ?? GetAttr(element, "Src");
                if (string.IsNullOrWhiteSpace(source))
                {
                    sink.Error("<Import> requires Source", Pos(element));
                    continue;
                }
                var imported = ParseStyleResource(source, styleParser, filePath, sink, importedStyleFiles, Pos(element));
                if (imported is null) continue;
                rules.AddRange(imported.Rules);
                mediaRules.AddRange(imported.MediaRules ?? []);
                continue;
            }
            if (element.TagName.Equals("Style", StringComparison.OrdinalIgnoreCase))
            {
                rules.AddRange(ParseXamlStyle(element, styleParser, filePath, sink));
                continue;
            }
            if (element.TagName.Equals("Media", StringComparison.OrdinalIgnoreCase) || element.TagName.Equals("MediaQuery", StringComparison.OrdinalIgnoreCase))
            {
                var query = GetAttr(element, "Query") ?? GetAttr(element, "Condition");
                if (string.IsNullOrWhiteSpace(query))
                {
                    sink.Error($"<{element.TagName}> requires Query", Pos(element));
                    continue;
                }
                var condition = styleParser.NormalizeMediaCondition(query, filePath, sink);
                if (condition is null) continue;
                var nested = new List<UxStyleRule>();
                foreach (var child in element.Children.OfType<IElement>())
                {
                    if (!child.TagName.Equals("Style", StringComparison.OrdinalIgnoreCase))
                    {
                        sink.Error($"<{element.TagName}> only accepts <Style> children", Pos(child));
                        continue;
                    }
                    nested.AddRange(ParseXamlStyle(child, styleParser, filePath, sink));
                }
                mediaRules.Add(new UxMediaRule(condition, nested, Pos(element)));
                continue;
            }
            sink.Error($"unsupported style element <{element.TagName}> (use <Import>, <Style>, or <Media Query=...>)", Pos(element));
        }
        return new UxStyleSheet(rules, mediaRules);
    }

    private UxStyleSheet? ParseStyleResource(string source, StyleParser styleParser, string importingFilePath, DiagnosticSink sink, HashSet<string> importedStyleFiles, SourcePosition position)
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(importingFilePath)!, source));
        if (!File.Exists(path))
        {
            sink.Error($"style resource '{source}' does not exist", position);
            return null;
        }
        if (!importedStyleFiles.Add(path))
        {
            sink.Error($"cyclic style resource import '{source}'", position);
            return null;
        }
        try
        {
            var text = File.ReadAllText(path);
            var parser = new XmlParser(new XmlParserOptions { IsKeepingSourceReferences = true });
            var document = parser.ParseDocument(text);
            var root = document.DocumentElement;
            if (root is null || !(root.TagName.Equals("ResourceDictionary", StringComparison.OrdinalIgnoreCase)
                               || root.TagName.Equals("Styles", StringComparison.OrdinalIgnoreCase)
                               || root.TagName.Equals("Page.Styles", StringComparison.OrdinalIgnoreCase)
                               || root.TagName.Equals("Component.Styles", StringComparison.OrdinalIgnoreCase)))
            {
                sink.Error($"style resource '{source}' must have a <ResourceDictionary> or <Styles> root", position);
                return null;
            }
            return ParseXamlStyles(root.Children.OfType<IElement>(), styleParser, path, sink, importedStyleFiles);
        }
        catch (Exception ex)
        {
            sink.Error($"unable to parse style resource '{source}': {ex.Message}", position);
            return null;
        }
        finally
        {
            importedStyleFiles.Remove(path);
        }
    }

    private static IReadOnlyList<UxStyleRule> ParseXamlStyle(IElement element, StyleParser styleParser, string filePath, DiagnosticSink sink)
    {
        var className = GetAttr(element, "Class");
        var id = GetAttr(element, "Id");
        var tagName = GetAttr(element, "Tag");
        var targetCount = new[] { className, id, tagName }.Count(value => !string.IsNullOrWhiteSpace(value));
        if (targetCount == 0)
        {
            sink.Error("<Style> requires one target attribute: Class, Id, or Tag", Pos(element));
            return [];
        }
        if (targetCount > 1)
        {
            sink.Error("<Style> accepts exactly one target attribute: Class, Id, or Tag", Pos(element));
            return [];
        }
        var setters = new List<KeyValuePair<string, string>>();
        foreach (var setter in element.Children.OfType<IElement>())
        {
            if (!setter.TagName.Equals("Setter", StringComparison.OrdinalIgnoreCase))
            {
                sink.Error("<Style> only accepts <Setter> children", Pos(setter));
                continue;
            }
            var property = GetAttr(setter, "Property");
            var value = GetAttr(setter, "Value");
            if (string.IsNullOrWhiteSpace(property) || value is null)
            {
                sink.Error("<Setter> requires Property and Value", Pos(setter));
                continue;
            }
            setters.Add(new(property, value));
        }
        var declarations = styleParser.ParseDeclarations(setters, filePath, sink);
        var selector = className is not null
            ? new StyleSelector(StyleSelectorKind.Class, className, Pos(element))
            : id is not null
                ? new StyleSelector(StyleSelectorKind.Id, id, Pos(element))
                : new StyleSelector(StyleSelectorKind.Tag, tagName!, Pos(element));
        return [new UxStyleRule([selector], declarations, Pos(element))];
    }

    private UxNode ParseElement(IElement el, string filePath, DiagnosticSink sink, IReadOnlyList<UxImportRef> imports, bool itemScope)
    {
        var tag = el.TagName;
        if (tag.Equals("img", StringComparison.OrdinalIgnoreCase)) tag = "Image";

        if (IsIfTag(tag) || tag.Equals("else", StringComparison.OrdinalIgnoreCase))
            return ParseIfBranch(el, filePath, sink, imports, itemScope);
        if (tag.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var hasItemsSource = el.Attributes.Any(a => a.Name.Equals("itemssource", StringComparison.OrdinalIgnoreCase) || a.Name.Equals("ItemsSource", StringComparison.OrdinalIgnoreCase));
            if (hasItemsSource)
                return ParseList(el, filePath, sink, imports);
        }
        if (tag.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase))
        {
            sink.Error("<ItemTemplate> must be inside <List>", Pos(el));
            return new UxElement("ItemTemplate", false, [], [], Pos(el));
        }

        var isListWithItemsSource = tag.Equals("list", StringComparison.OrdinalIgnoreCase) && el.Attributes.Any(a => a.Name.Equals("itemssource", StringComparison.OrdinalIgnoreCase));
        var isComponent = imports.Any(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
        var isNative = NativeTags.Contains(tag) || tag.Equals("component", StringComparison.OrdinalIgnoreCase);
        var isDirective = IsDirectiveTag(tag)
            && !tag.Equals("component", StringComparison.OrdinalIgnoreCase)
            && !(tag.Equals("list", StringComparison.OrdinalIgnoreCase) && !isListWithItemsSource);

        if (!isNative && !isComponent && !isDirective)
            sink.Error($"unknown element <{tag}> (did you forget <import>?)", Pos(el));

        var attrs = new List<UxAttr>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isStatic = false;
        var isConst = false;
        foreach (var a in el.Attributes)
        {
            var rawName = a.Name;
            var rawValue = a.Value ?? "";
            IReadOnlyList<string>? modifiers = null;
            if (rawName.StartsWith("@", StringComparison.Ordinal))
            {
                sink.Error($"attribute '{rawName}' uses unsupported event shorthand (use a named event attribute)", Pos(el));
                continue;
            }
            if (rawName.Equals("x:class", StringComparison.OrdinalIgnoreCase)) continue;
            if (rawName.Equals("static", StringComparison.OrdinalIgnoreCase))
            {
                isStatic = true;
                continue;
            }
            if (rawName.Equals("const", StringComparison.OrdinalIgnoreCase))
            {
                if (rawValue.Length > 0 && !rawValue.Equals("true", StringComparison.OrdinalIgnoreCase))
                    sink.Error("Const must be true when specified", Pos(el));
                else
                    isConst = true;
                continue;
            }
            if (rawName.Contains('.'))
            {
                var parts = rawName.Split('.', StringSplitOptions.RemoveEmptyEntries);
                rawName = parts[0];
                modifiers = parts.Skip(1).Where(part => !part.Equals("static", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            if (IsOfficialDirectiveAttr(rawName) && !rawName.Equals("show", StringComparison.OrdinalIgnoreCase))
            {
                sink.Error($"attribute '{rawName}' is a legacy directive (use <If>/<List>)", Pos(el));
                continue;
            }

            if (!seenNames.Add(rawName))
                sink.Error($"duplicate attribute '{rawName}'", Pos(el));

            var kind = ClassifyAttr(tag, rawName, isComponent);
            var value = ParseAttrValue(rawValue, rawName, kind, Pos(el), sink, itemScope);
            ValidateEnumAttribute(tag, rawName, value, Pos(el), sink);
            attrs.Add(new UxAttr(kind, rawName, value, Pos(el), modifiers));
        }
        ValidateRequiredAttributes(tag, attrs, Pos(el), sink);

        if (isDirective)
        {
            var allowed = AllowedAttrsForTag(tag);
            foreach (var at in attrs)
                if (!allowed.Contains(at.Name, StringComparer.OrdinalIgnoreCase))
                    sink.Error($"<{tag}> does not allow attribute '{at.Name}'", Pos(el));
        }

        var children = new List<UxNode>();
        var childItemScope = itemScope;
        foreach (var node in el.ChildNodes)
        {
            if (node is IText txt)
            {
                if (!string.IsNullOrWhiteSpace(txt.Data))
                    children.Add(new UxTextNode(ParseAttrValue(txt.Data, "text", AttrKind.Text, Pos(el), sink, childItemScope), Pos(el)));
            }
            else if (node is IElement child)
            {
                if (tag.Equals("list", StringComparison.OrdinalIgnoreCase) && child.TagName.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase))
                {
                    var inner = child.Children.OfType<IElement>().ToList();
                    if (inner.Count != 1)
                        sink.Error("<ItemTemplate> must have exactly one root", Pos(child));
                    var root = inner.FirstOrDefault();
                    if (root is not null) children.Add(ParseElement(root, filePath, sink, imports, itemScope: true));
                    continue;
                }
                children.Add(ParseElement(child, filePath, sink, imports, childItemScope));
            }
        }

        children = CoalesceIfChains(children, sink);
        // Text-capable UX elements treat a text-only body as their value
        // property; text nodes are only emitted as child spans when mixed with
        // element children.
        if (AllowsTextChildren(tag)
            && children.Count == 1
            && children[0] is UxTextNode textChild
            && !attrs.Any(attribute => attribute.Kind is AttrKind.Text || attribute.Name.Equals("value", StringComparison.OrdinalIgnoreCase)))
        {
            attrs.Add(new UxAttr(AttrKind.Text, "value", textChild.Value, Pos(el)));
            children.Clear();
        }
        // `static` only takes effect for a mixed static/dynamic element.  This
        // matches the runtime contract: a wholly literal element has no update
        // work to suppress, while handlers and bindings still need an
        // interactive static wrapper.
        isStatic = isStatic && attrs.Any(IsDynamicAttribute);
        if (isConst)
            ValidateConstSubtree(tag, isComponent, attrs, children, sink, Pos(el));
        var displayTag = isComponent
            ? imports.First(import => import.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)).Name
            : ToPascalTag(tag);
        UxNode result = new UxElement(displayTag, isComponent, attrs, children, Pos(el), isStatic, isConst);
        return result;
    }

    private static void ValidateConstSubtree(string tag, bool isComponent, IReadOnlyList<UxAttr> attrs, IReadOnlyList<UxNode> children, DiagnosticSink sink, SourcePosition position)
    {
        if (isComponent) sink.Error($"<Const> cannot instantiate component <{tag}>; component const support requires an explicit immutable contract", position);
        if (attrs.Any(attribute => attribute.Kind is AttrKind.Event or AttrKind.Model || attribute.Value is BindingValue or ExprValue))
            sink.Error("Const subtree may only contain literal attributes; bindings, expressions, events, and models are not allowed", position);
        foreach (var child in children)
        {
            switch (child)
            {
                case UxElement element:
                    ValidateConstSubtree(element.Tag, element.IsComponent, element.Attrs, element.Children, sink, element.Position ?? position);
                    break;
                case UxTextNode { Value: not LiteralValue }:
                    sink.Error("Const subtree may only contain literal text", child.Position ?? position);
                    break;
                default:
                    sink.Error("Const subtree may not contain conditional or list nodes", child.Position ?? position);
                    break;
            }
        }
    }

    private UxNode ParseIfBranch(IElement el, string filePath, DiagnosticSink sink, IReadOnlyList<UxImportRef> imports, bool itemScope)
    {
        var tag = el.TagName;
        var kind = tag.Equals("if", StringComparison.OrdinalIgnoreCase) ? IfBranchKind.If
                 : tag.Equals("elseif", StringComparison.OrdinalIgnoreCase) ? IfBranchKind.ElseIf
                 : IfBranchKind.Else;

        AttrValue? test = null;
        if (kind != IfBranchKind.Else)
        {
            var testRaw = GetAttr(el, "test");
            if (testRaw is null)
                sink.Error($"<{tag}> requires Test", Pos(el));
            else
                test = ParseAttrValue(testRaw, "Test", AttrKind.Test, Pos(el), sink, itemScope);
        }
        else
        {
            if (el.Attributes.Any(a => a.Name.Equals("test", StringComparison.OrdinalIgnoreCase)))
                sink.Error("<Else> must not have Test", Pos(el));
        }

        foreach (var a in el.Attributes)
            if (!a.Name.Equals("test", StringComparison.OrdinalIgnoreCase))
                sink.Error($"<{tag}> only allows Test", Pos(el));

        var children = el.Children.OfType<IElement>().Select(c => ParseElement(c, filePath, sink, imports, itemScope)).Cast<UxNode>().ToList();
        var branch = new UxIfBranch(kind, test, children, Pos(el));
        return new UxIfChain([branch], Pos(el));
    }

    private UxNode ParseList(IElement el, string filePath, DiagnosticSink sink, IReadOnlyList<UxImportRef> imports)
    {
        var src = GetAttr(el, "itemssource");
        if (src is null)
            sink.Error("<List> requires ItemsSource", Pos(el));
        var key = GetAttr(el, "key");

        var itemsSource = src is null ? new LiteralValue("", Pos(el)) : ParseAttrValue(src, "ItemsSource", AttrKind.ItemsSource, Pos(el), sink, false);

        var tmpl = el.Children.OfType<IElement>().FirstOrDefault(e => e.TagName.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase));
        if (tmpl is null)
        {
            sink.Error("<List> requires <ItemTemplate>", Pos(el));
            return new UxListNode(itemsSource, key, new UxElement("Div", false, [], [], Pos(el)), Pos(el));
        }
        var inner = tmpl.Children.OfType<IElement>().ToList();
        if (inner.Count != 1)
            sink.Error("<ItemTemplate> must have exactly one root", Pos(tmpl));
        var root = inner.FirstOrDefault();
        var rootNode = root is null ? (UxNode)new UxElement("Div", false, [], [], Pos(el)) : ParseElement(root, filePath, sink, imports, itemScope: true);

        foreach (var c in el.Children.OfType<IElement>().Where(e => !e.TagName.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase)))
            sink.Error($"<List> only allows <ItemTemplate>, got <{c.TagName}>", Pos(c));

        foreach (var a in el.Attributes)
            if (!a.Name.Equals("itemssource", StringComparison.OrdinalIgnoreCase) && !a.Name.Equals("key", StringComparison.OrdinalIgnoreCase))
                sink.Error($"<List> does not allow attribute '{a.Name}'", Pos(el));

        if (key is null)
            sink.Warning("<List> should have Key (stable id) for diff reuse", Pos(el));

        return new UxListNode(itemsSource, key, rootNode, Pos(el));
    }


    private static List<UxNode> CoalesceIfChains(List<UxNode> nodes, DiagnosticSink sink)
    {
        var outList = new List<UxNode>();
        List<UxIfBranch>? chain = null;
        SourcePosition? chainPos = null;
        void Flush()
        {
            if (chain is not null)
            {
                if (chain[0].Kind != IfBranchKind.If)
                    sink.Error("If chain must start with <If>", chainPos);
                outList.Add(new UxIfChain(chain, chainPos));
                chain = null; chainPos = null;
            }
        }
        foreach (var n in nodes)
        {
            if (n is UxIfChain single)
            {
                var br = single.Branches[0];
                if (br.Kind == IfBranchKind.If)
                {
                    Flush();
                    chain = new List<UxIfBranch> { br };
                    chainPos = single.Position;
                }
                else
                {
                    if (chain is null)
                        sink.Error($"<{br.Kind}> without preceding <If>", single.Position);
                    else
                        chain.Add(br);
                }
            }
            else
            {
                Flush();
                outList.Add(n);
            }
        }
        Flush();
        return outList;
    }

    private static AttrKind ClassifyAttr(string tag, string attrName, bool isComponent)
    {
        if (attrName.StartsWith("data-", StringComparison.OrdinalIgnoreCase)) return AttrKind.Dataset;
        if (attrName.Equals("Model", StringComparison.OrdinalIgnoreCase)) return AttrKind.Model;
        if (attrName.Equals("Class", StringComparison.OrdinalIgnoreCase)) return AttrKind.Class;
        if (attrName.Equals("Style", StringComparison.OrdinalIgnoreCase)) return AttrKind.Style;
        // Existing PascalCase templates use Source, whereas UX markup uses src.
        // Both lower to the runtime's `src` option and receive resource handling.
        if (attrName.Equals("Source", StringComparison.OrdinalIgnoreCase) || attrName.Equals("src", StringComparison.OrdinalIgnoreCase)) return AttrKind.Source;
        if (attrName.Equals("Text", StringComparison.OrdinalIgnoreCase)) return AttrKind.Text;
        if (attrName.Equals("Value", StringComparison.OrdinalIgnoreCase)) return AttrKind.Value;
        if (attrName.Equals("ItemsSource", StringComparison.OrdinalIgnoreCase)) return AttrKind.ItemsSource;
        if (attrName.Equals("Key", StringComparison.OrdinalIgnoreCase)) return AttrKind.Key;
        if (attrName.Equals("Test", StringComparison.OrdinalIgnoreCase)) return AttrKind.Test;
        if (attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && attrName.Length > 2)
            return AttrKind.Event;
        if (!isComponent && EventMap.Contains(attrName)) return AttrKind.Event;
        return AttrKind.Plain;
    }

    private static bool IsDynamicAttribute(UxAttr attribute)
        => attribute.Kind == AttrKind.Event
            || attribute.Value is not LiteralValue
            || (attribute.Kind == AttrKind.Plain
                && (attribute.Name.Equals("is", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.Equals("remotewidget", StringComparison.OrdinalIgnoreCase)));

    private static void ValidateEnumAttribute(string tag, string name, AttrValue value, SourcePosition? position, DiagnosticSink sink)
    {
        if (value is not LiteralValue literal) return;
        var key = tag + "." + name;
        if (!EnumAttributes.TryGetValue(key, out var allowed))
            EnumAttributes.TryGetValue("*." + name, out allowed);
        if (allowed is not null && !allowed.Contains(literal.Text))
            sink.Error($"attribute '{name}' on <{tag}> must be one of: {string.Join(", ", allowed.Select(item => item.Length == 0 ? "(empty)" : item))}", position);
    }

    private static void ValidateRequiredAttributes(string tag, IReadOnlyList<UxAttr> attrs, SourcePosition? position, DiagnosticSink sink)
    {
        if (!RequiredAttributes.TryGetValue(tag, out var required)) return;
        foreach (var name in required)
            if (!attrs.Any(attribute => attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                sink.Error($"<{tag}> requires attribute '{name}'", position);
    }

    private static AttrValue ParseAttrValue(string raw, string attrName, AttrKind kind, SourcePosition? pos, DiagnosticSink sink, bool itemScope)
    {
        raw = raw.Trim();
        if (raw.Contains("{{", StringComparison.Ordinal) || raw.Contains("}}", StringComparison.Ordinal))
        {
            sink.Error($"template interpolation is not supported in '{attrName}' (use {{Binding}} or {{Expr}})", pos);
            return new LiteralValue(raw, pos);
        }
        if (raw.StartsWith("{") && raw.EndsWith("}"))
        {
            var inner = raw[1..^1].Trim();
            if (inner.StartsWith("Binding", StringComparison.Ordinal))
            {
                var path = inner["Binding".Length..].Trim();
                if (string.IsNullOrEmpty(path))
                {
                    if (!itemScope)
                        sink.Error("{Binding} empty path only allowed inside <ItemTemplate>", pos);
                    return new BindingValue("", itemScope, pos);
                }
                if (path.Contains('[') || path.Contains('('))
                    sink.Error($"{{Binding {path}}} contains non dotted path (use {{Expr}})", pos);
                if (path.StartsWith("$"))
                    return new BindingValue(path, itemScope, pos);
                return new BindingValue(path, itemScope, pos);
            }
            if (inner.StartsWith("Expr", StringComparison.Ordinal))
            {
                var expr = inner["Expr".Length..].Trim();
                if (string.IsNullOrEmpty(expr))
                    sink.Error("{Expr} empty", pos);
                if (!IsBalanced(expr))
                    sink.Error($"{{Expr}} unbalanced: {expr}", pos);
                return new ExprValue(expr, itemScope, pos);
            }
            sink.Error($"unknown markup extension '{{{inner}}}'", pos);
            return new LiteralValue(raw, pos);
        }
        if (raw.Contains('{') || raw.Contains('}'))
            sink.Error($"literal attribute '{attrName}' must not contain '{{' or '}}' (use {{Binding}} / {{Expr}})", pos);
        return new LiteralValue(raw, pos);
    }

    private static bool IsBalanced(string s)
    {
        int depth = 0; bool inS = false, inD = false, inB = false, esc = false;
        foreach (var c in s)
        {
            if (esc) { esc = false; continue; }
            if (c == '\\') { esc = true; continue; }
            if (c == '\'' && !inD && !inB) inS = !inS;
            else if (c == '"' && !inS && !inB) inD = !inD;
            else if (c == '`' && !inS && !inD) inB = !inB;
            else if (!inS && !inD && !inB)
            {
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth < 0) return false; }
            }
        }
        return depth == 0 && !inS && !inD && !inB;
    }

    private static bool IsValidIdentifier(string s)
        => !string.IsNullOrEmpty(s) && Regex.IsMatch(s, @"^[A-Za-z_$][A-Za-z0-9_$]*$");

    private static bool IsDirectiveTag(string tag)
        => tag.Equals("list", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("if", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("elseif", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("else", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("import", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("page", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("component", StringComparison.OrdinalIgnoreCase);

    private static bool IsIfTag(string tag)
        => tag.Equals("if", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("elseif", StringComparison.OrdinalIgnoreCase);

    private static bool AllowsTextChildren(string tag)
        => tag.Equals("text", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("span", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("a", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("arc-text", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("richtext", StringComparison.OrdinalIgnoreCase)
        || tag.Equals("marquee", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfficialDirectiveAttr(string name)
        => new[] { "if", "elif", "else", "for", "tid" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> AllowedAttrsForTag(string tag)
    {
        if (tag.Equals("if", StringComparison.OrdinalIgnoreCase) || tag.Equals("elseif", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Test" };
        if (tag.Equals("else", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tag.Equals("list", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ItemsSource", "Key" };
        if (tag.Equals("itemtemplate", StringComparison.OrdinalIgnoreCase)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tag.Equals("page", StringComparison.OrdinalIgnoreCase) || tag.Equals("component", StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x:class" };
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetAttr(IElement el, string name)
        => el.Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || a.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static SourcePosition Pos(IElement el)
    {
        var pos = el.SourceReference?.Position;
        if (pos is null) return new SourcePosition("", 1, 1);
        return new SourcePosition("", pos.Value.Line, pos.Value.Column);
    }

    private static string ToPascalTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return tag;
        return char.ToUpperInvariant(tag[0]) + tag[1..].ToLowerInvariant();
    }

    private static string InferClassName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrEmpty(name)) return "Unknown";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
