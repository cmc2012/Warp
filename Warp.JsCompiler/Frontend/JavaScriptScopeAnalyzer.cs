using Warp.JsCompiler.Api;

namespace Warp.JsCompiler.Frontend;

internal enum JsBindingKind { Var, Let, Const, Function, Parameter, Import }

internal sealed class JsBinding(string name, JsBindingKind kind, JsScope scope, int line, int column)
{
    public string Name { get; } = name;
    public JsBindingKind Kind { get; } = kind;
    public JsScope Scope { get; } = scope;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public bool Captured { get; internal set; }
    public int ReferenceCount { get; internal set; }
}

internal sealed class JsScope(JsScope? parent, bool isFunction)
{
    private readonly Dictionary<string, JsBinding> _bindings = new(StringComparer.Ordinal);
    public JsScope? Parent { get; } = parent;
    public bool IsFunction { get; } = isFunction;
    public IReadOnlyDictionary<string, JsBinding> Bindings => _bindings;

    public bool TryDeclare(JsBinding binding) => _bindings.TryAdd(binding.Name, binding);
    public bool TryGet(string name, out JsBinding binding) => _bindings.TryGetValue(name, out binding!);
    public JsScope NearestFunctionScope() => IsFunction ? this : Parent?.NearestFunctionScope() ?? this;
}

internal sealed record JsScopeAnalysis(JsScope GlobalScope, IReadOnlyList<JsBinding> UnresolvedReferences);

/// <summary>Performs lexical binding resolution before bytecode generation.</summary>
internal sealed class JavaScriptScopeAnalyzer(string fileName)
{
    private readonly List<JsBinding> _unresolved = [];

    public JsScopeAnalysis Analyze(JsAstProgram program)
    {
        var global = new JsScope(null, true);
        AnalyzeStatements(program.Body, global);
        return new JsScopeAnalysis(global, _unresolved);
    }

    private void AnalyzeStatements(IReadOnlyList<JsStatement> statements, JsScope scope)
    {
        foreach (var statement in statements) AnalyzeStatement(statement, scope);
    }

    private void AnalyzeStatement(JsStatement statement, JsScope scope)
    {
        switch (statement)
        {
            case JsEmptyStatement:
                return;
            case JsBlockStatement block:
                AnalyzeStatements(block.Body, new JsScope(scope, false));
                return;
            case JsExpressionStatement expression:
                AnalyzeExpression(expression.Expression, scope);
                return;
            case JsVariableStatement variables:
                foreach (var declaration in variables.Declarations)
                {
                    var target = variables.Kind == "var" ? scope.NearestFunctionScope() : scope;
                    DeclarePattern(target, declaration.Pattern ?? new JsIdentifierPattern(declaration.Name, declaration.Line, declaration.Column), variables.Kind switch { "const" => JsBindingKind.Const, "let" => JsBindingKind.Let, _ => JsBindingKind.Var });
                    if (declaration.Initializer is not null) AnalyzeExpression(declaration.Initializer, scope);
                }
                return;
            case JsReturnStatement result when result.Argument is not null:
                AnalyzeExpression(result.Argument, scope);
                return;
            case JsThrowStatement thrown:
                AnalyzeExpression(thrown.Argument, scope);
                return;
            case JsIfStatement conditional:
                AnalyzeExpression(conditional.Test, scope); AnalyzeStatement(conditional.Consequent, scope);
                if (conditional.Alternate is not null) AnalyzeStatement(conditional.Alternate, scope);
                return;
            case JsWhileStatement loop:
                AnalyzeExpression(loop.Test, scope); AnalyzeStatement(loop.Body, scope);
                return;
            case JsDoWhileStatement loop:
                AnalyzeStatement(loop.Body, scope); AnalyzeExpression(loop.Test, scope);
                return;
            case JsForStatement loop:
                var forScope = new JsScope(scope, false);
                if (loop.Initializer is not null) AnalyzeStatement(loop.Initializer, forScope);
                if (loop.Test is not null) AnalyzeExpression(loop.Test, forScope);
                if (loop.Update is not null) AnalyzeExpression(loop.Update, forScope);
                AnalyzeStatement(loop.Body, forScope);
                return;
            case JsForInOfStatement loop:
                // The iterable is evaluated before a lexical loop binding is
                // introduced. The binding and body then share a loop scope.
                AnalyzeExpression(loop.Right, scope);
                var forInOfScope = new JsScope(scope, false);
                if (loop.Declaration is not null) AnalyzeStatement(loop.Declaration, forInOfScope);
                if (loop.Left is not null) AnalyzeExpression(loop.Left, forInOfScope);
                AnalyzeStatement(loop.Body, forInOfScope);
                return;
            case JsSwitchStatement selection:
                AnalyzeExpression(selection.Discriminant, scope);
                foreach (var entry in selection.Cases)
                {
                    if (entry.Test is not null) AnalyzeExpression(entry.Test, scope);
                    AnalyzeStatements(entry.Consequent, new JsScope(scope, false));
                }
                return;
            case JsTryStatement attempt:
                AnalyzeStatement(attempt.Body, scope);
                if (attempt.Handler is { } handler)
                {
                    var catchScope = new JsScope(scope, false);
                    if (handler.Pattern is { } pattern)
                        DeclarePattern(catchScope, pattern, JsBindingKind.Let);
                    else if (handler.Binding is { } binding)
                        Declare(catchScope, binding, JsBindingKind.Let, handler.Line, handler.Column);
                    AnalyzeStatement(handler.Body, catchScope);
                }
                if (attempt.Finalizer is not null) AnalyzeStatement(attempt.Finalizer, scope);
                return;
            case JsClassDeclaration declaration:
                Declare(scope, declaration.Name, JsBindingKind.Let, declaration.Line, declaration.Column);
                AnalyzeClass(declaration.SuperClass, declaration.Members, scope);
                return;
            case JsLabeledStatement labeled:
                AnalyzeStatement(labeled.Body, scope);
                return;
            case JsWithStatement contextual:
                AnalyzeExpression(contextual.Object, scope);
                AnalyzeStatement(contextual.Body, scope);
                return;
            case JsBreakStatement or JsContinueStatement:
                return;
            case JsFunctionStatement function:
                Declare(scope, function.Name, JsBindingKind.Function, function.Line, function.Column);
                var functionScope = new JsScope(scope, true);
                for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
                {
                    Declare(functionScope, function.Parameters[parameterIndex], JsBindingKind.Parameter, function.Line, function.Column);
                    if (function.ParameterDefaults is { } defaults && parameterIndex < defaults.Count && defaults[parameterIndex] is { } defaultValue)
                        AnalyzeExpression(defaultValue, functionScope);
                }
                AnalyzeStatements(function.Body.Body, functionScope);
                return;
            case JsImportStatement import:
                foreach (var binding in import.Bindings)
                    Declare(scope, binding.LocalName, JsBindingKind.Import, binding.Line, binding.Column);
                return;
            case JsExportStatement export:
                if (export.Declaration is not null) AnalyzeStatement(export.Declaration, scope);
                foreach (var binding in export.Bindings)
                    Resolve(new JsIdentifierExpression(binding.LocalName, binding.Line, binding.Column), scope);
                return;
        }
    }

    private void AnalyzeExpression(JsExpression expression, JsScope scope)
    {
        switch (expression)
        {
            case JsIdentifierExpression identifier:
                Resolve(identifier, scope);
                return;
            case JsLiteralExpression:
            case JsNewTargetExpression:
            case JsSuperExpression:
                return;
            case JsUnaryExpression unary:
                AnalyzeExpression(unary.Argument, scope);
                return;
            case JsUpdateExpression update:
                AnalyzeExpression(update.Argument, scope);
                return;
            case JsBinaryExpression binary:
                AnalyzeExpression(binary.Left, scope); AnalyzeExpression(binary.Right, scope);
                return;
            case JsAssignmentExpression assignment:
                AnalyzeExpression(assignment.Left, scope); AnalyzeExpression(assignment.Right, scope);
                return;
            case JsConditionalExpression conditional:
                AnalyzeExpression(conditional.Test, scope); AnalyzeExpression(conditional.Consequent, scope); AnalyzeExpression(conditional.Alternate, scope);
                return;
            case JsMemberExpression member:
                AnalyzeExpression(member.Object, scope);
                if (member.Computed) AnalyzeExpression(member.Property, scope);
                return;
            case JsPrivateIdentifierExpression:
                return;
            case JsCallExpression call:
                AnalyzeExpression(call.Callee, scope);
                foreach (var argument in call.Arguments) AnalyzeExpression(argument, scope);
                return;
            case JsFunctionExpression function:
                var functionScope = new JsScope(scope, true);
                if (function.Name is { Length: > 0 } name)
                    Declare(functionScope, name, JsBindingKind.Function, function.Line, function.Column);
                for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
                {
                    Declare(functionScope, function.Parameters[parameterIndex], JsBindingKind.Parameter, function.Line, function.Column);
                    if (function.ParameterDefaults is { } defaults && parameterIndex < defaults.Count && defaults[parameterIndex] is { } defaultValue)
                        AnalyzeExpression(defaultValue, functionScope);
                }
                AnalyzeStatements(function.Body.Body, functionScope);
                return;
            case JsClassExpression @class:
                AnalyzeClass(@class.SuperClass, @class.Members, scope);
                return;
            case JsNewExpression created:
                AnalyzeExpression(created.Callee, scope);
                foreach (var argument in created.Arguments) AnalyzeExpression(argument, scope);
                return;
            case JsSpreadExpression spread:
                AnalyzeExpression(spread.Argument, scope);
                return;
            case JsSequenceExpression sequence:
                foreach (var item in sequence.Expressions) AnalyzeExpression(item, scope);
                return;
            case JsYieldExpression yielded when yielded.Argument is not null:
                AnalyzeExpression(yielded.Argument, scope);
                return;
            case JsAwaitExpression awaited:
                AnalyzeExpression(awaited.Argument, scope);
                return;
            case JsArrayExpression array:
                foreach (var element in array.Elements) if (element is not null) AnalyzeExpression(element, scope);
                return;
            case JsObjectExpression obj:
                foreach (var property in obj.Properties)
                {
                    if (property.ComputedKey is not null) AnalyzeExpression(property.ComputedKey, scope);
                    AnalyzeExpression(property.Value, scope);
                }
                return;
        }
    }

    private void DeclarePattern(JsScope scope, JsBindingPattern pattern, JsBindingKind kind)
    {
        switch (pattern)
        {
            case JsIdentifierPattern identifier:
                Declare(scope, identifier.Name, kind, identifier.Line, identifier.Column);
                return;
            case JsRestPattern rest:
                DeclarePattern(scope, rest.Argument, kind);
                return;
            case JsAssignmentPattern assignment:
                DeclarePattern(scope, assignment.Left, kind);
                AnalyzeExpression(assignment.Right, scope);
                return;
            case JsArrayPattern array:
                foreach (var element in array.Elements) if (element is not null) DeclarePattern(scope, element, kind);
                return;
            case JsObjectPattern obj:
                foreach (var property in obj.Properties) DeclarePattern(scope, property.Value, kind);
                return;
        }
    }

    private void AnalyzeClass(JsExpression? superClass, IReadOnlyList<JsClassMember> members, JsScope scope)
    {
        if (superClass is not null) AnalyzeExpression(superClass, scope);
        foreach (var member in members)
        {
            if (member.ComputedKey is not null) AnalyzeExpression(member.ComputedKey, scope);
            if (member.Kind == JsClassMemberKind.Field)
            {
                // Field initializers run in the per-instance initializer
                // function.  Their outer lexical references are therefore
                // captures, unlike computed keys which run while defining
                // the class.
                if (member.Initializer is not null)
                    AnalyzeExpression(member.Initializer, new JsScope(scope, true));
                continue;
            }
            if (member.Kind == JsClassMemberKind.StaticBlock)
            {
                AnalyzeStatements(member.Body.Body, new JsScope(scope, false));
                continue;
            }
            var methodScope = new JsScope(scope, true);
            foreach (var parameter in member.Parameters)
                Declare(methodScope, parameter, JsBindingKind.Parameter, member.Line, member.Column);
            AnalyzeStatements(member.Body.Body, methodScope);
        }
    }

    private void Declare(JsScope scope, string name, JsBindingKind kind, int line, int column)
    {
        var binding = new JsBinding(name, kind, scope, line, column);
        if (scope.TryDeclare(binding)) return;
        // `var` declarations are function-scoped and may repeat an earlier
        // `var` declaration in that same scope (including a loop header).
        // Lexical declarations deliberately retain the duplicate diagnostic.
        if (kind == JsBindingKind.Var && scope.TryGet(name, out var existing) &&
            existing.Kind is JsBindingKind.Var or JsBindingKind.Parameter or JsBindingKind.Function)
            return;
        if (kind == JsBindingKind.Function && scope.TryGet(name, out existing) && existing.Kind == JsBindingKind.Var)
            return;
        throw new JavaScriptCompilationException($"Identifier '{name}' has already been declared.", fileName, line, column, "ECMA1003");
    }

    private void Resolve(JsIdentifierExpression identifier, JsScope scope)
    {
        for (var candidate = scope; candidate is not null; candidate = candidate.Parent)
        {
            if (!candidate.TryGet(identifier.Name, out var binding)) continue;
            binding.ReferenceCount++;
            // A binding is captured only when the reference crosses a function
            // boundary. Merely walking from a nested block to its containing
            // function must remain a local access; conversely, an outer block
            // binding referenced by an inner function is still captured.
            for (var current = scope; !ReferenceEquals(current, candidate); current = current.Parent!)
            {
                if (!current.IsFunction) continue;
                binding.Captured = true;
                break;
            }
            return;
        }
        _unresolved.Add(new JsBinding(identifier.Name, JsBindingKind.Var, scope, identifier.Line, identifier.Column));
    }
}
