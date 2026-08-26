using System.Text;
using System.Text.Json;

namespace Warp.Lsp;

internal sealed class JsonRpcServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
    private readonly WxamlLanguageService _service = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public JsonRpcServer(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReadMessageAsync(cancellationToken);
            if (message is null) return;
            if (!message.RootElement.TryGetProperty("method", out var methodValue)) continue;
            var method = methodValue.GetString() ?? "";
            var parameters = message.RootElement.TryGetProperty("params", out var parameterValue) ? parameterValue : default;
            var hasId = message.RootElement.TryGetProperty("id", out var id);

            if (method == "exit") return;
            object? result = method switch
            {
                "initialize" => Initialize(),
                "shutdown" => Shutdown(),
                "textDocument/didOpen" => DidOpen(parameters),
                "textDocument/didChange" => DidChange(parameters),
                "textDocument/didClose" => DidClose(parameters),
                "textDocument/completion" => Completion(parameters),
                "textDocument/hover" => Hover(parameters),
                "textDocument/definition" => Definition(parameters),
                "textDocument/references" => References(parameters),
                "textDocument/semanticTokens/full" => SemanticTokens(parameters),
                "textDocument/documentColor" => DocumentColor(parameters),
                _ => null
            };

            if (hasId) await WriteAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);
        }
    }

    private object Initialize() => new
    {
        capabilities = new
        {
            // Full synchronization keeps the server's in-memory document exact;
            // the didChange handler intentionally accepts full-text updates only.
            textDocumentSync = 1,
            completionProvider = new { triggerCharacters = new[] { "<", " ", "{" } },
            hoverProvider = true,
            definitionProvider = true,
            referencesProvider = true,
            colorProvider = true,
            semanticTokensProvider = new
            {
                legend = new { tokenTypes = WxamlSemanticTokens.TokenTypes, tokenModifiers = Array.Empty<string>() },
                full = true
            }
        },
        serverInfo = new { name = "warp-wxaml", version = "1.0" }
    };

    private object? Shutdown()
    {
        return null;
    }

    private object? DidOpen(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("textDocument", out var document)) return null;
        var uri = document.String("uri");
        var text = document.String("text");
        if (uri is null || text is null) return null;
        _documents[uri] = text;
        PublishDiagnostics(uri, text);
        return null;
    }

    private object? DidChange(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("textDocument", out var document) || !parameters.TryGetProperty("contentChanges", out var changes)) return null;
        var uri = document.String("uri");
        if (uri is null || !changes.EnumerateArray().LastOrDefault().TryGetProperty("text", out var textValue)) return null;
        var text = textValue.GetString() ?? "";
        _documents[uri] = text;
        PublishDiagnostics(uri, text);
        return null;
    }

    private object? DidClose(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("textDocument", out var document)) return null;
        var uri = document.String("uri");
        if (uri is null) return null;
        _documents.Remove(uri);
        SendNotification("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<LspDiagnostic>() });
        return null;
    }

    private object Completion(JsonElement parameters)
    {
        var (uri, position) = ReadDocumentPosition(parameters);
        return new { isIncomplete = false, items = _documents.TryGetValue(uri, out var text) ? _service.GetCompletions(text, position) : [] };
    }

    private object? Hover(JsonElement parameters)
    {
        var (uri, position) = ReadDocumentPosition(parameters);
        if (!_documents.TryGetValue(uri, out var text)) return null;
        var content = _service.GetHover(text, position);
        return content is null ? null : new { contents = new { kind = "markdown", value = content } };
    }

    private object? Definition(JsonElement parameters)
    {
        var (uri, position) = ReadDocumentPosition(parameters);
        if (!_documents.TryGetValue(uri, out var text)) return null;
        var target = _service.GetDefinition(uri, text, position);
        var origin = _service.GetNavigationOrigin(text, position);
        return target is null || origin is null
            ? target
            : new LspLocationLink(origin, target.Uri, target.Range, target.Range);
    }

    private object References(JsonElement parameters)
    {
        var (uri, position) = ReadDocumentPosition(parameters);
        return _documents.TryGetValue(uri, out var text) ? _service.GetReferences(uri, text, position) : [];
    }

    private object SemanticTokens(JsonElement parameters)
    {
        var (uri, _) = ReadDocumentPosition(parameters);
        return new { data = _documents.TryGetValue(uri, out var text) ? WxamlSemanticTokens.Encode(text) : Array.Empty<int>() };
    }

    private object DocumentColor(JsonElement parameters)
    {
        var (uri, _) = ReadDocumentPosition(parameters);
        return _documents.TryGetValue(uri, out var text) ? _service.GetDocumentColors(text) : Array.Empty<LspColorInformation>();
    }

    private (string Uri, LspPosition Position) ReadDocumentPosition(JsonElement parameters)
    {
        var uri = parameters.TryGetProperty("textDocument", out var document) ? document.String("uri") ?? "" : "";
        var position = parameters.TryGetProperty("position", out var value)
            ? new LspPosition(value.Int32("line"), value.Int32("character"))
            : new LspPosition(0, 0);
        return (uri, position);
    }

    private void PublishDiagnostics(string uri, string text)
        => SendNotification("textDocument/publishDiagnostics", new { uri, diagnostics = _service.GetDiagnostics(uri, text) });

    private void SendNotification(string method, object parameters)
        => WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, CancellationToken.None).GetAwaiter().GetResult();

    private async Task<JsonDocument?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken);
            if (line is null) return null;
            if (line.Length == 0) break;
            var separator = line.IndexOf(':');
            if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (!headers.TryGetValue("Content-Length", out var lengthText) || !int.TryParse(lengthText, out var length) || length < 0)
            return null;
        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await _input.ReadAsync(bytes.AsMemory(read), cancellationToken);
            if (count == 0) return null;
            read += count;
        }
        return JsonDocument.Parse(bytes);
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var one = new byte[1];
            if (await _input.ReadAsync(one, cancellationToken) == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.Where(value => value != '\r').ToArray());
            bytes.Add(one[0]);
        }
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, _json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _output.WriteAsync(header, cancellationToken);
        await _output.WriteAsync(payload, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }
}
