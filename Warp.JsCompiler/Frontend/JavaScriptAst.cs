namespace Warp.JsCompiler.Frontend;

public abstract record JsAstNode(int Line, int Column);
public sealed record JsAstProgram(IReadOnlyList<JsStatement> Body) : JsAstNode(1, 1);

public abstract record JsStatement(int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsEmptyStatement(int Line, int Column) : JsStatement(Line, Column);
public sealed record JsPrivateBrandStatement(int Line, int Column) : JsStatement(Line, Column);
public sealed record JsBlockStatement(IReadOnlyList<JsStatement> Body, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsExpressionStatement(JsExpression Expression, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsVariableStatement(string Kind, IReadOnlyList<JsVariableDeclarator> Declarations, int Line, int Column) : JsStatement(Line, Column);
public abstract record JsBindingPattern(int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsIdentifierPattern(string Name, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsArrayPattern(IReadOnlyList<JsBindingPattern?> Elements, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsObjectPattern(IReadOnlyList<JsObjectBindingProperty> Properties, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsRestPattern(JsBindingPattern Argument, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsAssignmentPattern(JsBindingPattern Left, JsExpression Right, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsAssignmentTargetPattern(JsExpression Target, int Line, int Column) : JsBindingPattern(Line, Column);
public sealed record JsObjectBindingProperty(string Key, JsBindingPattern Value, int Line, int Column,
    JsExpression? ComputedKey = null, bool IsShorthand = false) : JsAstNode(Line, Column);
public sealed record JsVariableDeclarator(string Name, JsExpression? Initializer, int Line, int Column, JsBindingPattern? Pattern = null) : JsAstNode(Line, Column);
public sealed record JsReturnStatement(JsExpression? Argument, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsThrowStatement(JsExpression Argument, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsIfStatement(JsExpression Test, JsStatement Consequent, JsStatement? Alternate, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsWhileStatement(JsExpression Test, JsStatement Body, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsDoWhileStatement(JsStatement Body, JsExpression Test, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsForStatement(JsStatement? Initializer, JsExpression? Test, JsExpression? Update, JsStatement Body, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsForInOfStatement(JsStatement? Declaration, JsExpression? Left, JsExpression Right, bool IsOf, JsStatement Body, int Line, int Column, bool IsAwait = false) : JsStatement(Line, Column);
public sealed record JsSwitchCase(JsExpression? Test, IReadOnlyList<JsStatement> Consequent, int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsSwitchStatement(JsExpression Discriminant, IReadOnlyList<JsSwitchCase> Cases, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsCatchClause(string? Binding, JsBlockStatement Body, int Line, int Column,
    JsBindingPattern? Pattern = null) : JsAstNode(Line, Column);
public sealed record JsTryStatement(JsBlockStatement Body, JsCatchClause? Handler, JsBlockStatement? Finalizer, int Line, int Column) : JsStatement(Line, Column);
public enum JsClassMemberKind { Constructor, Method, Getter, Setter, Field, StaticBlock }
public sealed record JsClassMember(string Name, IReadOnlyList<string> Parameters, JsBlockStatement Body, bool IsStatic, JsClassMemberKind Kind, int Line, int Column, bool Async = false, bool Generator = false, JsExpression? ComputedKey = null, JsExpression? Initializer = null, int DefinedArgCount = -1, IReadOnlyList<JsExpression?>? ParameterDefaults = null, IReadOnlyList<JsBindingPattern>? ParameterPatterns = null) : JsAstNode(Line, Column);
public sealed record JsClassDeclaration(string Name, JsExpression? SuperClass, IReadOnlyList<JsClassMember> Members, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsLabeledStatement(string Label, JsStatement Body, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsWithStatement(JsExpression Object, JsStatement Body, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsBreakStatement(string? Label, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsContinueStatement(string? Label, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsFunctionStatement(string Name, IReadOnlyList<string> Parameters, JsBlockStatement Body, bool Async, int Line, int Column, bool Generator = false, int DefinedArgCount = -1, IReadOnlyList<JsExpression?>? ParameterDefaults = null, bool IsNamedExpression = false, IReadOnlyList<JsBindingPattern>? ParameterPatterns = null, IReadOnlySet<string>? ProtectionTags = null) : JsStatement(Line, Column);
public enum JsImportBindingKind { Default, Named, Namespace }
public sealed record JsImportBinding(string LocalName, string ImportName, JsImportBindingKind Kind, int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsImportStatement(string Specifier, IReadOnlyList<JsImportBinding> Bindings, int Line, int Column) : JsStatement(Line, Column);
public sealed record JsExportBinding(string LocalName, string ExportName, int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsExportStatement(JsStatement? Declaration, IReadOnlyList<JsExportBinding> Bindings, bool IsDefault, int Line, int Column, string? Source = null) : JsStatement(Line, Column);
public sealed record JsExportAllStatement(string Source, int Line, int Column) : JsStatement(Line, Column);

public abstract record JsExpression(int Line, int Column) : JsAstNode(Line, Column);
public sealed record JsIdentifierExpression(string Name, int Line, int Column) : JsExpression(Line, Column);
/// <summary>A lexical class-private name, never a string property key.</summary>
public sealed record JsPrivateIdentifierExpression(string Name, int Line, int Column, bool IsFieldDefinition = false) : JsExpression(Line, Column);
/// <summary>The <c>super</c> base used by class method member access/calls.</summary>
public sealed record JsSuperExpression(int Line, int Column) : JsExpression(Line, Column);
public sealed record JsLiteralExpression(string Raw, JavaScriptTokenKind Kind, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsUnaryExpression(string Operator, JsExpression Argument, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsUpdateExpression(string Operator, JsExpression Argument, bool Prefix, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsBinaryExpression(string Operator, JsExpression Left, JsExpression Right, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsAssignmentExpression(string Operator, JsExpression Left, JsExpression Right, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsConditionalExpression(JsExpression Test, JsExpression Consequent, JsExpression Alternate, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsMemberExpression(JsExpression Object, JsExpression Property, bool Computed, int Line, int Column, bool Optional = false) : JsExpression(Line, Column);
public sealed record JsCallExpression(JsExpression Callee, IReadOnlyList<JsExpression> Arguments, int Line, int Column,
    bool Optional = false, bool DirectOptional = false) : JsExpression(Line, Column);
public sealed record JsFunctionExpression(string? Name, IReadOnlyList<string> Parameters, JsBlockStatement Body, bool Async, bool Arrow, int Line, int Column, bool Generator = false, int DefinedArgCount = -1, IReadOnlyList<JsExpression?>? ParameterDefaults = null, bool IsNamedExpression = false, IReadOnlyList<JsBindingPattern>? ParameterPatterns = null, IReadOnlySet<string>? ProtectionTags = null) : JsExpression(Line, Column);
public sealed record JsClassExpression(string? Name, JsExpression? SuperClass, IReadOnlyList<JsClassMember> Members, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsNewExpression(JsExpression Callee, IReadOnlyList<JsExpression> Arguments, int Line, int Column) : JsExpression(Line, Column);
/// <summary>The lexical <c>new.target</c> meta-property.</summary>
public sealed record JsNewTargetExpression(int Line, int Column) : JsExpression(Line, Column);
public sealed record JsImportMetaExpression(int Line, int Column) : JsExpression(Line, Column);
public sealed record JsDynamicImportExpression(JsExpression Specifier, int Line, int Column) : JsExpression(Line, Column);
/// <summary>A tagged template retains both cooked and raw string segments.</summary>
public sealed record JsTaggedTemplateExpression(JsExpression Tag, IReadOnlyList<string> Cooked,
    IReadOnlyList<string> Raw, IReadOnlyList<JsExpression> Substitutions, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsSpreadExpression(JsExpression Argument, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsSequenceExpression(IReadOnlyList<JsExpression> Expressions, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsYieldExpression(JsExpression? Argument, bool Delegate, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsAwaitExpression(JsExpression Argument, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsArrayExpression(IReadOnlyList<JsExpression?> Elements, int Line, int Column) : JsExpression(Line, Column);
public sealed record JsObjectExpression(IReadOnlyList<JsObjectProperty> Properties, int Line, int Column) : JsExpression(Line, Column);
public enum JsObjectPropertyKind { Value, Method, Getter, Setter }
public sealed record JsObjectProperty(string Key, JsExpression Value, bool Shorthand, int Line, int Column,
    JsObjectPropertyKind Kind = JsObjectPropertyKind.Value, bool IsPrototypeSetter = false, JsExpression? ComputedKey = null,
    bool IsAssignmentPatternDefault = false) : JsAstNode(Line, Column);
