using Warp.JsCompiler;
using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Target-version constants shared by assembly encoding and object serialization.</summary>
public sealed class TargetAbiBoundaryTests
{
    [Theory]
    [InlineData("null", 1u)]
    [InlineData("this", 8u)]
    [InlineData("with", 29u)]
    [InlineData("__FILE__", 30u)]
    [InlineData("class", 32u)]
    [InlineData("length", 50u)]
    [InlineData("eval", 60u)]
    [InlineData("Symbol.iterator", 200u)]
    public void Predefined_atom_lookup_returns_target_id(string name, uint expected)
        => Assert.Equal(expected, PredefinedAtomTable.TryGet(name));

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("Length")]
    [InlineData("symbol.iterator")]
    public void Predefined_atom_lookup_is_exact(string name)
        => Assert.Null(PredefinedAtomTable.TryGet(name));

    [Theory]
    [InlineData("module.js", "@aiot/module")]
    [InlineData("path/to/module.mjs", "@aiot/module")]
    [InlineData("without-extension", "@aiot/without-extension")]
    [InlineData("@aiot/already", "@aiot/already")]
    public void Target_module_name_normalizes_input(string input, string expected)
        => Assert.Equal(expected, BytecodeAbiProbe.ToModuleName(input));

    [Fact]
    public void Abi_constants_match_target_contract()
    {
        Assert.Equal(0x01, BytecodeAbiProbe.BytecodeVersion);
        Assert.Equal(212u, BytecodeAbiProbe.FirstDynamicAtom);
        Assert.Equal(2, BytecodeAbiProbe.InsertedPredefinedAtomCount);
        Assert.Equal(30, BytecodeAbiProbe.FirstShiftedPredefinedAtom);
    }
}
