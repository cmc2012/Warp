using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Warp;
using Warp.ComponentCompiler.Pipeline;
using Warp.Diagnostics;
using Warp.Packaging;
using Warp.JsCompiler.Api;

var verboseRequested = args.Any(argument => argument is "--verbose" or "-v");
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(verboseRequested ? LogLevel.Information : LogLevel.Warning);
});
var logger = loggerFactory.CreateLogger("Warp");

var projectOption = new Option<string>("--project", "-p")
{
    Description = "Project root (manifest.yaml / src)",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
};
var outputOption = new Option<string>("--output", "-o")
{
    Description = "Output directory (relative to project)",
    DefaultValueFactory = _ => "build",
};
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Verbose logging" };
var keepJavaScriptOption = new Option<bool>("--keep-javascript")
{
    Description = "Keep generated JavaScript next to bytecode (diagnostics only)",
};

var createCommand = new Command("create", "Create a new WXAML project from a template");
var createDirectoryArgument = new Argument<string>("directory") { Description = "New project directory" };
var createTemplateOption = new Option<string>("--template")
{
    Description = "Project template (hello-world)",
    DefaultValueFactory = _ => "hello-world"
};
createCommand.Arguments.Add(createDirectoryArgument);
createCommand.Options.Add(createTemplateOption);
createCommand.SetAction(parseResult =>
{
    var directory = parseResult.GetValue(createDirectoryArgument)!;
    var template = parseResult.GetValue(createTemplateOption)!;
    if (!template.Equals("hello-world", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unknown template '{template}'. Available templates: hello-world.");
        return 2;
    }

    try
    {
        var destination = Path.GetFullPath(directory);
        ProjectTemplateWriter.CreateHelloWorld(destination);
        Console.WriteLine($"Created WXAML project at {destination}");
        Console.WriteLine("Next: warp build --project " + destination);
        return 0;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
});

var lspCommand = new Command("lsp", "Start the WXAML language server over standard input/output");
lspCommand.SetAction(async (_, ct) =>
{
    await Warp.Lsp.LspHost.RunAsync(ct);
    return 0;
});

var buildCommand = new Command("build", "Compile a .wxaml project into device-ready bytecode");
buildCommand.Options.Add(projectOption);
buildCommand.Options.Add(outputOption);
buildCommand.Options.Add(verboseOption);
buildCommand.Options.Add(keepJavaScriptOption);
buildCommand.SetAction(async (parseResult, ct) =>
{
    var project = parseResult.GetValue(projectOption)!;
    var output = parseResult.GetValue(outputOption)!;
    var verbose = parseResult.GetValue(verboseOption);
    var keepJavaScript = parseResult.GetValue(keepJavaScriptOption);
    if (verbose) loggerFactory.CreateLogger("Warp").LogInformation("Verbose enabled");

    var opts = new BuildOptions(
        ProjectPath: Path.GetFullPath(project),
        OutputDir: output,
        KeepJavaScript: keepJavaScript);

    logger.LogInformation("Warp build project={Project} output={Output}", opts.ProjectPath, opts.OutputDir);
    // BuildResult.Print is the CLI's single diagnostic renderer.  Passing the
    // console logger into the pipeline would render every diagnostic twice.
    var pipeline = new WarpPipeline(opts);
    var result = await pipeline.BuildAsync(ct);
    result.Print(Console.Out, verbose);
    return result.Success ? 0 : 1;
});

var packCommand = new Command("pack", "Pack a bytecode build directory into an .rpk archive");
packCommand.Options.Add(projectOption);
var packOutputOption = new Option<string>("--output", "-o")
{
    Description = "Build directory to package (relative to project)",
    DefaultValueFactory = _ => "build",
};
packCommand.Options.Add(packOutputOption);
packCommand.Options.Add(new Option<string>("--rpk") { Description = "Output .rpk path", DefaultValueFactory = _ => "dist/app.rpk" });
var privateKeyOption = new Option<string?>("--private-key") { Description = "PEM private key used to sign the package" };
var certificateOption = new Option<string?>("--certificate") { Description = "PEM certificate used to sign the package" };
packCommand.Options.Add(privateKeyOption);
packCommand.Options.Add(certificateOption);
packCommand.SetAction(async (parseResult, ct) =>
{
    var project = parseResult.GetValue(projectOption)!;
    var output = parseResult.GetValue(packOutputOption)!;
    var rpk = parseResult.GetValue<string>("--rpk")!;
    var projectPath = Path.GetFullPath(project);
    var buildDir = Path.IsPathRooted(output) ? output : Path.Combine(projectPath, output);
    var sink = new DiagnosticSink();
    var packager = new RpkPackager(sink);
    var outPath = Path.IsPathRooted(rpk) ? rpk : Path.Combine(projectPath, rpk);
    var packed = await packager.PackAsync(buildDir, outPath, new RpkSigningOptions(
        ProjectDirectory: projectPath,
        PrivateKeyPath: parseResult.GetValue(privateKeyOption),
        CertificatePath: parseResult.GetValue(certificateOption)), ct);
    if (sink.HasErrors)
    {
        foreach (var diagnostic in sink.Diagnostics.Where(diagnostic => diagnostic.IsError))
            Console.Error.WriteLine(diagnostic);
        return 1;
    }
    Console.WriteLine($"Packed {packed}");
    return 0;
});

var diffCommand = new Command("diff", "Create a device-ready incremental RPK from two Warp build outputs");
var diffCurrentOption = new Option<string>("--current", "-c")
{
    Description = "Current build directory",
    DefaultValueFactory = _ => "build",
};
var diffPreviousOption = new Option<string?>("--previous")
{
    Description = "Previously deployed build directory; omit for a full deployment",
};
var diffRpkOption = new Option<string>("--rpk")
{
    Description = "Incremental RPK receiving changed build entries",
    DefaultValueFactory = _ => "dist/.diff.rpk",
};
diffCommand.Options.Add(projectOption);
diffCommand.Options.Add(diffCurrentOption);
diffCommand.Options.Add(diffPreviousOption);
diffCommand.Options.Add(diffRpkOption);
diffCommand.SetAction((parseResult, _) =>
{
    var project = Path.GetFullPath(parseResult.GetValue(projectOption)!);
    var current = ResolveProjectPath(project, parseResult.GetValue(diffCurrentOption)!);
    var previousValue = parseResult.GetValue(diffPreviousOption);
    var previous = string.IsNullOrWhiteSpace(previousValue) ? null : ResolveProjectPath(project, previousValue);
    var rpk = ResolveProjectPath(project, parseResult.GetValue(diffRpkOption)!);
    if (!Directory.Exists(current))
    {
        Console.Error.WriteLine($"Current build directory does not exist: {current}");
        return Task.FromResult(1);
    }

    var result = BuildDiff.CreateArchive(current, previous, rpk);
    Console.WriteLine(result.Archive is null
        ? "Diff: 0 changed"
        : $"Diff: {result.Changed} changed ({result.Archive})");
    return Task.FromResult(0);
});

var jsCompileCommand = new Command("js-compile", "Compile a JavaScript file to target bytecode");
var jsInputArgument = new Argument<string>("input") { Description = "Input .js or .mjs file" };
var jsOutputOption = new Option<string>("--output", "-o")
{
    Description = "Output bytecode file",
};
var jsKindOption = new Option<string>("--kind")
{
    Description = "Source kind: auto, script, or module",
    DefaultValueFactory = _ => "auto",
};
var jsGraphOption = new Option<bool>("--graph")
{
    Description = "Resolve relative static imports and write the complete module graph to the output directory",
};
jsCompileCommand.Arguments.Add(jsInputArgument);
jsCompileCommand.Options.Add(jsOutputOption);
jsCompileCommand.Options.Add(jsKindOption);
jsCompileCommand.Options.Add(jsGraphOption);
jsCompileCommand.SetAction(async (parseResult, _) =>
{
    var input = parseResult.GetValue(jsInputArgument)!;
    var output = parseResult.GetValue(jsOutputOption);
    var kindText = parseResult.GetValue(jsKindOption)!;
    var graph = parseResult.GetValue(jsGraphOption);
    if (!Enum.TryParse<JavaScriptSourceKind>(kindText, ignoreCase: true, out var kind))
    {
        Console.Error.WriteLine("--kind must be auto, script, or module.");
        return 2;
    }

    var inputPath = Path.GetFullPath(input);
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Input file not found: {input}");
        return 2;
    }
    var outputPath = Path.GetFullPath(output ?? Path.ChangeExtension(inputPath, ".jsc"));
    try
    {
        var source = File.ReadAllText(inputPath);
        var fileName = Path.GetRelativePath(Directory.GetCurrentDirectory(), inputPath).Replace(Path.DirectorySeparatorChar, '/');
        var compiler = new JavaScriptCompiler();
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (!graph)
        {
            var bytecode = compiler.Compile(new JavaScriptCompilationRequest(source, fileName, kind));
            File.WriteAllBytes(outputPath, bytecode.Bytes.ToArray());
            logger.LogInformation("Compiled {Input} -> {Output} ({Kind}, {Bytes} bytes)", input, outputPath, bytecode.Kind, bytecode.Bytes.Count);
        }
        else
        {
            var outputDirectory = outputPath.EndsWith(".jsc", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(outputPath)!
                : outputPath;
            Directory.CreateDirectory(outputDirectory);
            var moduleRoot = Path.GetDirectoryName(inputPath)!;
            // Module graph names are relative to the entry directory so they
            // are both resolver-stable and safe to materialize under output.
            var graphResult = compiler.CompileModuleGraph(new JavaScriptCompilationRequest(source, Path.GetFileName(inputPath), JavaScriptSourceKind.Module),
                new FileModuleResolver(moduleRoot));
            foreach (var module in graphResult.Modules)
            {
                var moduleOutput = Path.Combine(outputDirectory, Path.ChangeExtension(module.Key, ".jsc"));
                var moduleParent = Path.GetDirectoryName(moduleOutput);
                if (!string.IsNullOrEmpty(moduleParent)) Directory.CreateDirectory(moduleParent);
                File.WriteAllBytes(moduleOutput, module.Value.Bytes.ToArray());
            }
            logger.LogInformation("Compiled module graph rooted at {Input} ({Count} modules) -> {Output}", input, graphResult.Modules.Count, outputDirectory);
        }
        return 0;
    }
    catch (JavaScriptCompilationException exception)
    {
        Console.Error.WriteLine($"{exception.FileName}:{exception.Line}:{exception.Column}: {exception.Code}: {exception.Message}");
        return 1;
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"I/O error: {exception.Message}");
        return 1;
    }
});

var root = new RootCommand("Warp - Vela wxaml compiler (C#)");
root.Subcommands.Add(buildCommand);
root.Subcommands.Add(packCommand);
root.Subcommands.Add(diffCommand);
root.Subcommands.Add(jsCompileCommand);
root.Subcommands.Add(createCommand);
root.Subcommands.Add(lspCommand);

return root.Parse(args).Invoke();

static string ResolveProjectPath(string project, string path) => Path.IsPathRooted(path) ? path : Path.Combine(project, path);

namespace Warp
{
    internal static class BuildDiff
    {
        internal sealed record Result(string? Archive, int Changed);

        public static Result CreateArchive(string currentDirectory, string? previousDirectory, string archivePath)
        {
            currentDirectory = Path.GetFullPath(currentDirectory);
            archivePath = Path.GetFullPath(archivePath);
            previousDirectory = previousDirectory is null ? null : Path.GetFullPath(previousDirectory);
            if (SameOrChild(archivePath, currentDirectory) || (previousDirectory is not null && SameOrChild(archivePath, previousDirectory)))
                throw new ArgumentException("Diff archive must be outside the current and previous build directories.");
            if (File.Exists(archivePath)) File.Delete(archivePath);

            var current = Files(currentDirectory);
            var previous = previousDirectory is not null && Directory.Exists(previousDirectory)
                ? Files(previousDirectory)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            var changes = current.Where(entry => !previous.TryGetValue(entry.Key, out var old) || !SameContent(entry.Value, old)).ToArray();
            if (changes.Length == 0) return new Result(null, 0);

            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            foreach (var (relative, source) in changes)
            {
                archive.CreateEntryFromFile(source, relative, CompressionLevel.Optimal);
            }
            return new Result(archivePath, changes.Length);
        }

        private static Dictionary<string, string> Files(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), path => path, StringComparer.Ordinal);

        private static bool SameContent(string left, string right)
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);
            if (a.Length != b.Length) return false;
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leftStream), SHA256.HashData(rightStream));
        }

        private static bool SameOrChild(string candidate, string parent) => candidate.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    static class ProjectTemplateWriter
    {
        private static readonly IReadOnlyDictionary<string, string> HelloWorldFiles = new Dictionary<string, string>
        {
            ["Warp.Cli.Templates.HelloWorld.manifest.yaml"] = "manifest.yaml",
            ["Warp.Cli.Templates.HelloWorld.README.md"] = "README.md",
            ["Warp.Cli.Templates.HelloWorld.src.app.js"] = "src/app.js",
            ["Warp.Cli.Templates.HelloWorld.src.common.icon.svg"] = "src/common/icon.svg",
            ["Warp.Cli.Templates.HelloWorld.src.pages.home.home.js"] = "src/pages/home/home.js",
            ["Warp.Cli.Templates.HelloWorld.src.pages.home.home.wxaml"] = "src/pages/home/home.wxaml",
        };

        public static void CreateHelloWorld(string destination)
        {
            if (File.Exists(destination) || (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any(path => !IsIdeMetadata(path))))
                throw new IOException($"Cannot create project: '{destination}' already exists and contains project files.");

            var projectName = Path.GetFileName(destination.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(projectName)) throw new IOException("Project directory must have a name.");
            Directory.CreateDirectory(destination);

            var assembly = typeof(ProjectTemplateWriter).Assembly;
            foreach (var (resourceName, relativePath) in HelloWorldFiles)
            {
                var target = Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) throw new IOException($"Cannot create project: template file '{target}' already exists.");
                using var input = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Missing embedded template resource '{resourceName}'.");
                using var output = File.Create(target);
                input.CopyTo(output);
            }

            var manifestPath = Path.Combine(destination, "manifest.yaml");
            var manifest = File.ReadAllText(manifestPath);
            var packageSuffix = new string(projectName.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
            if (packageSuffix.Length == 0) packageSuffix = "app";
            manifest = manifest.Replace("com.example.helloworld", "com.example." + packageSuffix, StringComparison.Ordinal)
                .Replace("name: Hello World", "name: " + projectName, StringComparison.Ordinal);
            File.WriteAllText(manifestPath, manifest);
        }

        // IDEA creates this directory before NewProjectWizard.setupProject runs.
        // It is metadata rather than user project content and can safely coexist
        // with the generated template.
        private static bool IsIdeMetadata(string path) =>
            Path.GetFileName(path).Equals(".idea", StringComparison.OrdinalIgnoreCase);
    }

    sealed class FileModuleResolver(string root) : IJavaScriptModuleResolver
    {
        public JavaScriptModuleSource Resolve(string specifier, string referrer)
        {
            if (!specifier.StartsWith(".", StringComparison.Ordinal))
                return new JavaScriptModuleSource(specifier, string.Empty, IsExternal: true);
            var referrerPath = Path.IsPathRooted(referrer) ? referrer : Path.Combine(root, referrer);
            var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(referrerPath)!, specifier));
            // A source graph only owns JavaScript sources.  Native extensions are
            // resolved by the embedding runtime and are intentionally retained as
            // import specifiers even when their eventual build artifact is absent.
            if (Path.HasExtension(candidate) &&
                !candidate.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
                !candidate.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                return new JavaScriptModuleSource(specifier, string.Empty, IsExternal: true);
            if (!Path.HasExtension(candidate)) candidate += ".js";
            if (!File.Exists(candidate)) throw new FileNotFoundException("Module source was not found.", candidate);
            return new JavaScriptModuleSource(Path.GetRelativePath(root, candidate).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText(candidate));
        }
    }
}
