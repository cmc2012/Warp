using Warp.ComponentSyntax.Ast;

namespace Warp.ComponentCompiler.Translation;

/// <summary>
/// Lowers source tag selectors using the same runtime shape as the template
/// lowering pass. Nodes erased by a structural lowering are addressed through
/// compiler-owned classes that TemplateTranslator adds to their rendered roots.
/// </summary>
public sealed class StyleSelectorTransform
{
    private static readonly IReadOnlyDictionary<string, string> NativeTagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["img"] = "image"
    };
    private static readonly HashSet<string> StructuralTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "component", "list", "itemtemplate", "if", "elseif", "else"
    };

    private readonly HashSet<string> _inlineComponents;
    private readonly Dictionary<string, string> _runtimeComponents;
    private readonly HashSet<string> _classBackedTags;

    private StyleSelectorTransform(IEnumerable<UxImportRef> imports, IEnumerable<string> tagSelectors)
    {
        var importList = imports.ToArray();
        _inlineComponents = importList.Where(import => import.IsInline)
            .Select(import => import.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _runtimeComponents = importList.Where(import => !import.IsInline)
            .ToDictionary(import => import.Name, import => import.Name, StringComparer.OrdinalIgnoreCase);
        _classBackedTags = tagSelectors.Where(RequiresGeneratedClass)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static StyleSelectorTransform Create(UxStyleSheet? sheet, IEnumerable<UxImportRef>? imports = null)
    {
        var selectors = sheet is null ? [] : sheet.Rules.Concat((sheet.MediaRules ?? []).SelectMany(rule => rule.Rules))
            .SelectMany(rule => rule.Selectors)
            .Where(selector => selector.Kind == StyleSelectorKind.Tag)
            .Select(selector => selector.Name);
        return new StyleSelectorTransform(imports ?? [], selectors);
    }

    public StyleSelector Transform(StyleSelector selector)
    {
        if (selector.Kind != StyleSelectorKind.Tag) return selector;
        if (RequiresGeneratedClass(selector.Name))
            return selector with { Kind = StyleSelectorKind.Class, Name = GeneratedClass(selector.Name) };
        // Native nodes use lower-case runtime tags. Runtime components retain
        // their identifier, matching TemplateTranslator's __cc__ emission.
        return _runtimeComponents.TryGetValue(selector.Name, out var componentName)
            ? selector with { Name = componentName }
            : selector with { Name = RuntimeNativeTagName(selector.Name) };
    }

    public string? GeneratedClassFor(string tag)
        => _classBackedTags.Contains(tag) ? GeneratedClass(tag) : null;

    private bool RequiresGeneratedClass(string tag)
        => StructuralTags.Contains(tag) || _inlineComponents.Contains(tag);

    private static string RuntimeNativeTagName(string tag)
        => NativeTagAliases.TryGetValue(tag, out var alias) ? alias : tag.ToLowerInvariant();

    private static string GeneratedClass(string tag) => "__warp_tag_" + tag.ToLowerInvariant();
}
