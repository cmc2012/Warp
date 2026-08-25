using Warp.JsCompiler.Api;

namespace Warp.JsCompiler.Frontend;

/// <summary>Recursive-descent JavaScript parser. It deliberately owns its grammar and AST.</summary>
internal sealed class JavaScriptAstParser(IReadOnlyList<JavaScriptToken> tokens, string fileName,
    JavaScriptSourceKind sourceKind = JavaScriptSourceKind.Module)
{
    private int _index;
    // Grammar-sensitive constructs are decided while parsing.  Nested normal
    // and arrow functions must therefore shadow an outer async context.
    private readonly Stack<FunctionContext> _functionContexts = [];
    // Parentheses are semantically relevant to the early error for mixing
    // `??` with `&&`/`||`.  The AST deliberately does not retain a wrapper
    // node, so retain that parse-time fact by identity instead.
    private readonly HashSet<JsExpression> _parenthesizedExpressions = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(string Name, bool Iteration, int FunctionDepth)> _labels = [];
    private int _breakableDepth;
    private int _loopDepth;
    private readonly record struct FunctionContext(bool IsArrow, bool IsAsync, bool IsGenerator);
    private bool IsInAsyncFunction => _functionContexts.Count != 0 && _functionContexts.Peek().IsAsync;
    private bool IsInGeneratorFunction => _functionContexts.Count != 0 && _functionContexts.Peek().IsGenerator;

    public JsAstProgram ParseProgram()
    {
        var body = new List<JsStatement>();
        while (!AtEnd) body.Add(ParseStatement());
        return new JsAstProgram(body);
    }

    private JsStatement ParseStatement()
    {
        var start = Current;
        if (Match(";")) return new JsEmptyStatement(start.Line, start.Column);
        // This runtime build has no debugger hook.  Match the reference
        // parser, which accepts the statement and emits no bytecode instead
        // of treating `debugger` as a global identifier expression.
        if (MatchWord("debugger"))
        {
            ConsumeTerminator();
            return new JsEmptyStatement(start.Line, start.Column);
        }
        if (Match("{")) return ParseBlock(start);
        if (MatchWord("var") || MatchWord("let") || MatchWord("const")) return ParseVariables(Previous);
        if (MatchWord("return")) return ParseReturn(start);
        if (MatchWord("throw"))
        {
            if (HasLineTerminatorBeforeCurrent())
                throw Error(Current, "Line terminator not permitted after 'throw'.");
            return new JsThrowStatement(ParseExpressionAndTerminator(), start.Line, start.Column);
        }
        if (MatchWord("if")) return ParseIf(start);
        if (MatchWord("while")) return ParseWhile(start);
        if (MatchWord("do")) return ParseDoWhile(start);
        if (MatchWord("for")) return ParseFor(start);
        if (MatchWord("switch")) return ParseSwitch(start);
        if (MatchWord("try")) return ParseTry(start);
        if (MatchWord("class")) return ParseClassDeclaration(start);
        if (MatchWord("with")) return ParseWith(start);
        if (MatchWord("break"))
        {
            var label = Current.Kind == JavaScriptTokenKind.Identifier ? Advance().Text : null;
            if (label is null ? _breakableDepth == 0 : !_labels.Any(item => item.Name == label && item.FunctionDepth == _functionContexts.Count))
                throw Error(start, "Invalid break statement.");
            ConsumeTerminator();
            return new JsBreakStatement(label, start.Line, start.Column);
        }
        if (MatchWord("continue"))
        {
            var label = Current.Kind == JavaScriptTokenKind.Identifier ? Advance().Text : null;
            if (label is null ? _loopDepth == 0 : !_labels.Any(item => item.Name == label && item.Iteration && item.FunctionDepth == _functionContexts.Count))
                throw Error(start, "Invalid continue statement.");
            ConsumeTerminator();
            return new JsContinueStatement(label, start.Line, start.Column);
        }
        if (Current.Kind == JavaScriptTokenKind.Identifier && CheckNext(":"))
        {
            var label = Advance(); Advance();
            if (_labels.Any(item => item.Name == label.Text)) throw Error(label, "Duplicate label.");
            var iteration = CheckWord("while") || CheckWord("do") || CheckWord("for");
            _labels.Push((label.Text, iteration, _functionContexts.Count));
            try { return new JsLabeledStatement(label.Text, ParseStatement(), label.Line, label.Column); }
            finally { _labels.Pop(); }
        }
        if (MatchWord("function")) return ParseFunction(start, false);
        if (CheckWord("async") && CheckNextWord("function") && !HasLineTerminatorAfterCurrent()) { Advance(); Advance(); return ParseFunction(start, true); }
        if (CheckWord("import") && !CheckNext("(") && !CheckNext(".")) { Advance(); return ParseImport(start); }
        if (MatchWord("export")) return ParseExport(start);

        var expression = ParseSequenceExpression();
        ConsumeTerminator();
        return new JsExpressionStatement(expression, start.Line, start.Column);
    }

    private JsBlockStatement ParseBlock(JavaScriptToken start)
    {
        var body = new List<JsStatement>();
        while (!AtEnd && !Check("}")) body.Add(ParseStatement());
        Consume("}", "Expected '}' to close block.");
        return new JsBlockStatement(body, start.Line, start.Column);
    }

    private JsStatement ParseVariables(JavaScriptToken kind, bool consumeTerminator = true)
    {
        var declarations = new List<JsVariableDeclarator>();
        do
        {
            var pattern = ParseBindingPattern(allowDefault: false);
            JsExpression? initializer = Match("=") ? ParseExpression() : null;
            if (consumeTerminator && kind.Text == "const" && initializer is null)
                throw Error(kind, "Missing initializer in const declaration.");
            var name = pattern is JsIdentifierPattern identifier ? identifier.Name : string.Empty;
            declarations.Add(new JsVariableDeclarator(name, initializer, pattern.Line, pattern.Column, pattern));
        } while (Match(","));
        if (consumeTerminator) ConsumeTerminator();
        return new JsVariableStatement(kind.Text, declarations, kind.Line, kind.Column);
    }

    private JsBindingPattern ParseBindingPattern(bool allowDefault = true)
    {
        if (Current.Kind == JavaScriptTokenKind.Identifier)
        {
            var identifier = Advance();
            var pattern = new JsIdentifierPattern(identifier.Text, identifier.Line, identifier.Column);
            return allowDefault && Match("=")
                ? new JsAssignmentPattern(pattern, ParseExpression(), identifier.Line, identifier.Column)
                : pattern;
        }
        if (Match("..."))
        {
            var start = Previous;
            return new JsRestPattern(ParseBindingPattern(), start.Line, start.Column);
        }
        if (Match("["))
        {
            var start = Previous;
            var elements = new List<JsBindingPattern?>();
            while (!Check("]"))
            {
                if (Match(","))
                {
                    // The comma that denotes an elision also separates it from
                    // the next binding, so do not consume a second separator.
                    elements.Add(null);
                    continue;
                }
                elements.Add(ParseBindingPattern());
                if (elements[^1] is JsRestPattern && !Check("]"))
                    throw Error(Current, "Rest binding must be last.");
                if (!Check("]") && !Match(",")) break;
            }
            Consume("]", "Expected ']' after array binding.");
            var pattern = new JsArrayPattern(elements, start.Line, start.Column);
            return allowDefault && Match("=")
                ? new JsAssignmentPattern(pattern, ParseExpression(), start.Line, start.Column)
                : pattern;
        }
        if (Match("{"))
        {
            var start = Previous;
            var properties = new List<JsObjectBindingProperty>();
            while (!Check("}"))
            {
                if (Match("..."))
                {
                    var restStart = Previous;
                    var rest = new JsRestPattern(ParseBindingPattern(), restStart.Line, restStart.Column);
                    properties.Add(new JsObjectBindingProperty("...", rest, rest.Line, rest.Column));
                    if (!Check("}")) throw Error(Current, "Rest binding must be last.");
                }
                else
                {
                    if (Match("["))
                    {
                        var computedStart = Previous;
                        var computedKey = ParseExpression();
                        Consume("]", "Expected ']' after computed binding property.");
                        Consume(":", "Expected ':' after computed binding property.");
                        var computedValue = ParseBindingPattern();
                        properties.Add(new JsObjectBindingProperty(string.Empty, computedValue,
                            computedStart.Line, computedStart.Column, computedKey));
                        if (!Match(",")) break;
                        continue;
                    }
                    var key = Advance();
                    if (key.Kind is not (JavaScriptTokenKind.Identifier or JavaScriptTokenKind.String or JavaScriptTokenKind.Number))
                        throw Error(key, "Expected object binding property.");
                    JsBindingPattern value;
                    var shorthand = !Match(":");
                    if (!shorthand) value = ParseBindingPattern();
                    else
                    {
                        var identifier = new JsIdentifierPattern(key.Text, key.Line, key.Column);
                        value = Match("=")
                            ? new JsAssignmentPattern(identifier, ParseExpression(), key.Line, key.Column)
                            : identifier;
                    }
                    properties.Add(new JsObjectBindingProperty(key.Text, value, key.Line, key.Column,
                        IsShorthand: shorthand));
                }
                if (!Match(",")) break;
            }
            Consume("}", "Expected '}' after object binding.");
            var pattern = new JsObjectPattern(properties, start.Line, start.Column);
            return allowDefault && Match("=")
                ? new JsAssignmentPattern(pattern, ParseExpression(), start.Line, start.Column)
                : pattern;
        }
        throw Error(Current, "Expected binding pattern.");
    }

    private JsStatement ParseReturn(JavaScriptToken start)
    {
        if (_functionContexts.Count == 0) throw Error(start, "return is only valid inside a function.");
        if (HasLineTerminatorBeforeCurrent() || Check(";") || Check("}") || AtEnd) { ConsumeTerminator(); return new JsReturnStatement(null, start.Line, start.Column); }
        return new JsReturnStatement(ParseExpressionAndTerminator(), start.Line, start.Column);
    }

    private JsStatement ParseIf(JavaScriptToken start)
    {
        Consume("(", "Expected '(' after if.");
        var test = ParseExpression(); Consume(")", "Expected ')' after if condition.");
        var consequent = ParseStatement();
        var alternate = MatchWord("else") ? ParseStatement() : null;
        return new JsIfStatement(test, consequent, alternate, start.Line, start.Column);
    }

    private JsStatement ParseWhile(JavaScriptToken start)
    {
        Consume("(", "Expected '(' after while.");
        var test = ParseExpression(); Consume(")", "Expected ')' after while condition.");
        return new JsWhileStatement(test, ParseNestedStatement(loop: true), start.Line, start.Column);
    }

    private JsStatement ParseNestedStatement(bool loop)
    {
        _breakableDepth++;
        if (loop) _loopDepth++;
        try { return ParseStatement(); }
        finally
        {
            if (loop) _loopDepth--;
            _breakableDepth--;
        }
    }

    private JsStatement ParseDoWhile(JavaScriptToken start)
    {
        var body = ParseNestedStatement(loop: true);
        ConsumeWord("while", "Expected 'while' after do body.");
        Consume("(", "Expected '(' after while.");
        var test = ParseExpression();
        Consume(")", "Expected ')' after do-while condition.");
        ConsumeTerminator();
        return new JsDoWhileStatement(body, test, start.Line, start.Column);
    }

    private JsStatement ParseSwitch(JavaScriptToken start)
    {
        Consume("(", "Expected '(' after switch.");
        var discriminant = ParseExpression();
        Consume(")", "Expected ')' after switch expression.");
        Consume("{", "Expected '{' after switch expression.");
        var cases = new List<JsSwitchCase>();
        _breakableDepth++;
        try
        {
        while (!AtEnd && !Check("}"))
        {
            var caseStart = Current;
            JsExpression? test;
            if (MatchWord("case")) { test = ParseExpression(); Consume(":", "Expected ':' after case expression."); }
            else if (MatchWord("default")) { test = null; Consume(":", "Expected ':' after default."); }
            else throw Error(Current, "Expected case or default in switch.");
            var consequent = new List<JsStatement>();
            while (!AtEnd && !Check("}") && !CheckWord("case") && !CheckWord("default")) consequent.Add(ParseStatement());
            cases.Add(new JsSwitchCase(test, consequent, caseStart.Line, caseStart.Column));
        }
        Consume("}", "Expected '}' after switch.");
        return new JsSwitchStatement(discriminant, cases, start.Line, start.Column);
        }
        finally { _breakableDepth--; }
    }

    private JsStatement ParseTry(JavaScriptToken start)
    {
        Consume("{", "Expected '{' after try.");
        var body = ParseBlock(Previous);
        JsCatchClause? handler = null;
        JsBlockStatement? finalizer = null;
        if (MatchWord("catch"))
        {
            var catchStart = Previous;
            string? binding = null;
            JsBindingPattern? pattern = null;
            if (Match("("))
            {
                pattern = ParseBindingPattern(allowDefault: false);
                binding = pattern is JsIdentifierPattern identifier ? identifier.Name : null;
                Consume(")", "Expected ')' after catch binding.");
            }
            Consume("{", "Expected '{' after catch.");
            handler = new JsCatchClause(binding, ParseBlock(Previous), catchStart.Line, catchStart.Column, pattern);
        }
        if (MatchWord("finally"))
        {
            Consume("{", "Expected '{' after finally.");
            finalizer = ParseBlock(Previous);
        }
        if (handler is null && finalizer is null)
            throw Error(Current, "Expected catch or finally after try block.");
        return new JsTryStatement(body, handler, finalizer, start.Line, start.Column);
    }

    private JsStatement ParseClassDeclaration(JavaScriptToken start)
    {
        var name = ConsumeIdentifier("Expected class name.");
        var (superClass, members) = ParseClassTail();
        return new JsClassDeclaration(name.Text, superClass, members, start.Line, start.Column);
    }

    private JsStatement ParseWith(JavaScriptToken start)
    {
        Consume("(", "Expected '(' after with.");
        var obj = ParseExpression();
        Consume(")", "Expected ')' after with object.");
        return new JsWithStatement(obj, ParseStatement(), start.Line, start.Column);
    }

    private (JsExpression? SuperClass, IReadOnlyList<JsClassMember> Members) ParseClassTail()
    {
        JsExpression? superClass = null;
        if (MatchWord("extends")) superClass = ParsePostfix();
        Consume("{", "Expected '{' for class body.");
        var members = new List<JsClassMember>();
        while (!AtEnd && !Check("}"))
        {
            if (Match(";")) continue;
            var memberStart = Current;
            // `static` and `async` are contextual keywords in a class body.  In
            // particular, `static() {}` and `async() {}` declare methods whose
            // names happen to be those words; they are not modifiers.  Consume a
            // modifier only when another member-name token follows it.
            var isStatic = CheckWord("static") && IsClassStaticModifier();
            if (isStatic) Advance();
            if (isStatic && Match("{"))
            {
                // A static initialization block has no member key or
                // parameter list. Its body is a lexical block evaluated
                // while the class is being defined.
                members.Add(new JsClassMember("", [], ParseBlock(Previous), true,
                    JsClassMemberKind.StaticBlock, memberStart.Line, memberStart.Column));
                continue;
            }
            var isAsync = CheckWord("async") && !CheckNext("(");
            if (isAsync) Advance();
            var generator = Match("*");
            var kind = JsClassMemberKind.Method;
            var accessorPrefix = (CheckWord("get") || CheckWord("set")) &&
                (CheckNextIdentifierFollowedBy("(") || _index + 1 < tokens.Count && tokens[_index + 1].Text == "[");
            if (accessorPrefix)
            {
                kind = Advance().Text == "get" ? JsClassMemberKind.Getter : JsClassMemberKind.Setter;
            }
            JsExpression? computedKey = null;
            JavaScriptToken name;
            if (Match("["))
            {
                name = Previous;
                computedKey = ParseExpression();
                Consume("]", "Expected ']' after computed class member name.");
            }
            else
            {
                name = Advance();
                if (name.Kind is not (JavaScriptTokenKind.Identifier or JavaScriptTokenKind.String or JavaScriptTokenKind.Number))
                    throw Error(name, "Expected class member name.");
                name = name with { Text = StaticPropertyName(name) };
            }
            if (Match("=") || Check(";"))
            {
                if (isAsync || generator || kind is JsClassMemberKind.Getter or JsClassMemberKind.Setter)
                    throw Error(memberStart, "Invalid class field declaration.");
                var initializer = Previous.Text == "=" ? ParseExpression() : null;
                ConsumeTerminator();
                members.Add(new JsClassMember(name.Text, [], new JsBlockStatement([], memberStart.Line, memberStart.Column), isStatic,
                    JsClassMemberKind.Field, memberStart.Line, memberStart.Column, ComputedKey: computedKey, Initializer: initializer));
                continue;
            }
            if (name.Text == "#constructor") throw Error(name, "Private constructor is not allowed.");
            if (isStatic && computedKey is null && name.Text == "prototype")
                throw Error(name, "Static class member cannot be named prototype.");
            if (computedKey is null && name.Text == "constructor" && !isStatic)
            {
                if (members.Any(member => member.Kind == JsClassMemberKind.Constructor))
                    throw Error(name, "Duplicate constructor in class.");
                kind = JsClassMemberKind.Constructor;
            }
            Consume("(", "Expected '(' after class member name.");
            var parameterPatterns = new List<JsBindingPattern>();
            var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
            Consume(")", "Expected ')' after class member parameters.");
            Consume("{", "Expected '{' for class member body.");
            if (kind is JsClassMemberKind.Getter or JsClassMemberKind.Setter && (isAsync || generator))
                throw Error(memberStart, "Accessors cannot be async or generator methods.");
            ValidateAccessorParameters(kind, parameterPatterns, memberStart);
            if (kind == JsClassMemberKind.Constructor && (isAsync || generator))
                throw Error(memberStart, "Class constructors cannot be async or generator methods.");
            members.Add(new JsClassMember(name.Text, parameters, ParseFunctionBody(Previous, arrow: false, async: isAsync, generator: generator), isStatic, kind, memberStart.Line, memberStart.Column, isAsync, generator, computedKey, DefinedArgCount: definedArgCount, ParameterDefaults: parameterDefaults, ParameterPatterns: parameterPatterns));
        }
        Consume("}", "Expected '}' after class body.");
        return (superClass, members);
    }

    private bool CheckNextIdentifierFollowedBy(string token) =>
        _index + 2 < tokens.Count && tokens[_index + 1].Kind == JavaScriptTokenKind.Identifier && tokens[_index + 2].Text == token;

    private bool IsClassStaticModifier()
    {
        if (!CheckWord("static") || _index + 1 >= tokens.Count) return false;
        var next = tokens[_index + 1];
        // `static;` and `static = value;` are fields named "static"; a
        // modifier must introduce another member name, a computed key, a
        // generator, or a static block.
        return next.Text is not "(" and not ";" and not "=" and not "}";
    }

    private JsStatement ParseFor(JavaScriptToken start)
    {
        // `await` belongs to the for-head rather than to its left-hand
        // expression.  Preserve that distinction in the AST: the emitter
        // needs a different iterator protocol, not merely an await around the
        // right-hand expression.
        var isAwait = MatchWord("await");
        if (isAwait && !IsInAsyncFunction)
            throw Error(Previous, "'for await' is only valid in an async function.");
        Consume("(", "Expected '(' after for.");
        JsStatement? initializer;
        if (Match(";")) initializer = null;
        else if (MatchWord("var") || MatchWord("let") || MatchWord("const"))
        {
            initializer = ParseVariables(Previous, consumeTerminator: false);
            if (initializer is JsVariableStatement { Declarations: [var declaration] } variables &&
                declaration.Initializer is JsBinaryExpression { Operator: "in", Left: var initialValue, Right: var iterationRight })
            {
                var normalized = new JsVariableStatement(variables.Kind,
                    [declaration with { Initializer = initialValue }], variables.Line, variables.Column);
                Consume(")", "Expected ')' after for-in clause.");
                if (isAwait) throw Error(start, "'for await' loop should be used with 'of'.");
                return new JsForInOfStatement(normalized, null, iterationRight, false, ParseNestedStatement(loop: true), start.Line, start.Column);
            }
            if (MatchWord("in") || MatchWord("of"))
            {
                var isOf = Previous.Text == "of";
                var right = ParseExpression();
                Consume(")", "Expected ')' after for-in/of clause.");
                if (isAwait && !isOf) throw Error(start, "'for await' loop should be used with 'of'.");
                return new JsForInOfStatement(initializer, null, right, isOf, ParseNestedStatement(loop: true), start.Line, start.Column, isAwait);
            }
            if (initializer is JsVariableStatement { Kind: "const", Declarations: var declarations } &&
                declarations.Any(declaration => declaration.Initializer is null))
                throw Error(start, "Missing initializer in const declaration.");
            Consume(";", "Expected ';' after for initializer.");
        }
        else
        {
            var expression = HasForIterationOperator() ? ParsePostfix() : ParseSequenceExpression();
            if (MatchWord("in") || MatchWord("of"))
            {
                var isOf = Previous.Text == "of";
                var right = ParseExpression();
                Consume(")", "Expected ')' after for-in/of clause.");
                if (isAwait && !isOf) throw Error(start, "'for await' loop should be used with 'of'.");
                return new JsForInOfStatement(null, expression, right, isOf, ParseNestedStatement(loop: true), start.Line, start.Column, isAwait);
            }
            Consume(";", "Expected ';' after for initializer.");
            initializer = new JsExpressionStatement(expression, expression.Line, expression.Column);
        }
        var test = Check(";") ? null : ParseSequenceExpression();
        Consume(";", "Expected ';' after for condition.");
        var update = Check(")") ? null : ParseSequenceExpression();
        Consume(")", "Expected ')' after for clauses.");
        return new JsForStatement(initializer, test, update, ParseNestedStatement(loop: true), start.Line, start.Column);
    }

    private bool HasForIterationOperator()
    {
        var nesting = 0;
        for (var i = _index; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Text is "(" or "[" or "{") nesting++;
            else if (token.Text is ")" or "]" or "}")
            {
                if (nesting == 0) return false;
                nesting--;
            }
            else if (nesting == 0 && token.Text == ";") return false;
            else if (nesting == 0 && token.Kind == JavaScriptTokenKind.Identifier && token.Text is "in" or "of") return true;
        }
        return false;
    }

    private JsStatement ParseFunction(JavaScriptToken start, bool isAsync, bool allowAnonymous = false)
    {
        var generator = Match("*");
        var name = allowAnonymous && Check("(")
            ? new JavaScriptToken(JavaScriptTokenKind.Identifier, string.Empty, start.Line, start.Column)
            : ConsumeIdentifier("Expected function name.");
        Consume("(", "Expected '(' after function name.");
        var parameterPatterns = new List<JsBindingPattern>();
        var (parameters, definedArgCount, parameterDefaults) =
            ParseParameterNamesInFunctionContext(parameterPatterns, isAsync, generator);
        Consume(")", "Expected ')' after parameters.");
        Consume("{", "Expected function body.");
        return new JsFunctionStatement(name.Text, parameters, ParseFunctionBody(Previous, arrow: false, async: isAsync, generator: generator), isAsync, start.Line, start.Column, generator, definedArgCount, parameterDefaults, ParameterPatterns: parameterPatterns);
    }

    private JsStatement ParseImport(JavaScriptToken start)
    {
        var bindings = new List<JsImportBinding>();
        JavaScriptToken specifier;
        if (Current.Kind == JavaScriptTokenKind.String)
            specifier = Advance();
        else
        {
            if (Current.Kind == JavaScriptTokenKind.Identifier)
            {
                var local = Advance();
                bindings.Add(new JsImportBinding(local.Text, "default", JsImportBindingKind.Default, local.Line, local.Column));
                if (Match(",")) { }
            }
            if (Match("*"))
            {
                ConsumeWord("as", "Expected 'as' after '*'.");
                var local = ConsumeIdentifier("Expected namespace binding.");
                bindings.Add(new JsImportBinding(local.Text, "*", JsImportBindingKind.Namespace, local.Line, local.Column));
            }
            else if (Match("{"))
            {
                if (!Check("}")) do
                {
                    var imported = ConsumeIdentifier("Expected imported binding.");
                    var local = MatchWord("as") ? ConsumeIdentifier("Expected local binding.") : imported;
                    bindings.Add(new JsImportBinding(local.Text, imported.Text, JsImportBindingKind.Named, local.Line, local.Column));
                } while (Match(","));
                Consume("}", "Expected '}' after import bindings.");
            }
            ConsumeWord("from", "Expected 'from' in import declaration.");
            specifier = Current.Kind == JavaScriptTokenKind.String ? Advance() : throw Error(Current, "Expected module string.");
        }
        ConsumeTerminator();
        return new JsImportStatement(specifier.Text, bindings, start.Line, start.Column);
    }

    private JsStatement ParseExport(JavaScriptToken start)
    {
        if (MatchWord("default"))
        {
            if (CheckWord("async") && CheckNextWord("function") && !HasLineTerminatorAfterCurrent())
            {
                Advance(); Advance();
                return new JsExportStatement(ParseFunction(Previous, true, true), [], true, start.Line, start.Column);
            }
            if (MatchWord("function")) return new JsExportStatement(ParseFunction(Previous, false, true), [], true, start.Line, start.Column);
            var expression = ParseExpressionAndTerminator();
            return new JsExportStatement(new JsExpressionStatement(expression, start.Line, start.Column), [], true, start.Line, start.Column);
        }
        if (MatchWord("var") || MatchWord("let") || MatchWord("const")) return new JsExportStatement(ParseVariables(Previous), [], false, start.Line, start.Column);
        if (CheckWord("async") && CheckNextWord("function") && !HasLineTerminatorAfterCurrent())
        {
            Advance(); Advance();
            return new JsExportStatement(ParseFunction(Previous, true), [], false, start.Line, start.Column);
        }
        if (MatchWord("function")) return new JsExportStatement(ParseFunction(Previous, false), [], false, start.Line, start.Column);
        if (MatchWord("class")) return new JsExportStatement(ParseClassDeclaration(Previous), [], false, start.Line, start.Column);
        if (Match("*"))
        {
            var namespaceBinding = MatchWord("as") ? ConsumeIdentifier("Expected namespace export name.") : null;
            ConsumeWord("from", "Expected 'from' after '*'.");
            var source = Current.Kind == JavaScriptTokenKind.String ? Advance().Text : throw Error(Current, "Expected module string.");
            ConsumeTerminator();
            if (namespaceBinding is not null)
                return new JsExportStatement(null, [new JsExportBinding("*", namespaceBinding.Text, namespaceBinding.Line, namespaceBinding.Column)], false, start.Line, start.Column, source);
            return new JsExportAllStatement(source, start.Line, start.Column);
        }
        if (Match("{"))
        {
            var bindings = new List<JsExportBinding>();
            if (!Check("}")) do
            {
                var local = ConsumeIdentifier("Expected local export binding.");
                var exported = MatchWord("as") ? ConsumeIdentifier("Expected exported binding.") : local;
                bindings.Add(new JsExportBinding(local.Text, exported.Text, local.Line, local.Column));
            } while (Match(","));
            Consume("}", "Expected '}' after export bindings.");
            string? source = null;
            if (MatchWord("from"))
                source = Current.Kind == JavaScriptTokenKind.String ? Advance().Text : throw Error(Current, "Expected module string.");
            ConsumeTerminator();
            return new JsExportStatement(null, bindings, false, start.Line, start.Column, source);
        }
        while (!AtEnd && !Check(";")) Advance();
        ConsumeTerminator();
        return new JsExportStatement(null, [], false, start.Line, start.Column);
    }

    private JsExpression ParseExpression() => ParseAssignment();
    private JsExpression ParseSequenceExpression()
    {
        var expressions = new List<JsExpression> { ParseAssignment() };
        while (Match(",")) expressions.Add(ParseAssignment());
        return expressions.Count == 1 ? expressions[0] : new JsSequenceExpression(expressions, expressions[0].Line, expressions[0].Column);
    }
    private JsExpression ParseAssignment()
    {
        var left = ParseBinary(0);
        if (Match("="))
        {
            ValidateAssignmentTarget(left, allowPattern: true);
            return new JsAssignmentExpression("=", left, ParseAssignment(), left.Line, left.Column);
        }
        if (Check("&&=") || Check("||=") || Check("??=") ||
            Check("+=") || Check("-=") || Check("*=") || Check("/=") || Check("%=") ||
            Check("&=") || Check("|=") || Check("^=") || Check("<<=") || Check(">>=") || Check(">>>=") || Check("**="))
        {
            ValidateAssignmentTarget(left);
            return new JsAssignmentExpression(Advance().Text, left, ParseAssignment(), left.Line, left.Column);
        }
        if (!Match("?")) return left;
        var consequent = ParseExpression(); Consume(":", "Expected ':' in conditional expression.");
        return new JsConditionalExpression(left, consequent, ParseAssignment(), left.Line, left.Column);
    }

    private JsExpression ParseBinary(int minimum)
    {
        var left = ParseUnary();
        while (BinaryPrecedence(Current) >= minimum)
        {
            var op = Advance(); var precedence = BinaryPrecedence(op);
            if (op.Text == "**" && left is JsUnaryExpression && !_parenthesizedExpressions.Contains(left))
                throw Error(op, "Unary expression cannot be the left operand of '**'.");
            var right = ParseBinary(op.Text == "**" ? precedence : precedence + 1);
            ValidateLogicalNullishMix(op, left, right);
            left = new JsBinaryExpression(op.Text, left, right, left.Line, left.Column);
        }
        return left;
    }

    private JsExpression ParseUnary()
    {
        if (CheckWord("await") && IsInAsyncFunction && MatchWord("await"))
        {
            var token = Previous;
            return new JsAwaitExpression(ParseUnary(), token.Line, token.Column);
        }
        if (Check("++") || Check("--"))
        { var op = Advance(); var target = ParseUnary(); ValidateAssignmentTarget(target); return new JsUpdateExpression(op.Text, target, true, op.Line, op.Column); }
        if (Check("!") || Check("~") || Check("+") || Check("-") || CheckWord("typeof") || CheckWord("void") || CheckWord("delete"))
        { var op = Advance(); return new JsUnaryExpression(op.Text, ParseUnary(), op.Line, op.Column); }
        return ParsePostfix();
    }

    private JsExpression ParsePostfix()
    {
        var expression = ParsePrimary();
        while (true)
        {
            if (Match("."))
            {
                var property = ConsumeIdentifier("Expected member name.");
                JsExpression member = property.Text.StartsWith('#')
                    ? new JsPrivateIdentifierExpression(property.Text, property.Line, property.Column)
                    : new JsIdentifierExpression(property.Text, property.Line, property.Column);
                expression = new JsMemberExpression(expression, member, false, expression.Line, expression.Column);
            }
            else if (Match("?."))
            {
                if (Match("("))
                {
                    var arguments = ParseArgumentList();
                    Consume(")", "Expected ')' after optional call arguments.");
                    expression = new JsCallExpression(expression, arguments, expression.Line, expression.Column, true, true);
                }
                else if (Match("["))
                {
                    var property = ParseExpression(); Consume("]", "Expected ']' after optional member expression.");
                    expression = new JsMemberExpression(expression, property, true, expression.Line, expression.Column, true);
                }
                else
                {
                    var property = ConsumeIdentifier("Expected optional member name.");
                    JsExpression member = property.Text.StartsWith('#')
                        ? new JsPrivateIdentifierExpression(property.Text, property.Line, property.Column)
                        : new JsIdentifierExpression(property.Text, property.Line, property.Column);
                    expression = new JsMemberExpression(expression, member, false, expression.Line, expression.Column, true);
                }
            }
            else if (Match("[")) { var property = ParseExpression(); Consume("]", "Expected ']'."); expression = new JsMemberExpression(expression, property, true, expression.Line, expression.Column); }
            else if (Match("("))
            {
                var arguments = ParseArgumentList();
                Consume(")", "Expected ')' after arguments.");
                expression = new JsCallExpression(expression, arguments, expression.Line, expression.Column, expression is JsMemberExpression { Optional: true });
            }
            else if (Current.Kind == JavaScriptTokenKind.Template)
            {
                if (ContainsOptionalChain(expression)) throw Error(Current, "Optional chain cannot be used as a template tag.");
                var template = Advance();
                var (cooked, raw, substitutions) = ParseTemplateParts(template);
                expression = new JsTaggedTemplateExpression(expression, cooked, raw, substitutions, expression.Line, expression.Column);
            }
            else if (!HasLineTerminatorBeforeCurrent() && (Check("++") || Check("--")))
            {
                var op = Advance();
                ValidateAssignmentTarget(expression);
                expression = new JsUpdateExpression(op.Text, expression, false, expression.Line, expression.Column);
            }
            else break;
        }
        return expression;
    }

    private JsExpression ParsePrimary()
    {
        var token = Advance();
        if (token.Kind is JavaScriptTokenKind.Identifier)
        {
            if (token.Text == "await" && !IsInAsyncFunction)
                throw Error(token, "await is only valid in an async function.");
            if (token.Text == "import" && Match("."))
            {
                ConsumeWord("meta", "Expected 'meta' after 'import.'.");
                if (sourceKind != JavaScriptSourceKind.Module)
                    throw Error(token, "import.meta only valid in module code.");
                return new JsImportMetaExpression(token.Line, token.Column);
            }
            if (token.Text == "import" && Match("("))
            {
                var specifier = ParseExpression();
                Consume(")", "Expected ')' after import specifier.");
                return new JsDynamicImportExpression(specifier, token.Line, token.Column);
            }
            if (token.Text == "function") return ParseFunctionExpression(token, false);
            // ECMAScript 2021-03-27 accepts private names as class member names
            // and after '.', but predates the `#name in object` brand syntax.
            if (token.Text.StartsWith('#'))
                throw Error(token, "Private name is not valid in this expression.");
            if (token.Text == "async" && CheckWord("function")) { Advance(); return ParseFunctionExpression(token, true); }
            if (token.Text == "async" && Current.Kind == JavaScriptTokenKind.Identifier && CheckNext("=>"))
            {
                var parameter = Advance();
                Consume("=>", "Expected '=>' after async arrow parameter.");
                return ParseArrowFunction(token, [parameter.Text], 1, async: true);
            }
            if (token.Text == "async" && Check("(") && IsAsyncArrowParameterList())
            {
                Advance();
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after async arrow parameters.");
                Consume("=>", "Expected '=>' after async arrow parameters.");
                return ParseArrowFunction(token, parameters, definedArgCount, parameterDefaults, parameterPatterns, async: true);
            }
            if (token.Text == "class")
            {
                string? name = Check("{") || CheckWord("extends") ? null : ConsumeIdentifier("Expected class name.").Text;
                var (superClass, members) = ParseClassTail();
                return new JsClassExpression(name, superClass, members, token.Line, token.Column);
            }
            if (token.Text == "super") return new JsSuperExpression(token.Line, token.Column);
            if (token.Text == "new")
            {
                if (Match("."))
                {
                    ConsumeWord("target", "Expected 'target' after 'new.'.");
                    if (_functionContexts.Count == 0)
                        throw Error(token, "new.target is only valid inside a function.");
                    return new JsNewTargetExpression(token.Line, token.Column);
                }
                return ParseNewExpression(token);
            }
            if (token.Text == "yield")
            {
                if (!IsInGeneratorFunction) throw Error(token, "yield is only valid in a generator function.");
                var delegateYield = Match("*");
                var argument = Check(";") || Check("}") || AtEnd ? null : ParseAssignment();
                return new JsYieldExpression(argument, delegateYield, token.Line, token.Column);
            }
            if (token.Text is "true" or "false" or "null") return new JsLiteralExpression(token.Text, token.Kind, token.Line, token.Column);
            if (Match("=>")) return ParseArrowFunction(token, [token.Text], 1);
            return new JsIdentifierExpression(token.Text, token.Line, token.Column);
        }
        if (token.Kind == JavaScriptTokenKind.Template) return ParseTemplateLiteral(token);
        if (token.Kind is JavaScriptTokenKind.Number or JavaScriptTokenKind.String or JavaScriptTokenKind.Regex) return new JsLiteralExpression(token.Text, token.Kind, token.Line, token.Column);
        if (token.Text == "(")
        {
            if (IsArrowParameterList())
            {
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after arrow parameters.");
                Consume("=>", "Expected '=>' after arrow parameters.");
                return ParseArrowFunction(token, parameters, definedArgCount, parameterDefaults, parameterPatterns);
            }
            var expression = ParseSequenceExpression(); Consume(")", "Expected ')'."); _parenthesizedExpressions.Add(expression); return expression;
        }
        if (token.Text == "[") return ParseArray(token);
        if (token.Text == "{") return ParseObject(token);
        throw Error(token, "Expected expression.");
    }

    private JsExpression ParseFunctionExpression(JavaScriptToken start, bool isAsync)
    {
        var generator = Match("*");
        string? name = null;
        if (!Check("(")) name = ConsumeIdentifier("Expected function name.").Text;
        Consume("(", "Expected '(' after function name.");
        var parameterPatterns = new List<JsBindingPattern>();
        var (parameters, definedArgCount, parameterDefaults) =
            ParseParameterNamesInFunctionContext(parameterPatterns, isAsync, generator);
        Consume(")", "Expected ')' after parameters.");
        Consume("{", "Expected function body.");
        return new JsFunctionExpression(name, parameters, ParseFunctionBody(Previous, arrow: false, async: isAsync, generator: generator), isAsync, false, start.Line, start.Column, generator, definedArgCount, parameterDefaults, IsNamedExpression: name is not null, ParameterPatterns: parameterPatterns);
    }

    private (List<string> Names, int DefinedArgCount, List<JsExpression?> Defaults) ParseParameterNames(List<JsBindingPattern>? patterns = null)
    {
        var parameters = new List<string>();
        var defaults = new List<JsExpression?>();
        var syntheticIndex = 0;
        var definedArgCount = 0;
        var closesLength = false;
        if (Check(")")) return (parameters, definedArgCount, defaults);
        do
        {
            var pattern = ParseBindingPattern(allowDefault: false);
            patterns?.Add(pattern);
            var hasDefault = Match("=");
            var defaultValue = hasDefault ? ParseExpression() : null;
            // A rest formal is still an argument binding.  Keeping its real
            // name here lets lowering address the argument slot directly;
            // only destructuring formals need a synthetic carrier name.
            parameters.Add(pattern switch
            {
                JsIdentifierPattern identifier => identifier.Name,
                JsRestPattern { Argument: JsIdentifierPattern identifier } => identifier.Name,
                _ => $"\0pattern{syntheticIndex++}",
            });
            defaults.Add(defaultValue);
            if (!closesLength)
            {
                if (hasDefault || pattern is JsRestPattern) closesLength = true;
                else definedArgCount++;
            }
            if (pattern is JsRestPattern && !Check(")"))
                throw Error(Current, "Rest parameter must be last.");
            if (!Match(",")) break;
            if (Check(")")) break;
        } while (true);
        return (parameters, definedArgCount, defaults);
    }

    private (List<string> Names, int DefinedArgCount, List<JsExpression?> Defaults)
        ParseParameterNamesInFunctionContext(List<JsBindingPattern> patterns, bool isAsync, bool generator)
    {
        _functionContexts.Push(new FunctionContext(IsArrow: false, IsAsync: isAsync, IsGenerator: generator));
        try { return ParseParameterNames(patterns); }
        finally { _functionContexts.Pop(); }
    }

    private JsExpression ParseNewExpression(JavaScriptToken start)
    {
        var callee = ParsePrimary();
        if (Check("?.")) throw Error(Current, "Optional chain cannot be used as a constructor.");
        while (true)
        {
            if (Match("."))
            {
                var property = ConsumeIdentifier("Expected member name.");
                callee = new JsMemberExpression(callee, new JsIdentifierExpression(property.Text, property.Line, property.Column), false, callee.Line, callee.Column);
                continue;
            }
            if (Match("["))
            {
                var property = ParseExpression();
                Consume("]", "Expected ']' after constructor member expression.");
                callee = new JsMemberExpression(callee, property, true, callee.Line, callee.Column);
                continue;
            }
            break;
        }
        if (ContainsOptionalChain(callee)) throw Error(start, "Optional chain cannot be used as a constructor.");
        var arguments = new List<JsExpression>();
        if (Match("("))
        {
            arguments = ParseArgumentList();
            Consume(")", "Expected ')' after constructor arguments.");
        }
        return new JsNewExpression(callee, arguments, start.Line, start.Column);
    }

    private JsExpression ParseArrowFunction(JavaScriptToken start, IReadOnlyList<string> parameters, int definedArgCount, IReadOnlyList<JsExpression?>? parameterDefaults = null, IReadOnlyList<JsBindingPattern>? parameterPatterns = null, bool async = false)
    {
        JsBlockStatement body;
        _functionContexts.Push(new FunctionContext(IsArrow: true, IsAsync: async, IsGenerator: false));
        var savedBreakableDepth = _breakableDepth;
        var savedLoopDepth = _loopDepth;
        _breakableDepth = _loopDepth = 0;
        try
        {
            if (Match("{")) body = ParseBlock(Previous);
            else
            {
                var expression = ParseAssignment();
                body = new JsBlockStatement([new JsReturnStatement(expression, expression.Line, expression.Column)], expression.Line, expression.Column);
            }
        }
        finally
        {
            _breakableDepth = savedBreakableDepth;
            _loopDepth = savedLoopDepth;
            _functionContexts.Pop();
        }
        return new JsFunctionExpression(null, parameters, body, async, true, start.Line, start.Column, DefinedArgCount: definedArgCount, ParameterDefaults: parameterDefaults, ParameterPatterns: parameterPatterns);
    }

    private JsBlockStatement ParseFunctionBody(JavaScriptToken opening, bool arrow, bool async = false, bool generator = false)
    {
        _functionContexts.Push(new FunctionContext(arrow, async, generator));
        var savedBreakableDepth = _breakableDepth;
        var savedLoopDepth = _loopDepth;
        _breakableDepth = _loopDepth = 0;
        try { return ParseBlock(opening); }
        finally
        {
            _breakableDepth = savedBreakableDepth;
            _loopDepth = savedLoopDepth;
            _functionContexts.Pop();
        }
    }

    private bool IsArrowParameterList()
    {
        var index = _index;
        var nesting = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Text is "(" or "[" or "{") nesting++;
            else if (token.Text is ")" or "]" or "}")
            {
                if (token.Text == ")" && nesting == 0)
                    return index + 1 < tokens.Count && tokens[index + 1].Text == "=>";
                nesting--;
            }
            index++;
        }
        return false;
    }

    private bool IsAsyncArrowParameterList()
    {
        var index = _index;
        var nesting = 0;
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token.Text is "(" or "[" or "{") nesting++;
            else if (token.Text is ")" or "]" or "}")
            {
                nesting--;
                if (token.Text == ")" && nesting == 0)
                    return index + 1 < tokens.Count && tokens[index + 1].Text == "=>";
            }
            index++;
        }
        return false;
    }

    private JsExpression ParseArray(JavaScriptToken start)
    {
        var elements = new List<JsExpression?>();
        while (!Check("]")) { elements.Add(Check(",") ? null : Match("...") ? new JsSpreadExpression(ParseExpression(), Previous.Line, Previous.Column) : ParseExpression()); if (!Match(",")) break; }
        Consume("]", "Expected ']'.");
        return new JsArrayExpression(elements, start.Line, start.Column);
    }

    private JsExpression ParseTemplateLiteral(JavaScriptToken token)
    {
        var (cooked, _, substitutions) = ParseTemplateParts(token);
        var receiver = new JsLiteralExpression(cooked[0], JavaScriptTokenKind.String, token.Line, token.Column);
        if (substitutions.Count == 0) return receiver;
        // The bytecode compiler emits an interpolated template as a single
        // String.prototype.concat call.  This retains the receiver/value
        // stack contract while each substitution remains an ordinary
        // expression (including a comma sequence).
        var arguments = new List<JsExpression>();
        for (var index = 0; index < substitutions.Count; index++)
        {
            arguments.Add(substitutions[index]);
            if (cooked[index + 1].Length != 0 || index + 1 < substitutions.Count)
                arguments.Add(new JsLiteralExpression(cooked[index + 1], JavaScriptTokenKind.String, token.Line, token.Column));
        }
        var property = new JsIdentifierExpression("concat", token.Line, token.Column);
        return new JsCallExpression(new JsMemberExpression(receiver, property, false, token.Line, token.Column), arguments, token.Line, token.Column);
    }

    private (IReadOnlyList<string> Cooked, IReadOnlyList<string> Raw, IReadOnlyList<JsExpression> Substitutions) ParseTemplateParts(JavaScriptToken token)
    {
        var cooked = new List<string>(); var raw = new List<string>(); var substitutions = new List<JsExpression>();
        var text = token.Text; var cursor = 1; var literalStart = cursor;
        while (cursor < text.Length - 1)
        {
            if (text[cursor] == '\\') { cursor += 2; continue; }
            if (cursor + 1 < text.Length && text[cursor] == '$' && text[cursor + 1] == '{')
            {
                var chunk = text[literalStart..cursor]; raw.Add(chunk); cooked.Add(DecodeTemplateChunk(chunk));
                var expressionStart = cursor + 2;
                cursor = FindTemplateExpressionEnd(text, expressionStart, token);
                var nested = new JavaScriptAstParser(new JavaScriptScanner(text[expressionStart..cursor], fileName).Scan(),
                    fileName, sourceKind);
                var expression = nested.ParseSequenceExpression();
                if (!nested.AtEnd) throw Error(token, "Unexpected token in template interpolation.");
                substitutions.Add(expression);
                cursor++; literalStart = cursor; continue;
            }
            cursor++;
        }
        var finalChunk = text[literalStart..^1]; raw.Add(finalChunk); cooked.Add(DecodeTemplateChunk(finalChunk));
        return (cooked, raw, substitutions);
    }

    private static int FindTemplateExpressionEnd(string text, int start, JavaScriptToken token)
    {
        var depth = 1;
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] is '\'' or '\"')
            {
                var quote = text[index++];
                while (index < text.Length && text[index] != quote) { if (text[index] == '\\') index++; index++; }
                continue;
            }
            if (text[index] == '{') depth++;
            else if (text[index] == '}' && --depth == 0) return index;
        }
        throw new JavaScriptCompilationException("Unterminated template interpolation.", "<source>", token.Line, token.Column, "ECMA1009");
    }

    private static string DecodeTemplateChunk(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || ++index >= value.Length) { output.Append(value[index]); continue; }
            switch (value[index])
            {
                case 'n': output.Append('\n'); break;
                case 'r': output.Append('\r'); break;
                case 't': output.Append('\t'); break;
                case 'b': output.Append('\b'); break;
                case 'f': output.Append('\f'); break;
                case 'v': output.Append('\v'); break;
                case '\r':
                    if (index + 1 < value.Length && value[index + 1] == '\n') index++;
                    break;
                case '\n': break;
                case 'x' when index + 2 < value.Length && TryReadHex(value.AsSpan(index + 1, 2), out var byteValue):
                    output.Append((char)byteValue); index += 2; break;
                case 'u' when index + 1 < value.Length && value[index + 1] == '{':
                {
                    var close = value.IndexOf('}', index + 2);
                    if (close < 0 || !TryReadHex(value.AsSpan(index + 2, close - index - 2), out var codePoint) || codePoint > 0x10ffff)
                        throw new FormatException("Invalid Unicode escape in template literal.");
                    output.Append(codePoint is >= 0xd800 and <= 0xdfff ? ((char)codePoint).ToString() : char.ConvertFromUtf32(codePoint));
                    index = close;
                    break;
                }
                case 'u' when index + 4 < value.Length && TryReadHex(value.AsSpan(index + 1, 4), out var unicode):
                    output.Append((char)unicode); index += 4; break;
                case '0' when index + 1 == value.Length || value[index + 1] is < '0' or > '9': output.Append('\0'); break;
                default: output.Append(value[index]); break;
            }
        }
        return output.ToString();
    }

    private static string StaticPropertyName(JavaScriptToken token) => token.Kind switch
    {
        JavaScriptTokenKind.String => token.Text,
        _ => token.Text.Replace("_", string.Empty, StringComparison.Ordinal),
    };

    private static bool TryReadHex(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private JsExpression ParseObject(JavaScriptToken start)
    {
        var properties = new List<JsObjectProperty>();
        var hasPrototypeSetter = false;
        while (!Check("}"))
        {
            if (Match("..."))
            {
                var spread = Previous;
                var spreadValue = ParseExpression();
                properties.Add(new JsObjectProperty("...", spreadValue, false, spread.Line, spread.Column));
                if (!Match(",")) break;
                continue;
            }
            var generator = Match("*");
            JsExpression? computedKey = null;
            JavaScriptToken key;
            if (Match("["))
            {
                key = Previous;
                computedKey = ParseExpression();
                Consume("]", "Expected ']' after computed property name.");
            }
            else
            {
                key = Advance();
                if (key.Kind is not (JavaScriptTokenKind.Identifier or JavaScriptTokenKind.String or JavaScriptTokenKind.Number))
                    throw Error(key, "Expected object property name.");
            }
            var shorthand = false;
            var assignmentPatternDefault = false;
            var propertyKind = JsObjectPropertyKind.Value;
            var isPrototypeSetter = false;
            JsExpression value;
            var asyncGenerator = computedKey is null && key.Text == "async" && Match("*");
            if (computedKey is null && key.Text == "async" && Match("["))
            {
                computedKey = ParseExpression();
                Consume("]", "Expected ']' after computed async method name.");
                Consume("(", "Expected '(' after computed async method name.");
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after async method parameters.");
                Consume("{", "Expected '{' for async method body.");
                value = new JsFunctionExpression(string.Empty, parameters, ParseFunctionBody(Previous, arrow: false, async: true, generator: asyncGenerator), true, false, key.Line, key.Column, asyncGenerator, definedArgCount, parameterDefaults, ParameterPatterns: parameterPatterns);
                propertyKind = JsObjectPropertyKind.Method;
            }
            else if (computedKey is null && key.Text == "async" && Current.Kind == JavaScriptTokenKind.Identifier && CheckNext("("))
            {
                var methodName = Advance();
                Consume("(", "Expected '(' after async method name.");
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after async method parameters.");
                Consume("{", "Expected '{' for async method body.");
                value = new JsFunctionExpression(string.Empty, parameters, ParseFunctionBody(Previous, arrow: false, async: true, generator: asyncGenerator), true, false, key.Line, key.Column, asyncGenerator, definedArgCount, parameterDefaults, ParameterPatterns: parameterPatterns);
                key = methodName;
                propertyKind = JsObjectPropertyKind.Method;
            }
            else if (asyncGenerator)
                throw Error(Current, "Expected async generator method name.");
            else if (computedKey is null && key.Text is "get" or "set" && Match("["))
            {
                var accessorKind = key.Text == "get" ? JsObjectPropertyKind.Getter : JsObjectPropertyKind.Setter;
                computedKey = ParseExpression();
                Consume("]", "Expected ']' after computed accessor name.");
                Consume("(", "Expected '(' after computed accessor name.");
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after accessor parameters.");
                Consume("{", "Expected '{' for accessor body.");
                value = new JsFunctionExpression(string.Empty, parameters, ParseFunctionBody(Previous, arrow: false, async: false), false, false, key.Line, key.Column, DefinedArgCount: definedArgCount, ParameterDefaults: parameterDefaults, ParameterPatterns: parameterPatterns);
                propertyKind = accessorKind;
                ValidateAccessorParameters(accessorKind, parameterPatterns, key);
            }
            else if (computedKey is null && key.Text is "get" or "set" && Current.Kind == JavaScriptTokenKind.Identifier && CheckNext("("))
            {
                var accessorKind = key.Text == "get" ? JsObjectPropertyKind.Getter : JsObjectPropertyKind.Setter;
                var accessorName = Advance();
                Consume("(", "Expected '(' after accessor name.");
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after accessor parameters.");
                Consume("{", "Expected '{' for accessor body.");
                value = new JsFunctionExpression(string.Empty, parameters, ParseFunctionBody(Previous, arrow: false, async: false), false, false, key.Line, key.Column,
                    DefinedArgCount: definedArgCount, ParameterDefaults: parameterDefaults, ParameterPatterns: parameterPatterns);
                key = accessorName;
                propertyKind = accessorKind;
                ValidateAccessorParameters(accessorKind, parameterPatterns, key);
            }
            else if (Match(":"))
            {
                value = ParseExpression();
                isPrototypeSetter = computedKey is null && key.Text == "__proto__";
                if (isPrototypeSetter && hasPrototypeSetter)
                    throw Error(key, "Duplicate __proto__ fields are not allowed in an object literal.");
                hasPrototypeSetter |= isPrototypeSetter;
            }
            else if (Match("("))
            {
                if (generator && key.Text is "get" or "set")
                    throw Error(key, "Accessor methods cannot be generators.");
                var parameterPatterns = new List<JsBindingPattern>();
                var (parameters, definedArgCount, parameterDefaults) = ParseParameterNames(parameterPatterns);
                Consume(")", "Expected ')' after method parameters.");
                Consume("{", "Expected method body.");
                value = new JsFunctionExpression(string.Empty, parameters, ParseFunctionBody(Previous, arrow: false, async: false), false, false, key.Line, key.Column, generator, definedArgCount, parameterDefaults, ParameterPatterns: parameterPatterns);
                propertyKind = JsObjectPropertyKind.Method;
            }
            else
            {
                if (generator) throw Error(key, "Expected '(' after generator method name.");
                shorthand = true;
                value = new JsIdentifierExpression(key.Text, key.Line, key.Column);
                if (Match("="))
                {
                    assignmentPatternDefault = true;
                    value = new JsAssignmentExpression("=", value, ParseAssignment(), key.Line, key.Column);
                }
            }
            properties.Add(new JsObjectProperty(key.Text, value, shorthand, key.Line, key.Column, propertyKind, isPrototypeSetter, computedKey, assignmentPatternDefault));
            if (!Match(",")) break;
        }
        Consume("}", "Expected '}' after object literal.");
        return new JsObjectExpression(properties, start.Line, start.Column);
    }

    private List<JsExpression> ParseArgumentList()
    {
        var arguments = new List<JsExpression>();
        while (!Check(")"))
        {
            arguments.Add(Match("...")
                ? new JsSpreadExpression(ParseAssignment(), Previous.Line, Previous.Column)
                : ParseAssignment());
            if (!Match(",") || Check(")")) break;
        }
        return arguments;
    }

    private void ValidateAssignmentTarget(JsExpression expression, bool allowPattern = false)
    {
        if (expression is JsIdentifierExpression) return;
        if (expression is JsMemberExpression && !ContainsOptionalChain(expression)) return;
        if (allowPattern && expression is JsArrayExpression or JsObjectExpression) return;
        throw Error(Current, "Invalid assignment target.");
    }

    private static bool ContainsOptionalChain(JsExpression expression) => expression switch
    {
        JsMemberExpression member => member.Optional || ContainsOptionalChain(member.Object),
        JsCallExpression call => call.Optional || ContainsOptionalChain(call.Callee),
        _ => false,
    };

    private void ValidateAccessorParameters(JsClassMemberKind kind, IReadOnlyList<JsBindingPattern> parameters, JavaScriptToken token)
    {
        if (kind == JsClassMemberKind.Getter && parameters.Count != 0)
            throw Error(token, "Getter must not have parameters.");
        if (kind == JsClassMemberKind.Setter && (parameters.Count != 1 || parameters[0] is JsRestPattern))
            throw Error(token, "Setter must have exactly one non-rest parameter.");
    }

    private void ValidateAccessorParameters(JsObjectPropertyKind kind, IReadOnlyList<JsBindingPattern> parameters, JavaScriptToken token)
    {
        if (kind == JsObjectPropertyKind.Getter && parameters.Count != 0)
            throw Error(token, "Getter must not have parameters.");
        if (kind == JsObjectPropertyKind.Setter && (parameters.Count != 1 || parameters[0] is JsRestPattern))
            throw Error(token, "Setter must have exactly one non-rest parameter.");
    }

    private JsExpression ParseExpressionAndTerminator() { var result = ParseExpression(); ConsumeTerminator(); return result; }
    private bool HasLineTerminatorBeforeCurrent() => _index > 0 && Current.Line > Previous.Line;
    private bool HasLineTerminatorAfterCurrent() => _index + 1 < tokens.Count && tokens[_index + 1].Line > Current.Line;

    private void ValidateLogicalNullishMix(JavaScriptToken op, JsExpression left, JsExpression right)
    {
        if (op.Text is not ("??" or "&&" or "||")) return;
        var isNullish = op.Text == "??";
        if (IsUnparenthesizedMixedLogical(left, isNullish) || IsUnparenthesizedMixedLogical(right, isNullish))
            throw Error(op, "Cannot mix '??' with '&&' or '||' without parentheses.");
    }

    private bool IsUnparenthesizedMixedLogical(JsExpression expression, bool currentIsNullish) =>
        !_parenthesizedExpressions.Contains(expression) && expression is JsBinaryExpression binary &&
        (currentIsNullish ? binary.Operator is "&&" or "||" : binary.Operator == "??");

    private void ConsumeTerminator() { if (Check(";")) Advance(); }
    private bool AtEnd => Current.Kind == JavaScriptTokenKind.End;
    private JavaScriptToken Current => tokens[_index];
    private JavaScriptToken Previous => tokens[_index - 1];
    private JavaScriptToken Advance() => tokens[_index++];
    private bool Check(string text) => Current.Kind == JavaScriptTokenKind.Punctuation && Current.Text == text;
    private bool CheckNext(string text) => _index + 1 < tokens.Count && tokens[_index + 1].Kind == JavaScriptTokenKind.Punctuation && tokens[_index + 1].Text == text;
    private bool CheckWord(string text) => Current.Kind == JavaScriptTokenKind.Identifier && Current.Text == text;
    private bool CheckNextWord(string text) => _index + 1 < tokens.Count && tokens[_index + 1].Kind == JavaScriptTokenKind.Identifier && tokens[_index + 1].Text == text;
    private bool Match(string text) { if (!Check(text)) return false; Advance(); return true; }
    private bool MatchWord(string text) { if (!CheckWord(text)) return false; Advance(); return true; }
    private JavaScriptToken ConsumeWord(string text, string message) => MatchWord(text) ? Previous : throw Error(Current, message);
    private JavaScriptToken Consume(string text, string message) => Match(text) ? Previous : throw Error(Current, message);
    private JavaScriptToken ConsumeIdentifier(string message) => Current.Kind == JavaScriptTokenKind.Identifier ? Advance() : throw Error(Current, message);
    private JavaScriptCompilationException Error(JavaScriptToken token, string message) => new(message, fileName, token.Line, token.Column, "ECMA1002");
    private static int BinaryPrecedence(JavaScriptToken token) => token.Text switch
    {
        "||" or "??" => 1,
        "&&" => 2,
        "|" => 3,
        "^" => 4,
        "&" => 5,
        "==" or "!=" or "===" or "!==" => 6,
        "<" or ">" or "<=" or ">=" or "in" or "instanceof" => 7,
        "<<" or ">>" or ">>>" => 8,
        "+" or "-" => 9,
        "*" or "/" or "%" => 10,
        "**" => 11,
        _ => -1,
    };
}
