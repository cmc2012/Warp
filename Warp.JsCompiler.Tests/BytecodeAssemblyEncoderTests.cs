using Warp.JsCompiler;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Assembly.Passes;
using Warp.JsCompiler.Encoding;
using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class BytecodeAssemblyEncoderTests
{
    [Fact]
    public void Empty_module_control_prefix_matches_ecma()
    {
        var done = new BytecodeAssemblyLabelId(0);
        var function = Function([
            Op("push_this"),
            Op("if_false", new BytecodeAssemblyLabelOperand(done)),
            Op("return_undef"),
            Label(done),
            Op("return_undef"),
        ]);

        var encoded = Encode(function);

        Assert.Equal("08E8022929", Hex(encoded.Code));
        Assert.Equal(1, encoded.Metadata.MaximumStackSize);
    }

    [Theory]
    [InlineData(-1, "B2")]
    [InlineData(7, "BA")]
    [InlineData(-128, "BB80")]
    [InlineData(128, "BC8000")]
    [InlineData(32768, "0100800000")]
    public void Integer_layout_uses_ecma_short_forms(int value, string expected)
    {
        var encoded = Encode(Function([
            Op("push_i32", new BytecodeAssemblySignedOperand(value)),
            Op("return"),
        ]));

        Assert.Equal(expected + "28", Hex(encoded.Code));
    }

    [Fact]
    public void Put_followed_by_get_is_canonicalized_to_set()
    {
        var encoded = Encode(Function([
            Op("push_true"),
            Op("put_loc", new BytecodeAssemblyLocalOperand(2)),
            Op("get_loc", new BytecodeAssemblyLocalOperand(2)),
            Op("return"),
        ]));

        Assert.Equal("0ACD28", Hex(encoded.Code));
        Assert.Equal(1, encoded.Metadata.MaximumStackSize);
    }

    [Fact]
    public void Negated_integer_is_folded_before_short_selection()
    {
        var encoded = Encode(Function([
            Op("push_i32", new BytecodeAssemblySignedOperand(42)),
            Op("neg"),
            Op("return"),
        ]));

        Assert.Equal("BBD628", Hex(encoded.Code));
    }

    [Fact]
    public void Terminal_drop_before_return_undef_is_removed()
    {
        var encoded = Encode(Function([
            Op("push_true"),
            Op("drop"),
            Op("return_undef"),
        ]));

        Assert.Equal("0A29", Hex(encoded.Code));
    }

    [Fact]
    public void Atom_relocation_moves_from_instruction_to_byte_offset()
    {
        var atom = BytecodeAssemblyAtom.Named("value");
        var get = Op("get_var", new BytecodeAssemblyAtomReferenceOperand());
        var function = Function([get, Op("return")], [new(0, atom)]);

        var encoded = Encode(function);

        Assert.Equal("380000000028", Hex(encoded.Code));
        var relocation = Assert.Single(encoded.AtomRelocations);
        Assert.Equal(1, relocation.OperandOffset);
        Assert.Equal(atom, relocation.Atom);
    }

    [Fact]
    public void Length_field_is_rewritten_to_get_length_and_consumes_its_relocation()
    {
        var get = Op("get_field", new BytecodeAssemblyAtomReferenceOperand());
        var function = Function([Op("push_true"), get, Op("return")],
            [new(1, BytecodeAssemblyAtom.Predefined(PredefinedAtomTable.TryGet("length")!.Value))]);

        var encoded = Encode(function);

        Assert.Equal("0AE728", Hex(encoded.Code));
        Assert.Empty(encoded.AtomRelocations);
    }

    [Fact]
    public void Branch_layout_accounts_for_other_shortened_instructions()
    {
        var target = new BytecodeAssemblyLabelId(0);
        var encoded = Encode(Function([
            Op("push_true"),
            Op("if_false", new BytecodeAssemblyLabelOperand(target)),
            Op("push_i32", new BytecodeAssemblySignedOperand(1)),
            Op("drop"),
            Label(target),
            Op("return_undef"),
        ]));

        Assert.Equal("0AE803B40E29", Hex(encoded.Code));
    }

    [Fact]
    public void Stack_analysis_rejects_inconsistent_join()
    {
        var target = new BytecodeAssemblyLabelId(0);
        var function = Function([
            Op("push_true"),
            Op("if_false", new BytecodeAssemblyLabelOperand(target)),
            Op("push_true"),
            Label(target),
            Op("return_undef"),
        ]);

        Assert.Throws<InvalidOperationException>(() => new BytecodeAssemblyEncoder().Encode(
            new BytecodeAssemblyProgram(function.Id, [function])));
    }

    [Fact]
    public void Encoded_dto_does_not_reference_frontend_ir_or_writer_types()
    {
        var types = new[] { typeof(EncodedAssemblyProgram), typeof(EncodedAssemblyFunction),
            typeof(EncodedAssemblyAtomRelocation) };
        foreach (var property in types.SelectMany(type => type.GetProperties()))
        {
            var name = property.PropertyType.ToString();
            Assert.DoesNotContain("Ir", name, StringComparison.Ordinal);
            Assert.DoesNotContain("JsAst", name, StringComparison.Ordinal);
            Assert.DoesNotContain("IrFunctionObject", name, StringComparison.Ordinal);
            Assert.DoesNotContain("BytecodeObjectAtom", name, StringComparison.Ordinal);
        }
    }

    private static EncodedAssemblyFunction Encode(BytecodeAssemblyFunction function)
    {
        var assembly = new BytecodePeepholePass().Run(new BytecodeAssemblyProgram(function.Id, [function]));
        return Assert.Single(new BytecodeAssemblyEncoder().Encode(assembly).Functions);
    }

    private static BytecodeAssemblyInstruction Op(string name, BytecodeAssemblyOperand? operand = null) =>
        new(TargetOpcodeCatalog.Get(name), operand);

    private static BytecodeAssemblyInstruction Label(BytecodeAssemblyLabelId label) =>
        Op("label", new BytecodeAssemblyLabelOperand(label));

    private static BytecodeAssemblyFunction Function(IReadOnlyList<BytecodeAssemblyInstruction> instructions,
        IReadOnlyList<BytecodeAssemblyAtomRelocation>? relocations = null) =>
        new(new BytecodeAssemblyFunctionId(0), BytecodeAssemblyAtom.Predefined(0), instructions,
            new BytecodeAssemblyFunctionMetadata(), AtomRelocations: relocations);

    private static string Hex(IReadOnlyList<byte> bytes) => Convert.ToHexString(bytes.ToArray());
}
