using Warp.JsCompiler;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Encoding;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class BytecodeAssemblyTests
{
    [Fact]
    public void Minimal_program_is_structurally_valid()
    {
        var function = Function([
            Instruction("return_undef"),
        ]);

        BytecodeAssemblyVerifier.Verify(new BytecodeAssemblyProgram(function.Id, [function]));
    }

    [Fact]
    public void Atom_relocation_is_anchored_to_atom_instruction()
    {
        var function = Function([
            Instruction("get_var", new BytecodeAssemblyAtomReferenceOperand()),
            Instruction("return"),
        ], relocations: [new(0, BytecodeAssemblyAtom.Named("value"))]);

        BytecodeAssemblyVerifier.Verify(function);
    }

    [Fact]
    public void With_atom_relocation_retains_label_and_flags()
    {
        var label = new BytecodeAssemblyLabelId(1);
        var function = Function([
            Instruction("with_get_var", new BytecodeAssemblyAtomLabelOperand(label, 1)),
            Instruction("return_undef"),
            Instruction("label", new BytecodeAssemblyLabelOperand(label)),
            Instruction("return"),
        ], relocations: [new(0, BytecodeAssemblyAtom.Named("value"))]);

        BytecodeAssemblyVerifier.Verify(function);
    }

    [Fact]
    public void Function_constant_must_target_program_function()
    {
        var entry = Function([Instruction("return_undef")], constants:
            [new BytecodeAssemblyFunctionConstant(new BytecodeAssemblyConstantId(0), new BytecodeAssemblyFunctionId(7))]);
        var program = new BytecodeAssemblyProgram(entry.Id, [entry]);

        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(program));
    }

    [Fact]
    public void Constant_operand_must_target_function_constant()
    {
        var function = Function([
            Instruction("push_const", new BytecodeAssemblyConstantOperand(new BytecodeAssemblyConstantId(0))),
            Instruction("return"),
        ]);

        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(function));
    }

    [Fact]
    public void Atom_operand_requires_exactly_one_relocation()
    {
        var missing = Function([
            Instruction("get_var", new BytecodeAssemblyAtomReferenceOperand()),
            Instruction("return"),
        ]);
        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(missing));

        var duplicate = Function(missing.Instructions, relocations:
        [
            new(0, BytecodeAssemblyAtom.Named("first")),
            new(0, BytecodeAssemblyAtom.Named("second")),
        ]);
        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(duplicate));
    }

    [Fact]
    public void Relocation_rejects_non_atom_instruction()
    {
        var function = Function([Instruction("return_undef")], relocations:
            [new(0, BytecodeAssemblyAtom.Named("value"))]);

        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(function));
    }

    [Fact]
    public void Labels_are_unique_and_targets_are_bound()
    {
        var label = new BytecodeAssemblyLabelId(1);
        var duplicate = Function([
            Instruction("label", new BytecodeAssemblyLabelOperand(label)),
            Instruction("label", new BytecodeAssemblyLabelOperand(label)),
            Instruction("return_undef"),
        ]);
        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(duplicate));

        var missing = Function([
            Instruction("goto", new BytecodeAssemblyLabelOperand(label)),
        ]);
        Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(missing));
    }

    [Fact]
    public void Assembly_accepts_preselected_short_opcode()
    {
        var function = Function([
            Instruction("push_i8", new BytecodeAssemblySignedOperand(1)),
            Instruction("return"),
        ]);

        BytecodeAssemblyVerifier.Verify(function);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    public void Metadata_rejects_invalid_argument_or_stack_counts(int arguments, int stack)
    {
        var metadata = new BytecodeAssemblyFunctionMetadata(
            ArgumentCount: (ushort)arguments, DefinedArgumentCount: 1, MaximumStackSize: (ushort)stack);
        var function = Function([Instruction("return_undef")], metadata: metadata);

        if (arguments == 0)
            Assert.Throws<InvalidOperationException>(() => BytecodeAssemblyVerifier.Verify(function));
        else
            BytecodeAssemblyVerifier.Verify(function);
    }

    [Fact]
    public void Assembly_model_has_no_frontend_ir_or_writer_dto_properties()
    {
        var assemblyTypes = typeof(BytecodeAssemblyProgram).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(BytecodeAssemblyProgram).Namespace &&
                           type.Name.StartsWith("BytecodeAssembly", StringComparison.Ordinal));
        foreach (var property in assemblyTypes.SelectMany(type => type.GetProperties()))
        {
            var typeName = property.PropertyType.ToString();
            Assert.DoesNotContain("JsAst", typeName, StringComparison.Ordinal);
            Assert.DoesNotContain("Ir", typeName, StringComparison.Ordinal);
            Assert.DoesNotContain("IrFunctionObject", typeName, StringComparison.Ordinal);
            Assert.DoesNotContain("BytecodeObjectAtom", typeName, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Object_structure_reader_skips_non_function_constant_pool_values()
    {
        var bytes = new Warp.JsCompiler.Api.JavaScriptCompiler().Compile(new Warp.JsCompiler.Api.JavaScriptCompilationRequest(
            "export const data = { name: '值', items: [1, 2] }; export function make() { return () => data; }",
            "object-reader.js", Warp.JsCompiler.Api.JavaScriptSourceKind.Module)).Bytes;

        var root = BytecodeObjectStructureReader.ReadRoot(bytes.ToArray());

        Assert.True(root.BytecodeLength > 0);
        Assert.NotEmpty(root.Constants);
    }

    private static BytecodeAssemblyInstruction Instruction(string opcode, BytecodeAssemblyOperand? operand = null) =>
        new(TargetOpcodeCatalog.Get(opcode), operand);

    private static BytecodeAssemblyFunction Function(IReadOnlyList<BytecodeAssemblyInstruction> instructions,
        IReadOnlyList<BytecodeAssemblyConstant>? constants = null,
        IReadOnlyList<BytecodeAssemblyAtomRelocation>? relocations = null,
        BytecodeAssemblyFunctionMetadata? metadata = null) =>
        new(new BytecodeAssemblyFunctionId(0), BytecodeAssemblyAtom.Predefined(0), instructions,
            metadata ?? new BytecodeAssemblyFunctionMetadata(), constants, relocations);
}
