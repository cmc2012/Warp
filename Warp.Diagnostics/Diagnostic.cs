using Microsoft.Extensions.Logging;

namespace Warp.Diagnostics;

public sealed record SourcePosition(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine = 0,
    int EndColumn = 0);

public sealed record Diagnostic(
    LogLevel Level,
    string Message,
    SourcePosition? Position = null)
{
    public bool IsError => Level >= LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;

    public override string ToString()
        => Position is null || string.IsNullOrEmpty(Position.FilePath)
            ? $"[{Level}] {Message}"
            : $"[{Level}] {Position.FilePath}:{Position.StartLine}:{Position.StartColumn} {Message}";
}
