namespace Warp.JsCompiler.ObjectFormat;

/// <summary>
/// Immutable ABI facts for the target 2021-03-27 bytecode format.
/// These values are part of the bytecode compatibility contract, not options.
/// </summary>
internal static class BytecodeTargetAbi
{
    /// <summary>BC_VERSION with bignum support disabled.</summary>
    public const byte BytecodeVersion = 0x01;

    /// <summary>Module specifier prefix required by the target driver.</summary>
    public const string ModulePrefix = "@aiot/";

    // The target inserts two predefined atoms immediately after the upstream `with` atom.
    // Every predefined atom beginning with upstream `class` therefore moves by two.
    public const int FirstShiftedPredefinedAtom = 30;
    public const int InsertedPredefinedAtomCount = 2;
    public const uint FirstDynamicAtom = 212;
    /// <summary>JS_ATOM_empty_string after the target's two inserted atoms.</summary>
    public const uint EmptyStringAtom = 49;
    public const uint EvalFunctionAtom = 82;
    public const uint ReturnValueAtom = 83;

    public static int TranslatePredefinedAtomId(int upstreamAtomId) =>
        upstreamAtomId >= FirstShiftedPredefinedAtom
            ? checked(upstreamAtomId + InsertedPredefinedAtomCount)
            : upstreamAtomId;

    public static bool HasTargetModulePrefix(string moduleName) =>
        moduleName.StartsWith(ModulePrefix, StringComparison.Ordinal);

    public static string ToTargetModuleName(string moduleName)
    {
        if (HasTargetModulePrefix(moduleName)) return moduleName;
        return ModulePrefix + Path.GetFileNameWithoutExtension(moduleName);
    }
}
