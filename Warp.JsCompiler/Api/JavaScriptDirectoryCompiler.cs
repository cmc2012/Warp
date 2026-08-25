namespace Warp.JsCompiler.Api;

/// <summary>Options for recursively compiling JavaScript source files to raw bytecode files.</summary>
public sealed record JavaScriptDirectoryCompilationRequest(string SourceDirectory, string OutputDirectory)
{
    public SearchOption SearchOption { get; init; } = SearchOption.AllDirectories;
    public IReadOnlyCollection<string> Extensions { get; init; } = [".js", ".mjs"];

    /// <summary>Use the target driver's raw, stripped module-output mode.</summary>
    public bool CompileAsModules { get; init; } = true;
}

/// <summary>Compiles a source directory to a matching tree of raw <c>.jsc</c> files.</summary>
public sealed class JavaScriptDirectoryCompiler
{
    private readonly JavaScriptCompiler _compiler;

    public JavaScriptDirectoryCompiler(JavaScriptCompiler? compiler = null) =>
        _compiler = compiler ?? new JavaScriptCompiler();

    public async Task<IReadOnlyList<string>> CompileAsync(JavaScriptDirectoryCompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Directory.Exists(request.SourceDirectory))
            throw new DirectoryNotFoundException($"Source directory '{request.SourceDirectory}' does not exist.");

        var sourceRoot = Path.GetFullPath(request.SourceDirectory);
        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        var extensions = new HashSet<string>(request.Extensions, StringComparer.OrdinalIgnoreCase);
        var outputPaths = new List<string>();

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", request.SearchOption)
                     .Where(path => extensions.Contains(Path.GetExtension(path))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var relative = Path.GetRelativePath(sourceRoot, path);
            var outputPath = Path.ChangeExtension(Path.Combine(outputRoot, relative), ".jsc");
            var kind = request.CompileAsModules ? JavaScriptSourceKind.Module : JavaScriptSourceKind.Auto;
            var bytecode = _compiler.Compile(new JavaScriptCompilationRequest(source, relative, kind));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, bytecode.Bytes.ToArray(), cancellationToken).ConfigureAwait(false);
            outputPaths.Add(outputPath);
        }
        return outputPaths;
    }
}
