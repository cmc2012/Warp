using Warp.JsCompiler;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Encoding;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Encoding contracts at the assembly-to-bytecode boundary.</summary>
public sealed class BytecodeAssemblyEncodingBoundaryTests
{
    [Fact]
    public void Empty_module_function_lowers_to_ecma_return_undef()
    {
        var lowered = Encode(Op("return_undef"));

        Assert.Equal("29", Hex(lowered.Code));
        Assert.Equal(0, lowered.Metadata.MaximumStackSize);
    }

    [Theory]
    [InlineData("undefined", "0628")]
    [InlineData("null", "0728")]
    [InlineData("push_false", "0928")]
    [InlineData("push_true", "0A28")]
    public void Immediate_literals_match_ecma_opcode_bytes(string operation, string expected)
        => Assert.Equal(expected, Hex(Encode(Op(operation), Op("return")).Code));

    [Theory]
    [InlineData(-1, "B2")]
    [InlineData(0, "B3")]
    [InlineData(7, "BA")]
    [InlineData(-128, "BB80")]
    [InlineData(128, "BC8000")]
    [InlineData(32768, "0100800000")]
    public void Push_i32_uses_ecma_short_opcode_selection(int value, string expectedPush)
        => Assert.Equal(expectedPush + "28", Hex(Encode(
            Op("push_i32", new BytecodeAssemblySignedOperand(value)), Op("return")).Code));

    [Theory]
    [InlineData(0, "C3")]
    [InlineData(3, "C6")]
    [InlineData(4, "C004")]
    [InlineData(255, "C0FF")]
    [InlineData(256, "580001")]
    public void Get_loc_uses_ecma_short_opcode_selection(int local, string expectedGet)
        => Assert.Equal(expectedGet + "28", Hex(Encode(
            Op("get_loc", new BytecodeAssemblyLocalOperand(checked((ushort)local))), Op("return")).Code));

    [Theory]
    [InlineData(0, "C7")]
    [InlineData(3, "CA")]
    [InlineData(4, "C104")]
    [InlineData(255, "C1FF")]
    [InlineData(256, "590001")]
    public void Put_loc_uses_ecma_short_opcode_selection(int local, string expectedPut)
        => Assert.Equal("B4" + expectedPut + "29", Hex(Encode(
            Op("push_i32", new BytecodeAssemblySignedOperand(1)),
            Op("put_loc", new BytecodeAssemblyLocalOperand(checked((ushort)local))), Op("return_undef")).Code));

    [Fact]
    public void Integral_number_constant_is_lowered_like_ecma_push_i32()
        => Assert.Equal("BB2A28", Hex(Encode(
            Op("push_i32", new BytecodeAssemblySignedOperand(42)), Op("return")).Code));

    [Fact]
    public void Constant_pool_index_is_encoded_by_the_formal_assembly_encoder()
    {
        var id = new BytecodeAssemblyConstantId(0);
        Assert.Equal("BD0028", Hex(Encode([new BytecodeAssemblyNumberConstant(id, 1.5)],
            Op("push_const", new BytecodeAssemblyConstantOperand(id)), Op("return")).Code));
    }

    [Fact]
    public void Stack_analysis_rejects_underflow()
        => Assert.Throws<InvalidOperationException>(() => OperandStackAnalyzer.ComputeMaximumStack(
            [Op("drop"), Op("return_undef")]));

    [Fact]
    public void Unresolved_scope_operations_cannot_cross_the_assembly_boundary()
    {
        var instruction = Op("scope_get_var", new BytecodeAssemblyAtomReferenceOperand());
        var function = new BytecodeAssemblyFunction(new BytecodeAssemblyFunctionId(0), BytecodeAssemblyAtom.Predefined(0),
            [instruction, Op("return")], new BytecodeAssemblyFunctionMetadata(),
            AtomRelocations: [new BytecodeAssemblyAtomRelocation(0, BytecodeAssemblyAtom.Named("value"))]);

        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(function));
    }

    private static EncodedAssemblyFunction Encode(params BytecodeAssemblyInstruction[] instructions)
        => Encode([], instructions);

    private static EncodedAssemblyFunction Encode(IReadOnlyList<BytecodeAssemblyConstant> constants,
        params BytecodeAssemblyInstruction[] instructions)
    {
        var function = new BytecodeAssemblyFunction(new BytecodeAssemblyFunctionId(0), BytecodeAssemblyAtom.Predefined(0),
            instructions, new BytecodeAssemblyFunctionMetadata(), constants);
        var program = new BytecodePeepholePass().Run(new BytecodeAssemblyProgram(function.Id, [function]));
        return Assert.Single(new BytecodeAssemblyEncoder().Encode(program).Functions);
    }

    private static BytecodeAssemblyInstruction Op(string name, BytecodeAssemblyOperand? operand = null) =>
        new(TargetOpcodeCatalog.Get(name), operand);

    private static string Hex(IReadOnlyList<byte> code) => Convert.ToHexString(code.ToArray());
}
