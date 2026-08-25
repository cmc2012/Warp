using Warp.JsCompiler;
using Warp.JsCompiler.Encoding;
using Xunit;

namespace Warp.JsCompiler.Tests;

public sealed class TargetOpcodeDescriptorTests
{
    [Fact]
    public void Catalog_covers_canonical_temporary_and_short_opcode_spaces()
    {
        var all = TargetOpcodeCatalog.All;

        Assert.Equal(259, all.Count);
        Assert.Equal(all.Count, all.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enumerable.Range(0, TargetOpcodeCatalog.CanonicalCount),
            Codes(TargetOpcodeEncodingKind.Canonical));
        Assert.Equal(Enumerable.Range(TargetOpcodeCatalog.TemporaryStart,
                TargetOpcodeCatalog.TemporaryCount),
            Codes(TargetOpcodeEncodingKind.Temporary));
        Assert.Equal(Enumerable.Range(TargetOpcodeCatalog.TemporaryStart,
                TargetOpcodeCatalog.ShortCount),
            Codes(TargetOpcodeEncodingKind.Short));
        Assert.Equal(TargetOpcodeCatalog.FinalOpcodeCount,
            all.Where(entry => entry.EncodingKind != TargetOpcodeEncodingKind.Temporary)
                .Select(entry => entry.Code).Distinct().Count());
    }

    [Theory]
    [InlineData("invalid", 0, 1, 0, 0, "None", "Canonical")]
    [InlineData("call_method", 36, 3, 2, 1, "VariablePop", "Canonical")]
    [InlineData("catch", 108, 5, 0, 1, "Label", "Canonical")]
    [InlineData("with_put_var", 115, 10, 2, 1, "AtomLabelU8", "Canonical")]
    [InlineData("nop", 177, 1, 0, 0, "None", "Canonical")]
    [InlineData("enter_scope", 178, 3, 0, 0, "U16", "Temporary")]
    [InlineData("line_num", 192, 5, 0, 0, "U32", "Temporary")]
    [InlineData("push_minus1", 178, 1, 0, 1, "NoneInt", "Short")]
    [InlineData("fclosure8", 190, 2, 0, 1, "Constant8", "Short")]
    [InlineData("get_length", 231, 1, 1, 1, "None", "Short")]
    [InlineData("if_false8", 232, 2, 1, 0, "Label8", "Short")]
    [InlineData("call3", 239, 1, 1, 1, "InlineVariablePop", "Short")]
    [InlineData("typeof_is_function", 243, 1, 1, 1, "None", "Short")]
    public void Rows_match_ecma_opcode_h(string name, int code, int size, int pop, int push,
        string format, string encoding)
    {
        var descriptor = TargetOpcodeCatalog.Get(name);

        Assert.Equal(code, descriptor.Code);
        Assert.Equal(size, descriptor.Size);
        Assert.Equal(pop, descriptor.PopCount);
        Assert.Equal(push, descriptor.PushCount);
        Assert.Equal(format, descriptor.OperandFormat.ToString());
        Assert.Equal(encoding, descriptor.EncodingKind.ToString());
    }

    [Theory]
    [InlineData("call", 4, 5, -4)]
    [InlineData("tail_call_method", 3, 5, -5)]
    [InlineData("array_from", 7, 7, -6)]
    [InlineData("eval", 2, 3, -2)]
    [InlineData("call0", 99, 1, 0)]
    [InlineData("call3", 99, 4, -3)]
    public void Variable_pop_formats_compute_required_stack(string name, int operand, int requiredPop, int delta)
    {
        var descriptor = TargetOpcodeCatalog.Get(name);

        Assert.Equal(requiredPop, descriptor.RequiredPopCount(operand));
        Assert.Equal(delta, descriptor.StackDelta(operand));
    }

    [Theory]
    [InlineData("return", "Terminal", 0, 0)]
    [InlineData("goto", "UnconditionalBranch", 0, 1)]
    [InlineData("if_false", "ConditionalBranch", 1, 1)]
    [InlineData("catch", "Catch", 1, 1)]
    [InlineData("gosub", "FinallySubroutine", 1, 1)]
    [InlineData("with_get_var", "WithScopeLookup", 1, 1)]
    public void Control_kind_declares_fallthrough_and_target_edges(string name, string control,
        int fallthroughCount, int targetCount)
    {
        var descriptor = TargetOpcodeCatalog.Get(name);

        Assert.Equal(control, descriptor.ControlKind.ToString());
        Assert.Equal(fallthroughCount,
            descriptor.Successors.Count(rule => rule.Kind == TargetOpcodeSuccessorKind.Fallthrough));
        Assert.Equal(targetCount,
            descriptor.Successors.Count(rule => rule.Kind == TargetOpcodeSuccessorKind.Target));
    }

    [Theory]
    [InlineData("gosub", 1)]
    [InlineData("catch", 0)]
    [InlineData("with_get_var", 1)]
    [InlineData("with_delete_var", 1)]
    [InlineData("with_make_ref", 2)]
    [InlineData("with_get_ref", 2)]
    [InlineData("with_get_ref_undef", 2)]
    [InlineData("with_put_var", -1)]
    public void Special_target_edges_match_compute_stack_size(string name, int adjustment)
    {
        var target = Assert.Single(TargetOpcodeCatalog.Get(name).Successors,
            rule => rule.Kind == TargetOpcodeSuccessorKind.Target);
        Assert.Equal(adjustment, target.StackAdjustment);
    }

    [Fact]
    public void Final_lookup_selects_short_rows_over_overlapping_temporary_rows()
    {
        Assert.Equal("push_minus1", TargetOpcodeCatalog.GetFinal(178).Name);
        Assert.Equal("typeof_is_function", TargetOpcodeCatalog.GetFinal(243).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => TargetOpcodeCatalog.GetFinal(244));
    }

    private static IEnumerable<int> Codes(TargetOpcodeEncodingKind kind) =>
        TargetOpcodeCatalog.All.Where(entry => entry.EncodingKind == kind)
            .Select(entry => (int)entry.Code);
}
