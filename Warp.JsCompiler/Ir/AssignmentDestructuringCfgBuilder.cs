using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Ir;

/// <summary>
/// Emits destructuring assignments in parser order.  Unlike declaration
/// patterns, the right hand side is parsed after the binding program and the
/// assignment expression retains its original value below the iterator record.
/// </summary>
internal sealed class AssignmentDestructuringCfgBuilder(AstToIrLowerer owner)
{
    internal bool CanBuild(JsBindingPattern pattern) => pattern switch
    {
        JsArrayPattern array => array.Elements.All(element => element is null || CanBuildElement(element)),
        JsObjectPattern obj => obj.Properties.All(CanBuildObjectProperty),
        _ => false,
    };

    private static bool CanBuildObjectProperty(JsObjectBindingProperty property) =>
        property.ComputedKey is null && CanBuildObjectTarget(property.Value);

    private static bool CanBuildObjectTarget(JsBindingPattern pattern) => pattern switch
    {
        JsAssignmentTargetPattern { Target: JsIdentifierExpression or JsMemberExpression } => true,
        JsAssignmentPattern { Left: JsAssignmentTargetPattern { Target: JsIdentifierExpression or JsMemberExpression } } => true,
        _ => false,
    };

    private static bool CanBuildElement(JsBindingPattern pattern) => pattern switch
    {
        JsAssignmentTargetPattern { Target: JsIdentifierExpression or JsMemberExpression } => true,
        JsAssignmentPattern { Left: JsAssignmentTargetPattern { Target: JsIdentifierExpression or JsMemberExpression } } => true,
        JsArrayPattern nested => nested.Elements.All(element => element is null || CanBuildElement(element)),
        _ => false,
    };

    internal IrBlock Emit(IrFunction function, IrBlock entry, IrScopeId scope,
        JsBindingPattern pattern, JsExpression right, JsAstNode location)
    {
        // js_parse_destructuring_element(... hasval = FALSE) first emits a
        // jump over the binding program.  The RHS later jumps backwards to
        // label_assign, where `dup` preserves the expression completion.
        var assign = owner.NewBlockForDestructuring(function);
        var assignTail = EmitPattern(function, assign, scope, pattern);
        var parse = owner.NewBlockForDestructuring(function);
        var done = owner.NewBlockForDestructuring(function);
        entry.Terminator = new IrGotoTerminator(parse.Id, owner.LocationForDestructuring(location));
        assignTail.Terminator = new IrGotoTerminator(done.Id, owner.LocationForDestructuring(location));
        parse = owner.EmitExpressionForDestructuring(function, parse, scope, right);
        parse.Terminator = new IrGotoTerminator(assign.Id, owner.LocationForDestructuring(location));
        return done;
    }

    private IrBlock EmitPattern(IrFunction function, IrBlock block, IrScopeId scope,
        JsBindingPattern pattern)
    {
        return pattern switch
        {
            JsArrayPattern array => EmitArrayPattern(function, block, scope, array),
            JsObjectPattern obj => EmitObjectPattern(function, block, scope, obj),
            _ => throw new InvalidOperationException("Assignment parser-order builder expected a compound pattern."),
        };
    }

    private IrBlock EmitArrayPattern(IrFunction function, IrBlock block, IrScopeId scope,
        JsArrayPattern array)
    {
        owner.EmitForDestructuring(block, "dup", array);
        owner.EmitForDestructuring(block, "for_of_start", array);
        foreach (var element in array.Elements)
        {
            if (element is null)
            {
                owner.EmitForDestructuring(block, "for_of_next", array, new ImmediateOperand(0));
                owner.EmitForDestructuring(block, "drop", array);
                owner.EmitForDestructuring(block, "drop", array);
                continue;
            }
            if (element is JsArrayPattern nested)
            {
                owner.EmitForDestructuring(block, "for_of_next", nested, new ImmediateOperand(0));
                owner.EmitForDestructuring(block, "drop", nested);
                block = EmitPatternValue(function, block, scope, nested);
                continue;
            }
            var (target, defaultValue) = element switch
            {
                JsAssignmentTargetPattern direct => (direct, (JsExpression?)null),
                JsAssignmentPattern { Left: JsAssignmentTargetPattern left, Right: var right } => (left, right),
                _ => throw new InvalidOperationException("Unsupported assignment pattern element."),
            };
            if (target.Target is not (JsIdentifierExpression or JsMemberExpression))
                throw new InvalidOperationException("Unsupported assignment pattern element.");
            block = owner.EmitAssignmentReferenceForDestructuring(function, block, scope, target.Target, out var depth);
            owner.EmitForDestructuring(block, "for_of_next", target, new ImmediateOperand(depth));
            owner.EmitForDestructuring(block, "drop", target);
            if (defaultValue is not null)
                block = EmitDefaultValue(function, block, scope, target.Target, target, defaultValue);
            owner.EmitAssignmentStoreForDestructuring(block, target.Target);
        }
        owner.EmitForDestructuring(block, "iterator_close", array);
        return block;
    }

    private IrBlock EmitObjectPattern(IrFunction function, IrBlock block, IrScopeId scope,
        JsObjectPattern pattern)
    {
        // js_parse_destructuring_element(..., hasval = FALSE) starts its
        // assignment program by preserving the assignment-expression value
        // and coercing the duplicate into the source object.
        owner.EmitForDestructuring(block, "dup", pattern);
        owner.EmitForDestructuring(block, "to_object", pattern);
        foreach (var property in pattern.Properties)
        {
            var (target, defaultValue) = property.Value switch
            {
                JsAssignmentTargetPattern direct => (direct.Target, (JsExpression?)null),
                JsAssignmentPattern { Left: JsAssignmentTargetPattern left, Right: var right } => (left.Target, right),
                _ => throw new InvalidOperationException("Unsupported object assignment target."),
            };

            // `get_lvalue` is deliberately emitted before OP_get_field.  It
            // preserves a member base/key while a default expression runs,
            // and its depth dictates how the source receiver is rotated
            // beneath that retained lvalue.
            owner.EmitForDestructuring(block, "dup", property);
            block = owner.EmitAssignmentReferenceForDestructuring(function, block, scope, target, out var depth);
            switch (depth)
            {
                case 1:
                    owner.EmitForDestructuring(block, "swap", property);
                    break;
                case 2:
                    owner.EmitForDestructuring(block, "rot3l", property);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported object assignment lvalue depth.");
            }
            owner.EmitForDestructuring(block, "get_field", property, new AtomOperand(property.Key));
            if (defaultValue is not null)
                block = EmitDefaultValue(function, block, scope, target, property, defaultValue);
            owner.EmitAssignmentStoreForDestructuring(block, target);
        }
        owner.EmitForDestructuring(block, "drop", pattern);
        return block;
    }

    private IrBlock EmitDefaultValue(IrFunction function, IrBlock block, IrScopeId scope,
        JsExpression target, JsAstNode location, JsExpression defaultValue)
    {
        owner.EmitForDestructuring(block, "dup", location);
        owner.EmitForDestructuring(block, "push_undefined", location);
        owner.EmitForDestructuring(block, "strict_eq", location);
        var useDefault = owner.NewBlockForDestructuring(function);
        var store = owner.NewBlockForDestructuring(function);
        block.Terminator = new IrBranchTerminator(useDefault.Id, store.Id, owner.LocationForDestructuring(location));
        owner.EmitForDestructuring(useDefault, "drop", location);
        useDefault = owner.EmitExpressionWithInferredNameForDestructuring(function, useDefault, scope,
            defaultValue, target is JsIdentifierExpression identifier ? identifier.Name : null);
        useDefault.Terminator = new IrGotoTerminator(store.Id, owner.LocationForDestructuring(location));
        return store;
    }

    private IrBlock EmitPatternValue(IrFunction function, IrBlock block, IrScopeId scope,
        JsBindingPattern pattern)
    {
        if (pattern is JsArrayPattern array) return EmitPattern(function, block, scope, array);
        throw new InvalidOperationException("Unsupported nested assignment pattern.");
    }
}
