using Warp.JsCompiler.Api;
using Warp.JsCompiler.Assembly;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;
using Warp.JsCompiler.ObjectFormat;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class IrToBytecodeAssemblyLowererTests
{
    [Fact]
    public void Module_bindings_resolve_to_var_references_and_closure_metadata()
    {
        var assembly = Lower("const moduleValue = 1; moduleValue = moduleValue + 2;");
        var function = Assert.Single(assembly.Functions);

        Assert.Equal(["push_this", "if_false", "return_undef", "label", "push_i32", "put_var_ref",
                "get_var_ref_check", "push_i32", "add", "dup", "put_var_ref_check", "drop", "return_undef"],
            function.Instructions.Select(instruction => instruction.Opcode.Name));
        var closure = Assert.Single(function.Metadata.Closures!);
        Assert.Equal("moduleValue", closure.Name.Symbol);
        Assert.Equal(0u, closure.ParentIndex);
        Assert.True(closure.IsLocal);
        Assert.True(closure.IsConst);
        Assert.True(closure.IsLexical);
        Assert.Empty(function.Metadata.Locals!);
        Assert.DoesNotContain(function.Instructions, instruction =>
            instruction.Opcode.Name.StartsWith("scope_", StringComparison.Ordinal));
    }

    [Fact]
    public void Global_and_property_atoms_are_instruction_relocations()
    {
        var function = Assert.Single(Lower("const moduleValue = 1; external({ namedProperty: moduleValue });").Functions);
        var relocations = function.AtomRelocations!;

        Assert.Equal(["get_var", "define_field"], relocations.Select(relocation =>
            function.Instructions[relocation.InstructionIndex].Opcode.Name));
        Assert.Equal(["external", "namedProperty"], relocations.Select(relocation =>
            relocation.Atom.Symbol));
        Assert.Contains(function.Instructions, instruction => instruction.Opcode.Name == "get_var_ref_check");
        Assert.All(relocations, relocation => Assert.IsAssignableFrom<BytecodeAssemblyAtomOperand>(
            function.Instructions[relocation.InstructionIndex].Operand));
    }

    [Fact]
    public void Integral_and_string_literals_leave_the_object_constant_pool()
    {
        var function = Assert.Single(Lower("1; 'text'; [2, 3];").Functions);

        Assert.Empty(function.Constants!);
        // A discarded literal expression has no observable evaluation and
        // is omitted before assembly lowering; literals retained by the
        // array construction remain immediate bytecode operands.
        Assert.Equal([2L, 3L], function.Instructions
            .Where(instruction => instruction.Opcode.Name == "push_i32")
            .Select(instruction => Assert.IsType<BytecodeAssemblySignedOperand>(instruction.Operand).Value));
        Assert.DoesNotContain(function.AtomRelocations ?? [], relocation => relocation.Atom.Symbol == "text");
    }

    [Fact]
    public void Discarded_literals_do_not_allocate_object_constants()
    {
        var function = Assert.Single(Lower("1; 'inline'; 1.5;").Functions);

        Assert.Empty(function.Constants!);
        Assert.DoesNotContain(function.Instructions,
            instruction => instruction.Operand is BytecodeAssemblyConstantOperand);
    }

    [Fact]
    public void Canonical_tagged_integer_strings_remain_in_the_constant_pool()
    {
        var function = Assert.Single(Lower("consume('0', '123', '2147483647', '00', '2147483648');").Functions);

        Assert.Equal(["0", "123", "2147483647"], function.Constants!
            .Cast<BytecodeAssemblyStringConstant>().Select(constant => constant.Value));
        Assert.Equal(3, function.Instructions.Count(instruction => instruction.Opcode.Name == "push_const"));
        Assert.Equal(["00", "2147483648"], function.AtomRelocations!
            .Where(relocation => function.Instructions[relocation.InstructionIndex].Opcode.Name == "push_atom_value")
            .Select(relocation => relocation.Atom.Symbol));
    }

    [Fact]
    public void Scope_shadowing_resolves_from_the_instruction_scope_chain()
    {
        var function = Assert.Single(Lower(
            "let shadowedBinding = 1; { const shadowedBinding = 2; shadowedBinding; } shadowedBinding;").Functions);
        var closures = function.Metadata.Closures!;

        var closure = Assert.Single(closures);
        Assert.Equal("shadowedBinding", closure.Name.Symbol);
        Assert.Equal(0u, closure.ParentIndex);
        var local = Assert.Single(function.Metadata.Locals!);
        Assert.NotNull(local.Name);
        Assert.Equal("shadowedBinding", local.Name.Value.Symbol);
        Assert.Contains(function.Instructions, instruction => instruction.Opcode.Name == "get_loc_check" &&
            Assert.IsType<BytecodeAssemblyLocalOperand>(instruction.Operand).Index == 0);
        Assert.Contains(function.Instructions, instruction => instruction.Opcode.Name == "get_var_ref_check" &&
            Assert.IsType<BytecodeAssemblyVarReferenceOperand>(instruction.Operand).Index == 0);
    }

    [Fact]
    public void Multiple_blocks_are_labeled_in_stable_ir_order_and_lower_all_terminators()
    {
        var function = Function(IrFunctionForm.Script, entry: 20);
        function.Blocks.Add(Block(20, [Instruction("push_true")],
            new IrBranchTerminator(new IrBlockId(30), new IrBlockId(10), SourceLocation.None)));
        function.Blocks.Add(Block(10, [Instruction("push_i32", new ImmediateOperand(1))],
            new IrThrowTerminator(SourceLocation.None)));
        function.Blocks.Add(Block(30, [],
            new IrGotoTerminator(new IrBlockId(40), SourceLocation.None)));
        function.Blocks.Add(Block(40, [], new IrReturnTerminator(false, SourceLocation.None)));

        var lowered = Assert.Single(Lower(function).Functions);

        Assert.Equal(["label", "push_true", "if_true", "label", "push_i32", "throw",
                "label", "goto", "label", "get_loc", "return"],
            lowered.Instructions.Select(instruction => instruction.Opcode.Name));
        Assert.Equal([0, 1, 2, 3], lowered.Instructions.Where(instruction => instruction.Opcode.Name == "label")
            .Select(instruction => Assert.IsType<BytecodeAssemblyLabelOperand>(instruction.Operand).Label.Value));
        Assert.Equal(2, LabelTarget(lowered, 2));
        Assert.Equal(3, LabelTarget(lowered, 7));
    }

    [Fact]
    public void Module_prologue_targets_entry_block_label_without_allocating_a_conflicting_label()
    {
        var function = Function(IrFunctionForm.Module, entry: 7);
        function.Blocks.Add(Block(9, [], new IrReturnTerminator(false, SourceLocation.None)));
        function.Blocks.Add(Block(7, [], new IrGotoTerminator(new IrBlockId(9), SourceLocation.None)));

        var lowered = Assert.Single(Lower(function).Functions);

        Assert.Equal(["push_this", "if_false", "return_undef", "label", "goto", "label", "return_undef"],
            lowered.Instructions.Select(instruction => instruction.Opcode.Name));
        Assert.Equal(0, LabelTarget(lowered, 1));
        Assert.Equal(1, LabelTarget(lowered, 4));
        Assert.Equal([0, 1], lowered.Instructions.Where(instruction => instruction.Opcode.Name == "label")
            .Select(instruction => Assert.IsType<BytecodeAssemblyLabelOperand>(instruction.Operand).Label.Value));
    }

    [Fact]
    public void Ordinary_child_resolves_arguments_local_capture_and_fclosure_constant()
    {
        var assembly = LowerScript("const outer = 1; function read(argument) { return argument + outer; }");
        var root = assembly.Functions[0];
        var child = assembly.Functions[1];

        Assert.Equal(["check_define_var", "define_var", "check_define_var", "fclosure", "define_func",
                "label", "push_i32", "put_var_init", "get_loc", "return"],
            root.Instructions.Select(instruction => instruction.Opcode.Name));
        var closureConstant = Assert.IsType<BytecodeAssemblyFunctionConstant>(Assert.Single(root.Constants!));
        Assert.Equal(child.Id, closureConstant.Function);
        Assert.Equal(closureConstant.Id,
            Assert.IsType<BytecodeAssemblyConstantOperand>(root.Instructions[3].Operand).Constant);

        Assert.Equal(["label", "get_arg", "get_var", "add", "return"],
            child.Instructions.Select(instruction => instruction.Opcode.Name));
        Assert.Equal((ushort)1, child.Metadata.ArgumentCount);
        Assert.Equal((ushort)1, child.Metadata.DefinedArgumentCount);
        Assert.Empty(child.Metadata.Closures!);
        Assert.Empty(root.Metadata.Locals!);
    }

    [Fact]
    public void Function_expression_is_a_source_order_child_constant()
    {
        var assembly = LowerScript("const before = 1; const fn = function(value) { return value; }; const after = 2;");
        var root = assembly.Functions[0];
        var child = assembly.Functions[1];

        var functionConstant = Assert.IsType<BytecodeAssemblyFunctionConstant>(Assert.Single(root.Constants!));
        Assert.Equal(child.Id, functionConstant.Function);
        Assert.Equal("fclosure", Assert.Single(root.Instructions,
            instruction => instruction.Opcode.Name == "fclosure").Opcode.Name);
        Assert.Equal(["label", "get_arg", "return"], child.Instructions.Select(instruction => instruction.Opcode.Name));
    }

    [Fact]
    public void Grandchild_capture_is_forwarded_through_parent_closure()
    {
        var assembly = LowerScript("const outer = 1; function middle() { return function leaf() { return outer; }; }");
        var root = assembly.Functions[0];
        var middle = assembly.Functions[1];
        var leaf = assembly.Functions[2];

        Assert.Empty(middle.Metadata.Closures!);
        Assert.Empty(leaf.Metadata.Closures!);
        Assert.Equal("get_var", Assert.Single(leaf.Instructions,
            instruction => instruction.Opcode.Name == "get_var").Opcode.Name);
        Assert.Empty(root.Metadata.Locals!);
    }

    [Fact]
    public void Logical_argument_assignment_materializes_the_reference()
    {
        var assembly = LowerScript("function update(value) { return value &&= next(); }");
        var update = Assert.Single(assembly.Functions, function =>
            function.Name.Symbol == "update");
        var opcodes = update.Instructions.Select(instruction => instruction.Opcode.Name).ToArray();

        Assert.Contains("make_arg_ref", opcodes);
        Assert.Contains("get_ref_value", opcodes);
        Assert.Contains("put_ref_value", opcodes);
        Assert.Equal(2, opcodes.Count(opcode => opcode == "nip"));
        Assert.DoesNotContain("get_arg", opcodes);
        var makeReference = Assert.Single(update.Instructions,
            instruction => instruction.Opcode.Name == "make_arg_ref");
        Assert.Equal((ushort)0, Assert.IsType<BytecodeAssemblyAtomReferenceOperand>(makeReference.Operand).Flags);
    }

    [Fact]
    public void Catch_closure_keeps_the_outer_lexical_local_in_function_metadata()
    {
        var assembly = Lower("function make(error, task) { let read; try { task(); } catch (error) { read = () => error; } return [error, read]; }");
        var make = Assert.Single(assembly.Functions, function => function.Name.Symbol == "make");

        Assert.Contains(make.Metadata.Locals!, local => local.Name?.Symbol == "read");
    }

    [Fact]
    public void Arrow_this_is_materialized_as_a_frame_local_and_child_closure()
    {
        var program = new JavaScriptFrontEnd("const fn = () => this.value;", "/tmp/arrow-this.js",
            JavaScriptSourceKind.Module).Parse();
        var ir = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);
        new PseudoBindingPass().Run(ir);

        var assembly = new IrToBytecodeAssemblyLowerer().Run(ir);
        var root = assembly.Functions[0];
        Assert.Contains(root.Metadata.Locals!, local =>
            local.Name is { Kind: BytecodeAssemblyAtomKind.Predefined } name &&
            name.PredefinedId == PredefinedAtomTable.TryGet("this"));
        var arrow = Assert.Single(assembly.Functions, function => function.Id != root.Id);
        Assert.Contains(arrow.Metadata.Closures!, closure =>
            closure.Name.Kind == BytecodeAssemblyAtomKind.Predefined &&
            closure.Name.PredefinedId == PredefinedAtomTable.TryGet("this"));
    }

    private static BytecodeAssemblyProgram Lower(string source)
    {
        var program = new JavaScriptFrontEnd(source, "/tmp/ecma-ir-to-assembly.js", JavaScriptSourceKind.Module).Parse();
        var ir = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);
        return new IrToBytecodeAssemblyLowerer().Run(ir);
    }

    private static BytecodeAssemblyProgram LowerScript(string source)
    {
        var program = new JavaScriptFrontEnd(source, "/tmp/ecma-function-ir-to-assembly.js", JavaScriptSourceKind.Script).Parse();
        var ir = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Script);
        return new IrToBytecodeAssemblyLowerer().Run(ir);
    }

    private static BytecodeAssemblyProgram Lower(IrFunction function)
    {
        var module = new IrModule();
        module.Functions.Add(function);
        return new IrToBytecodeAssemblyLowerer().Run(module);
    }

    private static IrFunction Function(IrFunctionForm form, int entry)
    {
        var scope = new IrScopeId(0);
        var function = new IrFunction(new IrFunctionId(0), null,
            new IrFunctionOptions(IrFunctionKind.Normal, form, Strict: form == IrFunctionForm.Module,
                HasPrototype: false, HasSimpleParameterList: true, HasParameterExpressions: false,
                HasThisBinding: true, HasArgumentsBinding: false, NewTargetAllowed: false,
                SuperCallAllowed: false, SuperAllowed: false, ArgumentsAllowed: true,
                HasHomeObject: false, IsEval: false, IsGlobalVariableEnvironment: form == IrFunctionForm.Script),
            scope, scope, new IrBlockId(entry));
        function.Scopes.Add(new IrScope(scope, null, []));
        return function;
    }

    private static IrBlock Block(int id, IReadOnlyList<IrInstruction> instructions,
        IrTerminator terminator)
    {
        var block = new IrBlock(new IrBlockId(id)) { Terminator = terminator };
        block.Instructions.AddRange(instructions);
        return block;
    }

    private static IrInstruction Instruction(string operation, params IrOperand[] operands) =>
        new(operation, operands, SourceLocation.None);

    private static int LabelTarget(BytecodeAssemblyFunction function, int instructionIndex) =>
        Assert.IsType<BytecodeAssemblyLabelOperand>(function.Instructions[instructionIndex].Operand).Label.Value;
}
