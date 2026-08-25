using System.Collections.ObjectModel;

namespace Warp.JsCompiler.Api;

/// <summary>How the input is interpreted by the ECMAScript compiler.</summary>
public enum JavaScriptSourceKind
{
    Auto,
    Script,
    Module,
}

/// <summary>A single ECMAScript 2021-03-27 compilation input.</summary>
public sealed record JavaScriptCompilationRequest(string Source, string FileName, JavaScriptSourceKind Kind = JavaScriptSourceKind.Auto)
{
    /// <summary>
    /// Selects the compact object form used by the target directory driver.
    /// In this form source locations and local-name debug records are omitted.
    /// </summary>
    public bool StripDebugInfo { get; init; } = true;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Source);
        if (string.IsNullOrWhiteSpace(FileName))
            throw new ArgumentException("A ECMAScript source file name is required.", nameof(FileName));
    }
}

/// <summary>A serialized object accepted by ECMAScript 2021-03-27 JS_ReadObject.</summary>
public sealed class JavaScriptBytecode
{
    internal JavaScriptBytecode(byte[] bytes, string fileName, JavaScriptSourceKind kind)
    {
        Bytes = Array.AsReadOnly(bytes);
        FileName = fileName;
        Kind = kind;
    }

    public ReadOnlyCollection<byte> Bytes { get; }
    public string FileName { get; }
    public JavaScriptSourceKind Kind { get; }
}

/// <summary>Resolved source returned by a module resolver.</summary>
public sealed record JavaScriptModuleSource(string CanonicalName, string Source, bool IsExternal = false);

/// <summary>Resolves a static module specifier relative to its importing module.</summary>
public interface IJavaScriptModuleResolver
{
    JavaScriptModuleSource Resolve(string specifier, string referrer);
}

/// <summary>Dependency-first output of an ES module compilation.</summary>
public sealed class JavaScriptModuleGraph
{
    internal JavaScriptModuleGraph(IReadOnlyDictionary<string, JavaScriptBytecode> modules)
        => Modules = new ReadOnlyDictionary<string, JavaScriptBytecode>(new Dictionary<string, JavaScriptBytecode>(modules, StringComparer.Ordinal));

    public IReadOnlyDictionary<string, JavaScriptBytecode> Modules { get; }
}

/// <summary>Location-aware ECMAScript compile failure.</summary>
public sealed class JavaScriptCompilationException : Exception
{
    public JavaScriptCompilationException(string message, string fileName, int line, int column, string code = "ECMA1000", Exception? innerException = null)
        : base(message, innerException)
    {
        FileName = fileName;
        Line = line;
        Column = column;
        Code = code;
    }

    public string FileName { get; }
    public int Line { get; }
    public int Column { get; }
    public string Code { get; }
}
