using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Ir;

/// <summary>
/// Builds the declaration-pattern control flow in parser order.
///
/// A declaration pattern is deliberately not lowered as "evaluate the right
/// hand side, then bind it".  The parser first installs an <c>undefined</c>
/// placeholder, lays out the assignment program, and only then appends the
/// initializer expression.  This matters for both bytecode layout and the
/// default-value branches nested in the pattern.
/// </summary>
internal sealed class DestructuringCfgBuilder(AstToIrLowerer owner)
{
    // Every block has an explicit entry height relative to the statement:
    // entry=0, bind=1 (the source value), evaluate=1 (the synthetic
    // undefined placeholder), done=0.  The map is retained by construction
    // so later extensions cannot silently join incompatible stacks.
    private readonly Dictionary<IrBlockId, int> _entryHeights = [];

    internal bool CanBuild(JsBindingPattern pattern) => pattern switch
    {
        JsIdentifierPattern or JsAssignmentPattern or JsRestPattern => false,
        JsArrayPattern array => array.Elements.All(element => element is null || CanBuildElement(element)),
        JsObjectPattern obj => obj.Properties.All(property => property.ComputedKey is null && CanBuildElement(property.Value)),
        _ => false,
    };

    private static bool CanBuildElement(JsBindingPattern pattern) => pattern switch
    {
        JsIdentifierPattern => true,
        JsAssignmentPattern assignment => CanBuildElement(assignment.Left),
        // The grammar only permits a rest binding in its parent array/object
        // pattern.  Its argument is nevertheless checked here so this CFG
        // never accidentally accepts an assignment-target form that its store
        // operation cannot lower.
        JsRestPattern { Argument: JsIdentifierPattern } => true,
        JsArrayPattern array => array.Elements.All(element => element is null || CanBuildElement(element)),
        JsObjectPattern obj => obj.Properties.All(property => property.ComputedKey is null && CanBuildElement(property.Value)),
        _ => false,
    };

    internal IrBlock EmitDeclaration(IrFunction function, IrBlock entry, IrScopeId scope,
        JsBindingPattern pattern, JsExpression initializer, JsAstNode location)
    {
        owner.EmitForDestructuring(entry, "push_undefined", location);
        owner.EmitForDestructuring(entry, "dup", location);
        owner.EmitForDestructuring(entry, "push_undefined", location);
        owner.EmitForDestructuring(entry, "strict_eq", location);
        // Emit the assignment program before allocating parse/done blocks.
        // Pattern defaults allocate their own CFG blocks while the pattern is
        // parsed; they must remain directly after the conditional which
        // dispatches to them.  Allocating the outer initializer first would
        // interpose it between a property default and its join label, forcing
        // an extra goto and changing the serialized program.
        var assign = owner.NewBlockForDestructuring(function);
        _entryHeights[entry.Id] = 0;
        _entryHeights[assign.Id] = 1;

        // `assign` is the parser's label_assign entry.  Pattern lowering may
        // append default-value blocks and consequently return a different
        // tail block, but the deferred initializer must always jump back to
        // this entry rather than skipping straight to that tail.
        var assignTail = owner.EmitPatternForDestructuring(function, assign, scope, pattern);
        // The physical order now continues with the outer `label_parse`
        // initializer and finally `label_done`.
        var evaluate = owner.NewBlockForDestructuring(function);
        var done = owner.NewBlockForDestructuring(function);
        _entryHeights[evaluate.Id] = 1;
        _entryHeights[done.Id] = 0;
        entry.Terminator = new IrBranchTerminator(evaluate.Id, assign.Id, owner.LocationForDestructuring(location));
        assignTail.Terminator = new IrGotoTerminator(done.Id, owner.LocationForDestructuring(location));
        owner.EmitForDestructuring(evaluate, "drop", location);
        evaluate = owner.EmitExpressionForDestructuring(function, evaluate, scope, initializer);
        evaluate.Terminator = new IrGotoTerminator(assign.Id, owner.LocationForDestructuring(location));
        // `label_done` is an ordinary continuation label.  In particular it
        // must not become an artificial return when this declaration precedes
        // another statement; the enclosing statement visitor supplies the
        // implicit return only after all statements have been parsed.
        return done;
    }

}
