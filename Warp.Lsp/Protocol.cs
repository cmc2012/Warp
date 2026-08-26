using System.Text.Json;

namespace Warp.Lsp;

public sealed record LspPosition(int Line, int Character);
public sealed record LspRange(LspPosition Start, LspPosition End);
public sealed record LspDiagnostic(LspRange Range, int Severity, string Message, string Source = "wxaml");
// FilterText intentionally preserves the text the user typed.  IDEA otherwise
// applies its own case-sensitive matcher to LSP labels after the server has
// already selected case-insensitive candidates.
public sealed record LspTextEdit(LspRange Range, string NewText);
public sealed record LspCompletionItem(
    string Label,
    int Kind,
    string Detail,
    string? InsertText = null,
    string? FilterText = null,
    LspTextEdit? TextEdit = null);
public sealed record LspLocation(string Uri, LspRange Range);
public sealed record LspLocationLink(LspRange OriginSelectionRange, string TargetUri, LspRange TargetRange, LspRange TargetSelectionRange);
public sealed record LspColor(double Red, double Green, double Blue, double Alpha = 1);
public sealed record LspColorInformation(LspRange Range, LspColor Color);

internal static class JsonElementExtensions
{
    public static string? String(this JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public static int Int32(this JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : 0;
}
