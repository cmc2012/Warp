namespace Warp.JsCompiler.Assembly.Passes;

internal sealed class ModuleMetadataPass(BytecodeAssemblyAtom moduleName) : IBytecodeAssemblyPass
{
    public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program) =>
        program with
        {
            Module = program.Module is { } module
                ? module with { Name = moduleName }
                : new BytecodeAssemblyModuleMetadata(moduleName),
        };
}

/// <summary>
/// Adds the function debug header retained by the object format when debug
/// stripping is disabled. Source-location tables are optional in this format;
/// the source file and its initial line are nevertheless part of the function
/// identity used by diagnostics and backtraces.
/// </summary>
internal sealed class DebugMetadataPass(BytecodeAssemblyAtom fileName) : IBytecodeAssemblyPass
{
    public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program) => program with
    {
        Functions = program.Functions.Select(function => function with
        {
            Metadata = function.Metadata with
            {
                DebugInfo = function.Metadata.DebugInfo ?? new BytecodeAssemblyDebugInfo(fileName, 1, []),
            },
        }).ToArray(),
    };
}

internal sealed class StripDebugMetadataPass : IBytecodeAssemblyPass
{
    public BytecodeAssemblyProgram Run(BytecodeAssemblyProgram program) => program with
    {
        Functions = program.Functions.Select(function =>
        {
            var metadata = function.Metadata;
            // Direct eval recompiles source against this function's live
            // environment, so its vardef and closure names are runtime data,
            // not debug-only records. ECMAScript keeps both under -s whenever
            // has_eval_call is set.
            var hasDirectEval = function.Instructions.Any(instruction => instruction.Opcode.Name is "eval" or "apply_eval");
            var hasVariables = metadata.ArgumentCount != 0 || (metadata.Locals?.Count ?? 0) != 0;
            var closures = hasVariables && !hasDirectEval
                ? (metadata.Closures ?? []).Select(closure => closure with
                    { Name = BytecodeAssemblyAtom.Predefined(0) }).ToArray()
                : metadata.Closures;
            return function with
            {
                Metadata = metadata with
                {
                    JsMode = (byte)(metadata.JsMode | 2),
                    DebugInfo = null,
                    Closures = closures,
                    SerializeVariableDefinitions = hasDirectEval,
                },
            };
        }).ToArray(),
    };
}
