using Warp.JsCompiler.Api;

namespace Warp.JsCompiler.Frontend;

/// <summary>
/// Performs the binding-name early errors required by strict code.  Modules
/// are strict from their first token, so this validation belongs between AST
/// construction and scope analysis rather than in a code-generation pass.
/// </summary>
internal sealed class JavaScriptStrictBindingValidator(string fileName)
{
    internal void ValidateModule(JsAstProgram program)
    {
        foreach (var statement in program.Body) VisitStatement(statement);
    }

    private void VisitStatement(JsStatement statement)
    {
        switch (statement)
        {
            case JsBlockStatement block:
                foreach (var child in block.Body) VisitStatement(child);
                return;
            case JsVariableStatement variables:
                foreach (var declaration in variables.Declarations)
                    VisitPattern(declaration.Pattern ?? new JsIdentifierPattern(declaration.Name, declaration.Line, declaration.Column));
                return;
            case JsForStatement loop:
                if (loop.Initializer is not null) VisitStatement(loop.Initializer);
                VisitStatement(loop.Body);
                return;
            case JsForInOfStatement loop:
                if (loop.Declaration is not null) VisitStatement(loop.Declaration);
                VisitStatement(loop.Body);
                return;
            case JsIfStatement conditional:
                VisitStatement(conditional.Consequent);
                if (conditional.Alternate is not null) VisitStatement(conditional.Alternate);
                return;
            case JsWhileStatement loop:
                VisitStatement(loop.Body);
                return;
            case JsDoWhileStatement loop:
                VisitStatement(loop.Body);
                return;
            case JsSwitchStatement selection:
                foreach (var @case in selection.Cases)
                    foreach (var child in @case.Consequent) VisitStatement(child);
                return;
            case JsTryStatement tried:
                VisitStatement(tried.Body);
                if (tried.Handler is { } handler)
                {
                    if (handler.Pattern is not null) VisitPattern(handler.Pattern);
                    else if (handler.Binding is { } binding) Check(binding, handler.Line, handler.Column);
                    VisitStatement(handler.Body);
                }
                if (tried.Finalizer is not null) VisitStatement(tried.Finalizer);
                return;
            case JsLabeledStatement labeled:
                VisitStatement(labeled.Body);
                return;
            case JsWithStatement contextual:
                VisitStatement(contextual.Body);
                return;
            case JsFunctionStatement function:
                Check(function.Name, function.Line, function.Column);
                VisitParameters(function.Parameters, function.ParameterPatterns, function.Line, function.Column);
                VisitStatement(function.Body);
                return;
            case JsClassDeclaration declaration:
                Check(declaration.Name, declaration.Line, declaration.Column);
                VisitMembers(declaration.Members);
                return;
            case JsImportStatement import:
                foreach (var binding in import.Bindings) Check(binding.LocalName, binding.Line, binding.Column);
                return;
            case JsExportStatement { Declaration: { } declaration }:
                VisitStatement(declaration);
                return;
            case JsExpressionStatement expression:
                VisitExpression(expression.Expression);
                return;
            case JsReturnStatement { Argument: { } argument }:
                VisitExpression(argument);
                return;
            case JsThrowStatement thrown:
                VisitExpression(thrown.Argument);
                return;
        }
    }

    private void VisitMembers(IReadOnlyList<JsClassMember> members)
    {
        foreach (var member in members)
        {
            VisitParameters(member.Parameters, member.ParameterPatterns, member.Line, member.Column);
            VisitStatement(member.Body);
            if (member.Initializer is not null) VisitExpression(member.Initializer);
        }
    }

    private void VisitParameters(IReadOnlyList<string> names, IReadOnlyList<JsBindingPattern>? patterns, int line, int column)
    {
        for (var index = 0; index < names.Count; index++)
            if (patterns is not null && index < patterns.Count) VisitPattern(patterns[index]);
            else Check(names[index], line, column);
    }

    private void VisitPattern(JsBindingPattern pattern)
    {
        switch (pattern)
        {
            case JsIdentifierPattern identifier:
                Check(identifier.Name, identifier.Line, identifier.Column);
                break;
            case JsRestPattern rest:
                VisitPattern(rest.Argument);
                break;
            case JsAssignmentPattern assignment:
                VisitPattern(assignment.Left);
                VisitExpression(assignment.Right);
                break;
            case JsArrayPattern array:
                foreach (var item in array.Elements) if (item is not null) VisitPattern(item);
                break;
            case JsObjectPattern obj:
                foreach (var property in obj.Properties) VisitPattern(property.Value);
                break;
        }
    }

    private void VisitExpression(JsExpression expression)
    {
        switch (expression)
        {
            case JsFunctionExpression function:
                if (function.Name is not null) Check(function.Name, function.Line, function.Column);
                VisitParameters(function.Parameters, function.ParameterPatterns, function.Line, function.Column);
                VisitStatement(function.Body);
                return;
            case JsClassExpression @class:
                if (@class.Name is not null) Check(@class.Name, @class.Line, @class.Column);
                VisitMembers(@class.Members);
                return;
            case JsUnaryExpression unary: VisitExpression(unary.Argument); return;
            case JsUpdateExpression update: VisitExpression(update.Argument); return;
            case JsBinaryExpression binary: VisitExpression(binary.Left); VisitExpression(binary.Right); return;
            case JsAssignmentExpression assignment: VisitExpression(assignment.Left); VisitExpression(assignment.Right); return;
            case JsConditionalExpression conditional:
                VisitExpression(conditional.Test); VisitExpression(conditional.Consequent); VisitExpression(conditional.Alternate); return;
            case JsMemberExpression member:
                VisitExpression(member.Object); VisitExpression(member.Property); return;
            case JsCallExpression call:
                VisitExpression(call.Callee); foreach (var argument in call.Arguments) VisitExpression(argument); return;
            case JsNewExpression created:
                VisitExpression(created.Callee); foreach (var argument in created.Arguments) VisitExpression(argument); return;
            case JsDynamicImportExpression imported: VisitExpression(imported.Specifier); return;
            case JsSpreadExpression spread: VisitExpression(spread.Argument); return;
            case JsSequenceExpression sequence:
                foreach (var item in sequence.Expressions) VisitExpression(item);
                return;
            case JsYieldExpression { Argument: { } argument }: VisitExpression(argument); return;
            case JsAwaitExpression awaited: VisitExpression(awaited.Argument); return;
            case JsArrayExpression array:
                foreach (var item in array.Elements) if (item is not null) VisitExpression(item);
                return;
            case JsObjectExpression obj:
                foreach (var property in obj.Properties) VisitExpression(property.Value);
                return;
        }
    }

    private void Check(string name, int line, int column)
    {
        if (name is "eval" or "arguments")
            throw new JavaScriptCompilationException("invalid variable name in strict mode", fileName, line, column, "ECMA1002");
    }
}
