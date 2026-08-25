namespace Warp.JsCompiler.ObjectFormat;

/// <summary>Exposes bytecode ABI facts for conformance tests.</summary>
public static class BytecodeAbiProbe
{
    /// <summary>The bytecode version expected by the target runtime.</summary>
    public const byte BytecodeVersion = BytecodeTargetAbi.BytecodeVersion;

    public const uint FirstDynamicAtom = BytecodeTargetAbi.FirstDynamicAtom;
    public const int InsertedPredefinedAtomCount = BytecodeTargetAbi.InsertedPredefinedAtomCount;
    public const int FirstShiftedPredefinedAtom = BytecodeTargetAbi.FirstShiftedPredefinedAtom;

    /// <summary>Maps an upstream 2021-03-27 predefined atom to its target ID.</summary>
    public static int TranslatePredefinedAtom(int upstreamAtomId) =>
        BytecodeTargetAbi.TranslatePredefinedAtomId(upstreamAtomId);

    /// <summary>Converts a module name to the driver-required module name.</summary>
    public static string ToModuleName(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        return BytecodeTargetAbi.ToTargetModuleName(moduleName);
    }
}
