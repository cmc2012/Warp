using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Warp.JsCompiler.Ir;
using Warp.JsCompiler.Ir.Passes;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class IrScopeBindingConstructionTests
{
    [Fact]
    public void Empty_script_has_stable_root_scopes_and_entry()
    {
        var module = Build("");

        var function = Assert.Single(module.Functions);
        Assert.Equal(IrFunctionForm.Script, function.Options.Form);
        Assert.Equal(new IrScopeId(0), function.ArgumentScope);
        Assert.Equal(new IrScopeId(1), function.BodyScope);
        Assert.Collection(function.Scopes,
            argument =>
            {
                Assert.Null(argument.Parent);
                Assert.Empty(argument.Bindings);
            },
            body =>
            {
                Assert.Equal(function.ArgumentScope, body.Parent);
                Assert.Empty(body.Bindings);
            });
        Assert.IsType<IrReturnTerminator>(Assert.Single(function.Blocks).Terminator);
    }

    [Fact]
    public void Literal_constants_and_instructions_follow_source_order()
    {
        var function = Assert.Single(Build("1; 'value';").Functions);

        Assert.Collection(function.Constants,
            constant => Assert.Equal(1d, Assert.IsType<IrNumberConstant>(constant).Value),
            constant => Assert.Equal("value", Assert.IsType<IrStringConstant>(constant).Value));
        Assert.Equal(["push_const", "set_eval_ret", "push_const", "set_eval_ret"],
            function.Blocks[0].Instructions.Select(instruction => instruction.Operation));
    }

    [Fact]
    public void Immediate_literals_do_not_perturb_constant_order()
    {
        var function = Assert.Single(Build("true; false; null; 0x10; 'tail';").Functions);

        Assert.Equal(["push_true", "set_eval_ret", "push_false", "set_eval_ret", "push_null", "set_eval_ret",
                "push_const", "set_eval_ret", "push_const", "set_eval_ret"],
            function.Blocks[0].Instructions.Select(instruction => instruction.Operation));
        Assert.Equal(16d, Assert.IsType<IrNumberConstant>(function.Constants[0]).Value);
        Assert.Equal("tail", Assert.IsType<IrStringConstant>(function.Constants[1]).Value);
    }

    [Fact]
    public void Bindings_append_to_slots_but_prepend_to_each_scope_chain()
    {
        var function = Assert.Single(Build("var a; let b; const c = 0; { let d; const e = 1; }").Functions);

        Assert.Equal(["a", "b", "c", "d", "e"], function.Bindings.Select(binding => binding.Name));
        Assert.Equal([0, 1, 2, 3, 4], function.Bindings.Select(binding => binding.Id.Value));
        Assert.Equal(["a"], Names(function, function.Scopes[0]));
        Assert.Equal(["c", "b"], Names(function, function.Scopes[1]));
        Assert.Equal(function.BodyScope, function.Scopes[2].Parent);
        Assert.Equal(["e", "d"], Names(function, function.Scopes[2]));
        Assert.False(function.Bindings[0].IsLexical);
        Assert.True(function.Bindings[2].IsConst);
        Assert.True(function.Bindings[2].IsLexical);
    }

    [Fact]
    public void Function_children_and_parent_constants_follow_declaration_order()
    {
        var module = Build("function first(a) { let x; } function second() { const y = 1; }");

        Assert.Equal([null, "first", "second"], module.Functions.Select(function => function.Name));
        var root = module.Functions[0];
        Assert.Equal(["second", "first"], Names(root, root.Scopes[1]));
        Assert.Collection(root.Constants,
            constant => Assert.Equal(module.Functions[1].Id, Assert.IsType<IrFunctionConstant>(constant).Function),
            constant => Assert.Equal(module.Functions[2].Id, Assert.IsType<IrFunctionConstant>(constant).Function));
        Assert.Equal(["a"], Names(module.Functions[1], module.Functions[1].Scopes[0]));
        Assert.Equal(["x"], Names(module.Functions[1], module.Functions[1].Scopes[1]));
        Assert.Equal(["y"], Names(module.Functions[2], module.Functions[2].Scopes[1]));
    }

    [Fact]
    public void Function_declaration_records_parent_constant_scope_parameters_and_return()
    {
        var module = Build("function read(first, second) { return first + outer; }");
        var root = module.Functions[0];
        var child = module.Functions[1];
        var constant = Assert.IsType<IrFunctionConstant>(Assert.Single(root.Constants));

        Assert.Equal(child.Id, constant.Function);
        Assert.Equal(root.Id, child.ParentFunction);
        Assert.Equal(root.BodyScope, child.ParentScope);
        Assert.Equal(constant.Id, child.ParentConstant);
        Assert.Equal((ushort)2, child.DefinedArgumentCount);
        Assert.Equal(IrFunctionForm.Declaration, child.Options.Form);
        Assert.True(child.Options.HasPrototype);
        Assert.Equal(["first", "second"], child.Bindings.Where(binding => binding.IsArgument)
            .Select(binding => binding.Name));
        Assert.Equal(["scope_get_var", "scope_get_var", "add"], child.Blocks[0].Instructions
            .Select(instruction => instruction.Operation));
        Assert.True(Assert.IsType<IrReturnTerminator>(child.Blocks[0].Terminator).HasValue);
        var outer = child.Blocks[0].Instructions[1];
        Assert.Equal("outer", Assert.IsType<AtomOperand>(outer.Operands[0]).Value);
        Assert.Equal(child.BodyScope, Assert.IsType<IrScopeOperand>(outer.Operands[1]).Scope);
    }

    [Fact]
    public void Named_function_expression_reserves_constant_in_parent_source_order_without_unobserved_self_binding()
    {
        var module = Build("const before = 1; const fn = function named(value) { return value; }; const after = 2;");
        var root = module.Functions[0];
        var child = module.Functions[1];

        Assert.Collection(root.Constants,
            constant => Assert.Equal(1, Assert.IsType<IrNumberConstant>(constant).Value),
            constant => Assert.Equal(child.Id, Assert.IsType<IrFunctionConstant>(constant).Function),
            constant => Assert.Equal(2, Assert.IsType<IrNumberConstant>(constant).Value));
        Assert.Equal(new IrConstantId(1), child.ParentConstant);
        Assert.Equal(IrFunctionForm.Expression, child.Options.Form);
        Assert.True(child.Options.HasPrototype);
        Assert.DoesNotContain(child.Bindings, binding => binding.Kind == IrBindingKind.FunctionName);
        Assert.Equal(["fclosure"], root.Blocks[0].Instructions
            .Where(instruction => instruction.Operation == "fclosure")
            .Select(instruction => instruction.Operation));
    }

    [Fact]
    public void Nested_child_constants_do_not_perturb_parent_child_order()
    {
        var module = Build("function first() { return function inner() { return 1; }; } function second() {}");
        var root = module.Functions[0];
        var first = module.Functions[1];
        var inner = module.Functions[2];
        var second = module.Functions[3];

        Assert.Equal([first.Id, second.Id], root.Constants.Cast<IrFunctionConstant>()
            .Select(constant => constant.Function));
        Assert.Equal(inner.Id, Assert.IsType<IrFunctionConstant>(Assert.Single(first.Constants)).Function);
        Assert.Equal(first.Id, inner.ParentFunction);
        Assert.Equal(new IrConstantId(0), inner.ParentConstant);
    }

    [Fact]
    public void Identifier_and_assignment_remain_symbolic_until_resolution()
    {
        var function = Assert.Single(Build("let value; value = -source + 2; value += delta;").Functions);
        var instructions = function.Blocks[0].Instructions;

        // The lexical declaration initializes the completion value before
        // the first expression. Its prologue is independent of the lvalue
        // protocol under test, so assert the symbolic assignment subsequence
        // rather than treating the entry block as a fixed instruction list.
        var firstAssignment = instructions.FindIndex(instruction => instruction.Operation == "scope_make_ref");
        Assert.Equal(["scope_make_ref", "scope_get_var", "neg", "push_const", "add", "dup", "put_ref_value_copy", "set_eval_ret",
                "scope_make_ref", "get_ref_value", "scope_get_var", "add", "dup", "put_ref_value_copy", "set_eval_ret"],
            instructions.Skip(firstAssignment).Select(instruction => instruction.Operation));
        var references = instructions.Skip(firstAssignment).Where(instruction => instruction.Operation.StartsWith("scope_",
            StringComparison.Ordinal)).ToArray();
        Assert.All(references, instruction => Assert.IsType<IrScopeOperand>(instruction.Operands[^1]));
        Assert.Equal(["value", "source", "value", "delta"], references.Select(instruction =>
            Assert.IsType<AtomOperand>(instruction.Operands[0]).Value));
    }

    [Fact]
    public void Logical_identifier_assignment_keeps_a_persistent_lvalue_across_branches()
    {
        var update = Assert.Single(Build("function update(value) { return value &&= next(); }").Functions,
            function => function.Name == "update");

        Assert.Contains(update.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction.Operation == "scope_make_persistent_ref" &&
            Assert.IsType<AtomOperand>(instruction.Operands[0]).Value == "value" &&
            Assert.IsType<IrScopeOperand>(instruction.Operands[1]).Scope == update.BodyScope);
        var skipped = Assert.Single(update.Blocks, block =>
            block.Instructions.Select(instruction => instruction.Operation).SequenceEqual(["nip", "nip"]));
        Assert.IsType<IrGotoTerminator>(skipped.Terminator);
    }

    [Fact]
    public void Logical_identifier_assignment_names_an_anonymous_function_rhs()
    {
        var update = Assert.Single(Build(
            "function update(value) { return value ||= () => 1; }").Functions,
            function => function.Name == "update");
        var setName = Assert.Single(update.Blocks.SelectMany(block => block.Instructions),
            instruction => instruction.Operation == "set_name");

        Assert.Equal("value", Assert.IsType<AtomOperand>(Assert.Single(setName.Operands)).Value);
    }

    [Fact]
    public void Typeof_and_void_keep_their_phase_one_special_forms()
    {
        var function = Assert.Single(Build("typeof missing; void effect;").Functions);

        Assert.Equal(["scope_get_var_undef", "typeof", "set_eval_ret", "scope_get_var", "drop",
                "push_undefined", "set_eval_ret"],
            function.Blocks[0].Instructions.Select(instruction => instruction.Operation));
        var unresolved = function.Blocks[0].Instructions[0];
        Assert.Equal("missing", Assert.IsType<AtomOperand>(unresolved.Operands[0]).Value);
        Assert.Equal(function.BodyScope, Assert.IsType<IrScopeOperand>(unresolved.Operands[1]).Scope);
    }

    [Fact]
    public void Member_calls_preserve_receiver_and_source_evaluation_order()
    {
        var function = Assert.Single(Build("target.method(argument); target[key](left, right);").Functions);

        Assert.Equal(["scope_get_var", "get_field2", "scope_get_var", "call_method", "set_eval_ret",
                "scope_get_var", "scope_get_var", "get_array_el2", "scope_get_var", "scope_get_var",
                "call_method", "set_eval_ret"],
            function.Blocks[0].Instructions.Select(instruction => instruction.Operation));
        var calls = function.Blocks[0].Instructions.Where(instruction => instruction.Operation == "call_method").ToArray();
        Assert.Equal([1L, 2L], calls.Select(call => Assert.IsType<ImmediateOperand>(call.Operands[0]).Value));
    }

    [Fact]
    public void Array_and_object_literals_keep_ecma_phase_one_order()
    {
        var function = Assert.Single(Build("[first, second, , tail]; ({ named: value, [key]: computed });").Functions);

        Assert.Equal(["scope_get_var", "scope_get_var", "array_from", "scope_get_var", "define_field", "set_eval_ret",
                "object", "scope_get_var", "define_field", "scope_get_var", "scope_get_var",
                "define_array_el", "drop", "set_eval_ret"],
            function.Blocks[0].Instructions.Select(instruction => instruction.Operation));
        var arrayFrom = Assert.Single(function.Blocks[0].Instructions, instruction =>
            instruction.Operation == "array_from");
        Assert.Equal(2, Assert.IsType<ImmediateOperand>(arrayFrom.Operands[0]).Value);
        Assert.Equal(["3", "named"], function.Blocks[0].Instructions
            .Where(instruction => instruction.Operation == "define_field")
            .Select(instruction => Assert.IsType<AtomOperand>(instruction.Operands[0]).Value));
    }

    [Fact]
    public void If_else_uses_source_order_blocks_and_an_explicit_join()
    {
        var function = Assert.Single(Build("if (condition) left(); else right(); tail();").Functions);

        Assert.Equal([0, 1, 2, 3], function.Blocks.Select(block => block.Id.Value));
        Assert.Equal(["scope_get_var"], Operations(function.Blocks[0]));
        var branch = Assert.IsType<IrBranchTerminator>(function.Blocks[0].Terminator);
        Assert.Equal(new IrBlockId(1), branch.WhenTrue);
        Assert.Equal(new IrBlockId(2), branch.WhenFalse);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(function.Blocks[1]));
        Assert.Equal(new IrBlockId(3), Assert.IsType<IrGotoTerminator>(function.Blocks[1].Terminator).Target);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(function.Blocks[2]));
        Assert.Equal(new IrBlockId(3), Assert.IsType<IrGotoTerminator>(function.Blocks[2].Terminator).Target);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(function.Blocks[3]));
        Assert.IsType<IrReturnTerminator>(function.Blocks[3].Terminator);
    }

    [Fact]
    public void While_break_and_continue_target_exit_and_test_blocks()
    {
        var function = Assert.Single(Build(
            "while (condition) { if (stop) break; work(); continue; }").Functions);

        var preheader = Assert.IsType<IrGotoTerminator>(function.Blocks[0].Terminator);
        var test = function.Blocks.Single(block => block.Id == preheader.Target);
        var loopBranch = Assert.IsType<IrBranchTerminator>(test.Terminator);
        var body = function.Blocks.Single(block => block.Id == loopBranch.WhenTrue);
        var exit = function.Blocks.Single(block => block.Id == loopBranch.WhenFalse);
        var innerBranch = Assert.IsType<IrBranchTerminator>(body.Terminator);
        Assert.Equal(exit.Id, Assert.IsType<IrGotoTerminator>(
            function.Blocks.Single(block => block.Id == innerBranch.WhenTrue).Terminator).Target);
        Assert.Equal(test.Id, Assert.IsType<IrGotoTerminator>(
            function.Blocks.Single(block => block.Id == innerBranch.WhenFalse).Terminator).Target);
        Assert.Contains(function.Blocks.SelectMany(block => block.Instructions), instruction =>
            instruction.Operation == "enter_scope");
    }

    [Fact]
    public void Do_while_continue_targets_the_post_body_test()
    {
        var function = Assert.Single(Build("do { work(); continue; } while (condition);").Functions);

        Assert.Equal(new IrBlockId(1), Assert.IsType<IrGotoTerminator>(function.Blocks[0].Terminator).Target);
        Assert.Equal(new IrBlockId(2), Assert.IsType<IrGotoTerminator>(function.Blocks[1].Terminator).Target);
        Assert.Equal(["scope_get_var"], Operations(function.Blocks[2]));
        var branch = Assert.IsType<IrBranchTerminator>(function.Blocks[2].Terminator);
        Assert.Equal(new IrBlockId(1), branch.WhenTrue);
        Assert.Equal(new IrBlockId(3), branch.WhenFalse);
    }

    [Fact]
    public void For_continue_flows_through_update_and_return_terminates_its_branch()
    {
        var function = Assert.Single(Build(
            "function run() { for (let item = first(); check(item); step(item)) { if (done) break; continue; } return item; }")
            .Functions, candidate => candidate.Name == "run");
        var blocks = function.Blocks.ToDictionary(block => block.Id);

        Assert.Contains(function.Blocks[0].Instructions, instruction => instruction.Operation == "enter_scope");
        var testId = Assert.IsType<IrGotoTerminator>(function.Blocks[0].Terminator).Target;
        var testBranch = Assert.IsType<IrBranchTerminator>(blocks[testId].Terminator);
        var body = blocks[testBranch.WhenTrue];
        var exit = blocks[testBranch.WhenFalse];
        var bodyBranch = Assert.IsType<IrBranchTerminator>(body.Terminator);
        Assert.Equal(exit.Id, Assert.IsType<IrGotoTerminator>(
            blocks[bodyBranch.WhenTrue].Terminator).Target);
        var continueBlock = blocks[bodyBranch.WhenFalse];
        var update = blocks[Assert.IsType<IrGotoTerminator>(continueBlock.Terminator).Target];
        Assert.Equal(["scope_get_var", "scope_get_var", "call", "drop"], Operations(update));
        Assert.Equal(testId, Assert.IsType<IrGotoTerminator>(update.Terminator).Target);
        Assert.Contains(exit.Instructions, instruction => instruction.Operation == "leave_scope");
        Assert.True(Assert.IsType<IrReturnTerminator>(exit.Terminator).HasValue);
    }

    [Fact]
    public void Update_expressions_preserve_prefix_new_values_and_postfix_old_values()
    {
        var function = Assert.Single(Build(
            "consume(++value, target.field--, ++target[key]);").Functions);

        Assert.Equal([
                "scope_get_var",
                "scope_make_ref", "get_ref_value", "inc", "insert3", "put_ref_value",
                "scope_get_var", "get_field2", "post_dec", "perm3", "put_field",
                "scope_get_var", "scope_get_var", "to_propkey2", "dup2", "get_array_el",
                "inc", "insert3", "put_array_el",
                "call", "set_eval_ret"],
            Operations(function.Blocks[0]));
        var symbolic = function.Blocks[0].Instructions.Single(instruction =>
            instruction.Operation == "scope_make_ref");
        Assert.Equal("value", Assert.IsType<AtomOperand>(symbolic.Operands[0]).Value);
        Assert.Equal(function.BodyScope, Assert.IsType<IrScopeOperand>(symbolic.Operands[1]).Scope);
    }

    [Fact]
    public void Script_update_statements_preserve_the_completion_value()
    {
        var function = Assert.Single(Build("value++; target.field--; target[key]++;").Functions);

        Assert.Equal([
                "scope_make_ref", "get_ref_value", "post_inc", "perm4", "put_ref_value", "set_eval_ret",
                "scope_get_var", "get_field2", "post_dec", "perm3", "put_field", "set_eval_ret",
                "scope_get_var", "scope_get_var", "to_propkey2", "dup2", "get_array_el",
                "post_inc", "perm4", "put_array_el", "set_eval_ret"],
            Operations(function.Blocks[0]));
        Assert.Equal(3, function.Blocks[0].Instructions.Count(instruction =>
            instruction.Operation == "set_eval_ret"));
    }

    [Fact]
    public void For_update_discards_increment_result_before_the_back_edge()
    {
        var function = Assert.Single(Build("for (; condition; index++) work();").Functions);
        var testId = Assert.IsType<IrGotoTerminator>(function.Blocks[0].Terminator).Target;
        var branch = Assert.IsType<IrBranchTerminator>(function.Blocks[testId.Value].Terminator);
        var body = function.Blocks[branch.WhenTrue.Value];
        var updateId = Assert.IsType<IrGotoTerminator>(body.Terminator).Target;
        var update = function.Blocks[updateId.Value];

        Assert.Equal(["scope_make_ref", "get_ref_value", "post_inc", "put_ref_value", "drop"], Operations(update));
        Assert.Equal(testId, Assert.IsType<IrGotoTerminator>(update.Terminator).Target);
    }

    [Fact]
    public void Switch_tests_in_source_order_and_models_fallthrough_break_and_default()
    {
        var function = Assert.Single(Build(
            "switch (subject) { case first: one(); case second: two(); break; default: fallback(); } tail();")
            .Functions);

        var entry = function.Blocks[0];
        Assert.Equal(["scope_get_var", "enter_scope", "dup", "scope_get_var", "strict_eq"], Operations(entry));
        var firstTest = Assert.IsType<IrBranchTerminator>(entry.Terminator);
        var secondTestBlock = function.Blocks.Single(block => block.Id == firstTest.WhenFalse);
        Assert.Equal(["dup", "scope_get_var", "strict_eq"], Operations(secondTestBlock));
        var secondTest = Assert.IsType<IrBranchTerminator>(secondTestBlock.Terminator);

        // Case match edges target their bodies directly. The parser emits
        // test/body pairs in source order, rather than forwarding through
        // synthetic empty match blocks.
        var firstBody = function.Blocks.Single(block => block.Id == firstTest.WhenTrue);
        var secondBody = function.Blocks.Single(block => block.Id == secondTest.WhenTrue);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(firstBody));
        Assert.Equal(secondBody.Id, Assert.IsType<IrGotoTerminator>(firstBody.Terminator).Target);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(secondBody));

        var defaultBody = function.Blocks.Single(block => block.Id == secondTest.WhenFalse);
        Assert.Equal(["scope_get_var", "call", "set_eval_ret"], Operations(defaultBody));

        var exit = function.Blocks.Single(block => block.Id == Assert.IsType<IrGotoTerminator>(secondBody.Terminator).Target);
        Assert.Equal(["drop", "leave_scope", "scope_get_var", "call", "set_eval_ret"], Operations(exit));
        Assert.Equal(exit.Id, Assert.IsType<IrGotoTerminator>(defaultBody.Terminator).Target);
    }

    [Fact]
    public void Switch_cases_share_one_lexical_scope()
    {
        var function = Assert.Single(Build(
            "switch (value) { case 0: let fromCase = 1; break; default: const fromDefault = 2; }").Functions);
        var switchScope = Assert.Single(function.Scopes, scope => scope.Id != function.ArgumentScope &&
                                                                  scope.Id != function.BodyScope);

        Assert.Equal(function.BodyScope, switchScope.Parent);
        Assert.Equal(["fromDefault", "fromCase"], Names(function, switchScope));
        Assert.All(function.Bindings.Where(binding => binding.Name is "fromCase" or "fromDefault"),
            binding => Assert.Equal(switchScope.Id, binding.Scope));
        Assert.Equal(1, function.Blocks.SelectMany(block => block.Instructions)
            .Count(instruction => instruction.Operation == "enter_scope"));
        Assert.Equal(1, function.Blocks.SelectMany(block => block.Instructions)
            .Count(instruction => instruction.Operation == "leave_scope"));
    }

    [Fact]
    public void Nested_for_of_blocks_keep_unique_cfg_ids_after_layout_reordering()
    {
        var function = Assert.Single(Build(
            "for (const row of rows) { for (const cell of row) { if (cell) continue; use(cell); } }").Functions);

        Assert.Equal(function.Blocks.Count, function.Blocks.Select(block => block.Id).Distinct().Count());
    }

    [Fact]
    public void Catch_closure_retains_outer_lexical_binding()
    {
        var module = Build("function make(error, task) { let read; try { task(); } catch (error) { read = () => error; } return [error, read]; }");
        var make = Assert.Single(module.Functions, function => function.Name == "make");

        var read = Assert.Single(make.Bindings, binding => binding.Name == "read");
        Assert.True(read.IsLexical);
        Assert.Contains(module.Functions, function => function.Options.Form == IrFunctionForm.Arrow);
    }

    [Fact]
    public void Arrow_this_creates_a_lexical_parent_binding_in_the_pseudo_binding_pass()
    {
        var program = new JavaScriptFrontEnd("const fn = () => this.value;", "/tmp/arrow-this.js",
            JavaScriptSourceKind.Module).Parse();
        var module = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);
        new PseudoBindingPass().Run(module);
        var root = module.Functions[0];

        Assert.Contains(root.Bindings, binding => binding.Name == "this");
    }

    [Fact]
    public void Pseudo_binding_pass_keeps_top_level_arguments_as_a_global_name()
    {
        var program = new JavaScriptFrontEnd("arguments; function read() { return arguments; }",
            "/tmp/top-level-arguments.js", JavaScriptSourceKind.Module).Parse();
        var module = new AstToIrLowerer().Run(program.Ast, JavaScriptSourceKind.Module);

        new PseudoBindingPass().Run(module);

        var root = module.Functions[0];
        var read = Assert.Single(module.Functions, function => function.Name == "read");
        Assert.DoesNotContain(root.Bindings, binding => binding.Name == "arguments");
        Assert.Contains(read.Bindings, binding => binding.Name == "arguments");
    }

    private static IrModule Build(string source)
    {
        var program = new JavaScriptFrontEnd(source, "/tmp/ecma-ir-construction.js", JavaScriptSourceKind.Script).Parse();
        return new AstToIrLowerer().Run(program.Ast);
    }

    private static string[] Names(IrFunction function, IrScope scope) =>
        scope.Bindings.Select(id => function.Bindings[id.Value].Name).ToArray();

    private static string[] Operations(IrBlock block) =>
        block.Instructions.Select(instruction => instruction.Operation).ToArray();
}
