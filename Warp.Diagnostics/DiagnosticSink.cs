using Microsoft.Extensions.Logging;

namespace Warp.Diagnostics;

public sealed class DiagnosticSink
{
    private readonly List<Diagnostic> _items = [];
    private readonly ILogger? _logger;

    public DiagnosticSink(ILogger? logger = null) => _logger = logger;

    public IReadOnlyList<Diagnostic> Diagnostics => _items;
    public bool HasErrors => _items.Any(d => d.IsError);

    public void Add(LogLevel level, string message, SourcePosition? pos = null)
    {
        var d = new Diagnostic(level, message, pos);
        _items.Add(d);
        if (_logger is not null)
            _logger.Log(level, "{Position}{Message}", pos is null ? "" : $"{pos.FilePath}:{pos.StartLine}:{pos.StartColumn} ", message);
    }

    public void Error(string message, SourcePosition? pos = null) => Add(LogLevel.Error, message, pos);
    public void Warning(string message, SourcePosition? pos = null) => Add(LogLevel.Warning, message, pos);
    public void Info(string message, SourcePosition? pos = null) => Add(LogLevel.Information, message, pos);
    public void Critical(string message, SourcePosition? pos = null) => Add(LogLevel.Critical, message, pos);
    public void Fatal(string message, SourcePosition? pos = null) => Add(LogLevel.Critical, message, pos);

    // compatibility shims (code param ignored, kept for any remaining call sites)
    public void Error(string code, string message, SourcePosition? pos = null) => Error(message, pos);
    public void Warning(string code, string message, SourcePosition? pos = null) => Warning(message, pos);
    public void Info(string code, string message, SourcePosition? pos = null) => Info(message, pos);
    public void Fatal(string code, string message, SourcePosition? pos = null) => Fatal(message, pos);
    public void Critical(string code, string message, SourcePosition? pos = null) => Critical(message, pos);

    public void Merge(IEnumerable<Diagnostic> others)
    {
        _items.AddRange(others);
        if (_logger is not null)
            foreach (var d in others) _logger.Log(d.Level, "{Position}{Message}", d.Position is null ? "" : $"{d.Position.FilePath}:{d.Position.StartLine}:{d.Position.StartColumn} ", d.Message);
    }

    public void ThrowIfErrors()
    {
        if (HasErrors)
            throw new WarpCompilationException(_items.Where(d => d.IsError).ToList());
    }
}

public sealed class WarpCompilationException : Exception
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public WarpCompilationException(IReadOnlyList<Diagnostic> diagnostics)
        : base($"Warp compilation failed with {diagnostics.Count} error(s):\n{string.Join("\n", diagnostics.Take(5))}")
    {
        Diagnostics = diagnostics;
    }
}
