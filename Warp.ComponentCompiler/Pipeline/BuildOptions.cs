namespace Warp.ComponentCompiler.Pipeline;

public sealed record BuildOptions(
    string ProjectPath,
    string SourceRoot = "src",
    string OutputDir = "build",
    bool OptimizeCssAttr = false,
    bool EnableStats = false,
    bool KeepJavaScript = false);
