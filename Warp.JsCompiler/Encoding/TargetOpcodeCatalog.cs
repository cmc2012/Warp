namespace Warp.JsCompiler.Encoding;

/// <summary>Operand formats from ECMAScript 2021-03-27 ecma-opcode.h.</summary>
public enum TargetOpcodeOperandFormat
{
    None, NoneInt, NoneLocal, NoneArgument, NoneVarReference,
    U8, I8, Local8, Constant8, Label8, U16, I16, Label16,
    VariablePop, InlineVariablePop, VariablePopU16,
    Local, Argument, VarReference, U32, I32, Constant, Label, Atom,
    AtomU8, AtomU16, AtomLabelU8, AtomLabelU16, LabelU16,
}

public enum TargetOpcodeEncodingKind
{
    Canonical,
    Temporary,
    Short,
}

public enum TargetOpcodeControlKind
{
    Fallthrough,
    ConditionalBranch,
    UnconditionalBranch,
    Catch,
    FinallySubroutine,
    WithScopeLookup,
    Terminal,
}

public enum TargetOpcodeSuccessorKind
{
    Fallthrough,
    Target,
}

/// <summary>
/// A control-flow successor and the stack-height adjustment applied after the
/// opcode's ordinary pop/push effect. ECMAScript uses non-zero adjustments for
/// finally return addresses and failed with-environment lookups.
/// </summary>
internal readonly record struct TargetOpcodeSuccessorRule(
    TargetOpcodeSuccessorKind Kind,
    int StackAdjustment);

/// <summary>
/// One row of the ECMAScript opcode table. Temporary and short opcodes deliberately
/// overlap numerically from OP_TEMP_START; EncodingKind disambiguates them.
/// </summary>
public sealed record TargetOpcodeDescriptor(
    byte Code,
    string Name,
    byte Size,
    byte PopCount,
    byte PushCount,
    TargetOpcodeOperandFormat OperandFormat,
    TargetOpcodeEncodingKind EncodingKind,
    TargetOpcodeControlKind ControlKind)
{
    internal int RequiredPopCount(int variableOperand = 0)
    {
        if (variableOperand < 0) throw new ArgumentOutOfRangeException(nameof(variableOperand));
        return OperandFormat switch
        {
            TargetOpcodeOperandFormat.VariablePop or TargetOpcodeOperandFormat.VariablePopU16 =>
                checked(PopCount + variableOperand),
            TargetOpcodeOperandFormat.InlineVariablePop =>
                checked(PopCount + InlineArgumentCount()),
            _ => PopCount,
        };
    }

    internal int StackDelta(int variableOperand = 0) =>
        checked(PushCount - RequiredPopCount(variableOperand));

    internal IReadOnlyList<TargetOpcodeSuccessorRule> Successors => ControlKind switch
    {
        TargetOpcodeControlKind.Terminal => [],
        TargetOpcodeControlKind.UnconditionalBranch =>
            [new(TargetOpcodeSuccessorKind.Target, 0)],
        TargetOpcodeControlKind.ConditionalBranch or TargetOpcodeControlKind.Catch =>
            [new(TargetOpcodeSuccessorKind.Fallthrough, 0), new(TargetOpcodeSuccessorKind.Target, 0)],
        TargetOpcodeControlKind.FinallySubroutine =>
            [new(TargetOpcodeSuccessorKind.Fallthrough, 0), new(TargetOpcodeSuccessorKind.Target, 1)],
        TargetOpcodeControlKind.WithScopeLookup =>
            [new(TargetOpcodeSuccessorKind.Fallthrough, 0),
             new(TargetOpcodeSuccessorKind.Target, WithTargetAdjustment())],
        _ => [new(TargetOpcodeSuccessorKind.Fallthrough, 0)],
    };

    private int InlineArgumentCount() => Name switch
    {
        "call0" => 0,
        "call1" => 1,
        "call2" => 2,
        "call3" => 3,
        _ => throw new InvalidOperationException($"Inline variable-pop format is invalid for '{Name}'."),
    };

    private int WithTargetAdjustment() => Name switch
    {
        "with_get_var" or "with_delete_var" => 1,
        "with_make_ref" or "with_get_ref" or "with_get_ref_undef" => 2,
        "with_put_var" => -1,
        _ => throw new InvalidOperationException($"With-scope control kind is invalid for '{Name}'."),
    };
}

/// <summary>
/// Complete opcode metadata for the non-BIGNUM ECMAScript 2021-03-27 ABI used by
/// this compiler. Rows are in ecma-opcode.h order.
/// </summary>
public static class TargetOpcodeCatalog
{
    internal const int CanonicalCount = 178;
    internal const int TemporaryCount = 15;
    internal const int ShortCount = 66;
    internal const byte TemporaryStart = 178;
    internal const byte FinalOpcodeCount = 244;

    private static readonly TargetOpcodeDescriptor[] Entries =
    [
        new(0, "invalid", 1, 0, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(1, "push_i32", 5, 0, 1, TargetOpcodeOperandFormat.I32, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(2, "push_const", 5, 0, 1, TargetOpcodeOperandFormat.Constant, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(3, "fclosure", 5, 0, 1, TargetOpcodeOperandFormat.Constant, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(4, "push_atom_value", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(5, "private_symbol", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(6, "undefined", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(7, "null", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(8, "push_this", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(9, "push_false", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(10, "push_true", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(11, "object", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(12, "special_object", 2, 0, 1, TargetOpcodeOperandFormat.U8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(13, "rest", 3, 0, 1, TargetOpcodeOperandFormat.U16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(14, "drop", 1, 1, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(15, "nip", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(16, "nip1", 1, 3, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(17, "dup", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(18, "dup1", 1, 2, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(19, "dup2", 1, 2, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(20, "dup3", 1, 3, 6, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(21, "insert2", 1, 2, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(22, "insert3", 1, 3, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(23, "insert4", 1, 4, 5, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(24, "perm3", 1, 3, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(25, "perm4", 1, 4, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(26, "perm5", 1, 5, 5, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(27, "swap", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(28, "swap2", 1, 4, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(29, "rot3l", 1, 3, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(30, "rot3r", 1, 3, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(31, "rot4l", 1, 4, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(32, "rot5l", 1, 5, 5, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(33, "call_constructor", 3, 2, 1, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(34, "call", 3, 1, 1, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(35, "tail_call", 3, 1, 0, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(36, "call_method", 3, 2, 1, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(37, "tail_call_method", 3, 2, 0, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(38, "array_from", 3, 0, 1, TargetOpcodeOperandFormat.VariablePop, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(39, "apply", 3, 3, 1, TargetOpcodeOperandFormat.U16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(40, "return", 1, 1, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(41, "return_undef", 1, 0, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(42, "check_ctor_return", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(43, "check_ctor", 1, 0, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(44, "check_brand", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(45, "add_brand", 1, 2, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(46, "return_async", 1, 1, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(47, "throw", 1, 1, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(48, "throw_error", 6, 0, 0, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(49, "eval", 5, 1, 1, TargetOpcodeOperandFormat.VariablePopU16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(50, "apply_eval", 3, 2, 1, TargetOpcodeOperandFormat.U16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(51, "regexp", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(52, "get_super", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(53, "import", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(54, "check_var", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(55, "get_var_undef", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(56, "get_var", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(57, "put_var", 5, 1, 0, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(58, "put_var_init", 5, 1, 0, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(59, "put_var_strict", 5, 2, 0, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(60, "get_ref_value", 1, 2, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(61, "put_ref_value", 1, 3, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(62, "define_var", 6, 0, 0, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(63, "check_define_var", 6, 0, 0, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(64, "define_func", 6, 1, 0, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(65, "get_field", 5, 1, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(66, "get_field2", 5, 1, 2, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(67, "put_field", 5, 2, 0, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(68, "get_private_field", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(69, "put_private_field", 1, 3, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(70, "define_private_field", 1, 3, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(71, "get_array_el", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(72, "get_array_el2", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(73, "put_array_el", 1, 3, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(74, "get_super_value", 1, 3, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(75, "put_super_value", 1, 4, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(76, "define_field", 5, 2, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(77, "set_name", 5, 1, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(78, "set_name_computed", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(79, "set_proto", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(80, "set_home_object", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(81, "define_array_el", 1, 3, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(82, "append", 1, 3, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(83, "copy_data_properties", 2, 3, 3, TargetOpcodeOperandFormat.U8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(84, "define_method", 6, 2, 1, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(85, "define_method_computed", 2, 3, 1, TargetOpcodeOperandFormat.U8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(86, "define_class", 6, 2, 2, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(87, "define_class_computed", 6, 3, 3, TargetOpcodeOperandFormat.AtomU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(88, "get_loc", 3, 0, 1, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(89, "put_loc", 3, 1, 0, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(90, "set_loc", 3, 1, 1, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(91, "get_arg", 3, 0, 1, TargetOpcodeOperandFormat.Argument, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(92, "put_arg", 3, 1, 0, TargetOpcodeOperandFormat.Argument, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(93, "set_arg", 3, 1, 1, TargetOpcodeOperandFormat.Argument, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(94, "get_var_ref", 3, 0, 1, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(95, "put_var_ref", 3, 1, 0, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(96, "set_var_ref", 3, 1, 1, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(97, "set_loc_uninitialized", 3, 0, 0, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(98, "get_loc_check", 3, 0, 1, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(99, "put_loc_check", 3, 1, 0, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(100, "put_loc_check_init", 3, 1, 0, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(101, "get_var_ref_check", 3, 0, 1, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(102, "put_var_ref_check", 3, 1, 0, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(103, "put_var_ref_check_init", 3, 1, 0, TargetOpcodeOperandFormat.VarReference, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(104, "close_loc", 3, 0, 0, TargetOpcodeOperandFormat.Local, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(105, "if_false", 5, 1, 0, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.ConditionalBranch),
        new(106, "if_true", 5, 1, 0, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.ConditionalBranch),
        new(107, "goto", 5, 0, 0, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.UnconditionalBranch),
        new(108, "catch", 5, 0, 1, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Catch),
        new(109, "gosub", 5, 0, 0, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.FinallySubroutine),
        new(110, "ret", 1, 1, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Terminal),
        new(111, "to_object", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(112, "to_propkey", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(113, "to_propkey2", 1, 2, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(114, "with_get_var", 10, 1, 0, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(115, "with_put_var", 10, 2, 1, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(116, "with_delete_var", 10, 1, 0, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(117, "with_make_ref", 10, 1, 0, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(118, "with_get_ref", 10, 1, 0, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(119, "with_get_ref_undef", 10, 1, 0, TargetOpcodeOperandFormat.AtomLabelU8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.WithScopeLookup),
        new(120, "make_loc_ref", 7, 0, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(121, "make_arg_ref", 7, 0, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(122, "make_var_ref_ref", 7, 0, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(123, "make_var_ref", 5, 0, 2, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(124, "for_in_start", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(125, "for_of_start", 1, 1, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(126, "for_await_of_start", 1, 1, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(127, "for_in_next", 1, 1, 3, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(128, "for_of_next", 2, 3, 5, TargetOpcodeOperandFormat.U8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(129, "iterator_check_object", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(130, "iterator_get_value_done", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(131, "iterator_close", 1, 3, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(132, "iterator_close_return", 1, 4, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(133, "iterator_next", 1, 4, 4, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(134, "iterator_call", 2, 4, 5, TargetOpcodeOperandFormat.U8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(135, "initial_yield", 1, 0, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(136, "yield", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(137, "yield_star", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(138, "async_yield_star", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(139, "await", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(140, "neg", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(141, "plus", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(142, "dec", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(143, "inc", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(144, "post_dec", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(145, "post_inc", 1, 1, 2, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(146, "dec_loc", 2, 0, 0, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(147, "inc_loc", 2, 0, 0, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(148, "add_loc", 2, 1, 0, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(149, "not", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(150, "lnot", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(151, "typeof", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(152, "delete", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(153, "delete_var", 5, 0, 1, TargetOpcodeOperandFormat.Atom, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(154, "mul", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(155, "div", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(156, "mod", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(157, "add", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(158, "sub", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(159, "pow", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(160, "shl", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(161, "sar", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(162, "shr", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(163, "lt", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(164, "lte", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(165, "gt", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(166, "gte", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(167, "instanceof", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(168, "in", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(169, "eq", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(170, "neq", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(171, "strict_eq", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(172, "strict_neq", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(173, "and", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(174, "xor", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(175, "or", 1, 2, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(176, "is_undefined_or_null", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(177, "nop", 1, 0, 0, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Canonical, TargetOpcodeControlKind.Fallthrough),
        new(178, "enter_scope", 3, 0, 0, TargetOpcodeOperandFormat.U16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(179, "leave_scope", 3, 0, 0, TargetOpcodeOperandFormat.U16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(180, "label", 5, 0, 0, TargetOpcodeOperandFormat.Label, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(181, "scope_get_var_undef", 7, 0, 1, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(182, "scope_get_var", 7, 0, 1, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(183, "scope_put_var", 7, 1, 0, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(184, "scope_delete_var", 7, 0, 1, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(185, "scope_make_ref", 11, 0, 2, TargetOpcodeOperandFormat.AtomLabelU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(186, "scope_get_ref", 7, 0, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(187, "scope_put_var_init", 7, 0, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(188, "scope_get_private_field", 7, 1, 1, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(189, "scope_get_private_field2", 7, 1, 2, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(190, "scope_put_private_field", 7, 1, 1, TargetOpcodeOperandFormat.AtomU16, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(191, "set_class_name", 5, 1, 1, TargetOpcodeOperandFormat.U32, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(192, "line_num", 5, 0, 0, TargetOpcodeOperandFormat.U32, TargetOpcodeEncodingKind.Temporary, TargetOpcodeControlKind.Fallthrough),
        new(178, "push_minus1", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(179, "push_0", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(180, "push_1", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(181, "push_2", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(182, "push_3", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(183, "push_4", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(184, "push_5", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(185, "push_6", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(186, "push_7", 1, 0, 1, TargetOpcodeOperandFormat.NoneInt, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(187, "push_i8", 2, 0, 1, TargetOpcodeOperandFormat.I8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(188, "push_i16", 3, 0, 1, TargetOpcodeOperandFormat.I16, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(189, "push_const8", 2, 0, 1, TargetOpcodeOperandFormat.Constant8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(190, "fclosure8", 2, 0, 1, TargetOpcodeOperandFormat.Constant8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(191, "push_empty_string", 1, 0, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(192, "get_loc8", 2, 0, 1, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(193, "put_loc8", 2, 1, 0, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(194, "set_loc8", 2, 1, 1, TargetOpcodeOperandFormat.Local8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(195, "get_loc0", 1, 0, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(196, "get_loc1", 1, 0, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(197, "get_loc2", 1, 0, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(198, "get_loc3", 1, 0, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(199, "put_loc0", 1, 1, 0, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(200, "put_loc1", 1, 1, 0, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(201, "put_loc2", 1, 1, 0, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(202, "put_loc3", 1, 1, 0, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(203, "set_loc0", 1, 1, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(204, "set_loc1", 1, 1, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(205, "set_loc2", 1, 1, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(206, "set_loc3", 1, 1, 1, TargetOpcodeOperandFormat.NoneLocal, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(207, "get_arg0", 1, 0, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(208, "get_arg1", 1, 0, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(209, "get_arg2", 1, 0, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(210, "get_arg3", 1, 0, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(211, "put_arg0", 1, 1, 0, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(212, "put_arg1", 1, 1, 0, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(213, "put_arg2", 1, 1, 0, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(214, "put_arg3", 1, 1, 0, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(215, "set_arg0", 1, 1, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(216, "set_arg1", 1, 1, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(217, "set_arg2", 1, 1, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(218, "set_arg3", 1, 1, 1, TargetOpcodeOperandFormat.NoneArgument, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(219, "get_var_ref0", 1, 0, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(220, "get_var_ref1", 1, 0, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(221, "get_var_ref2", 1, 0, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(222, "get_var_ref3", 1, 0, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(223, "put_var_ref0", 1, 1, 0, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(224, "put_var_ref1", 1, 1, 0, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(225, "put_var_ref2", 1, 1, 0, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(226, "put_var_ref3", 1, 1, 0, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(227, "set_var_ref0", 1, 1, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(228, "set_var_ref1", 1, 1, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(229, "set_var_ref2", 1, 1, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(230, "set_var_ref3", 1, 1, 1, TargetOpcodeOperandFormat.NoneVarReference, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(231, "get_length", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(232, "if_false8", 2, 1, 0, TargetOpcodeOperandFormat.Label8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.ConditionalBranch),
        new(233, "if_true8", 2, 1, 0, TargetOpcodeOperandFormat.Label8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.ConditionalBranch),
        new(234, "goto8", 2, 0, 0, TargetOpcodeOperandFormat.Label8, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.UnconditionalBranch),
        new(235, "goto16", 3, 0, 0, TargetOpcodeOperandFormat.Label16, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.UnconditionalBranch),
        new(236, "call0", 1, 1, 1, TargetOpcodeOperandFormat.InlineVariablePop, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(237, "call1", 1, 1, 1, TargetOpcodeOperandFormat.InlineVariablePop, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(238, "call2", 1, 1, 1, TargetOpcodeOperandFormat.InlineVariablePop, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(239, "call3", 1, 1, 1, TargetOpcodeOperandFormat.InlineVariablePop, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(240, "is_undefined", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(241, "is_null", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(242, "typeof_is_undefined", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
        new(243, "typeof_is_function", 1, 1, 1, TargetOpcodeOperandFormat.None, TargetOpcodeEncodingKind.Short, TargetOpcodeControlKind.Fallthrough),
    ];

    private static readonly IReadOnlyDictionary<byte, TargetOpcodeDescriptor> FinalByCode =
        Entries.Where(static entry => entry.EncodingKind != TargetOpcodeEncodingKind.Temporary)
            .ToDictionary(static entry => entry.Code);

    private static readonly IReadOnlyDictionary<string, TargetOpcodeDescriptor> ByName =
        Entries.ToDictionary(static entry => entry.Name, StringComparer.Ordinal);

    public static IReadOnlyList<TargetOpcodeDescriptor> All => Entries;

    public static TargetOpcodeDescriptor GetFinal(byte code) =>
        FinalByCode.TryGetValue(code, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Not a final ECMAScript opcode.");

    public static TargetOpcodeDescriptor Get(string name) =>
        ByName.TryGetValue(name, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"Unknown ECMAScript opcode '{name}'.", nameof(name));
}
