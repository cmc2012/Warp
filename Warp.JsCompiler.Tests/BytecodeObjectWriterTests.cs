using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class BytecodeObjectWriterTests
{
    [Fact]
    public void Writes_empty_script_function_with_target_predefined_atom_ids()
    {
        var function = new IrFunctionObject(
            BytecodeObjectAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom),
            [0x29],
            StackSize: 1);

        Assert.Equal("01000E420200A401000000010000010029", Hex(new BytecodeObjectWriter().Write(function)));
    }

    [Fact]
    public void Writes_dynamic_atom_table_vardef_closure_debug_and_constant_pool()
    {
        var name = BytecodeObjectAtom.Dynamic("work");
        var local = BytecodeObjectAtom.Dynamic("value");
        var function = new IrFunctionObject(
            name,
            [0x06, 0x28],
            AtomRelocations: null,
            ArgumentCount: 1,
            VariableCount: 0,
            DefinedArgumentCount: 1,
            StackSize: 1,
            Variables: [new(local, 0, -1, IsConst: true, IsLexical: true, IsCaptured: true)],
            Closures: [new(local, 0, IsLocal: true, IsConst: true, IsLexical: true)],
            Constants: [new BytecodeIntegerValue(-65), new BytecodeStringValue("ok")],
            Debug: new(BytecodeObjectAtom.Dynamic("unit.js"), 3, [0x01, 0x02]));

        Assert.Equal(
            "010308776F726B0A76616C75650E756E69742E6A730E420600A8030100010101020201AA03000070AA03000D0628AC030302010205BF7F07046F6B",
            Hex(new BytecodeObjectWriter().Write(function)));
    }

    [Theory]
    [InlineData(0u, "00")]
    [InlineData(127u, "7F")]
    [InlineData(128u, "8001")]
    [InlineData(16384u, "808001")]
    public void Writes_unsigned_leb128(uint value, string expected)
    {
        var bytes = new List<byte>();
        BytecodeObjectWriter.WriteUnsigned(bytes, value);
        Assert.Equal(expected, Hex(bytes));
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(63, "3F")]
    [InlineData(64, "C000")]
    [InlineData(-1, "7F")]
    [InlineData(-65, "BF7F")]
    public void Writes_signed_leb128(int value, string expected)
    {
        var bytes = new List<byte>();
        BytecodeObjectWriter.WriteSigned(bytes, value);
        Assert.Equal(expected, Hex(bytes));
    }

    [Fact]
    public void Resets_dynamic_atom_numbering_between_root_objects()
    {
        var writer = new BytecodeObjectWriter();
        var first = writer.Write(new IrFunctionObject(BytecodeObjectAtom.Dynamic("first"), []));
        var second = writer.Write(new IrFunctionObject(BytecodeObjectAtom.Dynamic("second"), []));

        Assert.Equal(0x01, first[1]);
        Assert.Equal(0x01, second[1]);
        Assert.Contains("second", System.Text.Encoding.Latin1.GetString(second));
    }

    [Fact]
    public void Relocates_bytecode_atom_operands_without_mutating_input()
    {
        byte[] bytecode = [0x38, 0, 0, 0, 0, 0x28];
        var function = new IrFunctionObject(
            BytecodeObjectAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom),
            bytecode,
            AtomRelocations: [new(1, BytecodeObjectAtom.Dynamic("binding"))]);

        var output = new BytecodeObjectWriter().Write(function);

        Assert.Equal([0x38, 0xD4, 0, 0, 0, 0x28], output[^6..]);
        Assert.Equal([0x38, 0, 0, 0, 0, 0x28], bytecode);
    }

    [Fact]
    public void Writes_complete_module_tables_in_ecma_field_order()
    {
        var function = new IrFunctionObject(
            BytecodeObjectAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom), []);
        var module = new BytecodeModuleValue(
            BytecodeObjectAtom.Dynamic("main"),
            function,
            RequiredModules: [BytecodeObjectAtom.Dynamic("dep"), BytecodeObjectAtom.Dynamic("other")],
            Exports:
            [
                new BytecodeObjectLocalExport(3, BytecodeObjectAtom.Dynamic("answer")),
                new BytecodeObjectIndirectExport(1, BytecodeObjectAtom.Dynamic("source"),
                    BytecodeObjectAtom.Dynamic("renamed")),
            ],
            StarExports: [new(0)],
            Imports: [new(2, BytecodeObjectAtom.Dynamic("default"), 1)]);

        Assert.Equal(
            "0107086D61696E066465700A6F746865720C616E737765720C736F757263650E72656E616D65640E64656661756C740FA80302AA03AC03020003AE030101B003B20301000102B403010E420200A4010000000000000000",
            Hex(new BytecodeObjectWriter().Write(module)));
    }

    [Fact]
    public void Rejects_module_table_index_outside_required_module_table()
    {
        var module = new BytecodeModuleValue(
            BytecodeObjectAtom.Dynamic("main"),
            new IrFunctionObject(BytecodeObjectAtom.Predefined(BytecodeTargetAbi.EvalFunctionAtom), []),
            RequiredModules: [BytecodeObjectAtom.Dynamic("dep")],
            StarExports: [new(1)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BytecodeObjectWriter().Write(module));
    }

    private static string Hex(IEnumerable<byte> bytes) => Convert.ToHexString(bytes.ToArray());
}
