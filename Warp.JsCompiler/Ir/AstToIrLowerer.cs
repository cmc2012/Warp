#nullable disable warnings

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;

namespace Warp.JsCompiler.Ir;

internal sealed class AstToIrLowerer
{
	private sealed class ScopeBuilder(IrScopeId id, IrScopeId? parent)
	{
		internal IrScopeId Id { get; } = id;

		internal IrScopeId? Parent { get; } = parent;

		internal List<IrBindingId> Bindings { get; } = new List<IrBindingId>();
	}

	private sealed record StatementTargets(IrBlockId Break, IrBlockId? Continue, int FinallyDepth, string? Label = null, StatementTargets? Parent = null, int CleanupDepth = 0, IReadOnlyList<ExitScope>? ExitScopes = null, IReadOnlyList<ExitScope>? IteratorExitScopes = null);

	private readonly record struct ExitScope(IrScopeId Scope, int FinallyDepthAtEntry);

	private sealed record FinallyContext(IrBlockId Block, int BaseCleanupDepth, int IteratorDepth);

	private abstract record OptionalChainSegment(bool IsOptional, JsExpression Node);

	private sealed record OptionalMemberSegment(JsMemberExpression Member) : OptionalChainSegment(Member.Optional, Member);

	private sealed record OptionalCallSegment(JsCallExpression Call) : OptionalChainSegment(Call.DirectOptional, Call);

	private readonly DestructuringCfgBuilder _destructuringCfg;

	private readonly AssignmentDestructuringCfgBuilder _assignmentDestructuringCfg;

	private readonly List<FinallyContext> _activeFinallyBlocks = new List<FinallyContext>();

	private readonly List<JsForInOfStatement> _activeIteratorLoops = new List<JsForInOfStatement>();

	private int _activeCatchReturnOffsets;

	private int _activeCleanupDepth;

	private readonly IrModule _module = new IrModule();

	private readonly Dictionary<IrFunctionId, List<ScopeBuilder>> _scopeBuilders = new Dictionary<IrFunctionId, List<ScopeBuilder>>();

	private int _nextFunction;

	internal AstToIrLowerer()
	{
		_destructuringCfg = new DestructuringCfgBuilder(this);
		_assignmentDestructuringCfg = new AssignmentDestructuringCfgBuilder(this);
	}

	internal IrModule Run(JsAstProgram program, JavaScriptSourceKind kind = JavaScriptSourceKind.Script)
	{
		ArgumentNullException.ThrowIfNull(program, "program");
		BuildFunction(new IrFunctionId(_nextFunction++), null, Array.Empty<string>(), program.Body, (kind == JavaScriptSourceKind.Module) ? IrFunctionForm.Module : IrFunctionForm.Script);
		IrVerifier.Verify(_module);
		return _module;
	}

	private IrFunction BuildFunction(IrFunctionId id, string? name, IReadOnlyList<string> parameters, IReadOnlyList<JsStatement> statements, IrFunctionForm form, bool async = false, bool generator = false, int definedArgumentCount = -1, IReadOnlyList<JsExpression?>? parameterDefaults = null, IReadOnlyList<JsBindingPattern>? parameterPatterns = null, IrFunctionId? parentFunction = null, IrScopeId? parentScope = null, IrConstantId? parentConstant = null, SourceLocation declarationLocation = default(SourceLocation), bool hasFunctionNameBinding = false, Func<IrFunction, List<ScopeBuilder>, IrBlock, IrScopeId, IrBlock>? bodyEmitter = null, IReadOnlySet<string>? protectionTags = null)
	{
		FinallyContext[] collection = _activeFinallyBlocks.ToArray();
		JsForInOfStatement[] collection2 = _activeIteratorLoops.ToArray();
		int activeCleanupDepth = _activeCleanupDepth;
		int activeCatchReturnOffsets = _activeCatchReturnOffsets;
		_activeFinallyBlocks.Clear();
		_activeIteratorLoops.Clear();
		_activeCleanupDepth = 0;
		_activeCatchReturnOffsets = 0;
		IrScopeId ecmaScopeId = new IrScopeId(0);
		IrScopeId bodyScope = new IrScopeId(1);
		IrScopeId ecmaScopeId2 = new IrScopeId(2);
		bool flag = (parameterDefaults != null && parameterDefaults.Any((JsExpression value) => (object)value != null)) || (parameterPatterns?.Any(PatternHasInitializer) ?? false);
		object obj;
		if (parentFunction.HasValue)
		{
			IrFunctionId parent = parentFunction.GetValueOrDefault();
			obj = _module.Functions.Single((IrFunction candidate) => candidate.Id == parent).Options;
		}
		else
		{
			obj = null;
		}
		IrFunctionOptions ecmaIrFunctionOptions = (IrFunctionOptions)obj;
		bool flag2 = ecmaIrFunctionOptions?.Strict ?? false;
		bool flag3 = form == IrFunctionForm.Arrow;
		IrFunctionKind kind = ((generator & async) ? IrFunctionKind.AsyncGenerator : (generator ? IrFunctionKind.Generator : (async ? IrFunctionKind.Async : IrFunctionKind.Normal)));
		IrFunctionForm form2 = form;
		bool flag4 = flag2;
		bool flag5 = flag4;
		if (!flag5)
		{
			bool flag6 = form - 3 <= IrFunctionForm.ClassFieldInitializer;
			flag5 = flag6;
		}
		bool strict = flag5 || HasUseStrictDirective(statements);
		bool flag7 = form <= IrFunctionForm.Expression;
		bool hasPrototype = flag7 && !async && !generator;
		bool hasSimpleParameterList = form != IrFunctionForm.ClassFieldInitializer && (parameterPatterns == null || parameterPatterns.All((JsBindingPattern pattern) => pattern is JsIdentifierPattern)) && (parameterDefaults?.All((JsExpression value) => (object)value == null) ?? true);
		bool hasParameterExpressions = flag;
		bool hasThisBinding = form != IrFunctionForm.Arrow;
		bool flag8 = ((form == IrFunctionForm.Arrow || form == IrFunctionForm.ClassFieldInitializer || form - 9 <= IrFunctionForm.Expression) ? true : false);
		bool hasArgumentsBinding = !flag8;
		bool newTargetAllowed = (flag3 ? ecmaIrFunctionOptions.NewTargetAllowed : (form != IrFunctionForm.Script && form != IrFunctionForm.Module));
		bool superCallAllowed = (flag3 ? ecmaIrFunctionOptions.SuperCallAllowed : (form == IrFunctionForm.DerivedClassConstructor));
		bool superAllowed;
		if (flag3)
		{
			superAllowed = ecmaIrFunctionOptions.SuperAllowed;
		}
		else
		{
			bool flag6 = form - 3 <= IrFunctionForm.Method;
			superAllowed = flag6;
		}
		bool argumentsAllowed = (flag3 ? ecmaIrFunctionOptions.ArgumentsAllowed : (form != IrFunctionForm.ClassFieldInitializer));
		bool flag9 = form - 3 <= IrFunctionForm.Method;
		bool hasHomeObject = flag9;
		bool isGlobalVariableEnvironment = form - 9 <= IrFunctionForm.Expression;
		IrFunction ecmaIrFunction = new IrFunction(id, name, new IrFunctionOptions(kind, form2, strict, hasPrototype, hasSimpleParameterList, hasParameterExpressions, hasThisBinding, hasArgumentsBinding, newTargetAllowed, superCallAllowed, superAllowed, argumentsAllowed, hasHomeObject, IsEval: false, isGlobalVariableEnvironment), ecmaScopeId, bodyScope, new IrBlockId(0), parentFunction, parentScope, parentConstant, checked((ushort)((definedArgumentCount < 0) ? parameters.Count : definedArgumentCount)), declarationLocation, protectionTags);
		_module.Functions.Add(ecmaIrFunction);
		List<ScopeBuilder> list = new List<ScopeBuilder>
		{
			new ScopeBuilder(ecmaScopeId, null),
			new ScopeBuilder(bodyScope, ecmaScopeId)
		};
		_scopeBuilders.Add(ecmaIrFunction.Id, list);
		if (ecmaIrFunction.Options.HasParameterExpressions)
		{
			list.Add(new ScopeBuilder(ecmaScopeId2, null));
		}
		IrBlock ecmaIrBlock = new IrBlock(ecmaIrFunction.Entry);
		ecmaIrFunction.Blocks.Add(ecmaIrBlock);
		ecmaIrFunction.NextBlockId = ecmaIrFunction.Entry.Value + 1;
		if (form == IrFunctionForm.ClassConstructor)
		{
			Emit(ecmaIrBlock, "check_ctor", new JsEmptyStatement(declarationLocation.Line, declarationLocation.Column));
			ecmaIrBlock = EmitClassFieldInitializerCall(ecmaIrFunction, ecmaIrBlock, declarationLocation);
		}
		for (int num = 0; num < parameters.Count; num++)
		{
			JsBindingPattern jsBindingPattern = parameterPatterns?[num];
			isGlobalVariableEnvironment = ((jsBindingPattern is JsArrayPattern || jsBindingPattern is JsObjectPattern) ? true : false);
			bool flag10 = isGlobalVariableEnvironment;
			if (ecmaIrFunction.Options.HasParameterExpressions && !flag10)
			{
				IEnumerable<string> enumerable;
				if ((object)jsBindingPattern != null)
				{
					enumerable = EnumerateBindings(jsBindingPattern);
				}
				else
				{
					IEnumerable<string> enumerable2 = new ReadOnlySingleElementList<string>(parameters[num]);
					enumerable = enumerable2;
				}
				foreach (string item in enumerable)
				{
					AddBinding(ecmaIrFunction, list[ecmaScopeId2.Value], item, IrBindingKind.Normal, isArgument: false, isConst: false, isLexical: true);
				}
			}
			AddBinding(ecmaIrFunction, list[ecmaScopeId.Value], flag10 ? string.Empty : parameters[num], IrBindingKind.Normal, isArgument: true);
			if (!(ecmaIrFunction.Options.HasParameterExpressions & flag10))
			{
				continue;
			}
			foreach (string item2 in EnumerateBindings(jsBindingPattern))
			{
				AddBinding(ecmaIrFunction, list[ecmaScopeId2.Value], item2, IrBindingKind.Normal, isArgument: false, isConst: false, isLexical: true);
			}
		}
		if (hasFunctionNameBinding && name != null && FunctionBodyReferencesName(statements, name))
		{
			AddBinding(ecmaIrFunction, list[ecmaScopeId.Value], name, IrBindingKind.FunctionName, isArgument: false, ecmaIrFunction.Options.Strict);
		}
		if (ecmaIrFunction.Options.HasParameterExpressions)
		{
			foreach (JsBindingPattern item3 in parameterPatterns?.Where((JsBindingPattern pattern) => (pattern is JsArrayPattern || pattern is JsObjectPattern) ? true : false) ?? Array.Empty<JsBindingPattern>())
			{
				foreach (string item4 in EnumerateBindings(item3).Reverse())
				{
					AddBinding(ecmaIrFunction, list[ecmaScopeId.Value], item4, IrBindingKind.Normal);
				}
			}
		}
		else if (parameterPatterns != null)
		{
			foreach (JsBindingPattern item5 in parameterPatterns.Where((JsBindingPattern pattern) => (pattern is JsArrayPattern || pattern is JsObjectPattern) ? true : false))
			{
				foreach (string item6 in EnumerateBindings(item5))
				{
					AddBinding(ecmaIrFunction, list[ecmaScopeId.Value], item6, IrBindingKind.Normal);
				}
			}
		}
		if (ecmaIrFunction.Options.HasParameterExpressions)
		{
			foreach (IrBindingId binding in list[ecmaScopeId2.Value].Bindings)
			{
				IrBinding ecmaIrBinding = ecmaIrFunction.Bindings[binding.Value];
				ecmaIrBlock.Instructions.Add(new IrInstruction("scope_set_uninitialized", new ReadOnlyArray<IrOperand>(new IrOperand[2]
				{
					new AtomOperand(ecmaIrBinding.Name),
					new IrScopeOperand(ecmaScopeId2)
				}), declarationLocation));
			}
		}
		if (parameterPatterns != null)
		{
			for (int num2 = 0; num2 < parameterPatterns.Count; num2++)
			{
				JsBindingPattern jsBindingPattern2 = parameterPatterns[num2];
				bool hasParameterExpressions2 = ecmaIrFunction.Options.HasParameterExpressions;
				bool flag11 = hasParameterExpressions2;
				if (flag11)
				{
					isGlobalVariableEnvironment = ((jsBindingPattern2 is JsArrayPattern || jsBindingPattern2 is JsObjectPattern) ? true : false);
					flag11 = isGlobalVariableEnvironment;
				}
				if (flag11)
				{
					Emit(ecmaIrBlock, "get_arg_slot", jsBindingPattern2, new ImmediateOperand(num2));
					JsExpression jsExpression = parameterDefaults?[num2];
					ecmaIrBlock = (((object)jsExpression == null) ? EmitParameterExpressionPattern(ecmaIrFunction, ecmaIrBlock, ecmaScopeId2, jsBindingPattern2) : EmitDefaultedParameterPattern(ecmaIrFunction, ecmaIrBlock, ecmaScopeId2, jsBindingPattern2, jsExpression));
				}
				else if (jsBindingPattern2 is JsRestPattern { Argument: JsIdentifierPattern argument })
				{
					Emit(ecmaIrBlock, "rest", argument, new ImmediateOperand(num2));
					if (ecmaIrFunction.Options.HasParameterExpressions)
					{
						Emit(ecmaIrBlock, "dup", argument);
						Emit(ecmaIrBlock, "scope_put_var_init", argument, new AtomOperand(argument.Name), new IrScopeOperand(ecmaScopeId2));
					}
					Emit(ecmaIrBlock, "put_arg_direct", argument, new ImmediateOperand(num2));
				}
				else if (jsBindingPattern2 is JsIdentifierPattern jsIdentifierPattern)
				{
					JsExpression jsExpression2 = parameterDefaults?[num2];
					if ((object)jsExpression2 != null)
					{
						Emit(ecmaIrBlock, "get_arg_direct", jsIdentifierPattern, new ImmediateOperand(num2));
						Emit(ecmaIrBlock, "dup", jsIdentifierPattern);
						Emit(ecmaIrBlock, "push_undefined", jsIdentifierPattern);
						Emit(ecmaIrBlock, "strict_eq", jsIdentifierPattern);
						IrBlock ecmaIrBlock2 = NewBlock(ecmaIrFunction);
						IrBlock ecmaIrBlock3 = NewBlock(ecmaIrFunction);
						ecmaIrBlock.Terminator = new IrBranchTerminator(ecmaIrBlock2.Id, ecmaIrBlock3.Id, Location(jsIdentifierPattern));
						Emit(ecmaIrBlock2, "drop", jsIdentifierPattern);
						ecmaIrBlock2 = EmitExpressionWithInferredName(ecmaIrFunction, ecmaIrBlock2, ecmaScopeId2, jsExpression2, jsIdentifierPattern.Name);
						Emit(ecmaIrBlock2, "dup", jsIdentifierPattern);
						Emit(ecmaIrBlock2, "put_arg_direct", jsIdentifierPattern, new ImmediateOperand(num2));
						ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(jsIdentifierPattern));
						ecmaIrBlock = ecmaIrBlock3;
					}
					else if (ecmaIrFunction.Options.HasParameterExpressions)
					{
						Emit(ecmaIrBlock, "get_arg_direct", jsIdentifierPattern, new ImmediateOperand(num2));
					}
					if (ecmaIrFunction.Options.HasParameterExpressions)
					{
						Emit(ecmaIrBlock, "scope_put_var_init", jsIdentifierPattern, new AtomOperand(jsIdentifierPattern.Name), new IrScopeOperand(ecmaScopeId2));
					}
				}
			}
		}
		if (!ecmaIrFunction.Options.HasParameterExpressions && parameterPatterns != null)
		{
			for (int num3 = 0; num3 < parameterPatterns.Count; num3++)
			{
				JsBindingPattern jsBindingPattern3 = parameterPatterns[num3];
				if ((!(jsBindingPattern3 is JsArrayPattern) && !(jsBindingPattern3 is JsObjectPattern)) || 1 == 0)
				{
					continue;
				}
				if (jsBindingPattern3 is JsObjectPattern jsObjectPattern)
				{
					IReadOnlyList<JsObjectBindingProperty> properties = jsObjectPattern.Properties;
					if (properties.All((JsObjectBindingProperty property) => (object)property != null && (object)property.ComputedKey == null && property.Value is JsIdentifierPattern))
					{
						Emit(ecmaIrBlock, "get_arg_slot", jsObjectPattern, new ImmediateOperand(num3));
						Emit(ecmaIrBlock, "to_object", jsObjectPattern);
						foreach (JsObjectBindingProperty item7 in properties)
						{
							JsIdentifierPattern jsIdentifierPattern2 = (JsIdentifierPattern)item7.Value;
							Emit(ecmaIrBlock, "dup", item7);
							Emit(ecmaIrBlock, "scope_make_direct_ref", jsIdentifierPattern2, new AtomOperand(jsIdentifierPattern2.Name), new IrScopeOperand(ecmaScopeId));
							Emit(ecmaIrBlock, "rot3l", item7);
							Emit(ecmaIrBlock, "get_field", item7, new AtomOperand(item7.Key));
							Emit(ecmaIrBlock, "put_ref_value_direct", jsIdentifierPattern2);
						}
						Emit(ecmaIrBlock, "drop", jsObjectPattern);
						continue;
					}
				}
				Emit(ecmaIrBlock, "get_arg_slot", jsBindingPattern3, new ImmediateOperand(num3));
				ecmaIrBlock = EmitParameterExpressionPattern(ecmaIrFunction, ecmaIrBlock, ecmaScopeId, jsBindingPattern3);
			}
		}
		if (ecmaIrFunction.Options.HasParameterExpressions && parameterPatterns != null)
		{
			HashSet<string> hashSet = parameterPatterns.Where((JsBindingPattern pattern) => (pattern is JsArrayPattern || pattern is JsObjectPattern) ? true : false).SelectMany(EnumerateBindings).ToHashSet<string>(StringComparer.Ordinal);
			foreach (IrBindingId binding2 in list[ecmaScopeId2.Value].Bindings)
			{
				IrBinding ecmaIrBinding2 = ecmaIrFunction.Bindings[binding2.Value];
				if (hashSet.Contains(ecmaIrBinding2.Name))
				{
					ecmaIrBlock.Instructions.Add(new IrInstruction("scope_get_var", new ReadOnlyArray<IrOperand>(new IrOperand[2]
					{
						new AtomOperand(ecmaIrBinding2.Name),
						new IrScopeOperand(ecmaScopeId2)
					}), declarationLocation));
					ecmaIrBlock.Instructions.Add(new IrInstruction("scope_put_var_init", new ReadOnlyArray<IrOperand>(new IrOperand[2]
					{
						new AtomOperand(ecmaIrBinding2.Name),
						new IrScopeOperand(ecmaScopeId)
					}), declarationLocation));
				}
			}
		}
		if (form == IrFunctionForm.DerivedClassConstructor)
		{
			ecmaIrBlock.Instructions.Add(new IrInstruction("check_ctor", Array.Empty<IrOperand>(), declarationLocation));
		}
		if (ecmaIrFunction.Options.HasParameterExpressions)
		{
			ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId2)), declarationLocation));
		}
		IrBlock ecmaIrBlock4 = ecmaIrBlock;
		int count = ecmaIrBlock4.Instructions.Count;
		IrFunctionKind kind2 = ecmaIrFunction.Options.Kind;
		if ((kind2 == IrFunctionKind.Generator || kind2 == IrFunctionKind.AsyncGenerator) ? true : false)
		{
			Emit(ecmaIrBlock, "initial_yield", new JsEmptyStatement(declarationLocation.Line, declarationLocation.Column));
		}
		ecmaIrBlock = ((bodyEmitter == null) ? VisitStatements(ecmaIrFunction, list, bodyScope, ecmaIrBlock, statements, null) : bodyEmitter(ecmaIrFunction, list, ecmaIrBlock, bodyScope));
		EnsureEvalPseudoBindings(ecmaIrFunction, list);
		isGlobalVariableEnvironment = form - 9 <= IrFunctionForm.Expression;
		if (!isGlobalVariableEnvironment && ecmaIrFunction.Bindings.Any((IrBinding binding) => binding.Scope == bodyScope && binding.IsLexical))
		{
			ecmaIrBlock4.Instructions.Insert(count, new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(bodyScope)), declarationLocation));
		}
		if ((object)ecmaIrBlock.Terminator == null && form == IrFunctionForm.DerivedClassConstructor)
		{
			Emit(ecmaIrBlock, "scope_get_var", new JsIdentifierExpression("this", declarationLocation.Line, declarationLocation.Column), new AtomOperand("this"), new IrScopeOperand(bodyScope));
			ecmaIrBlock.Terminator = new IrReturnTerminator(HasValue: true, SourceLocation.None);
		}
		else
		{
			IrBlock ecmaIrBlock5 = ecmaIrBlock;
			if ((object)ecmaIrBlock5.Terminator == null)
			{
				IrTerminator ecmaIrTerminator = (ecmaIrBlock5.Terminator = new IrReturnTerminator(HasValue: false, SourceLocation.None));
			}
		}
		foreach (ScopeBuilder item8 in list)
		{
			ecmaIrFunction.Scopes.Add(new IrScope(item8.Id, item8.Parent, item8.Bindings.ToArray()));
		}
		_activeFinallyBlocks.AddRange(collection);
		_activeIteratorLoops.AddRange(collection2);
		_activeCleanupDepth = activeCleanupDepth;
		_activeCatchReturnOffsets = activeCatchReturnOffsets;
		return ecmaIrFunction;
	}

	private void EnsureEvalPseudoBindings(IrFunction function, List<ScopeBuilder> scopes)
	{
		if (!function.Blocks.SelectMany((IrBlock block) => block.Instructions).Any((IrInstruction instruction) => instruction.Operation == "eval"))
		{
			return;
		}
		IrFunction ecmaIrFunction = FindActivationOwner(function, "this");
		if (ecmaIrFunction != null)
		{
			EnsureActivationBinding(function, "this");
			EnsureActivationBinding(function, "new.target");
			if (ecmaIrFunction.Options.Form == IrFunctionForm.DerivedClassConstructor)
			{
				EnsureActivationBinding(function, "this_active_func");
			}
			if (ecmaIrFunction.Options.HasHomeObject)
			{
				EnsureActivationBinding(function, "home_object");
			}
		}
		if (FindActivationOwner(function, "arguments") != null)
		{
			EnsureActivationBinding(function, "arguments");
		}
	}

	private void EnsureActivationBinding(IrFunction function, string name)
	{
		IrFunction ecmaIrFunction = FindActivationOwner(function, name);
		if (ecmaIrFunction != null && !ecmaIrFunction.Bindings.Any((IrBinding binding) => binding.Name == name))
		{
			List<ScopeBuilder> list = _scopeBuilders[ecmaIrFunction.Id];
			AddBinding(ecmaIrFunction, list[ecmaIrFunction.ArgumentScope.Value], name, IrBindingKind.Normal, isArgument: false, isConst: false, name == "this" && ecmaIrFunction.Options.Form == IrFunctionForm.DerivedClassConstructor);
		}
	}

	private IrFunction? FindActivationOwner(IrFunction function, string name)
	{
		IrFunction ecmaIrFunction = function;
		while (true)
		{
			if ((name == "arguments") ? ecmaIrFunction.Options.HasArgumentsBinding : ecmaIrFunction.Options.HasThisBinding)
			{
				return ecmaIrFunction;
			}
			IrFunctionId? parentFunction = ecmaIrFunction.ParentFunction;
			if (!parentFunction.HasValue)
			{
				break;
			}
			IrFunctionId parentId = parentFunction.GetValueOrDefault();
			ecmaIrFunction = _module.Functions.Single((IrFunction item) => item.Id == parentId);
		}
		return null;
	}

	private IrBlock EmitParameterExpressionPattern(IrFunction function, IrBlock block, IrScopeId scope, JsBindingPattern pattern, bool assignmentTarget = false)
	{
		if (!(pattern is JsArrayPattern jsArrayPattern))
		{
			if (pattern is JsObjectPattern jsObjectPattern)
			{
				Emit(block, "to_object", jsObjectPattern);
				bool flag = jsObjectPattern.Properties.Any((JsObjectBindingProperty property) => property.Value is JsRestPattern);
				if (flag)
				{
					Emit(block, "object", jsObjectPattern);
					Emit(block, "swap", jsObjectPattern);
				}
				foreach (JsObjectBindingProperty property in jsObjectPattern.Properties)
				{
					if (property.Value is JsRestPattern jsRestPattern)
					{
						Emit(block, "object", jsRestPattern);
						Emit(block, "copy_data_properties", jsRestPattern, new ImmediateOperand(68L));
						block = EmitParameterPatternValue(function, block, scope, jsRestPattern.Argument, assignmentTarget);
						continue;
					}
					if (assignmentTarget && !flag && (object)property.ComputedKey == null && property.Value is JsAssignmentPattern { Left: JsAssignmentTargetPattern left } jsAssignmentPattern)
					{
						JsExpression right = jsAssignmentPattern.Right;
						if (true)
						{
							block = EmitObjectAssignmentTargetPattern(function, block, scope, property, left.Target, right);
							continue;
						}
					}
					bool flag3;
					bool flag4;
					if ((object)property.ComputedKey != null)
					{
						block = VisitExpression(function, block, scope, property.ComputedKey);
						if (flag)
						{
							Emit(block, "to_propkey", property);
							Emit(block, "perm3", property);
							Emit(block, "push_null", property);
							Emit(block, "define_array_el", property);
							Emit(block, "perm3", property);
						}
						bool flag2 = !flag;
						flag3 = flag2;
						if (flag3)
						{
							JsBindingPattern value = property.Value;
							if (value is JsIdentifierPattern)
							{
								goto IL_0466;
							}
							if (value is JsAssignmentPattern jsAssignmentPattern2)
							{
								JsBindingPattern left2 = jsAssignmentPattern2.Left;
								if (left2 is JsIdentifierPattern)
								{
									goto IL_0466;
								}
							}
							flag4 = false;
							goto IL_046e;
						}
						goto IL_0472;
					}
					if (flag)
					{
						Emit(block, "swap", property);
						Emit(block, "push_null", property);
						Emit(block, "define_field", property, new AtomOperand(property.Key));
						Emit(block, "swap", property);
					}
					bool flag5 = !property.IsShorthand;
					bool flag6 = flag5;
					if (flag6)
					{
						JsBindingPattern left2 = property.Value;
						if (left2 is JsIdentifierPattern)
						{
							goto IL_0594;
						}
						if (left2 is JsAssignmentPattern jsAssignmentPattern3)
						{
							JsBindingPattern value = jsAssignmentPattern3.Left;
							if (value is JsIdentifierPattern)
							{
								goto IL_0594;
							}
						}
						flag4 = false;
						goto IL_059c;
					}
					goto IL_05a0;
					IL_0594:
					flag4 = true;
					goto IL_059c;
					IL_0466:
					flag4 = true;
					goto IL_046e;
					IL_059c:
					flag6 = flag4;
					goto IL_05a0;
					IL_05a0:
					if (flag6)
					{
						Emit(block, "dup", property);
						Emit(block, "get_field", property, new AtomOperand(property.Key));
					}
					else
					{
						Emit(block, "get_field2", property, new AtomOperand(property.Key));
					}
					block = EmitParameterPatternValue(function, block, scope, property.Value, assignmentTarget);
					continue;
					IL_0472:
					if (flag3)
					{
						Emit(block, "to_propkey2", property);
						Emit(block, "dup1", property);
						Emit(block, "get_array_el", property);
					}
					else
					{
						Emit(block, "get_array_el2", property);
					}
					block = EmitParameterPatternValue(function, block, scope, property.Value, assignmentTarget);
					continue;
					IL_046e:
					flag3 = flag4;
					goto IL_0472;
				}
				Emit(block, "drop", jsObjectPattern);
				if (flag)
				{
					Emit(block, "drop", jsObjectPattern);
				}
				return block;
			}
			return EmitParameterPatternValue(function, block, scope, pattern, assignmentTarget);
		}
		Emit(block, "for_of_start", jsArrayPattern);
		foreach (JsBindingPattern element in jsArrayPattern.Elements)
		{
			if (element is JsRestPattern jsRestPattern2)
			{
				Emit(block, "array_from", jsRestPattern2, new ImmediateOperand(0L));
				Emit(block, "push_i32", jsRestPattern2, new ImmediateOperand(0L));
				IrBlock ecmaIrBlock = NewBlock(function);
				IrBlock ecmaIrBlock2 = NewBlock(function);
				IrBlock ecmaIrBlock3 = NewBlock(function);
				block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(jsRestPattern2));
				Emit(ecmaIrBlock, "for_of_next", jsRestPattern2, new ImmediateOperand(2L));
				ecmaIrBlock.Terminator = new IrBranchTerminator(ecmaIrBlock3.Id, ecmaIrBlock2.Id, Location(jsRestPattern2));
				Emit(ecmaIrBlock2, "define_array_el", jsRestPattern2);
				Emit(ecmaIrBlock2, "inc", jsRestPattern2);
				ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(jsRestPattern2));
				Emit(ecmaIrBlock3, "drop", jsRestPattern2);
				Emit(ecmaIrBlock3, "drop", jsRestPattern2);
				block = EmitParameterPatternValue(function, ecmaIrBlock3, scope, jsRestPattern2.Argument, assignmentTarget);
				break;
			}
			Emit(block, "for_of_next", element ?? jsArrayPattern, new ImmediateOperand(0L));
			Emit(block, "drop", element ?? jsArrayPattern);
			if ((object)element == null)
			{
				Emit(block, "drop", jsArrayPattern);
			}
			else
			{
				block = EmitParameterPatternValue(function, block, scope, element, assignmentTarget);
			}
		}
		Emit(block, "iterator_close", jsArrayPattern);
		return block;
	}

	private IrBlock EmitObjectAssignmentTargetPattern(IrFunction function, IrBlock block, IrScopeId scope, JsObjectBindingProperty property, JsExpression target, JsExpression defaultValue)
	{
		Emit(block, "dup", property);
		if (!(target is JsIdentifierExpression jsIdentifierExpression))
		{
			if (!(target is JsMemberExpression { Optional: false } jsMemberExpression))
			{
				goto IL_0132;
			}
			if (!jsMemberExpression.Computed)
			{
				JsExpression property2 = jsMemberExpression.Property;
				if (!(property2 is JsIdentifierExpression))
				{
					goto IL_0132;
				}
				block = VisitExpression(function, block, scope, jsMemberExpression.Object);
				Emit(block, "swap", property);
			}
			else
			{
				JsMemberExpression jsMemberExpression2 = jsMemberExpression;
				block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
				block = VisitExpression(function, block, scope, jsMemberExpression2.Property);
				Emit(block, "to_propkey2", jsMemberExpression2);
				Emit(block, "rot3l", property);
			}
		}
		else
		{
			Emit(block, "scope_make_ref", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(scope));
			Emit(block, "rot3l", property);
		}
		Emit(block, "get_field", property, new AtomOperand(property.Key));
		Emit(block, "dup", property);
		Emit(block, "push_undefined", property);
		Emit(block, "strict_eq", property);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, Location(property));
		Emit(ecmaIrBlock, "drop", property);
		ecmaIrBlock = EmitExpressionWithInferredName(function, ecmaIrBlock, scope, defaultValue, (target is JsIdentifierExpression jsIdentifierExpression3) ? jsIdentifierExpression3.Name : null);
		ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, Location(property));
		if (!(target is JsIdentifierExpression))
		{
			if (target is JsMemberExpression jsMemberExpression3)
			{
				if (!jsMemberExpression3.Computed)
				{
					JsExpression property3 = jsMemberExpression3.Property;
					if (property3 is JsIdentifierExpression jsIdentifierExpression4)
					{
						Emit(ecmaIrBlock2, "put_field", property, new AtomOperand(jsIdentifierExpression4.Name));
					}
				}
				else
				{
					Emit(ecmaIrBlock2, "put_array_el", property);
				}
			}
		}
		else
		{
			Emit(ecmaIrBlock2, "put_ref_value", property);
		}
		return ecmaIrBlock2;
		IL_0132:
		throw Unsupported(target);
	}

	private IrBlock EmitDefaultedParameterPattern(IrFunction function, IrBlock block, IrScopeId scope, JsBindingPattern pattern, JsExpression defaultValue)
	{
		Emit(block, "dup", pattern);
		Emit(block, "push_undefined", pattern);
		Emit(block, "strict_eq", pattern);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		function.Blocks.Remove(ecmaIrBlock2);
		function.Blocks.Remove(ecmaIrBlock3);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock2.Id, ecmaIrBlock.Id, Location(pattern));
		IrBlock ecmaIrBlock4 = EmitParameterExpressionPattern(function, ecmaIrBlock, scope, pattern);
		ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(pattern));
		function.Blocks.Insert(function.Blocks.IndexOf(ecmaIrBlock4) + 1, ecmaIrBlock2);
		Emit(ecmaIrBlock2, "drop", pattern);
		ecmaIrBlock2 = VisitExpression(function, ecmaIrBlock2, scope, defaultValue);
		ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(pattern));
		function.Blocks.Insert(function.Blocks.IndexOf(ecmaIrBlock2) + 1, ecmaIrBlock3);
		return ecmaIrBlock3;
	}

	private IrBlock EmitParameterPatternValue(IrFunction function, IrBlock block, IrScopeId scope, JsBindingPattern pattern, bool assignmentTarget = false)
	{
		if (pattern is JsAssignmentPattern jsAssignmentPattern)
		{
			JsBindingPattern left = jsAssignmentPattern.Left;
			if ((left is JsObjectPattern || left is JsArrayPattern) ? true : false)
			{
				return EmitCompositeDefaultedParameterPattern(function, block, scope, jsAssignmentPattern, assignmentTarget);
			}
			Emit(block, "dup", jsAssignmentPattern);
			Emit(block, "push_undefined", jsAssignmentPattern);
			Emit(block, "strict_eq", jsAssignmentPattern);
			IrBlock ecmaIrBlock = NewBlock(function);
			IrBlock ecmaIrBlock2 = NewBlock(function);
			function.Blocks.Remove(ecmaIrBlock);
			function.Blocks.Remove(ecmaIrBlock2);
			int num = function.Blocks.IndexOf(block) + 1;
			function.Blocks.Insert(num, ecmaIrBlock);
			function.Blocks.Insert(num + 1, ecmaIrBlock2);
			block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, Location(jsAssignmentPattern));
			Emit(ecmaIrBlock, "drop", jsAssignmentPattern);
			ecmaIrBlock = EmitExpressionWithInferredName(function, ecmaIrBlock, scope, jsAssignmentPattern.Right, GetBindingName(jsAssignmentPattern.Left));
			ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, Location(jsAssignmentPattern));
			block = ecmaIrBlock2;
			pattern = jsAssignmentPattern.Left;
		}
		if (pattern is JsIdentifierPattern jsIdentifierPattern)
		{
			if (assignmentTarget)
			{
				Emit(block, "scope_make_ref", jsIdentifierPattern, new AtomOperand(jsIdentifierPattern.Name), new IrScopeOperand(scope));
				Emit(block, "put_ref_value", jsIdentifierPattern);
			}
			else
			{
				Emit(block, "scope_put_var_init", jsIdentifierPattern, new AtomOperand(jsIdentifierPattern.Name), new IrScopeOperand(scope));
			}
			return block;
		}
		if (pattern is JsAssignmentTargetPattern jsAssignmentTargetPattern)
		{
			return EmitAssignmentTarget(function, block, scope, jsAssignmentTargetPattern.Target);
		}
		return EmitParameterExpressionPattern(function, block, scope, pattern, assignmentTarget);
	}

	private IrBlock EmitCompositeDefaultedParameterPattern(IrFunction function, IrBlock block, IrScopeId scope, JsAssignmentPattern assignment, bool assignmentTarget)
	{
		Emit(block, "dup", assignment);
		Emit(block, "push_undefined", assignment);
		Emit(block, "strict_eq", assignment);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		function.Blocks.Remove(ecmaIrBlock2);
		function.Blocks.Remove(ecmaIrBlock3);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock2.Id, ecmaIrBlock.Id, Location(assignment));
		IrBlock ecmaIrBlock4 = EmitParameterExpressionPattern(function, ecmaIrBlock, scope, assignment.Left, assignmentTarget);
		ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(assignment));
		function.Blocks.Insert(function.Blocks.IndexOf(ecmaIrBlock4) + 1, ecmaIrBlock2);
		Emit(ecmaIrBlock2, "drop", assignment);
		IrBlock ecmaIrBlock5 = VisitExpression(function, ecmaIrBlock2, scope, assignment.Right);
		ecmaIrBlock5.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(assignment));
		function.Blocks.Insert(function.Blocks.IndexOf(ecmaIrBlock5) + 1, ecmaIrBlock3);
		return ecmaIrBlock3;
	}

	private IrBlock EmitAssignmentTarget(IrFunction function, IrBlock block, IrScopeId scope, JsExpression target)
	{
		if (1 == 0)
		{
		}
		IrBlock result;
		if (!(target is JsIdentifierExpression identifier))
		{
			if (!(target is JsMemberExpression { Optional: false } jsMemberExpression))
			{
				goto IL_0079;
			}
			if (!jsMemberExpression.Computed)
			{
				JsExpression property = jsMemberExpression.Property;
				if (!(property is JsIdentifierExpression property2))
				{
					goto IL_0079;
				}
				result = EmitAssignmentNamedMemberTarget(function, block, scope, jsMemberExpression, property2);
			}
			else
			{
				JsMemberExpression member = jsMemberExpression;
				result = EmitAssignmentComputedMemberTarget(function, block, scope, member);
			}
		}
		else
		{
			result = EmitAssignmentIdentifierTarget(block, scope, identifier);
		}
		if (1 == 0)
		{
		}
		return result;
		IL_0079:
		throw Unsupported(target);
	}

	private IrBlock EmitAssignmentIdentifierTarget(IrBlock block, IrScopeId scope, JsIdentifierExpression identifier)
	{
		Emit(block, "scope_make_ref", identifier, new AtomOperand(identifier.Name), new IrScopeOperand(scope));
		Emit(block, "put_ref_value", identifier);
		return block;
	}

	private IrBlock EmitAssignmentNamedMemberTarget(IrFunction function, IrBlock block, IrScopeId scope, JsMemberExpression member, JsIdentifierExpression property)
	{
		Emit(block, "swap", member);
		block = VisitExpression(function, block, scope, member.Object);
		Emit(block, "swap", member);
		Emit(block, "put_field", member, new AtomOperand(property.Name));
		return block;
	}

	private IrBlock EmitAssignmentComputedMemberTarget(IrFunction function, IrBlock block, IrScopeId scope, JsMemberExpression member)
	{
		Emit(block, "swap", member);
		block = VisitExpression(function, block, scope, member.Object);
		block = VisitExpression(function, block, scope, member.Property);
		Emit(block, "rot3l", member);
		Emit(block, "put_array_el", member);
		return block;
	}

	private static bool PatternHasInitializer(JsBindingPattern pattern)
	{
		if (1 == 0)
		{
		}
		bool result = pattern is JsAssignmentPattern || ((pattern is JsArrayPattern jsArrayPattern) ? jsArrayPattern.Elements.Any((JsBindingPattern element) => (object)element != null && PatternHasInitializer(element)) : ((pattern is JsObjectPattern jsObjectPattern) ? jsObjectPattern.Properties.Any((JsObjectBindingProperty property) => (object)property.ComputedKey != null || PatternHasInitializer(property.Value)) : (pattern is JsRestPattern jsRestPattern && PatternHasInitializer(jsRestPattern.Argument))));
		if (1 == 0)
		{
		}
		return result;
	}

	private IrBlock VisitStatements(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, IReadOnlyList<JsStatement> statements, StatementTargets? targets)
	{
		foreach (JsStatement statement in statements)
		{
			if ((object)block.Terminator != null)
			{
				block = NewBlock(function);
			}
			JsStatement jsStatement = statement;
			JsStatement jsStatement2 = jsStatement;
			if (jsStatement2 is JsEmptyStatement)
			{
				continue;
			}
			if (!(jsStatement2 is JsExpressionStatement jsExpressionStatement))
			{
				if (!(jsStatement2 is JsThrowStatement jsThrowStatement))
				{
					if (!(jsStatement2 is JsReturnStatement jsReturnStatement))
					{
						if (!(jsStatement2 is JsBreakStatement jsBreakStatement))
						{
							if (jsStatement2 is JsContinueStatement jsContinueStatement)
							{
								if ((object)targets != null)
								{
									StatementTargets statementTargets = FindContinueTarget(targets, jsContinueStatement.Label);
									if ((object)statementTargets != null)
									{
										EmitFinallyForJump(block, jsContinueStatement, statementTargets);
										block.Terminator = new IrGotoTerminator(statementTargets.Continue.Value, Location(jsContinueStatement));
										continue;
									}
								}
								throw new InvalidOperationException("continue is not inside a loop.");
							}
							if (!(jsStatement2 is JsVariableStatement jsVariableStatement))
							{
								if (!(jsStatement2 is JsBlockStatement jsBlockStatement))
								{
									if (!(jsStatement2 is JsIfStatement conditional))
									{
										if (!(jsStatement2 is JsWhileStatement loop))
										{
											if (!(jsStatement2 is JsDoWhileStatement loop2))
											{
												if (!(jsStatement2 is JsForStatement loop3))
												{
													if (!(jsStatement2 is JsForInOfStatement jsForInOfStatement))
													{
														if (jsStatement2 is JsLabeledStatement labeled)
														{
															block = VisitLabeled(function, scopes, scope, block, labeled, targets);
															continue;
														}
														if (jsStatement2 is JsSwitchStatement selection)
														{
															block = VisitSwitch(function, scopes, scope, block, selection, targets);
															continue;
														}
														if (jsStatement2 is JsTryStatement jsTryStatement)
														{
															block = (((object)jsTryStatement.Finalizer == null) ? VisitCatchOnlyTry(function, scopes, scope, block, jsTryStatement, targets) : VisitTryFinally(function, scopes, scope, block, jsTryStatement, targets));
															continue;
														}
														if (jsStatement2 is JsClassDeclaration jsClassDeclaration)
														{
															AddBinding(function, scopes[scope.Value], jsClassDeclaration.Name, IrBindingKind.Normal, isArgument: false, isConst: false, isLexical: true);
															block = EmitClass(function, scopes, scope, block, jsClassDeclaration.Name, jsClassDeclaration.SuperClass, jsClassDeclaration.Members, jsClassDeclaration, isDeclaration: true);
															continue;
														}
														if (jsStatement2 is JsFunctionStatement jsFunctionStatement)
														{
															bool flag = scope != function.BodyScope;
															bool flag2 = !flag;
															bool flag3 = flag2;
															if (flag3)
															{
																IrFunctionForm form = function.Options.Form;
																bool flag4 = form - 9 <= IrFunctionForm.Expression;
																flag3 = !flag4;
															}
															ScopeBuilder scopeBuilder = (flag3 ? scopes[function.ArgumentScope.Value] : scopes[scope.Value]);
															AddBinding(function, scopeBuilder, jsFunctionStatement.Name, (!jsFunctionStatement.Async && !jsFunctionStatement.Generator) ? IrBindingKind.FunctionDeclaration : IrBindingKind.NewFunctionDeclaration, isArgument: false, isConst: false, flag);
															IrConstantId ecmaConstantId = new IrConstantId(function.Constants.Count);
															IrFunctionId ecmaFunctionId = new IrFunctionId(_nextFunction++);
															function.Constants.Add(new IrFunctionConstant(ecmaConstantId, ecmaFunctionId));
											BuildFunction(ecmaFunctionId, jsFunctionStatement.Name, jsFunctionStatement.Parameters, jsFunctionStatement.Body.Body, IrFunctionForm.Declaration, jsFunctionStatement.Async, jsFunctionStatement.Generator, jsFunctionStatement.DefinedArgCount, jsFunctionStatement.ParameterDefaults, jsFunctionStatement.ParameterPatterns, function.Id, scope, ecmaConstantId, Location(jsFunctionStatement), protectionTags: jsFunctionStatement.ProtectionTags);
															if (flag)
															{
																block.Instructions.Add(new IrInstruction("block_function_initializer", new ReadOnlyArray<IrOperand>(new IrOperand[3]
																{
																	new AtomOperand(jsFunctionStatement.Name),
																	new IrScopeOperand(scopeBuilder.Id),
																	new IrConstantOperand(ecmaConstantId)
																}), Location(jsFunctionStatement)));
																block.Instructions.Add(new IrInstruction("fclosure", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(ecmaConstantId)), Location(jsFunctionStatement)));
																Emit(block, "drop", jsFunctionStatement);
															}
															else
															{
																block.Instructions.Add(new IrInstruction("fclosure", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(ecmaConstantId)), Location(jsFunctionStatement)));
																block.Instructions.Add(new IrInstruction("scope_put_var_init", new ReadOnlyArray<IrOperand>(new IrOperand[2]
																{
																	new AtomOperand(jsFunctionStatement.Name),
																	new IrScopeOperand(scopeBuilder.Id)
																}), Location(jsFunctionStatement)));
															}
															continue;
														}
														if (jsStatement2 is JsImportStatement jsImportStatement)
														{
															int requiredModuleIndex = RequiredModule(jsImportStatement.Specifier);
															foreach (JsImportBinding binding2 in jsImportStatement.Bindings)
															{
																IrBindingId binding = AddBinding(function, scopes[scope.Value], binding2.LocalName, IrBindingKind.Normal, isArgument: false, isConst: true, isLexical: true);
																_module.Imports.Add(new IrImport(binding, binding2.ImportName, requiredModuleIndex, binding2.Kind == JsImportBindingKind.Namespace));
															}
															continue;
														}
														if (jsStatement2 is JsExportAllStatement jsExportAllStatement)
														{
															_module.StarExports.Add(RequiredModule(jsExportAllStatement.Source));
															continue;
														}
														if (jsStatement2 is JsExportStatement jsExportStatement)
														{
															if ((object)jsExportStatement != null && jsExportStatement.IsDefault && jsExportStatement.Declaration is JsFunctionStatement jsFunctionStatement2)
															{
																string text = (string.IsNullOrEmpty(jsFunctionStatement2.Name) ? "*default*" : jsFunctionStatement2.Name);
																bool flag5 = string.IsNullOrEmpty(jsFunctionStatement2.Name);
																AddBinding(function, scopes[scope.Value], text, (!jsFunctionStatement2.Async && !jsFunctionStatement2.Generator) ? IrBindingKind.FunctionDeclaration : IrBindingKind.NewFunctionDeclaration, isArgument: false, flag5, flag5);
																IrConstantId ecmaConstantId2 = new IrConstantId(function.Constants.Count);
																IrFunctionId ecmaFunctionId2 = new IrFunctionId(_nextFunction++);
																function.Constants.Add(new IrFunctionConstant(ecmaConstantId2, ecmaFunctionId2));
												BuildFunction(ecmaFunctionId2, jsFunctionStatement2.Name, jsFunctionStatement2.Parameters, jsFunctionStatement2.Body.Body, IrFunctionForm.Declaration, jsFunctionStatement2.Async, jsFunctionStatement2.Generator, jsFunctionStatement2.DefinedArgCount, jsFunctionStatement2.ParameterDefaults, jsFunctionStatement2.ParameterPatterns, function.Id, scope, ecmaConstantId2, Location(jsFunctionStatement2), protectionTags: jsFunctionStatement2.ProtectionTags);
																Emit(block, "fclosure", jsFunctionStatement2, new IrConstantOperand(ecmaConstantId2));
																if (flag5)
																{
																	Emit(block, "set_name", jsFunctionStatement2, new AtomOperand("default"));
																}
																Emit(block, "scope_put_var_init", jsFunctionStatement2, new AtomOperand(text), new IrScopeOperand(scope));
																_module.Exports.Add(new IrLocalExport(text, "default"));
															}
															else if ((object)jsExportStatement != null && jsExportStatement.IsDefault && jsExportStatement.Declaration is JsExpressionStatement jsExpressionStatement2)
															{
																AddBinding(function, scopes[scope.Value], "*default*", IrBindingKind.Normal, isArgument: false, isConst: false, isLexical: true);
																block = ((jsExpressionStatement2.Expression is JsClassExpression { Name: null } jsClassExpression) ? EmitClass(function, scopes, scope, block, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, "default") : VisitExpression(function, block, scope, jsExpressionStatement2.Expression));
																if (jsExpressionStatement2.Expression is JsFunctionExpression { Name: null })
																{
																	Emit(block, "set_name", jsExpressionStatement2.Expression, new AtomOperand("default"));
																}
																Emit(block, "scope_put_var_init", jsExportStatement, new AtomOperand("*default*"), new IrScopeOperand(scope));
																_module.Exports.Add(new IrLocalExport("*default*", "default"));
															}
															else
															{
																JsStatement declaration = jsExportStatement.Declaration;
																if ((object)declaration != null)
																{
																	block = VisitStatement(function, scopes, scope, block, declaration, targets);
																	foreach (string item in DeclaredNames(declaration))
																	{
																		_module.Exports.Add(new IrLocalExport(item, jsExportStatement.IsDefault ? "default" : item));
																	}
																}
															}
															string source = jsExportStatement.Source;
															int num = ((source != null) ? RequiredModule(source) : (-1));
															foreach (JsExportBinding binding3 in jsExportStatement.Bindings)
															{
																if (num >= 0)
																{
																	_module.Exports.Add(new IrIndirectExport(num, binding3.LocalName, binding3.ExportName));
																}
																else
																{
																	_module.Exports.Add(new IrLocalExport(binding3.LocalName, binding3.ExportName));
																}
															}
															continue;
														}
													}
													else
													{
														if (jsForInOfStatement.IsOf)
														{
															block = VisitForOf(function, scopes, scope, block, jsForInOfStatement, targets);
															continue;
														}
														JsForInOfStatement jsForInOfStatement2 = jsForInOfStatement;
														if (!jsForInOfStatement2.IsOf)
														{
															block = VisitForIn(function, scopes, scope, block, jsForInOfStatement2, targets);
															continue;
														}
													}
													throw new NotSupportedException("Scope construction does not support " + statement.GetType().Name + " yet.");
												}
												block = VisitFor(function, scopes, scope, block, loop3, targets);
											}
											else
											{
												block = VisitDoWhile(function, scopes, scope, block, loop2, targets);
											}
										}
										else
										{
											block = VisitWhile(function, scopes, scope, block, loop, targets);
										}
									}
									else
									{
										block = VisitIf(function, scopes, scope, block, conditional, targets);
									}
								}
								else
								{
									IrScopeId scope2 = PushScope(scopes, scope);
									block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(jsBlockStatement)));
									block = VisitStatements(function, scopes, scope2, block, jsBlockStatement.Body, AddExitScope(targets, scope2));
									if ((object)block.Terminator == null)
									{
										block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(jsBlockStatement)));
									}
								}
								continue;
							}
							foreach (JsVariableDeclarator declaration2 in jsVariableStatement.Declarations)
							{
								IrScopeId scope3 = ((jsVariableStatement.Kind == "var") ? new IrScopeId(0) : scope);
								IEnumerable<string> enumerable;
								if ((object)declaration2.Pattern != null)
								{
									enumerable = EnumerateBindings(declaration2.Pattern);
								}
								else
								{
									IEnumerable<string> enumerable2 = new ReadOnlySingleElementList<string>(declaration2.Name);
									enumerable = enumerable2;
								}
								foreach (string item2 in enumerable)
								{
									AddBinding(function, scopes[scope3.Value], item2, IrBindingKind.Normal, isArgument: false, jsVariableStatement.Kind == "const", jsVariableStatement.Kind != "var");
								}
								if ((object)declaration2.Initializer != null)
								{
									object obj;
									if (!(declaration2.Pattern is JsIdentifierPattern jsIdentifierPattern) || !(declaration2.Initializer is JsClassExpression { Name: null } jsClassExpression2))
									{
										JsBindingPattern pattern = declaration2.Pattern;
										obj = (((object)pattern != null && _destructuringCfg.CanBuild(pattern)) ? _destructuringCfg.EmitDeclaration(function, block, scope3, pattern, declaration2.Initializer, declaration2) : VisitExpression(function, block, scope, declaration2.Initializer));
									}
									else
									{
										obj = EmitClass(function, scopes, scope, block, string.Empty, jsClassExpression2.SuperClass, jsClassExpression2.Members, jsClassExpression2, isDeclaration: false, jsIdentifierPattern.Name);
									}
									block = (IrBlock)obj;
									JsBindingPattern pattern2 = declaration2.Pattern;
									if ((object)pattern2 != null)
									{
										if (pattern2 is JsIdentifierPattern jsIdentifierPattern2 && IsAnonymousNameable(declaration2.Initializer) && (!(declaration2.Initializer is JsClassExpression jsClassExpression3) || jsClassExpression3.Name != null))
										{
											Emit(block, "set_name", declaration2.Initializer, new AtomOperand(jsIdentifierPattern2.Name));
										}
										if (!_destructuringCfg.CanBuild(pattern2))
										{
											block = EmitParameterExpressionPattern(function, block, scope3, pattern2);
										}
									}
									else
									{
										if (IsAnonymousNameable(declaration2.Initializer) && (!(declaration2.Initializer is JsClassExpression jsClassExpression4) || jsClassExpression4.Name != null))
										{
											Emit(block, "set_name", declaration2.Initializer, new AtomOperand(declaration2.Name));
										}
										block.Instructions.Add(new IrInstruction("scope_put_var_init", new ReadOnlyArray<IrOperand>(new IrOperand[2]
										{
											new AtomOperand(declaration2.Name),
											new IrScopeOperand(scope3)
										}), Location(declaration2)));
									}
								}
								else if (jsVariableStatement.Kind != "var" && declaration2.Pattern is JsIdentifierPattern jsIdentifierPattern3)
								{
									Emit(block, "push_undefined", declaration2);
									Emit(block, "scope_put_var_init", declaration2, new AtomOperand(jsIdentifierPattern3.Name), new IrScopeOperand(scope3));
								}
							}
						}
						else
						{
							if ((object)targets == null)
							{
								throw new InvalidOperationException("break is not inside a loop or switch.");
							}
							StatementTargets statementTargets2 = FindBreakTarget(targets, jsBreakStatement.Label);
							EmitFinallyForJump(block, jsBreakStatement, statementTargets2);
							block.Terminator = new IrGotoTerminator(statementTargets2.Break, Location(jsBreakStatement));
						}
						continue;
					}
					if ((object)jsReturnStatement.Argument != null)
					{
						block = VisitExpression(function, block, scope, jsReturnStatement.Argument);
					}
					bool hasValue = (object)jsReturnStatement.Argument != null || _activeFinallyBlocks.Count != 0 || _activeIteratorLoops.Count != 0;
					hasValue = EmitCatchReturnOffsets(block, jsReturnStatement, hasValue);
					EmitFinallyForReturn(block, jsReturnStatement, hasValue);
					if (function.Options.Form == IrFunctionForm.DerivedClassConstructor)
					{
						EnsureActivationBinding(function, "this");
						IrBlock ecmaIrBlock = NewBlock(function);
						IrBlock ecmaIrBlock2 = null;
						if (hasValue)
						{
							ecmaIrBlock2 = NewBlock(function);
							Emit(block, "check_ctor_return", jsReturnStatement);
							block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, Location(jsReturnStatement));
							Emit(ecmaIrBlock, "drop", jsReturnStatement);
							ecmaIrBlock2.Terminator = new IrReturnTerminator(HasValue: true, Location(jsReturnStatement));
						}
						else
						{
							block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(jsReturnStatement));
						}
						Emit(ecmaIrBlock, "scope_get_var", jsReturnStatement, new AtomOperand("this"), new IrScopeOperand(scope));
						ecmaIrBlock.Terminator = ((ecmaIrBlock2 != null) ? ((IrTerminator)new IrGotoTerminator(ecmaIrBlock2.Id, Location(jsReturnStatement))) : ((IrTerminator)new IrReturnTerminator(HasValue: true, Location(jsReturnStatement))));
					}
					else
					{
						block.Terminator = new IrReturnTerminator(hasValue, Location(jsReturnStatement));
					}
				}
				else
				{
					block = VisitExpression(function, block, scope, jsThrowStatement.Argument);
					block.Terminator = new IrThrowTerminator(Location(jsThrowStatement));
				}
				continue;
			}
			bool flag6 = function.Options.Form == IrFunctionForm.Script;
			if (jsExpressionStatement.Expression is JsUpdateExpression update)
			{
				block = EmitUpdate(function, block, scope, update, flag6, !flag6);
				if (flag6)
				{
					block.Instructions.Add(Instruction("set_eval_ret", jsExpressionStatement));
				}
			}
			else if (flag6)
			{
				block = VisitExpression(function, block, scope, jsExpressionStatement.Expression);
				block.Instructions.Add(Instruction("set_eval_ret", jsExpressionStatement));
			}
			else
			{
				block = VisitDiscardedExpression(function, block, scope, jsExpressionStatement.Expression);
			}
		}
		return block;
	}

	private IrBlock VisitIf(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsIfStatement conditional, StatementTargets? targets)
	{
		if (conditional.Alternate is null && conditional.Test is JsBinaryExpression { Operator: "&&" } logicalAnd)
		{
			// In a condition, QuickJS branches through an && chain directly;
			// materializing its value first adds dup/drop and changes the final
			// label layout.  Keep this narrowly scoped to an if-without-else,
			// whose false path is its lexical continuation.
			var terms = FlattenLogicalChain(logicalAnd, "&&").ToArray();
			var tests = new List<IrBlock> { block };
			for (var index = 1; index < terms.Length; index++) tests.Add(NewBlock(function));
			IrBlock consequent = NewBlock(function);
			IrBlock exit = NewBlock(function);
			for (var index = 0; index < terms.Length; index++)
			{
				var evaluated = VisitExpression(function, tests[index], scope, terms[index]);
				evaluated.Terminator = new IrBranchTerminator(
					index + 1 < tests.Count ? tests[index + 1].Id : consequent.Id,
					exit.Id, Location(terms[index]));
			}
			consequent = VisitStatement(function, scopes, scope, consequent, conditional.Consequent, targets);
			if (consequent.Terminator is null)
				consequent.Terminator = new IrGotoTerminator(exit.Id, Location(conditional.Consequent));
			return exit;
		}

		block = VisitExpression(function, block, scope, conditional.Test);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = ecmaIrBlock;
		ecmaIrBlock = VisitStatement(function, scopes, scope, ecmaIrBlock, conditional.Consequent, targets);
		IrBlock ecmaIrBlock3 = (((object)conditional.Alternate == null) ? null : NewBlock(function));
		IrBlock ecmaIrBlock4 = NewBlock(function);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock2.Id, ecmaIrBlock3?.Id ?? ecmaIrBlock4.Id, Location(conditional.Test));
		IrBlock ecmaIrBlock5 = ecmaIrBlock;
		if ((object)ecmaIrBlock5.Terminator == null)
		{
			IrTerminator ecmaIrTerminator = (ecmaIrBlock5.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(conditional.Consequent)));
		}
		if (ecmaIrBlock3 != null)
		{
			ecmaIrBlock3 = VisitStatement(function, scopes, scope, ecmaIrBlock3, conditional.Alternate, targets);
			ecmaIrBlock5 = ecmaIrBlock3;
			if ((object)ecmaIrBlock5.Terminator == null)
			{
				IrTerminator ecmaIrTerminator = (ecmaIrBlock5.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(conditional.Alternate)));
			}
			function.Blocks.Remove(ecmaIrBlock4);
			function.Blocks.Add(ecmaIrBlock4);
		}
		return ecmaIrBlock4;
	}

	private IrBlock VisitLabeled(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsLabeledStatement labeled, StatementTargets? outerTargets)
	{
		List<string> list = new List<string> { labeled.Label };
		JsStatement body;
		for (body = labeled.Body; body is JsLabeledStatement jsLabeledStatement; body = jsLabeledStatement.Body)
		{
			list.Add(jsLabeledStatement.Label);
		}
		JsStatement jsStatement = body;
		if (1 == 0)
		{
		}
		IrBlock result;
		if (!(jsStatement is JsWhileStatement loop))
		{
			if (!(jsStatement is JsDoWhileStatement loop2))
			{
				if (!(jsStatement is JsForStatement loop3))
				{
					if (!(jsStatement is JsForInOfStatement jsForInOfStatement))
					{
						goto IL_010b;
					}
					if (jsForInOfStatement.IsOf)
					{
						result = VisitForOf(function, scopes, scope, block, jsForInOfStatement, outerTargets, list);
					}
					else
					{
						JsForInOfStatement jsForInOfStatement2 = jsForInOfStatement;
						if (jsForInOfStatement2.IsOf)
						{
							goto IL_010b;
						}
						result = VisitForIn(function, scopes, scope, block, jsForInOfStatement2, outerTargets, list);
					}
				}
				else
				{
					result = VisitFor(function, scopes, scope, block, loop3, outerTargets, list);
				}
			}
			else
			{
				result = VisitDoWhile(function, scopes, scope, block, loop2, outerTargets, list);
			}
		}
		else
		{
			result = VisitWhile(function, scopes, scope, block, loop, outerTargets, list);
		}
		goto IL_011e;
		IL_011e:
		if (1 == 0)
		{
		}
		return result;
		IL_010b:
		result = VisitNonLoopLabeled(function, scopes, scope, block, body, list, outerTargets);
		goto IL_011e;
	}

	private IrBlock VisitNonLoopLabeled(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsStatement body, IReadOnlyList<string> labels, StatementTargets? outerTargets)
	{
		IrBlock ecmaIrBlock = NewBlock(function);
		block = VisitStatement(function, scopes, scope, block, body, LoopTargets(ecmaIrBlock.Id, null, labels, outerTargets));
		function.Blocks.Remove(ecmaIrBlock);
		function.Blocks.Add(ecmaIrBlock);
		IrBlock ecmaIrBlock2 = block;
		if ((object)ecmaIrBlock2.Terminator == null)
		{
			IrTerminator ecmaIrTerminator = (ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(body)));
		}
		return ecmaIrBlock;
	}

	private StatementTargets LoopTargets(IrBlockId @break, IrBlockId? @continue, IReadOnlyList<string>? labels, StatementTargets? parent, IrScopeId? exitScope = null)
	{
		int count = _activeFinallyBlocks.Count;
		int activeCleanupDepth = _activeCleanupDepth;
		IReadOnlyList<ExitScope> exitScopes;
		if (exitScope.HasValue)
		{
			IrScopeId valueOrDefault = exitScope.GetValueOrDefault();
			IReadOnlyList<ExitScope> readOnlyList = new ReadOnlySingleElementList<ExitScope>(new ExitScope(valueOrDefault, _activeFinallyBlocks.Count));
			exitScopes = readOnlyList;
		}
		else
		{
			IReadOnlyList<ExitScope> readOnlyList = Array.Empty<ExitScope>();
			exitScopes = readOnlyList;
		}
		StatementTargets statementTargets = new StatementTargets(@break, @continue, count, null, parent, activeCleanupDepth, exitScopes);
		if (labels == null)
		{
			return statementTargets;
		}
		for (int num = labels.Count - 1; num >= 0; num--)
		{
			statementTargets = new StatementTargets(@break, @continue, _activeFinallyBlocks.Count, labels[num], statementTargets, _activeCleanupDepth, statementTargets.ExitScopes);
		}
		return statementTargets;
	}

	private StatementTargets? AddExitScope(StatementTargets? targets, IrScopeId scope)
	{
		return ((object)targets == null) ? null : new StatementTargets(targets.Break, targets.Continue, targets.FinallyDepth, null, targets, targets.CleanupDepth, new ReadOnlySingleElementList<ExitScope>(new ExitScope(scope, _activeFinallyBlocks.Count)));
	}

	private StatementTargets? IteratorClosingTargets(StatementTargets? targets, IrScopeId loopScope)
	{
		if ((object)targets == null)
		{
			return null;
		}
		StatementTargets parent = IteratorClosingTargets(targets.Parent, loopScope);
		ExitScope exitScope = new ExitScope(loopScope, _activeFinallyBlocks.Count);
		StatementTargets statementTargets = targets with
		{
			Parent = parent
		};
		StatementTargets statementTargets2 = statementTargets;
		ExitScope exitScope2 = exitScope;
		IReadOnlyList<ExitScope> readOnlyList = targets.IteratorExitScopes ?? Array.Empty<ExitScope>();
		int num = 0;
		ExitScope[] array = new ExitScope[1 + readOnlyList.Count];
		array[num] = exitScope2;
		num++;
		foreach (ExitScope item in readOnlyList)
		{
			array[num] = item;
			num++;
		}
			return statementTargets with { IteratorExitScopes = new ReadOnlyArray<ExitScope>(array) };
	}

	private static StatementTargets FindBreakTarget(StatementTargets targets, string? label)
	{
		List<ExitScope> list = new List<ExitScope>();
		HashSet<IrScopeId> hashSet = new HashSet<IrScopeId>();
		StatementTargets statementTargets = targets;
		while ((object)statementTargets != null)
		{
			foreach (ExitScope item in statementTargets.ExitScopes ?? Array.Empty<ExitScope>())
			{
				if (hashSet.Add(item.Scope))
				{
					list.Add(item);
				}
			}
			if (label == null)
			{
				if (statementTargets.Parent?.Break != statementTargets.Break)
				{
					return targets with
					{
						ExitScopes = list
					};
				}
			}
			else if (statementTargets.Label == label)
			{
				return statementTargets with
				{
					ExitScopes = list
				};
			}
			statementTargets = statementTargets.Parent;
		}
		throw new InvalidOperationException("Undefined label '" + label + "'.");
	}

	private static StatementTargets? FindContinueTarget(StatementTargets targets, string? label)
	{
		List<ExitScope> list = new List<ExitScope>();
		HashSet<IrScopeId> hashSet = new HashSet<IrScopeId>();
		StatementTargets statementTargets = targets;
		while ((object)statementTargets != null)
		{
			foreach (ExitScope item in statementTargets.ExitScopes ?? Array.Empty<ExitScope>())
			{
				if (hashSet.Add(item.Scope))
				{
					list.Add(item);
				}
			}
			if (label == null)
			{
				if (!statementTargets.Continue.HasValue)
				{
					return null;
				}
				if (statementTargets.Parent?.Continue != statementTargets.Continue)
				{
					return targets with
					{
						ExitScopes = list
					};
				}
			}
			else if (statementTargets.Label == label && statementTargets.Continue.HasValue)
			{
				return statementTargets with
				{
					ExitScopes = list
				};
			}
			statementTargets = statementTargets.Parent;
		}
		throw new InvalidOperationException("Undefined label '" + label + "'.");
	}

	private void EmitFinallyForReturn(IrBlock block, JsAstNode source, bool hasValue)
	{
		if (_activeFinallyBlocks.Count == 0 && _activeIteratorLoops.Count == 0)
		{
			return;
		}
		int num = _activeIteratorLoops.Count;
		int num2 = _activeCleanupDepth;
		for (int num3 = _activeFinallyBlocks.Count - 1; num3 >= 0; num3--)
		{
			FinallyContext finallyContext = _activeFinallyBlocks[num3];
			while (num > finallyContext.IteratorDepth)
			{
				if (!hasValue)
				{
					Emit(block, "push_undefined", source);
					hasValue = true;
				}
				Emit(block, "iterator_close_return", source);
				Emit(block, "iterator_close", source);
				num--;
			}
			for (int num4 = num2; num4 > finallyContext.BaseCleanupDepth; num4--)
			{
				Emit(block, hasValue ? "nip" : "drop", source);
			}
			if (!hasValue)
			{
				Emit(block, "push_undefined", source);
				hasValue = true;
			}
			Emit(block, "gosub", source, new IrBlockOperand(finallyContext.Block));
			num2 = finallyContext.BaseCleanupDepth;
		}
		while (num > 0)
		{
			if (!hasValue)
			{
				Emit(block, "push_undefined", source);
				hasValue = true;
			}
			Emit(block, "iterator_close_return", source);
			Emit(block, "iterator_close", source);
			num--;
		}
	}

	private static IrBlock EmitAsyncIteratorCloseForReturn(IrFunction function, IrBlock block, JsAstNode source)
	{
		Emit(block, "iterator_close_return", source);
		Emit(block, "drop", source);
		Emit(block, "drop", source);
		Emit(block, "get_field2", source, new AtomOperand("return"));
		Emit(block, "dup", source);
		Emit(block, "is_undefined_or_null", source);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		function.Blocks.Remove(ecmaIrBlock);
		function.Blocks.Remove(ecmaIrBlock2);
		function.Blocks.Remove(ecmaIrBlock3);
		int index = function.Blocks.IndexOf(block) + 1;
		function.Blocks.Insert(index++, ecmaIrBlock);
		function.Blocks.Insert(index++, ecmaIrBlock2);
		function.Blocks.Insert(index, ecmaIrBlock3);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock2.Id, ecmaIrBlock.Id, Location(source));
		Emit(ecmaIrBlock, "call_method", source, new ImmediateOperand(0L));
		Emit(ecmaIrBlock, "iterator_check_object", source);
		Emit(ecmaIrBlock, "await", source);
		ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(source));
		Emit(ecmaIrBlock2, "drop", source);
		ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(source));
		Emit(ecmaIrBlock3, "drop", source);
		return ecmaIrBlock3;
	}

	private bool EmitCatchReturnOffsets(IrBlock block, JsAstNode source, bool hasValue)
	{
		for (int i = 0; i < _activeCatchReturnOffsets; i++)
		{
			Emit(block, hasValue ? "nip" : "drop", source);
			if (!hasValue)
			{
				Emit(block, "push_undefined", source);
				hasValue = true;
			}
		}
		return hasValue;
	}

	private void EmitFinallyForJump(IrBlock block, JsAstNode source, StatementTargets target)
	{
		int cleanupDepth = target.CleanupDepth;
		int num = _activeCleanupDepth;
		IReadOnlyList<ExitScope> exitScopes = target.ExitScopes ?? Array.Empty<ExitScope>();
		HashSet<IrScopeId> closedScopes = new HashSet<IrScopeId>();
		HashSet<ExitScope> closedIterators = new HashSet<ExitScope>();
		for (int num2 = _activeFinallyBlocks.Count - 1; num2 >= target.FinallyDepth; num2--)
		{
			CloseScopesNestedInside(num2);
			FinallyContext finallyContext = _activeFinallyBlocks[num2];
			for (int num3 = num; num3 > finallyContext.BaseCleanupDepth; num3--)
			{
				Emit(block, "drop", source);
			}
			Emit(block, "push_undefined", source);
			Emit(block, "gosub", source, new IrBlockOperand(finallyContext.Block));
			Emit(block, "drop", source);
			num = finallyContext.BaseCleanupDepth;
		}
		foreach (ExitScope item in exitScopes)
		{
			CloseScope(item);
		}
		for (int num4 = num; num4 > cleanupDepth; num4--)
		{
			Emit(block, "drop", source);
		}
		void CloseScope(ExitScope exitScope)
		{
			if (!closedScopes.Add(exitScope.Scope))
			{
				return;
			}
			block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(exitScope.Scope)), Location(source)));
			foreach (ExitScope item2 in target.IteratorExitScopes ?? Array.Empty<ExitScope>())
			{
				if (!(item2.Scope != exitScope.Scope) && closedIterators.Add(item2))
				{
					Emit(block, "iterator_close", source);
				}
			}
		}
		void CloseScopesNestedInside(int finallyDepth)
		{
			foreach (ExitScope item3 in exitScopes)
			{
				if (item3.FinallyDepthAtEntry > finallyDepth)
				{
					CloseScope(item3);
				}
			}
		}
	}

	private IrBlock VisitCatchOnlyTry(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsTryStatement tried, StatementTargets? targets)
	{
		if ((object)tried.Handler == null)
		{
			return VisitStatement(function, scopes, scope, block, tried.Body, targets);
		}
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		Emit(block, "catch", tried, new IrBlockOperand(ecmaIrBlock.Id));
		_activeCatchReturnOffsets++;
		block = VisitStatement(function, scopes, scope, block, tried.Body, targets);
		_activeCatchReturnOffsets--;
		function.Blocks.Remove(ecmaIrBlock);
		function.Blocks.Remove(ecmaIrBlock2);
		function.Blocks.Remove(ecmaIrBlock3);
		if ((object)block.Terminator == null)
		{
			Emit(block, "drop", tried.Body);
			block.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(tried.Body));
		}
		IrScopeId ecmaScopeId = PushScope(scopes, scope);
		function.Blocks.Add(ecmaIrBlock);
		ecmaIrBlock.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(tried.Handler)));
		if (tried.Handler.Pattern is JsIdentifierPattern jsIdentifierPattern)
		{
			AddBinding(function, scopes[ecmaScopeId.Value], jsIdentifierPattern.Name, IrBindingKind.Catch);
			Emit(ecmaIrBlock, "scope_put_var", jsIdentifierPattern, new AtomOperand(jsIdentifierPattern.Name), new IrScopeOperand(ecmaScopeId));
		}
		else if ((object)tried.Handler.Pattern != null)
		{
			ecmaIrBlock = EmitCatchPattern(function, scopes, ecmaIrBlock, ecmaScopeId, tried.Handler);
		}
		else
		{
			Emit(ecmaIrBlock, "drop", tried.Handler);
		}
		Emit(ecmaIrBlock, "catch", tried.Handler, new IrBlockOperand(ecmaIrBlock2.Id));
		_activeCatchReturnOffsets++;
		ecmaIrBlock = VisitStatement(function, scopes, ecmaScopeId, ecmaIrBlock, tried.Handler.Body, targets);
		_activeCatchReturnOffsets--;
		if ((object)ecmaIrBlock.Terminator == null)
		{
			ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(tried.Handler)));
			Emit(ecmaIrBlock, "drop", tried.Handler);
			ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(tried.Handler.Body));
		}
		function.Blocks.Add(ecmaIrBlock2);
		ecmaIrBlock2.Terminator = new IrThrowTerminator(Location(tried.Handler));
		function.Blocks.Add(ecmaIrBlock3);
		return ecmaIrBlock3;
	}

	private IrBlock EmitClass(IrFunction function, List<ScopeBuilder> scopes, IrScopeId outerScope, IrBlock block, string name, JsExpression? superClass, IReadOnlyList<JsClassMember> members, JsAstNode node, bool isDeclaration, string? inferredName = null, bool classNameComputed = false)
	{
		if (members.Any((JsClassMember member) => member.Kind == JsClassMemberKind.StaticBlock))
		{
			JsClassMember jsClassMember = members.First((JsClassMember item) => item.Kind == JsClassMemberKind.StaticBlock);
			throw new JavaScriptCompilationException("Class static blocks are not supported by this language revision.", "<source>", jsClassMember.Line, jsClassMember.Column, "ECMA1002");
		}
		JsClassMember jsClassMember2 = members.SingleOrDefault((JsClassMember member) => member.Kind == JsClassMemberKind.Constructor);
		bool flag = (object)jsClassMember2 != null;
		bool flag2 = (object)jsClassMember2 == null && (object)superClass != null;
		if ((object)jsClassMember2 == null)
		{
			jsClassMember2 = new JsClassMember("constructor", Array.Empty<string>(), new JsBlockStatement(Array.Empty<JsStatement>(), node.Line, node.Column), IsStatic: false, JsClassMemberKind.Constructor, node.Line, node.Column);
		}
		IrScopeId ecmaScopeId = PushScope(scopes, outerScope);
		bool flag3 = !string.IsNullOrEmpty(name);
		string value = (flag3 ? name : (inferredName ?? string.Empty));
		AtomOperand ecmaAtomOperand = ((flag3 || inferredName != null) ? new AtomOperand(value) : AtomOperand.EmptyString);
		if (flag3)
		{
			AddBinding(function, scopes[ecmaScopeId.Value], name, IrBindingKind.Normal, isArgument: false, isConst: true, isLexical: true);
		}
		IrScopeId ecmaScopeId2 = PushScope(scopes, ecmaScopeId);
		Dictionary<JsClassMember, string> dictionary = new Dictionary<JsClassMember, string>();
		JsClassMember[] array = members.Where((JsClassMember member) => member.Kind == JsClassMemberKind.Field).ToArray();
		JsClassMember[] fields = array.Where((JsClassMember member) => !member.IsStatic).ToArray();
		JsClassMember[] fields2 = array.Where((JsClassMember member) => member.IsStatic).ToArray();
		int num = 0;
		int num2 = 0;
		JsClassMember[] array2 = array;
		foreach (JsClassMember jsClassMember3 in array2)
		{
			if ((object)jsClassMember3.ComputedKey != null)
			{
				int value2 = (jsClassMember3.IsStatic ? num2++ : num++);
				dictionary.Add(jsClassMember3, jsClassMember3.IsStatic ? $"<static_computed_field>{value2}" : $"<computed_field>{value2}");
			}
		}
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(node)));
		if ((object)superClass == null)
		{
			Emit(block, "push_undefined", node);
		}
		else
		{
			block = VisitExpression(function, block, ecmaScopeId, superClass);
		}
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId2)), Location(node)));
		IrFunction ecmaIrFunction = null;
		IrFunction ecmaIrFunction2 = null;
		int count = block.Instructions.Count;
		block.Instructions.Add(new IrInstruction("push_const", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(new IrConstantId(0))), Location(node)));
		Emit(block, classNameComputed ? "define_class_computed" : "define_class", node, ecmaAtomOperand, new ImmediateOperand(((object)superClass != null) ? 1 : 0));
		IrConstantId? ecmaConstantId = null;
		foreach (JsClassMember member in members)
		{
			if (member.Kind == JsClassMemberKind.Constructor)
			{
				IrConstantId value3 = AddClassFunction(function, ecmaScopeId2, member, ((object)superClass == null) ? IrFunctionForm.ClassConstructor : IrFunctionForm.DerivedClassConstructor, node);
				ecmaConstantId = value3;
				continue;
			}
			if (member.Kind == JsClassMemberKind.Field)
			{
				if (member.IsStatic)
				{
					Emit(block, "swap", member);
				}
				string text = null;
				if ((object)member.ComputedKey != null)
				{
					text = dictionary[member];
					AddBinding(function, scopes[ecmaScopeId2.Value], text, IrBindingKind.Normal, isArgument: false, isConst: true, isLexical: true);
				}
				bool flag4 = member.Name.StartsWith("#", StringComparison.Ordinal);
				if (member.Name.StartsWith("#", StringComparison.Ordinal))
				{
					AddBinding(function, scopes[ecmaScopeId2.Value], member.Name, IrBindingKind.PrivateField, isArgument: false, isConst: true, isLexical: true);
					Emit(block, "private_symbol", member, new AtomOperand(member.Name));
					Emit(block, "scope_put_var_init", member, new AtomOperand(member.Name), new IrScopeOperand(ecmaScopeId2));
				}
				if (member.IsStatic)
				{
					if (ecmaIrFunction2 == null)
					{
						ecmaIrFunction2 = BuildInstanceFieldInitializer(function, ecmaScopeId2, fields2, dictionary, node, hasBrand: false);
					}
				}
				else if (ecmaIrFunction == null)
				{
					ecmaIrFunction = BuildInstanceFieldInitializer(function, ecmaScopeId2, fields, dictionary, node, hasBrand: false);
				}
				if ((object)member.ComputedKey != null)
				{
					block = VisitExpression(function, block, outerScope, member.ComputedKey);
					Emit(block, "to_propkey", member);
					Emit(block, "scope_put_var_init", member, new AtomOperand(text), new IrScopeOperand(ecmaScopeId2));
				}
				if (member.IsStatic)
				{
					Emit(block, "swap", member);
				}
				continue;
			}
			if (member.IsStatic)
			{
				Emit(block, "swap", member);
			}
			if ((object)member.ComputedKey != null)
			{
				block = VisitExpression(function, block, outerScope, member.ComputedKey);
			}
			bool flag5 = member.Name.StartsWith("#", StringComparison.Ordinal);
			bool flag6 = flag5;
			bool flag7 = flag6;
			if (flag7)
			{
				JsClassMemberKind kind = member.Kind;
				bool flag8 = (uint)(kind - 2) <= 1u;
				flag7 = flag8;
			}
			string text2 = (flag7 ? DeclarePrivateAccessor(function, scopes[ecmaScopeId2.Value], member) : null);
			if (flag5)
			{
				if (member.IsStatic)
				{
					if (ecmaIrFunction2 == null)
					{
						ecmaIrFunction2 = BuildInstanceFieldInitializer(function, ecmaScopeId2, fields2, dictionary, node, hasBrand: true);
					}
					EnableClassInitializerBrand(ecmaIrFunction2);
				}
				else
				{
					if (ecmaIrFunction == null)
					{
						ecmaIrFunction = BuildInstanceFieldInitializer(function, ecmaScopeId2, fields, dictionary, node, hasBrand: true);
					}
					EnableClassInitializerBrand(ecmaIrFunction);
				}
			}
			JsClassMemberKind kind2 = member.Kind;
			if (1 == 0)
			{
			}
			IrFunctionForm ecmaFunctionForm = kind2 switch
			{
				JsClassMemberKind.Getter => IrFunctionForm.Getter, 
				JsClassMemberKind.Setter => IrFunctionForm.Setter, 
				_ => IrFunctionForm.Method, 
			};
			if (1 == 0)
			{
			}
			IrFunctionForm form = ecmaFunctionForm;
			IrConstantId constant = AddClassFunction(function, ecmaScopeId2, member, form, member);
			if (flag5 && member.Kind == JsClassMemberKind.Setter)
			{
				AddBinding(function, scopes[ecmaScopeId2.Value], text2, IrBindingKind.PrivateSetter, isArgument: false, isConst: true, isLexical: true);
			}
			if (flag5)
			{
				_module.Functions.Single((IrFunction child) => child.ParentFunction == function.Id && child.ParentConstant == constant).RequiresHomeObject = true;
			}
			Emit(block, "fclosure", member, new IrConstantOperand(constant));
			if (flag5)
			{
				Emit(block, "set_home_object", member);
				if (text2 == null)
				{
					AddBinding(function, scopes[ecmaScopeId2.Value], member.Name, IrBindingKind.PrivateMethod, isArgument: false, isConst: true, isLexical: true);
					Emit(block, "set_name", member, new AtomOperand(member.Name));
				}
				Emit(block, "scope_put_var_init", member, new AtomOperand(text2 ?? member.Name), new IrScopeOperand(ecmaScopeId2));
				if (member.IsStatic)
				{
					Emit(block, "swap", member);
				}
				continue;
			}
			JsClassMemberKind kind3 = member.Kind;
			if (1 == 0)
			{
			}
			int num4 = kind3 switch
			{
				JsClassMemberKind.Getter => 1, 
				JsClassMemberKind.Setter => 2, 
				_ => 0, 
			};
			if (1 == 0)
			{
			}
			int num5 = num4;
			if ((object)member.ComputedKey == null)
			{
				Emit(block, "define_method", member, new AtomOperand(member.Name), new ImmediateOperand(num5));
			}
			else
			{
				Emit(block, "define_method_computed", member, new ImmediateOperand(num5));
			}
			if (member.IsStatic)
			{
				Emit(block, "swap", member);
			}
		}
		if (!flag)
		{
			IrConstantId ecmaConstantId2 = AddClassFunction(function, ecmaScopeId2, jsClassMember2, ((object)superClass == null) ? IrFunctionForm.ClassConstructor : IrFunctionForm.DerivedClassConstructor, node);
			ecmaConstantId = ecmaConstantId2;
			if (flag2)
			{
				EmitImplicitDerivedConstructor(function, ecmaConstantId2, node);
			}
		}
		IrConstantId constant2 = ecmaConstantId ?? throw new InvalidOperationException("Class constructor was not emitted.");
		block.Instructions[count] = new IrInstruction("push_const", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(constant2)), Location(node));
		IrConstantId? ecmaConstantId3 = ((ecmaIrFunction == null) ? ((IrConstantId?)null) : new IrConstantId?(LinkClassFieldInitializer(function, ecmaIrFunction)));
		IrConstantId? ecmaConstantId4 = ((ecmaIrFunction2 == null) ? ((IrConstantId?)null) : new IrConstantId?(LinkClassFieldInitializer(function, ecmaIrFunction2)));
		AddBinding(function, scopes[ecmaScopeId2.Value], "class_fields_init", IrBindingKind.Normal, isArgument: false, isConst: true, isLexical: true);
		if (!ecmaConstantId3.HasValue)
		{
			Emit(block, "push_undefined", node);
		}
		else
		{
			Emit(block, "fclosure", node, new IrConstantOperand(ecmaConstantId3.Value));
			Emit(block, "set_home_object", node);
		}
		Emit(block, "scope_put_var_init", node, new AtomOperand("class_fields_init"), new IrScopeOperand(ecmaScopeId2));
		Emit(block, "drop", node);
		if (ecmaConstantId4.HasValue)
		{
			Emit(block, "dup", node);
			Emit(block, "fclosure", node, new IrConstantOperand(ecmaConstantId4.Value));
			Emit(block, "set_home_object", node);
			Emit(block, "call_method", node, new ImmediateOperand(0L));
			Emit(block, "drop", node);
		}
		if (flag3)
		{
			Emit(block, "dup", node);
			Emit(block, "scope_put_var_init", node, new AtomOperand(name), new IrScopeOperand(ecmaScopeId2));
		}
		block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId2)), Location(node)));
		block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(node)));
		if (isDeclaration)
		{
			Emit(block, "scope_put_var_init", node, new AtomOperand(name), new IrScopeOperand(outerScope));
		}
		return block;
	}

	private void EmitImplicitDerivedConstructor(IrFunction parent, IrConstantId constant, JsAstNode node)
	{
		IrFunctionId functionId = ((IrFunctionConstant)parent.Constants.Single((IrConstant item) => item.Id == constant)).Function;
		IrFunction derived = _module.Functions.Single((IrFunction item) => item.Id == functionId);
		IrBlock ecmaIrBlock = derived.Blocks.Single((IrBlock block) => block.Id == derived.Entry);
		ecmaIrBlock.Terminator = null;
		IrInstruction ecmaIrInstruction = ecmaIrBlock.Instructions.LastOrDefault();
		if ((object)ecmaIrInstruction != null && ecmaIrInstruction.Operation == "scope_get_var")
		{
			IReadOnlyList<IrOperand> operands = ecmaIrInstruction.Operands;
			if (operands != null && operands.Count >= 1 && operands[0] is AtomOperand { Value: "this" })
			{
				ecmaIrBlock.Instructions.RemoveAt(ecmaIrBlock.Instructions.Count - 1);
			}
		}
		Emit(ecmaIrBlock, "scope_get_var", node, new AtomOperand("this_active_func"), new IrScopeOperand(derived.ArgumentScope));
		Emit(ecmaIrBlock, "get_super", node);
		Emit(ecmaIrBlock, "scope_get_var", node, new AtomOperand("new.target"), new IrScopeOperand(derived.ArgumentScope));
		Emit(ecmaIrBlock, "array_from", node, new ImmediateOperand(0L));
		Emit(ecmaIrBlock, "push_i32", node, new ImmediateOperand(0L));
		Emit(ecmaIrBlock, "scope_get_var", node, new AtomOperand("arguments"), new IrScopeOperand(derived.ArgumentScope));
		Emit(ecmaIrBlock, "append", node);
		Emit(ecmaIrBlock, "drop", node);
		Emit(ecmaIrBlock, "apply", node, new ImmediateOperand(1L));
		Emit(ecmaIrBlock, "dup", node);
		Emit(ecmaIrBlock, "scope_put_var_init", node, new AtomOperand("this"), new IrScopeOperand(derived.ArgumentScope));
		IrBlock ecmaIrBlock2 = EmitClassFieldInitializerCall(derived, ecmaIrBlock, Location(node));
		Emit(ecmaIrBlock2, "drop", node);
		Emit(ecmaIrBlock2, "scope_get_var", node, new AtomOperand("this"), new IrScopeOperand(derived.BodyScope));
		ecmaIrBlock2.Terminator = new IrReturnTerminator(HasValue: true, Location(node));
	}

	private static IrBlock EmitClassFieldInitializerCall(IrFunction function, IrBlock block, JsAstNode node)
	{
		return EmitClassFieldInitializerCall(function, block, Location(node));
	}

	private static IrBlock EmitClassFieldInitializerCall(IrFunction function, IrBlock block, SourceLocation location)
	{
		block.Instructions.Add(new IrInstruction("scope_get_var", new ReadOnlyArray<IrOperand>(new IrOperand[2]
		{
			new AtomOperand("class_fields_init"),
			new IrScopeOperand(function.BodyScope)
		}), location));
		block.Instructions.Add(new IrInstruction("dup", Array.Empty<IrOperand>(), location));
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, location);
		ecmaIrBlock.Instructions.Add(new IrInstruction("scope_get_var", new ReadOnlyArray<IrOperand>(new IrOperand[2]
		{
			new AtomOperand("this"),
			new IrScopeOperand(function.BodyScope)
		}), location));
		ecmaIrBlock.Instructions.Add(new IrInstruction("swap", Array.Empty<IrOperand>(), location));
		ecmaIrBlock.Instructions.Add(new IrInstruction("call_method", new ReadOnlySingleElementList<IrOperand>(new ImmediateOperand(0L)), location));
		ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, location);
		ecmaIrBlock2.Instructions.Add(new IrInstruction("drop", Array.Empty<IrOperand>(), location));
		return ecmaIrBlock2;
	}

	private IrConstantId AddClassFunction(IrFunction parent, IrScopeId classScope, JsClassMember member, IrFunctionForm form, JsAstNode location)
	{
		IrConstantId ecmaConstantId = new IrConstantId(parent.Constants.Count);
		IrFunctionId ecmaFunctionId = new IrFunctionId(_nextFunction++);
		parent.Constants.Add(new IrFunctionConstant(ecmaConstantId, ecmaFunctionId));
		BuildFunction(ecmaFunctionId, null, member.Parameters, member.Body.Body, form, member.Async, member.Generator, member.DefinedArgCount, member.ParameterDefaults, member.ParameterPatterns, parent.Id, classScope, ecmaConstantId, Location(location));
		return ecmaConstantId;
	}

	private static IrConstantId LinkClassFieldInitializer(IrFunction parent, IrFunction child)
	{
		IrConstantId ecmaConstantId = new IrConstantId(parent.Constants.Count);
		parent.Constants.Add(new IrFunctionConstant(ecmaConstantId, child.Id));
		child.LinkParentConstant(ecmaConstantId);
		return ecmaConstantId;
	}

	private IrFunction BuildInstanceFieldInitializer(IrFunction parent, IrScopeId classScope, IReadOnlyList<JsClassMember> fields, IReadOnlyDictionary<JsClassMember, string> computedFieldNames, JsAstNode location, bool hasBrand)
	{
		IrFunctionId ecmaFunctionId = new IrFunctionId(_nextFunction++);
		IrFunctionId id = ecmaFunctionId;
		string[] parameters = Array.Empty<string>();
		JsStatement[] statements = Array.Empty<JsStatement>();
		IrFunctionId? parentFunction = parent.Id;
		IrScopeId? parentScope = classScope;
		SourceLocation declarationLocation = Location(location);
		return BuildFunction(id, null, parameters, statements, IrFunctionForm.ClassFieldInitializer, async: false, generator: false, -1, null, null, parentFunction, parentScope, null, declarationLocation, hasFunctionNameBinding: false, delegate(IrFunction child, List<ScopeBuilder> _, IrBlock block, IrScopeId scope)
		{
			block = EmitClassInitializerBrandGuard(child, block, scope, Location(location), hasBrand);
			foreach (JsClassMember field in fields)
			{
				block = VisitExpression(child, block, scope, new JsIdentifierExpression("this", field.Line, field.Column));
				if ((object)field.ComputedKey != null)
				{
					Emit(block, "scope_get_var", field, new AtomOperand(computedFieldNames[field]), new IrScopeOperand(scope));
				}
				else if (field.Name.StartsWith("#", StringComparison.Ordinal))
				{
					Emit(block, "scope_get_var", field, new AtomOperand(field.Name), new IrScopeOperand(scope));
				}
				if ((object)field.Initializer == null)
				{
					Emit(block, "push_undefined", field);
				}
				else if (field.Initializer is JsClassExpression { Name: null } jsClassExpression)
				{
					string text = (((object)field.ComputedKey == null && !field.Name.StartsWith("#", StringComparison.Ordinal)) ? field.Name : null);
					block = EmitClass(child, _scopeBuilders[child.Id], scope, block, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, text, text == null);
				}
				else
				{
					block = VisitExpression(child, block, scope, field.Initializer);
				}
				if (field.Name.StartsWith("#", StringComparison.Ordinal))
				{
					if (field.Initializer is JsFunctionExpression)
					{
						Emit(block, "set_name_computed", field);
					}
					Emit(block, "define_private_field", field);
				}
				else if ((object)field.ComputedKey == null)
				{
					if (field.Initializer is JsFunctionExpression { IsNamedExpression: false })
					{
						Emit(block, "set_name", field, new AtomOperand(field.Name));
					}
					Emit(block, "define_field", field, new AtomOperand(field.Name));
				}
				else
				{
					if (field.Initializer is JsFunctionExpression)
					{
						Emit(block, "set_name_computed", field);
					}
					Emit(block, "define_array_el", field);
					Emit(block, "drop", field);
				}
			}
			return block;
		});
	}

	private static IrBlock EmitClassInitializerBrandGuard(IrFunction function, IrBlock block, IrScopeId scope, SourceLocation location, bool hasBrand)
	{
		block.Instructions.Add(new IrInstruction(hasBrand ? "push_true" : "push_false", Array.Empty<IrOperand>(), location));
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, location);
		ecmaIrBlock.Instructions.Add(new IrInstruction("scope_get_var", new ReadOnlyArray<IrOperand>(new IrOperand[2]
		{
			new AtomOperand("this"),
			new IrScopeOperand(scope)
		}), location));
		ecmaIrBlock.Instructions.Add(new IrInstruction("scope_get_var", new ReadOnlyArray<IrOperand>(new IrOperand[2]
		{
			new AtomOperand("home_object"),
			new IrScopeOperand(scope)
		}), location));
		ecmaIrBlock.Instructions.Add(new IrInstruction("add_brand", Array.Empty<IrOperand>(), location));
		ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, location);
		return ecmaIrBlock2;
	}

	private static void EnableClassInitializerBrand(IrFunction initializer)
	{
		IrBlock ecmaIrBlock = initializer.Blocks.Single((IrBlock block) => block.Id == initializer.Entry);
		int num = ecmaIrBlock.Instructions.FindIndex(delegate(IrInstruction instruction)
		{
			string operation = instruction.Operation;
			return (operation == "push_false" || operation == "push_true") ? true : false;
		});
		if (num < 0)
		{
			throw new InvalidOperationException("Class initializer has no brand guard.");
		}
		ecmaIrBlock.Instructions[num] = ecmaIrBlock.Instructions[num]with
		{
			Operation = "push_true"
		};
	}

	private static string DeclarePrivateAccessor(IrFunction function, ScopeBuilder scope, JsClassMember member)
	{
		IrBinding ecmaIrBinding = scope.Bindings.Select((IrBindingId id) => function.Bindings[id.Value]).FirstOrDefault((IrBinding binding) => binding.Name == member.Name);
		IrBindingKind ecmaBindingKind = ((member.Kind == JsClassMemberKind.Getter) ? IrBindingKind.PrivateGetter : IrBindingKind.PrivateSetter);
		if ((object)ecmaIrBinding == null)
		{
			AddBinding(function, scope, member.Name, ecmaBindingKind, isArgument: false, isConst: true, isLexical: true);
		}
		else
		{
			IrBindingKind kind = ecmaIrBinding.Kind;
			if (1 == 0)
			{
			}
			IrBindingKind ecmaBindingKind2;
			if (kind != IrBindingKind.PrivateGetter)
			{
				if (kind != IrBindingKind.PrivateSetter || ecmaBindingKind != IrBindingKind.PrivateGetter)
				{
					goto IL_00af;
				}
				ecmaBindingKind2 = IrBindingKind.PrivateGetterSetter;
			}
			else
			{
				if (ecmaBindingKind != IrBindingKind.PrivateSetter)
				{
					goto IL_00af;
				}
				ecmaBindingKind2 = IrBindingKind.PrivateGetterSetter;
			}
			if (1 == 0)
			{
			}
			IrBindingKind kind2 = ecmaBindingKind2;
			function.Bindings[ecmaIrBinding.Id.Value] = ecmaIrBinding with
			{
				Kind = kind2
			};
		}
		if (member.Kind == JsClassMemberKind.Getter)
		{
			return member.Name;
		}
		return member.Name + "<set>";
		IL_00af:
		throw new InvalidOperationException("Duplicate private accessor '" + member.Name + "'.");
	}

	private IrBlock VisitTryFinally(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsTryStatement tried, StatementTargets? targets)
	{
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = (((object)tried.Handler == null) ? null : NewBlock(function));
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlock ecmaIrBlock4 = NewBlock(function);
		int insertionIndex = function.Blocks.IndexOf(block) + 1;
		int nextBlockId = function.NextBlockId;
		Emit(block, "catch", tried, new IrBlockOperand(ecmaIrBlock.Id));
		int activeCleanupDepth = _activeCleanupDepth;
		_activeCleanupDepth++;
		_activeFinallyBlocks.Add(new FinallyContext(ecmaIrBlock3.Id, activeCleanupDepth, _activeIteratorLoops.Count));
		block = VisitStatement(function, scopes, scope, block, tried.Body, targets);
		_activeFinallyBlocks.RemoveAt(_activeFinallyBlocks.Count - 1);
		_activeCleanupDepth--;
		function.Blocks.Remove(ecmaIrBlock);
		if (ecmaIrBlock2 != null)
		{
			function.Blocks.Remove(ecmaIrBlock2);
		}
		function.Blocks.Remove(ecmaIrBlock3);
		function.Blocks.Remove(ecmaIrBlock4);
		insertionIndex = MoveNewBlocksTo(function, nextBlockId, insertionIndex);
		if ((object)block.Terminator == null)
		{
			IrBlock ecmaIrBlock5 = block;
			IrFunctionKind kind = function.Options.Kind;
			bool parserContinuation = kind - 2 <= IrFunctionKind.Generator;
			ecmaIrBlock5.ParserContinuation = parserContinuation;
			Emit(block, "drop", tried.Body);
			Emit(block, "push_undefined", tried.Body);
			Emit(block, "gosub", tried.Body, new IrBlockOperand(ecmaIrBlock3.Id));
			Emit(block, "drop", tried.Body);
			block.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(tried.Body));
		}
		if ((object)tried.Handler == null)
		{
			function.Blocks.Insert(insertionIndex++, ecmaIrBlock);
			Emit(ecmaIrBlock, "gosub", tried, new IrBlockOperand(ecmaIrBlock3.Id));
			ecmaIrBlock.Terminator = new IrThrowTerminator(Location(tried));
		}
		else
		{
			function.Blocks.Insert(insertionIndex++, ecmaIrBlock);
			IrScopeId ecmaScopeId = PushScope(scopes, scope);
			ecmaIrBlock.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(tried.Handler)));
			if (tried.Handler.Pattern is JsIdentifierPattern jsIdentifierPattern)
			{
				AddBinding(function, scopes[ecmaScopeId.Value], jsIdentifierPattern.Name, IrBindingKind.Catch);
				Emit(ecmaIrBlock, "scope_put_var", jsIdentifierPattern, new AtomOperand(jsIdentifierPattern.Name), new IrScopeOperand(ecmaScopeId));
			}
			else if ((object)tried.Handler.Pattern != null)
			{
				ecmaIrBlock = EmitCatchPattern(function, scopes, ecmaIrBlock, ecmaScopeId, tried.Handler);
			}
			else
			{
				Emit(ecmaIrBlock, "drop", tried.Handler);
			}
			Emit(ecmaIrBlock, "catch", tried.Handler, new IrBlockOperand(ecmaIrBlock2.Id));
			int nextBlockId2 = function.NextBlockId;
			int activeCleanupDepth2 = _activeCleanupDepth;
			_activeCleanupDepth++;
			_activeFinallyBlocks.Add(new FinallyContext(ecmaIrBlock3.Id, activeCleanupDepth2, _activeIteratorLoops.Count));
			ecmaIrBlock = VisitStatement(function, scopes, ecmaScopeId, ecmaIrBlock, tried.Handler.Body, targets);
			_activeFinallyBlocks.RemoveAt(_activeFinallyBlocks.Count - 1);
			_activeCleanupDepth--;
			insertionIndex = MoveNewBlocksTo(function, nextBlockId2, insertionIndex);
			if ((object)ecmaIrBlock.Terminator == null)
			{
				ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(tried.Handler)));
				Emit(ecmaIrBlock, "drop", tried.Handler);
				Emit(ecmaIrBlock, "push_undefined", tried.Handler);
				Emit(ecmaIrBlock, "gosub", tried.Handler, new IrBlockOperand(ecmaIrBlock3.Id));
				Emit(ecmaIrBlock, "drop", tried.Handler);
				ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(tried.Handler.Body));
			}
			function.Blocks.Insert(insertionIndex++, ecmaIrBlock2);
			Emit(ecmaIrBlock2, "gosub", tried.Handler, new IrBlockOperand(ecmaIrBlock3.Id));
			ecmaIrBlock2.Terminator = new IrThrowTerminator(Location(tried.Handler));
		}
		function.Blocks.Insert(insertionIndex++, ecmaIrBlock3);
		int nextBlockId3 = function.NextBlockId;
		ecmaIrBlock3 = VisitStatement(function, scopes, scope, ecmaIrBlock3, tried.Finalizer, targets);
		IrBlock ecmaIrBlock6 = ecmaIrBlock3;
		if ((object)ecmaIrBlock6.Terminator == null)
		{
			IrTerminator ecmaIrTerminator = (ecmaIrBlock6.Terminator = new IrFinallyReturnTerminator(Location(tried.Finalizer)));
		}
		insertionIndex = MoveNewBlocksTo(function, nextBlockId3, insertionIndex);
		function.Blocks.Insert(insertionIndex, ecmaIrBlock4);
		return ecmaIrBlock4;
	}

	private static int MoveNewBlocksTo(IrFunction function, int firstAllocatedId, int insertionIndex)
	{
		IrBlock[] array = function.Blocks.Where((IrBlock candidate) => candidate.Id.Value >= firstAllocatedId).ToArray();
		IrBlock[] array2 = array;
		foreach (IrBlock item in array2)
		{
			function.Blocks.Remove(item);
		}
		function.Blocks.InsertRange(insertionIndex, array);
		return insertionIndex + array.Length;
	}

	private IrBlock EmitCatchPattern(IrFunction function, List<ScopeBuilder> scopes, IrBlock block, IrScopeId catchScope, JsCatchClause clause)
	{
		JsBindingPattern pattern = clause.Pattern ?? throw new InvalidOperationException("Catch pattern is required.");
		foreach (string item in EnumerateBindings(pattern))
		{
			AddBinding(function, scopes[catchScope.Value], item, IrBindingKind.Catch, isArgument: false, isConst: false, isLexical: true);
		}
		return EmitParameterExpressionPattern(function, block, catchScope, pattern);
	}

	private IrBlock VisitWhile(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsWhileStatement loop, StatementTargets? outerTargets, IReadOnlyList<string>? labels = null)
	{
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlockId id = ecmaIrBlock.Id;
		block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(loop));
		IReadOnlyList<JsExpression> conditions = loop.Test is JsBinaryExpression { Operator: "&&" } logical
			? FlattenLogicalChain(logical, "&&").ToArray()
			: [loop.Test];
		var conditionBlocks = new List<IrBlock> { ecmaIrBlock };
		for (var index = 1; index < conditions.Count; index++) conditionBlocks.Add(NewBlock(function));
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		for (var index = 0; index < conditions.Count; index++)
		{
			var condition = VisitExpression(function, conditionBlocks[index], scope, conditions[index]);
			condition.Terminator = new IrBranchTerminator(
				index + 1 < conditionBlocks.Count ? conditionBlocks[index + 1].Id : ecmaIrBlock2.Id,
				ecmaIrBlock3.Id, Location(conditions[index]));
		}
		ecmaIrBlock2 = VisitStatement(function, scopes, scope, ecmaIrBlock2, loop.Body, LoopTargets(ecmaIrBlock3.Id, id, labels, outerTargets));
		IrBlock ecmaIrBlock4 = ecmaIrBlock2;
		if ((object)ecmaIrBlock4.Terminator == null)
		{
			IrTerminator ecmaIrTerminator = (ecmaIrBlock4.Terminator = new IrGotoTerminator(id, Location(loop.Body)));
		}
		function.Blocks.Remove(ecmaIrBlock3);
		function.Blocks.Add(ecmaIrBlock3);
		return ecmaIrBlock3;
	}

	private IrBlock VisitDoWhile(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsDoWhileStatement loop, StatementTargets? outerTargets, IReadOnlyList<string>? labels = null)
	{
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlockId id = ecmaIrBlock.Id;
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(loop));
		ecmaIrBlock = VisitStatement(function, scopes, scope, ecmaIrBlock, loop.Body, LoopTargets(ecmaIrBlock3.Id, ecmaIrBlock2.Id, labels, outerTargets));
		IrBlock ecmaIrBlock4 = ecmaIrBlock;
		if ((object)ecmaIrBlock4.Terminator == null)
		{
			IrTerminator ecmaIrTerminator = (ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, Location(loop.Body)));
		}
		function.Blocks.Remove(ecmaIrBlock2);
		function.Blocks.Remove(ecmaIrBlock3);
		function.Blocks.Add(ecmaIrBlock2);
		function.Blocks.Add(ecmaIrBlock3);
		ecmaIrBlock2 = VisitExpression(function, ecmaIrBlock2, scope, loop.Test);
		ecmaIrBlock2.Terminator = new IrBranchTerminator(id, ecmaIrBlock3.Id, Location(loop.Test));
		return ecmaIrBlock3;
	}

	private IrBlock VisitFor(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsForStatement loop, StatementTargets? outerTargets, IReadOnlyList<string>? labels = null)
	{
		IrScopeId scope2 = PushScope(scopes, scope);
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
		if ((object)loop.Initializer != null)
		{
			block = VisitStatement(function, scopes, scope2, block, loop.Initializer, null);
			if ((object)block.Terminator == null)
			{
				block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
			}
		}
		if ((object)loop.Test == null && (object)loop.Update == null)
		{
			IrBlock ecmaIrBlock = NewBlock(function);
			IrBlockId id = ecmaIrBlock.Id;
			IrBlock ecmaIrBlock2 = NewBlock(function);
			block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(loop));
			ecmaIrBlock = VisitStatement(function, scopes, scope2, ecmaIrBlock, loop.Body, LoopTargets(ecmaIrBlock2.Id, id, labels, outerTargets));
			if ((object)ecmaIrBlock.Terminator == null)
			{
				ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
				ecmaIrBlock.Terminator = new IrGotoTerminator(id, Location(loop.Body));
			}
			ecmaIrBlock2.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
			return ecmaIrBlock2;
		}
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlockId id2 = ecmaIrBlock3.Id;
		block.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(loop));
		IReadOnlyList<JsExpression> conditions = loop.Test is JsBinaryExpression { Operator: "&&" } logical
			? FlattenLogicalChain(logical, "&&").ToArray()
			: loop.Test is null ? [] : [loop.Test];
		var conditionBlocks = new List<IrBlock> { ecmaIrBlock3 };
		for (var index = 1; index < conditions.Count; index++) conditionBlocks.Add(NewBlock(function));
		IrBlock ecmaIrBlock4 = NewBlock(function);
		IrBlock ecmaIrBlock5 = NewBlock(function);
		IrBlock ecmaIrBlock6 = NewBlock(function);
		function.Blocks.Remove(ecmaIrBlock5);
		function.Blocks.Remove(ecmaIrBlock6);
		if (conditions.Count > 0)
		{
			for (var index = 0; index < conditions.Count; index++)
			{
				var condition = VisitExpression(function, conditionBlocks[index], scope2, conditions[index]);
				condition.Terminator = new IrBranchTerminator(
					index + 1 < conditionBlocks.Count ? conditionBlocks[index + 1].Id : ecmaIrBlock4.Id,
					ecmaIrBlock6.Id, Location(conditions[index]));
			}
		}
		else
		{
			ecmaIrBlock3.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(loop));
		}
		ecmaIrBlock4 = VisitStatement(function, scopes, scope2, ecmaIrBlock4, loop.Body, LoopTargets(ecmaIrBlock6.Id, ecmaIrBlock5.Id, labels, outerTargets));
		if ((object)ecmaIrBlock4.Terminator == null)
		{
			ecmaIrBlock4.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
			ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock5.Id, Location(loop.Body));
		}
		if ((object)loop.Update != null)
		{
			ecmaIrBlock5 = ((!(loop.Update is JsUpdateExpression update)) ? VisitDiscardedExpression(function, ecmaIrBlock5, scope2, loop.Update) : EmitUpdate(function, ecmaIrBlock5, scope2, update, valueUsed: false));
		}
		ecmaIrBlock5.Terminator = new IrGotoTerminator(id2, Location(loop));
		ecmaIrBlock6.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(scope2)), Location(loop)));
		function.Blocks.Add(ecmaIrBlock5);
		function.Blocks.Add(ecmaIrBlock6);
		return ecmaIrBlock6;
	}

	private IrBlock VisitDiscardedExpression(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression)
	{
		if (!(expression is JsUpdateExpression update))
		{
			if (!(expression is JsSequenceExpression jsSequenceExpression))
			{
				if (expression is JsLiteralExpression)
				{
					return block;
				}
				block = VisitExpression(function, block, scope, expression);
				Emit(block, "drop", expression);
				return block;
			}
			foreach (JsExpression expression2 in jsSequenceExpression.Expressions)
			{
				block = VisitDiscardedExpression(function, block, scope, expression2);
			}
			return block;
		}
		return EmitUpdate(function, block, scope, update, valueUsed: false);
	}

	private IrBlock VisitForOf(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsForInOfStatement loop, StatementTargets? outerTargets, IReadOnlyList<string>? labels = null)
	{
		IrScopeId ecmaScopeId = PushScope(scopes, scope);
		string text = null;
		JsBindingPattern jsBindingPattern = null;
		if (loop.Declaration is JsVariableStatement jsVariableStatement)
		{
			IReadOnlyList<JsVariableDeclarator> declarations = jsVariableStatement.Declarations;
			if (declarations != null && declarations.Count == 1)
			{
				JsVariableDeclarator jsVariableDeclarator = declarations[0];
				if ((object)jsVariableDeclarator != null)
				{
					JsBindingPattern pattern = jsVariableDeclarator.Pattern;
					if ((object)pattern != null)
					{
						jsBindingPattern = pattern;
						if (pattern is JsIdentifierPattern jsIdentifierPattern)
						{
							text = jsIdentifierPattern.Name;
						}
						foreach (string item in EnumerateBindings(pattern))
						{
							ScopeBuilder scope2 = scopes[ecmaScopeId.Value];
							string name = item;
							bool isConst = jsVariableStatement.Kind == "const";
							string kind = jsVariableStatement.Kind;
							bool isLexical = ((kind == "const" || kind == "let") ? true : false);
							AddBinding(function, scope2, name, IrBindingKind.Normal, isArgument: false, isConst, isLexical);
						}
						goto IL_0144;
					}
				}
			}
		}
		if ((object)loop.Declaration != null || !(loop.Left is JsIdentifierExpression))
		{
			throw Unsupported(loop);
		}
		goto IL_0144;
		IL_0144:
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		block = VisitExpression(function, block, scope, loop.Right);
		block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		Emit(block, loop.IsAwait ? "for_await_of_start" : "for_of_start", loop);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = ecmaIrBlock;
		if (text != null)
		{
			Emit(ecmaIrBlock, "scope_put_var_init", loop, new AtomOperand(text), new IrScopeOperand(ecmaScopeId));
		}
		else if ((object)jsBindingPattern != null)
		{
			ecmaIrBlock = EmitParameterExpressionPattern(function, ecmaIrBlock, ecmaScopeId, jsBindingPattern);
		}
		else
		{
			JsIdentifierExpression jsIdentifierExpression = (JsIdentifierExpression)loop.Left;
			Emit(ecmaIrBlock, "scope_make_ref", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(ecmaScopeId));
			Emit(ecmaIrBlock, "put_ref_value", jsIdentifierExpression);
		}
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlock ecmaIrBlock4 = NewBlock(function);
		IrBlock ecmaIrBlock5 = NewBlock(function);
		StatementTargets parent = IteratorClosingTargets(outerTargets, ecmaScopeId);
		_activeIteratorLoops.Add(loop);
		ecmaIrBlock = VisitStatement(function, scopes, ecmaScopeId, ecmaIrBlock, loop.Body, LoopTargets(ecmaIrBlock5.Id, ecmaIrBlock3.Id, labels, parent, ecmaScopeId));
		_activeIteratorLoops.RemoveAt(_activeIteratorLoops.Count - 1);
		block.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(loop));
		if ((object)ecmaIrBlock.Terminator == null)
		{
			ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
			ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(loop.Body));
		}
		if (loop.IsAwait)
		{
			Emit(ecmaIrBlock3, "dup3", loop);
			Emit(ecmaIrBlock3, "drop", loop);
			Emit(ecmaIrBlock3, "call_method", loop, new ImmediateOperand(0L));
			Emit(ecmaIrBlock3, "await", loop);
			Emit(ecmaIrBlock3, "iterator_get_value_done", loop);
		}
		else
		{
			Emit(ecmaIrBlock3, "for_of_next", loop, new ImmediateOperand(0L));
		}
		ecmaIrBlock3.Terminator = new IrBranchTerminator(ecmaIrBlock4.Id, ecmaIrBlock2.Id, Location(loop));
		Emit(ecmaIrBlock4, "drop", loop);
		ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock5.Id, Location(loop));
		Emit(ecmaIrBlock5, "iterator_close", loop);
		ecmaIrBlock5.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		function.Blocks.Remove(ecmaIrBlock3);
		function.Blocks.Remove(ecmaIrBlock4);
		function.Blocks.Remove(ecmaIrBlock5);
		function.Blocks.Add(ecmaIrBlock3);
		function.Blocks.Add(ecmaIrBlock4);
		function.Blocks.Add(ecmaIrBlock5);
		return ecmaIrBlock5;
	}

	private IrBlock VisitForIn(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsForInOfStatement loop, StatementTargets? outerTargets, IReadOnlyList<string>? labels = null)
	{
		IrScopeId ecmaScopeId = PushScope(scopes, scope);
		string text = null;
		IrScopeId scope2 = ecmaScopeId;
		bool flag = false;
		if (loop.Declaration is JsVariableStatement jsVariableStatement)
		{
			IReadOnlyList<JsVariableDeclarator> declarations = jsVariableStatement.Declarations;
			if (declarations != null && declarations.Count == 1)
			{
				JsVariableDeclarator jsVariableDeclarator = declarations[0];
				if ((object)jsVariableDeclarator != null && jsVariableDeclarator.Pattern is JsIdentifierPattern jsIdentifierPattern)
				{
					text = jsIdentifierPattern.Name;
					flag = jsVariableStatement.Kind == "var";
					scope2 = (flag ? new IrScopeId(0) : ecmaScopeId);
					ScopeBuilder scope3 = scopes[scope2.Value];
					string name = text;
					bool isConst = jsVariableStatement.Kind == "const";
					string kind = jsVariableStatement.Kind;
					bool isLexical = ((kind == "const" || kind == "let") ? true : false);
					AddBinding(function, scope3, name, IrBindingKind.Normal, isArgument: false, isConst, isLexical);
					goto IL_0125;
				}
			}
		}
		if ((object)loop.Declaration != null || !(loop.Left is JsIdentifierExpression))
		{
			throw Unsupported(loop);
		}
		goto IL_0125;
		IL_0125:
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		block = VisitExpression(function, block, scope, loop.Right);
		block.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		Emit(block, "for_in_start", loop);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = ecmaIrBlock;
		if (text != null)
		{
			Emit(ecmaIrBlock, flag ? "scope_put_var" : "scope_put_var_init", loop, new AtomOperand(text), new IrScopeOperand(scope2));
		}
		else
		{
			JsIdentifierExpression jsIdentifierExpression = (JsIdentifierExpression)loop.Left;
			Emit(ecmaIrBlock, "scope_make_ref", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(scope));
			Emit(ecmaIrBlock, "put_ref_value", jsIdentifierExpression);
		}
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlock ecmaIrBlock4 = NewBlock(function);
		IrBlock ecmaIrBlock5 = NewBlock(function);
		ecmaIrBlock = VisitStatement(function, scopes, ecmaScopeId, ecmaIrBlock, loop.Body, LoopTargets(ecmaIrBlock5.Id, ecmaIrBlock3.Id, labels, outerTargets, ecmaScopeId));
		block.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(loop));
		if ((object)ecmaIrBlock.Terminator == null)
		{
			ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
			ecmaIrBlock.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(loop.Body));
		}
		Emit(ecmaIrBlock3, "for_in_next", loop);
		ecmaIrBlock3.Terminator = new IrBranchTerminator(ecmaIrBlock4.Id, ecmaIrBlock2.Id, Location(loop));
		Emit(ecmaIrBlock4, "drop", loop);
		ecmaIrBlock4.Terminator = new IrGotoTerminator(ecmaIrBlock5.Id, Location(loop));
		Emit(ecmaIrBlock5, "for_in_end", loop);
		ecmaIrBlock5.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(loop)));
		function.Blocks.Remove(ecmaIrBlock3);
		function.Blocks.Remove(ecmaIrBlock4);
		function.Blocks.Remove(ecmaIrBlock5);
		function.Blocks.Add(ecmaIrBlock3);
		function.Blocks.Add(ecmaIrBlock4);
		function.Blocks.Add(ecmaIrBlock5);
		return ecmaIrBlock5;
	}

	private IrBlock VisitStatement(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsStatement statement, StatementTargets? targets)
	{
		return VisitStatements(function, scopes, scope, block, new ReadOnlySingleElementList<JsStatement>(statement), targets);
	}

	private IrBlock VisitSwitch(IrFunction function, List<ScopeBuilder> scopes, IrScopeId scope, IrBlock block, JsSwitchStatement selection, StatementTargets? outerTargets)
	{
		block = VisitExpression(function, block, scope, selection.Discriminant);
		IrScopeId ecmaScopeId = PushScope(scopes, scope);
		block.Instructions.Add(new IrInstruction("enter_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(selection)));
		if (selection.Cases.All((JsSwitchCase @case) => (object)@case.Test != null))
		{
			return VisitSwitchWithoutDefault(function, scopes, ecmaScopeId, block, selection, outerTargets);
		}
		IrBlock[] array = selection.Cases.Select((JsSwitchCase _) => NewBlock(function)).ToArray();
		IrBlock?[] tests = new IrBlock[selection.Cases.Count];
		if ((object)selection.Cases[0].Test != null)
		{
			tests[0] = block;
		}
		for (int num = 1; num < selection.Cases.Count; num++)
		{
			if ((object)selection.Cases[num].Test != null)
			{
				tests[num] = NewBlock(function);
			}
		}
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock[] array2 = array;
		foreach (IrBlock item in array2)
		{
			function.Blocks.Remove(item);
		}
		foreach (IrBlock item2 in tests.Where((IrBlock candidate) => candidate != null && candidate != block))
		{
			function.Blocks.Remove(item2);
		}
		function.Blocks.Remove(ecmaIrBlock);
		int num3 = Array.FindIndex(selection.Cases.ToArray(), (JsSwitchCase @case) => (object)@case.Test == null);
		if (tests[0] == null)
		{
			IrBlock ecmaIrBlock2 = tests.FirstOrDefault((IrBlock candidate) => candidate != null);
			block.Terminator = new IrGotoTerminator((ecmaIrBlock2 ?? array[num3]).Id, Location(selection));
		}
		for (int num4 = 0; num4 < selection.Cases.Count; num4++)
		{
			JsSwitchCase jsSwitchCase = selection.Cases[num4];
			if ((object)jsSwitchCase.Test != null)
			{
				IrBlock ecmaIrBlock3 = tests[num4] ?? throw new InvalidOperationException("Missing switch test block.");
				if (ecmaIrBlock3 != block)
				{
					function.Blocks.Add(ecmaIrBlock3);
				}
				function.Blocks.Add(array[num4]);
				Emit(ecmaIrBlock3, "dup", jsSwitchCase);
				ecmaIrBlock3 = VisitExpression(function, ecmaIrBlock3, ecmaScopeId, jsSwitchCase.Test);
				Emit(ecmaIrBlock3, "strict_eq", jsSwitchCase.Test);
				IrBlockId whenFalse = (from candidate in Enumerable.Range(num4 + 1, selection.Cases.Count - num4 - 1)
					select tests[candidate]).FirstOrDefault((IrBlock candidate) => candidate != null)?.Id ?? ((num3 >= 0) ? array[num3].Id : ecmaIrBlock.Id);
				ecmaIrBlock3.Terminator = new IrBranchTerminator(array[num4].Id, whenFalse, Location(jsSwitchCase));
			}
			else
			{
				function.Blocks.Add(array[num4]);
			}
			IrBlockId? ecmaBlockId = outerTargets?.Continue;
			_activeCleanupDepth++;
			IrBlock ecmaIrBlock4 = VisitStatements(function, scopes, ecmaScopeId, array[num4], jsSwitchCase.Consequent, new StatementTargets(ecmaIrBlock.Id, ecmaBlockId, _activeFinallyBlocks.Count, null, outerTargets, _activeCleanupDepth));
			_activeCleanupDepth--;
			IrBlock ecmaIrBlock5 = ecmaIrBlock4;
			if ((object)ecmaIrBlock5.Terminator == null)
			{
				IrTerminator ecmaIrTerminator = (ecmaIrBlock5.Terminator = new IrGotoTerminator((num4 + 1 < array.Length) ? array[num4 + 1].Id : ecmaIrBlock.Id, Location(jsSwitchCase)));
			}
		}
		function.Blocks.Add(ecmaIrBlock);
		Emit(ecmaIrBlock, "drop", selection);
		ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(ecmaScopeId)), Location(selection)));
		return ecmaIrBlock;
	}

	private IrBlock VisitSwitchWithoutDefault(IrFunction function, List<ScopeBuilder> scopes, IrScopeId switchScope, IrBlock firstTest, JsSwitchStatement selection, StatementTargets? outerTargets)
	{
		IrBlock[] array = new IrBlock[selection.Cases.Count];
		IrBlock[] array2 = new IrBlock[selection.Cases.Count];
		array[0] = firstTest;
		array2[0] = NewBlock(function);
		for (int i = 1; i < selection.Cases.Count; i++)
		{
			array[i] = NewBlock(function);
			array2[i] = NewBlock(function);
		}
		IrBlock ecmaIrBlock = NewBlock(function);
		for (int j = 0; j < selection.Cases.Count; j++)
		{
			JsSwitchCase jsSwitchCase = selection.Cases[j];
			IrBlock block = array[j];
			Emit(block, "dup", jsSwitchCase);
			block = VisitExpression(function, block, switchScope, jsSwitchCase.Test);
			Emit(block, "strict_eq", jsSwitchCase.Test);
			block.Terminator = new IrBranchTerminator(array2[j].Id, (j + 1 < array.Length) ? array[j + 1].Id : ecmaIrBlock.Id, Location(jsSwitchCase));
		}
		for (int k = 0; k < selection.Cases.Count; k++)
		{
			IrBlockId? ecmaBlockId = outerTargets?.Continue;
			_activeCleanupDepth++;
			array2[k] = VisitStatements(function, scopes, switchScope, array2[k], selection.Cases[k].Consequent, new StatementTargets(ecmaIrBlock.Id, ecmaBlockId, _activeFinallyBlocks.Count, null, outerTargets, _activeCleanupDepth));
			_activeCleanupDepth--;
			IrBlock ecmaIrBlock2 = array2[k];
			if ((object)ecmaIrBlock2.Terminator == null)
			{
				IrTerminator ecmaIrTerminator = (ecmaIrBlock2.Terminator = new IrGotoTerminator((k + 1 < array2.Length) ? array2[k + 1].Id : ecmaIrBlock.Id, Location(selection.Cases[k])));
			}
		}
		Emit(ecmaIrBlock, "drop", selection);
		ecmaIrBlock.Instructions.Add(new IrInstruction("leave_scope", new ReadOnlySingleElementList<IrOperand>(new IrScopeOperand(switchScope)), Location(selection)));
		return ecmaIrBlock;
	}

	private static IrBlock NewBlock(IrFunction function)
	{
		IrBlock ecmaIrBlock = new IrBlock(new IrBlockId(function.NextBlockId++));
		function.Blocks.Add(ecmaIrBlock);
		return ecmaIrBlock;
	}

	private static IrScopeId PushScope(List<ScopeBuilder> scopes, IrScopeId parent)
	{
		IrScopeId ecmaScopeId = new IrScopeId(scopes.Count);
		scopes.Add(new ScopeBuilder(ecmaScopeId, parent));
		return ecmaScopeId;
	}

	private static IrBindingId AddBinding(IrFunction function, ScopeBuilder scope, string name, IrBindingKind kind, bool isArgument = false, bool isConst = false, bool isLexical = false)
	{
		bool flag = !isArgument && !isLexical;
		bool flag2 = flag;
		if (flag2)
		{
			bool flag3 = kind <= IrBindingKind.NewFunctionDeclaration;
			flag2 = flag3;
		}
		if (flag2)
		{
			IrBinding ecmaIrBinding = scope.Bindings.Select((IrBindingId id) => function.Bindings[id.Value]).FirstOrDefault(delegate(IrBinding binding)
			{
				bool flag4 = binding.Name == name && !binding.IsLexical;
				bool flag5 = flag4;
				if (flag5)
				{
					IrBindingKind kind2 = binding.Kind;
					bool flag6 = kind2 <= IrBindingKind.NewFunctionDeclaration;
					flag5 = flag6;
				}
				return flag5;
			});
			if ((object)ecmaIrBinding != null)
			{
				bool flag3 = kind - 1 <= IrBindingKind.FunctionDeclaration;
				if (flag3 && ecmaIrBinding.Kind == IrBindingKind.Normal)
				{
					function.Bindings[ecmaIrBinding.Id.Value] = ecmaIrBinding with
					{
						Kind = kind
					};
				}
				return ecmaIrBinding.Id;
			}
		}
		IrBindingId ecmaBindingId = new IrBindingId(function.Bindings.Count);
		function.Bindings.Add(new IrBinding(ecmaBindingId, name, scope.Id, kind, isArgument, isConst, isLexical));
		scope.Bindings.Insert(0, ecmaBindingId);
		return ecmaBindingId;
	}

	private int RequiredModule(string specifier)
	{
		int num = _module.RequiredModules.IndexOf(specifier);
		if (num >= 0)
		{
			return num;
		}
		_module.RequiredModules.Add(specifier);
		return _module.RequiredModules.Count - 1;
	}

	private static IEnumerable<string> DeclaredNames(JsStatement declaration)
	{
		if (1 == 0)
		{
		}
		IEnumerable<string> result = ((declaration is JsVariableStatement jsVariableStatement) ? jsVariableStatement.Declarations.SelectMany(delegate(JsVariableDeclarator item)
		{
			IEnumerable<string> result2;
			if ((object)item.Pattern != null)
			{
				result2 = EnumerateBindings(item.Pattern);
			}
			else
			{
				IEnumerable<string> enumerable = new ReadOnlySingleElementList<string>(item.Name);
				result2 = enumerable;
			}
			return result2;
		}) : ((declaration is JsFunctionStatement jsFunctionStatement) ? new ReadOnlySingleElementList<string>(jsFunctionStatement.Name) : ((!(declaration is JsClassDeclaration jsClassDeclaration)) ? ((IEnumerable<string>)Array.Empty<string>()) : ((IEnumerable<string>)new ReadOnlySingleElementList<string>(jsClassDeclaration.Name)))));
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool FunctionBodyReferencesName(IReadOnlyList<JsStatement> statements, string name)
	{
		return new JavaScriptScopeAnalyzer("<function-expression>").Analyze(new JsAstProgram(statements)).UnresolvedReferences.Any((JsBinding reference) => reference.Name == name);
	}

	private IrBlock EmitYieldStar(IrFunction function, IrBlock block, IrScopeId scope, JsYieldExpression yielded)
	{
		if ((object)yielded.Argument == null)
		{
			Emit(block, "push_undefined", yielded);
		}
		else
		{
			block = VisitExpression(function, block, scope, yielded.Argument);
		}
		bool flag = function.Options.Kind == IrFunctionKind.AsyncGenerator;
		Emit(block, flag ? "for_await_of_start" : "for_of_start", yielded);
		Emit(block, "drop", yielded);
		Emit(block, "push_undefined", yielded);
		Emit(block, "push_undefined", yielded);
		IrBlock ecmaIrBlock = NewBlock(function);
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlock ecmaIrBlock4 = NewBlock(function);
		IrBlock ecmaIrBlock5 = NewBlock(function);
		IrBlock ecmaIrBlock6 = NewBlock(function);
		IrBlock ecmaIrBlock7 = NewBlock(function);
		IrBlock ecmaIrBlock8 = NewBlock(function);
		IrBlock ecmaIrBlock9 = NewBlock(function);
		IrBlock ecmaIrBlock10 = NewBlock(function);
		IrBlock ecmaIrBlock11 = NewBlock(function);
		IrBlock ecmaIrBlock12 = NewBlock(function);
		IrBlock ecmaIrBlock13 = NewBlock(function);
		IrBlock ecmaIrBlock14 = NewBlock(function);
		IrBlock ecmaIrBlock15 = NewBlock(function);
		block.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(yielded));
		Emit(ecmaIrBlock, "iterator_next", yielded);
		if (flag)
		{
			Emit(ecmaIrBlock, "await", yielded);
		}
		Emit(ecmaIrBlock, "iterator_check_object", yielded);
		Emit(ecmaIrBlock, "get_field2", yielded, new AtomOperand("done"));
		ecmaIrBlock.Terminator = new IrBranchTerminator(ecmaIrBlock14.Id, ecmaIrBlock2.Id, Location(yielded));
		if (flag)
		{
			Emit(ecmaIrBlock2, "get_field", yielded, new AtomOperand("value"));
			Emit(ecmaIrBlock2, "await", yielded);
			Emit(ecmaIrBlock2, "async_yield_star", yielded);
		}
		else
		{
			Emit(ecmaIrBlock2, "yield_star", yielded);
		}
		Emit(ecmaIrBlock2, "dup", yielded);
		ecmaIrBlock2.Terminator = new IrBranchTerminator(ecmaIrBlock4.Id, ecmaIrBlock3.Id, Location(yielded));
		Emit(ecmaIrBlock3, "drop", yielded);
		ecmaIrBlock3.Terminator = new IrGotoTerminator(ecmaIrBlock.Id, Location(yielded));
		Emit(ecmaIrBlock4, "push_i32", yielded, new ImmediateOperand(2L));
		Emit(ecmaIrBlock4, "strict_eq", yielded);
		ecmaIrBlock4.Terminator = new IrBranchTerminator(ecmaIrBlock9.Id, ecmaIrBlock5.Id, Location(yielded));
		if (flag)
		{
			Emit(ecmaIrBlock5, "await", yielded);
		}
		Emit(ecmaIrBlock5, "iterator_call", yielded, new ImmediateOperand(0L));
		ecmaIrBlock5.Terminator = new IrBranchTerminator(ecmaIrBlock8.Id, ecmaIrBlock6.Id, Location(yielded));
		if (flag)
		{
			Emit(ecmaIrBlock6, "await", yielded);
		}
		Emit(ecmaIrBlock6, "iterator_check_object", yielded);
		Emit(ecmaIrBlock6, "get_field2", yielded, new AtomOperand("done"));
		ecmaIrBlock6.Terminator = new IrBranchTerminator(ecmaIrBlock7.Id, ecmaIrBlock2.Id, Location(yielded));
		Emit(ecmaIrBlock7, "get_field", yielded, new AtomOperand("value"));
		ecmaIrBlock7.Terminator = new IrGotoTerminator(ecmaIrBlock8.Id, Location(yielded));
		Emit(ecmaIrBlock8, "nip", yielded);
		Emit(ecmaIrBlock8, "nip", yielded);
		Emit(ecmaIrBlock8, "nip", yielded);
		bool hasValue = EmitCatchReturnOffsets(ecmaIrBlock8, yielded, hasValue: true);
		EmitFinallyForReturn(ecmaIrBlock8, yielded, hasValue);
		ecmaIrBlock8.Terminator = new IrReturnTerminator(hasValue, Location(yielded));
		Emit(ecmaIrBlock9, "iterator_call", yielded, new ImmediateOperand(1L));
		ecmaIrBlock9.Terminator = new IrBranchTerminator(ecmaIrBlock11.Id, ecmaIrBlock10.Id, Location(yielded));
		if (flag)
		{
			Emit(ecmaIrBlock10, "await", yielded);
		}
		Emit(ecmaIrBlock10, "iterator_check_object", yielded);
		Emit(ecmaIrBlock10, "get_field2", yielded, new AtomOperand("done"));
		ecmaIrBlock10.Terminator = new IrBranchTerminator(ecmaIrBlock14.Id, ecmaIrBlock2.Id, Location(yielded));
		Emit(ecmaIrBlock11, "iterator_call", yielded, new ImmediateOperand(2L));
		if (flag)
		{
			ecmaIrBlock11.Terminator = new IrBranchTerminator(ecmaIrBlock13.Id, ecmaIrBlock12.Id, Location(yielded));
			Emit(ecmaIrBlock12, "await", yielded);
			ecmaIrBlock12.Terminator = new IrGotoTerminator(ecmaIrBlock13.Id, Location(yielded));
		}
		else
		{
			Emit(ecmaIrBlock11, "drop", yielded);
			ecmaIrBlock11.Terminator = new IrGotoTerminator(ecmaIrBlock13.Id, Location(yielded));
			ecmaIrBlock12.Terminator = new IrGotoTerminator(ecmaIrBlock13.Id, Location(yielded));
		}
		Emit(ecmaIrBlock13, "throw_error", yielded, new AtomOperand(""), new ImmediateOperand(4L));
		ecmaIrBlock13.Terminator = new IrInstructionTerminal(Location(yielded));
		Emit(ecmaIrBlock14, "get_field", yielded, new AtomOperand("value"));
		Emit(ecmaIrBlock14, "nip", yielded);
		Emit(ecmaIrBlock14, "nip", yielded);
		Emit(ecmaIrBlock14, "nip", yielded);
		ecmaIrBlock14.Terminator = new IrGotoTerminator(ecmaIrBlock15.Id, Location(yielded));
		return ecmaIrBlock15;
	}

	private IrBlock VisitExpression(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression)
	{
		// Spread expressions normally get consumed by their enclosing call, array,
		// object, or constructor lowering.  They can also reach this generic path
		// while a nested expression is being visited (for example from a rest/spread
		// argument in a method body).  A spread is not a standalone runtime value;
		// its operand is the value that the enclosing emitter appends or copies.
		if (expression is JsSpreadExpression spread)
		{
			return VisitExpression(function, block, scope, spread.Argument);
		}
		if (ContainsOptionalChain(expression))
		{
			return EmitOptionalChain(function, block, scope, expression);
		}
		if (!(expression is JsSuperExpression node))
		{
			if (!(expression is JsIdentifierExpression jsIdentifierExpression))
			{
				if (!(expression is JsNewTargetExpression node2))
				{
					if (!(expression is JsImportMetaExpression node3))
					{
						if (!(expression is JsDynamicImportExpression jsDynamicImportExpression))
						{
							if (!(expression is JsAwaitExpression jsAwaitExpression))
							{
								if (!(expression is JsSequenceExpression jsSequenceExpression))
								{
									if (!(expression is JsYieldExpression jsYieldExpression))
									{
										if (!(expression is JsLiteralExpression jsLiteralExpression))
										{
											if (!(expression is JsUnaryExpression jsUnaryExpression))
											{
												if (!(expression is JsBinaryExpression jsBinaryExpression))
												{
													if (!(expression is JsConditionalExpression expression2))
													{
														if (!(expression is JsAssignmentExpression assignment))
														{
															if (!(expression is JsUpdateExpression update))
															{
																if (expression is JsMemberExpression jsMemberExpression)
																{
																	if (jsMemberExpression.Optional)
																	{
																		goto IL_0f16;
																	}
																	block = VisitExpression(function, block, scope, jsMemberExpression.Object);
																	if (jsMemberExpression.Object is JsSuperExpression)
																	{
																		if (jsMemberExpression.Computed)
																		{
																			block = VisitExpression(function, block, scope, jsMemberExpression.Property);
																		}
																		else
																		{
																			if (!(jsMemberExpression.Property is JsIdentifierExpression jsIdentifierExpression2))
																			{
																				throw Unsupported(jsMemberExpression);
																			}
																			EmitStringConstant(function, block, jsIdentifierExpression2.Name, jsIdentifierExpression2);
																		}
																		Emit(block, "get_super_value", jsMemberExpression);
																	}
																	else if (jsMemberExpression.Computed)
																	{
																		block = VisitExpression(function, block, scope, jsMemberExpression.Property);
																		Emit(block, "get_array_el", jsMemberExpression);
																	}
																	else if (jsMemberExpression.Property is JsIdentifierExpression jsIdentifierExpression3)
																	{
																		Emit(block, "get_field", jsMemberExpression, new AtomOperand(jsIdentifierExpression3.Name));
																	}
																	else
																	{
																		if (!(jsMemberExpression.Property is JsPrivateIdentifierExpression jsPrivateIdentifierExpression))
																		{
																			throw Unsupported(jsMemberExpression);
																		}
																		Emit(block, "scope_get_private_field", jsMemberExpression, new AtomOperand(jsPrivateIdentifierExpression.Name), new IrScopeOperand(scope));
																	}
																}
																else if (expression is JsCallExpression jsCallExpression)
																{
																	if (jsCallExpression.Optional)
																	{
																		goto IL_0f16;
																	}
																	block = EmitCall(function, block, scope, jsCallExpression);
																}
																else if (!(expression is JsTaggedTemplateExpression tagged))
																{
																	if (!(expression is JsNewExpression jsNewExpression))
																	{
																		if (!(expression is JsArrayExpression array))
																		{
																			if (!(expression is JsObjectExpression obj))
																			{
																				if (!(expression is JsFunctionExpression jsFunctionExpression))
																				{
																					if (!(expression is JsClassExpression jsClassExpression))
																					{
																						goto IL_0f16;
																					}
																					block = EmitClass(function, _scopeBuilders[function.Id], scope, block, jsClassExpression.Name ?? string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false);
																				}
																				else
																				{
																					IrConstantId ecmaConstantId = new IrConstantId(function.Constants.Count);
																					IrFunctionId ecmaFunctionId = new IrFunctionId(_nextFunction++);
																					function.Constants.Add(new IrFunctionConstant(ecmaConstantId, ecmaFunctionId));
											BuildFunction(ecmaFunctionId, jsFunctionExpression.Name, jsFunctionExpression.Parameters, jsFunctionExpression.Body.Body, (!jsFunctionExpression.Arrow) ? IrFunctionForm.Expression : IrFunctionForm.Arrow, jsFunctionExpression.Async, jsFunctionExpression.Generator, jsFunctionExpression.DefinedArgCount, jsFunctionExpression.ParameterDefaults, jsFunctionExpression.ParameterPatterns, function.Id, scope, ecmaConstantId, Location(jsFunctionExpression), jsFunctionExpression.Name != null, protectionTags: jsFunctionExpression.ProtectionTags);
																					block.Instructions.Add(new IrInstruction("fclosure", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(ecmaConstantId)), Location(jsFunctionExpression)));
																				}
																			}
																			else
																			{
																				block = EmitObject(function, block, scope, obj);
																			}
																		}
																		else
																		{
																			block = EmitArray(function, block, scope, array);
																		}
																	}
																	else
																	{
																		block = VisitExpression(function, block, scope, jsNewExpression.Callee);
																		Emit(block, "dup", jsNewExpression.Callee);
																		if (jsNewExpression.Arguments.Any((JsExpression argument) => argument is JsSpreadExpression))
																		{
																			block = EmitSpreadConstructorArguments(function, block, scope, jsNewExpression);
																			Emit(block, "perm3", jsNewExpression);
																			Emit(block, "apply", jsNewExpression, new ImmediateOperand(1L));
																		}
																		else
																		{
																			foreach (JsExpression argument in jsNewExpression.Arguments)
																			{
																				block = VisitExpression(function, block, scope, argument);
																			}
																			Emit(block, "call_constructor", jsNewExpression, new ImmediateOperand(jsNewExpression.Arguments.Count));
																		}
																	}
																}
																else
																{
																	block = EmitTaggedTemplate(function, block, scope, tagged);
																}
															}
															else
															{
																block = EmitUpdate(function, block, scope, update, valueUsed: true);
															}
														}
														else
														{
															block = EmitAssignment(function, block, scope, assignment);
														}
													}
													else
													{
														block = EmitConditionalExpression(function, block, scope, expression2);
													}
												}
												else
												{
													bool flag;
													switch (jsBinaryExpression.Operator)
													{
													case "&&":
													case "||":
													case "??":
														flag = true;
														break;
													default:
														flag = false;
														break;
													}
													if (flag)
													{
														block = EmitLogicalExpression(function, block, scope, jsBinaryExpression);
													}
													else
													{
														block = VisitExpression(function, block, scope, jsBinaryExpression.Left);
														block = VisitExpression(function, block, scope, jsBinaryExpression.Right);
														Emit(block, BinaryOperation(jsBinaryExpression.Operator), jsBinaryExpression);
													}
												}
											}
											else if ((object)jsUnaryExpression != null && jsUnaryExpression.Operator == "typeof" && jsUnaryExpression.Argument is JsIdentifierExpression jsIdentifierExpression4)
											{
												Emit(block, "scope_get_var_undef", jsIdentifierExpression4, new AtomOperand(jsIdentifierExpression4.Name), new IrScopeOperand(scope));
												Emit(block, "typeof", jsUnaryExpression);
											}
											else if (jsUnaryExpression.Operator == "void")
											{
												if (!(jsUnaryExpression.Argument is JsLiteralExpression))
												{
													block = VisitExpression(function, block, scope, jsUnaryExpression.Argument);
													Emit(block, "drop", jsUnaryExpression);
												}
												Emit(block, "push_undefined", jsUnaryExpression);
											}
											else if ((object)jsUnaryExpression != null && jsUnaryExpression.Operator == "delete" && jsUnaryExpression.Argument is JsMemberExpression { Optional: false } jsMemberExpression2 && !(jsMemberExpression2.Object is JsSuperExpression))
											{
												block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
												if (jsMemberExpression2.Computed)
												{
													block = VisitExpression(function, block, scope, jsMemberExpression2.Property);
												}
												else
												{
													if (!(jsMemberExpression2.Property is JsIdentifierExpression jsIdentifierExpression5))
													{
														throw Unsupported(jsMemberExpression2);
													}
													EmitStringConstant(function, block, jsIdentifierExpression5.Name, jsIdentifierExpression5);
												}
												Emit(block, "delete", jsUnaryExpression);
											}
											else
											{
												block = VisitExpression(function, block, scope, jsUnaryExpression.Argument);
												Emit(block, UnaryOperation(jsUnaryExpression.Operator), jsUnaryExpression);
											}
										}
										else if (jsLiteralExpression.Kind == JavaScriptTokenKind.Regex)
										{
											(string Pattern, string Flags) tuple = RegularExpressionBytecodeCompiler.SplitLiteral(jsLiteralExpression.Raw);
											string item = tuple.Pattern;
											string item2 = tuple.Flags;
											IrConstantId ecmaConstantId2 = new IrConstantId(function.Constants.Count);
											function.Constants.Add(new IrRegExpPatternConstant(ecmaConstantId2, item));
											IrConstantId ecmaConstantId3 = new IrConstantId(function.Constants.Count);
											function.Constants.Add(new IrRegExpBytecodeConstant(ecmaConstantId3, RegularExpressionBytecodeCompiler.Compile(item, item2)));
											Emit(block, "push_const", jsLiteralExpression, new IrConstantOperand(ecmaConstantId2));
											Emit(block, "push_const", jsLiteralExpression, new IrConstantOperand(ecmaConstantId3));
											Emit(block, "regexp", jsLiteralExpression);
										}
										else
										{
											string text;
											if (jsLiteralExpression.Kind == JavaScriptTokenKind.String)
											{
												text = null;
											}
											else
											{
												string raw = jsLiteralExpression.Raw;
												if (1 == 0)
												{
												}
												string text2 = raw switch
												{
													"true" => "push_true", 
													"false" => "push_false", 
													"null" => "push_null", 
													"undefined" => "push_undefined", 
													_ => null, 
												};
												if (1 == 0)
												{
												}
												text = text2;
											}
											string text3 = text;
											if (text3 != null)
											{
												block.Instructions.Add(new IrInstruction(text3, Array.Empty<IrOperand>(), Location(jsLiteralExpression)));
											}
											else
											{
												IrConstant ecmaIrConstant = ((jsLiteralExpression.Kind == JavaScriptTokenKind.String) ? new IrStringConstant(new IrConstantId(function.Constants.Count), jsLiteralExpression.Raw) : ((jsLiteralExpression.Kind == JavaScriptTokenKind.Number) ? ((IrConstant)new IrNumberConstant(new IrConstantId(function.Constants.Count), ParseNumber(jsLiteralExpression.Raw))) : ((IrConstant)new IrStringConstant(new IrConstantId(function.Constants.Count), jsLiteralExpression.Raw))));
												function.Constants.Add(ecmaIrConstant);
												block.Instructions.Add(new IrInstruction("push_const", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(ecmaIrConstant.Id)), Location(jsLiteralExpression)));
											}
										}
									}
									else if (!jsYieldExpression.Delegate)
									{
										if ((object)jsYieldExpression.Argument == null)
										{
											Emit(block, "push_undefined", jsYieldExpression);
										}
										else
										{
											block = VisitExpression(function, block, scope, jsYieldExpression.Argument);
										}
										if (function.Options.Kind == IrFunctionKind.AsyncGenerator)
										{
											Emit(block, "await", jsYieldExpression);
										}
										Emit(block, "yield", jsYieldExpression);
										IrBlock ecmaIrBlock = NewBlock(function);
										IrBlock ecmaIrBlock2 = NewBlock(function);
										block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, Location(jsYieldExpression));
										bool hasValue = EmitCatchReturnOffsets(ecmaIrBlock, jsYieldExpression, hasValue: true);
										IrBlock ecmaIrBlock3 = ecmaIrBlock;
										bool flag2 = false;
										if (function.Options.Kind == IrFunctionKind.AsyncGenerator && _activeIteratorLoops.Count != 0)
										{
											List<JsForInOfStatement> activeIteratorLoops = _activeIteratorLoops;
											JsForInOfStatement item3 = activeIteratorLoops[activeIteratorLoops.Count - 1];
											_activeIteratorLoops.RemoveAt(_activeIteratorLoops.Count - 1);
											ecmaIrBlock3 = EmitAsyncIteratorCloseForReturn(function, ecmaIrBlock3, jsYieldExpression);
											EmitFinallyForReturn(ecmaIrBlock3, jsYieldExpression, hasValue);
											_activeIteratorLoops.Add(item3);
											flag2 = true;
										}
										else
										{
											EmitFinallyForReturn(ecmaIrBlock3, jsYieldExpression, hasValue);
										}
										if (!flag2)
										{
											ecmaIrBlock.Terminator = new IrReturnTerminator(hasValue, Location(jsYieldExpression));
										}
										ecmaIrBlock3.Terminator = new IrReturnTerminator(hasValue, Location(jsYieldExpression));
										block = ecmaIrBlock2;
									}
									else
									{
										JsYieldExpression yielded = jsYieldExpression;
										block = EmitYieldStar(function, block, scope, yielded);
									}
								}
								else
								{
									for (int num = 0; num < jsSequenceExpression.Expressions.Count; num++)
									{
										if (num + 1 < jsSequenceExpression.Expressions.Count && jsSequenceExpression.Expressions[num] is JsLiteralExpression)
										{
											continue;
										}
										if (num + 1 < jsSequenceExpression.Expressions.Count && jsSequenceExpression.Expressions[num] is JsUpdateExpression update2)
										{
											block = EmitUpdate(function, block, scope, update2, valueUsed: false);
											continue;
										}
										block = VisitExpression(function, block, scope, jsSequenceExpression.Expressions[num]);
										if (num + 1 < jsSequenceExpression.Expressions.Count)
										{
											Emit(block, "drop", jsSequenceExpression.Expressions[num]);
										}
									}
								}
							}
							else
							{
								block = VisitExpression(function, block, scope, jsAwaitExpression.Argument);
								Emit(block, "await", jsAwaitExpression);
							}
						}
						else
						{
							block = VisitExpression(function, block, scope, jsDynamicImportExpression.Specifier);
							Emit(block, "import", jsDynamicImportExpression);
						}
					}
					else
					{
						Emit(block, "special_object", node3, new ImmediateOperand(6L));
					}
				}
				else
				{
					EnsureActivationBinding(function, "new.target");
					Emit(block, "scope_get_var", node2, new AtomOperand("new.target"), new IrScopeOperand(scope));
				}
			}
			else
			{
				if (jsIdentifierExpression.Name == "arguments")
				{
					EnsureActivationBinding(function, "arguments");
				}
				else if (jsIdentifierExpression.Name == "this")
				{
					EnsureActivationBinding(function, "this");
				}
				Emit(block, "scope_get_var", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(scope));
			}
		}
		else
		{
			EnsureActivationBinding(function, "this");
			EnsureActivationBinding(function, "home_object");
			Emit(block, "scope_get_var", node, new AtomOperand("this"), new IrScopeOperand(scope));
			Emit(block, "scope_get_var", node, new AtomOperand("home_object"), new IrScopeOperand(scope));
			Emit(block, "get_super", node);
		}
		return block;
		IL_0f16:
		throw Unsupported(expression);
	}

	private IrBlock EmitTaggedTemplate(IrFunction function, IrBlock block, IrScopeId scope, JsTaggedTemplateExpression tagged)
	{
		IrConstantId ecmaConstantId = new IrConstantId(function.Constants.Count);
		function.Constants.Add(new IrTemplateConstant(ecmaConstantId, tagged.Cooked, tagged.Raw));
		JsMemberExpression jsMemberExpression = tagged.Tag as JsMemberExpression;
		if ((object)jsMemberExpression != null && !jsMemberExpression.Optional && !jsMemberExpression.Computed && jsMemberExpression.Property is JsIdentifierExpression jsIdentifierExpression && !(jsMemberExpression.Object is JsSuperExpression))
		{
			block = VisitExpression(function, block, scope, jsMemberExpression.Object);
			Emit(block, "get_field2", jsMemberExpression, new AtomOperand(jsIdentifierExpression.Name));
		}
		else if ((object)jsMemberExpression != null && !jsMemberExpression.Optional && jsMemberExpression.Computed && !(jsMemberExpression.Object is JsSuperExpression))
		{
			block = VisitExpression(function, block, scope, jsMemberExpression.Object);
			block = VisitExpression(function, block, scope, jsMemberExpression.Property);
			Emit(block, "get_array_el2", jsMemberExpression);
		}
		else
		{
			block = VisitExpression(function, block, scope, tagged.Tag);
		}
		Emit(block, "push_const", tagged, new IrConstantOperand(ecmaConstantId));
		foreach (JsExpression substitution in tagged.Substitutions)
		{
			block = VisitExpression(function, block, scope, substitution);
		}
		Emit(block, ((object)jsMemberExpression == null) ? "call" : "call_method", tagged, new ImmediateOperand(tagged.Substitutions.Count + 1));
		return block;
	}

	private IrBlock EmitOptionalChain(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression)
	{
		List<OptionalChainSegment> list = new List<OptionalChainSegment>();
		JsExpression expression2 = DecomposeOptionalChain(expression, list);
		block = VisitExpression(function, block, scope, expression2);
		List<IrBlock> list2 = new List<IrBlock>();
		for (int i = 0; i < list.Count; i++)
		{
			OptionalChainSegment optionalChainSegment = list[i];
			bool flag = i + 1 < list.Count && list[i + 1] is OptionalCallSegment;
			int num = ((!(optionalChainSegment is OptionalCallSegment) || i <= 0 || !(list[i - 1] is OptionalMemberSegment)) ? 1 : 2);
			if (optionalChainSegment.IsOptional)
			{
				Emit(block, "dup", optionalChainSegment.Node);
				Emit(block, "is_undefined_or_null", optionalChainSegment.Node);
				IrBlock ecmaIrBlock = NewBlock(function);
				IrBlock ecmaIrBlock2 = NewBlock(function);
				block.Terminator = new IrBranchTerminator(ecmaIrBlock.Id, ecmaIrBlock2.Id, Location(optionalChainSegment.Node));
				for (int j = 0; j < num; j++)
				{
					Emit(ecmaIrBlock, "drop", optionalChainSegment.Node);
				}
				Emit(ecmaIrBlock, "push_undefined", optionalChainSegment.Node);
				list2.Add(ecmaIrBlock);
				block = ecmaIrBlock2;
			}
			OptionalChainSegment optionalChainSegment2 = optionalChainSegment;
			OptionalChainSegment optionalChainSegment3 = optionalChainSegment2;
			if (optionalChainSegment3 is OptionalMemberSegment optionalMemberSegment)
			{
				JsMemberExpression member = optionalMemberSegment.Member;
				if (member.Computed)
				{
					block = VisitExpression(function, block, scope, member.Property);
					Emit(block, flag ? "get_array_el2" : "get_array_el", member);
					continue;
				}
				if (!(member.Property is JsIdentifierExpression jsIdentifierExpression))
				{
					throw Unsupported(member);
				}
				Emit(block, flag ? "get_field2" : "get_field", member, new AtomOperand(jsIdentifierExpression.Name));
			}
			else
			{
				if (!(optionalChainSegment3 is OptionalCallSegment optionalCallSegment))
				{
					continue;
				}
				JsCallExpression call = optionalCallSegment.Call;
				foreach (JsExpression argument in call.Arguments)
				{
					block = VisitExpression(function, block, scope, argument);
				}
				Emit(block, (num == 2) ? "call_method" : "call", call, new ImmediateOperand(call.Arguments.Count));
			}
		}
		IrBlock ecmaIrBlock3 = NewBlock(function);
		block.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(expression));
		foreach (IrBlock item in list2)
		{
			item.Terminator = new IrGotoTerminator(ecmaIrBlock3.Id, Location(expression));
		}
		return ecmaIrBlock3;
	}

	private static JsExpression DecomposeOptionalChain(JsExpression expression, ICollection<OptionalChainSegment> segments)
	{
		if (!(expression is JsMemberExpression jsMemberExpression))
		{
			if (expression is JsCallExpression jsCallExpression)
			{
				JsExpression result = DecomposeOptionalChain(jsCallExpression.Callee, segments);
				segments.Add(new OptionalCallSegment(jsCallExpression));
				return result;
			}
			return expression;
		}
		JsExpression result2 = DecomposeOptionalChain(jsMemberExpression.Object, segments);
		segments.Add(new OptionalMemberSegment(jsMemberExpression));
		return result2;
	}

	private static bool ContainsOptionalChain(JsExpression expression)
	{
		if (1 == 0)
		{
		}
		bool result = ((expression is JsMemberExpression jsMemberExpression) ? (jsMemberExpression.Optional || ContainsOptionalChain(jsMemberExpression.Object)) : (expression is JsCallExpression jsCallExpression && (jsCallExpression.DirectOptional || ContainsOptionalChain(jsCallExpression.Callee))));
		if (1 == 0)
		{
		}
		return result;
	}

	private IrBlock EmitLogicalExpression(IrFunction function, IrBlock block, IrScopeId scope, JsBinaryExpression expression)
	{
		JsExpression[] array = FlattenLogicalChain(expression, expression.Operator).ToArray();
		block = VisitExpression(function, block, scope, array[0]);
		List<(IrBlock, IrBlockId)> list = new List<(IrBlock, IrBlockId)>();
		for (int i = 1; i < array.Length; i++)
		{
			Emit(block, "dup", array[i - 1]);
			if (expression.Operator == "??")
			{
				Emit(block, "is_undefined_or_null", array[i - 1]);
			}
			IrBlock item = block;
			IrBlock ecmaIrBlock = NewBlock(function);
			Emit(ecmaIrBlock, "drop", array[i - 1]);
			block = VisitExpression(function, ecmaIrBlock, scope, array[i]);
			list.Add((item, ecmaIrBlock.Id));
		}
		IrBlock ecmaIrBlock2 = ((block.Instructions.Count == 0 && (object)block.Terminator == null) ? block : NewBlock(function));
		for (var index = 0; index < list.Count; index++)
		{
			var item4 = list[index];
			IrBlock item2 = item4.Item1;
			IrBlockId item3 = item4.Item2;
			IrBlock ecmaIrBlock3 = item2;
			string text = expression.Operator;
			if (1 == 0)
			{
			}
			// A parenthesized middle logical expression retains its own join.
			// QuickJS routes a preceding short-circuit edge through that join so
			// the enclosing chain performs the following logical test uniformly.
			var falseTarget = expression.Operator == "&&" && index + 1 < list.Count &&
				array[index + 1] is JsBinaryExpression { Operator: "&&" or "||" or "??" }
				? list[index + 1].Item1.Id
				: ecmaIrBlock2.Id;
			IrBranchTerminator terminator = text switch
			{
				"&&" => new IrBranchTerminator(item3, falseTarget, Location(expression)),
				"||" => new IrBranchTerminator(ecmaIrBlock2.Id, item3, Location(expression)), 
				"??" => new IrBranchTerminator(item3, ecmaIrBlock2.Id, Location(expression)), 
				_ => throw new InvalidOperationException("Unknown logical expression."), 
			};
			if (1 == 0)
			{
			}
			ecmaIrBlock3.Terminator = terminator;
		}
		if (block != ecmaIrBlock2)
		{
			block.Terminator = new IrGotoTerminator(ecmaIrBlock2.Id, Location(expression));
		}
		return ecmaIrBlock2;
	}

	private static IEnumerable<JsExpression> FlattenLogicalChain(JsExpression expression, string operation)
	{
		JsBinaryExpression binary = expression as JsBinaryExpression;
		if ((object)binary != null && binary.Operator == operation)
		{
			foreach (JsExpression item in FlattenLogicalChain(binary.Left, operation))
			{
				yield return item;
			}
			foreach (JsExpression item2 in FlattenLogicalChain(binary.Right, operation))
			{
				yield return item2;
			}
		}
		else
		{
			yield return expression;
		}
	}

	private IrBlock EmitConditionalExpression(IrFunction function, IrBlock block, IrScopeId scope, JsConditionalExpression expression)
	{
		block = VisitExpression(function, block, scope, expression.Test);
		IrBlock ecmaIrBlock = block;
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlockId id = ecmaIrBlock2.Id;
		ecmaIrBlock2 = VisitExpression(function, ecmaIrBlock2, scope, expression.Consequent);
		IrBlock ecmaIrBlock3 = NewBlock(function);
		IrBlockId id2 = ecmaIrBlock3.Id;
		ecmaIrBlock3 = VisitExpression(function, ecmaIrBlock3, scope, expression.Alternate);
		IrBlock ecmaIrBlock4 = NewBlock(function);
		ecmaIrBlock.Terminator = new IrBranchTerminator(id, id2, Location(expression.Test));
		ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(expression.Consequent));
		ecmaIrBlock3.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(expression.Alternate));
		return ecmaIrBlock4;
	}

	private IrBlock EmitAssignment(IrFunction function, IrBlock block, IrScopeId scope, JsAssignmentExpression assignment)
	{
		bool flag;
		switch (assignment.Operator)
		{
		case "&&=":
		case "||=":
		case "??=":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			return EmitLogicalAssignment(function, block, scope, assignment);
		}
		bool flag2 = assignment.Operator == "=";
		bool flag3 = flag2;
		if (flag3)
		{
			JsExpression left = assignment.Left;
			flag = ((left is JsArrayExpression || left is JsObjectExpression) ? true : false);
			flag3 = flag;
		}
		if (flag3)
		{
			JsBindingPattern pattern = ToAssignmentPattern(assignment.Left);
			if (_assignmentDestructuringCfg.CanBuild(pattern))
			{
				return _assignmentDestructuringCfg.Emit(function, block, scope, pattern, assignment.Right, assignment);
			}
			block = VisitExpression(function, block, scope, assignment.Right);
			Emit(block, "dup", assignment);
			return EmitParameterExpressionPattern(function, block, scope, pattern, assignmentTarget: true);
		}
		bool flag4 = assignment.Operator != "=";
		JsExpression left2 = assignment.Left;
		JsExpression jsExpression = left2;
		JsExpression property;
		if (jsExpression is JsMemberExpression jsMemberExpression)
		{
			if (jsMemberExpression.Optional)
			{
				goto IL_0734;
			}
			if (!jsMemberExpression.Computed)
			{
				JsExpression jsExpression2 = jsMemberExpression.Object;
				if (jsExpression2 is JsSuperExpression)
				{
					property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression))
					{
						goto IL_01a1;
					}
					block = VisitExpression(function, block, scope, jsMemberExpression.Object);
					EmitStringConstant(function, block, jsIdentifierExpression.Name, jsIdentifierExpression);
					if (flag4)
					{
						Emit(block, "to_propkey", jsMemberExpression);
						Emit(block, "dup3", jsMemberExpression);
						Emit(block, "get_super_value", jsMemberExpression);
					}
					else
					{
						Emit(block, "to_propkey", jsMemberExpression);
					}
					block = VisitExpression(function, block, scope, assignment.Right);
					if (flag4)
					{
						IrBlock block2 = block;
						string text = assignment.Operator;
						Emit(block2, BinaryOperation(text.Substring(0, text.Length - 1)), assignment);
					}
					Emit(block, "insert4", assignment);
					Emit(block, "put_super_value", assignment);
				}
				else
				{
					property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression2))
					{
						goto IL_01a1;
					}
					JsIdentifierExpression jsIdentifierExpression3 = jsIdentifierExpression2;
					JsMemberExpression jsMemberExpression2 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
					if (flag4)
					{
						Emit(block, "get_field2", jsMemberExpression2, new AtomOperand(jsIdentifierExpression3.Name));
					}
					block = VisitExpression(function, block, scope, assignment.Right);
					if (flag4)
					{
						IrBlock block3 = block;
						string text = assignment.Operator;
						Emit(block3, BinaryOperation(text.Substring(0, text.Length - 1)), assignment);
					}
					Emit(block, "insert2", assignment);
					Emit(block, "put_field", assignment, new AtomOperand(jsIdentifierExpression3.Name));
				}
			}
			else
			{
				JsExpression jsExpression2 = jsMemberExpression.Object;
				if (jsExpression2 is JsSuperExpression)
				{
					JsMemberExpression jsMemberExpression3 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression3.Object);
					block = VisitExpression(function, block, scope, jsMemberExpression3.Property);
					Emit(block, "to_propkey", jsMemberExpression3);
					if (flag4)
					{
						Emit(block, "dup3", jsMemberExpression3);
						Emit(block, "get_super_value", jsMemberExpression3);
					}
					block = VisitExpression(function, block, scope, assignment.Right);
					if (flag4)
					{
						IrBlock block4 = block;
						string text = assignment.Operator;
						Emit(block4, BinaryOperation(text.Substring(0, text.Length - 1)), assignment);
					}
					Emit(block, "insert4", assignment);
					Emit(block, "put_super_value", assignment);
				}
				else
				{
					JsMemberExpression jsMemberExpression4 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression4.Object);
					block = VisitExpression(function, block, scope, jsMemberExpression4.Property);
					// A computed assignment keeps both the receiver and its canonical
					// property key alive while the right-hand side is evaluated.  This is
					// required for plain `target[key] = value` as well as compound forms;
					// omitting it before a closure RHS produces invalid target bytecode.
					Emit(block, "to_propkey2", jsMemberExpression4);
					if (flag4)
					{
						Emit(block, "dup2", jsMemberExpression4);
						Emit(block, "get_array_el", jsMemberExpression4);
					}
					block = VisitExpression(function, block, scope, assignment.Right);
					if (flag4)
					{
						IrBlock block5 = block;
						string text = assignment.Operator;
						Emit(block5, BinaryOperation(text.Substring(0, text.Length - 1)), assignment);
					}
					Emit(block, "insert3", assignment);
					Emit(block, "put_array_el", assignment);
				}
			}
		}
		else
		{
			if (!(jsExpression is JsIdentifierExpression jsIdentifierExpression4))
			{
				goto IL_0734;
			}
			Emit(block, "scope_make_ref", jsIdentifierExpression4, new AtomOperand(jsIdentifierExpression4.Name), new IrScopeOperand(scope));
			if (flag4)
			{
				Emit(block, "get_ref_value", assignment.Left);
			}
			block = ((!flag4 && assignment.Right is JsClassExpression { Name: null } jsClassExpression) ? EmitClass(function, _scopeBuilders[function.Id], scope, block, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, jsIdentifierExpression4.Name) : VisitExpression(function, block, scope, assignment.Right));
			if (!flag4 && IsAnonymousNameable(assignment.Right) && (!(assignment.Right is JsClassExpression jsClassExpression2) || jsClassExpression2.Name != null))
			{
				Emit(block, "set_name", assignment.Right, new AtomOperand(jsIdentifierExpression4.Name));
			}
			if (flag4)
			{
				IrBlock block6 = block;
				string text = assignment.Operator;
				Emit(block6, BinaryOperation(text.Substring(0, text.Length - 1)), assignment);
			}
			Emit(block, "dup", assignment);
			Emit(block, "put_ref_value_copy", assignment);
		}
		goto IL_0741;
		IL_0734:
		throw Unsupported(assignment.Left);
		IL_01a1:
		if (property is JsPrivateIdentifierExpression jsPrivateIdentifierExpression)
		{
			JsMemberExpression jsMemberExpression5 = jsMemberExpression;
			if (!flag4)
			{
				block = VisitExpression(function, block, scope, jsMemberExpression5.Object);
				block = VisitExpression(function, block, scope, assignment.Right);
				Emit(block, "insert2", assignment);
				Emit(block, "scope_put_private_field", assignment, new AtomOperand(jsPrivateIdentifierExpression.Name), new IrScopeOperand(scope));
				goto IL_0741;
			}
		}
		goto IL_0734;
		IL_0741:
		return block;
	}

	private static bool IsAnonymousNameable(JsExpression expression)
	{
		if (1 == 0)
		{
		}
		bool result;
		if (expression is JsFunctionExpression jsFunctionExpression)
		{
			string name = jsFunctionExpression.Name;
			if (name == null)
			{
				result = true;
				goto IL_003e;
			}
		}
		else if (expression is JsClassExpression jsClassExpression)
		{
			string name2 = jsClassExpression.Name;
			if (name2 == null)
			{
				result = true;
				goto IL_003e;
			}
		}
		result = false;
		goto IL_003e;
		IL_003e:
		if (1 == 0)
		{
		}
		return result;
	}

	private IrBlock EmitExpressionWithInferredName(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression, string? inferredName)
	{
		if (inferredName == null)
		{
			return VisitExpression(function, block, scope, expression);
		}
		if (expression is JsClassExpression { Name: null } jsClassExpression)
		{
			return EmitClass(function, _scopeBuilders[function.Id], scope, block, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, inferredName);
		}
		block = VisitExpression(function, block, scope, expression);
		if (expression is JsFunctionExpression { IsNamedExpression: false })
		{
			Emit(block, "set_name", expression, new AtomOperand(inferredName));
		}
		return block;
	}

	private static string? GetBindingName(JsBindingPattern pattern)
	{
		if (1 == 0)
		{
		}
		string result;
		if (!(pattern is JsIdentifierPattern jsIdentifierPattern))
		{
			if (pattern is JsAssignmentTargetPattern jsAssignmentTargetPattern)
			{
				JsExpression target = jsAssignmentTargetPattern.Target;
				if (target is JsIdentifierExpression jsIdentifierExpression)
				{
					result = jsIdentifierExpression.Name;
					goto IL_0047;
				}
			}
			result = null;
		}
		else
		{
			result = jsIdentifierPattern.Name;
		}
		goto IL_0047;
		IL_0047:
		if (1 == 0)
		{
		}
		return result;
	}

	private static JsBindingPattern ToAssignmentPattern(JsExpression expression)
	{
		if (1 == 0)
		{
		}
		JsBindingPattern result;
		if (!(expression is JsIdentifierExpression) && !(expression is JsMemberExpression))
		{
			if (expression is JsAssignmentExpression jsAssignmentExpression)
			{
				string text = jsAssignmentExpression.Operator;
				if (!(text == "="))
				{
					goto IL_0154;
				}
				result = new JsAssignmentPattern(ToAssignmentPattern(jsAssignmentExpression.Left), jsAssignmentExpression.Right, jsAssignmentExpression.Line, jsAssignmentExpression.Column);
			}
			else if (!(expression is JsSpreadExpression jsSpreadExpression))
			{
				if (!(expression is JsArrayExpression jsArrayExpression))
				{
					if (!(expression is JsObjectExpression jsObjectExpression))
					{
						goto IL_0154;
					}
					result = new JsObjectPattern(jsObjectExpression.Properties.Select((JsObjectProperty property) => new JsObjectBindingProperty(property.Key, ToAssignmentPattern(property.Value), property.Line, property.Column, property.ComputedKey)).ToArray(), jsObjectExpression.Line, jsObjectExpression.Column);
				}
				else
				{
					result = new JsArrayPattern(jsArrayExpression.Elements.Select((JsExpression element) => ((object)element == null) ? null : ToAssignmentPattern(element)).ToArray(), jsArrayExpression.Line, jsArrayExpression.Column);
				}
			}
			else
			{
				result = new JsRestPattern(ToAssignmentPattern(jsSpreadExpression.Argument), jsSpreadExpression.Line, jsSpreadExpression.Column);
			}
		}
		else
		{
			result = new JsAssignmentTargetPattern(expression, expression.Line, expression.Column);
		}
		if (1 == 0)
		{
		}
		return result;
		IL_0154:
		throw new NotSupportedException("Invalid assignment-pattern target '" + expression.GetType().Name + "'.");
	}

	private IrBlock EmitLogicalAssignment(IrFunction function, IrBlock block, IrScopeId scope, JsAssignmentExpression assignment)
	{
		int num = 0;
		JsExpression left = assignment.Left;
		JsExpression jsExpression = left;
		if (!(jsExpression is JsIdentifierExpression jsIdentifierExpression))
		{
			if (!(jsExpression is JsMemberExpression { Optional: false } jsMemberExpression))
			{
				goto IL_0278;
			}
			if (!jsMemberExpression.Computed)
			{
				JsExpression jsExpression2 = jsMemberExpression.Object;
				if (jsExpression2 is JsSuperExpression)
				{
					JsExpression property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression2))
					{
						goto IL_0278;
					}
					block = VisitExpression(function, block, scope, jsMemberExpression.Object);
					EmitStringConstant(function, block, jsIdentifierExpression2.Name, jsIdentifierExpression2);
					Emit(block, "to_propkey", jsMemberExpression);
					Emit(block, "dup3", jsMemberExpression);
					Emit(block, "get_super_value", jsMemberExpression);
					num = 3;
				}
				else
				{
					JsExpression property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression3))
					{
						goto IL_0278;
					}
					JsIdentifierExpression jsIdentifierExpression4 = jsIdentifierExpression3;
					JsMemberExpression jsMemberExpression2 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
					Emit(block, "get_field2", jsMemberExpression2, new AtomOperand(jsIdentifierExpression4.Name));
					num = 1;
				}
			}
			else
			{
				JsExpression jsExpression2 = jsMemberExpression.Object;
				if (jsExpression2 is JsSuperExpression)
				{
					JsMemberExpression jsMemberExpression3 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression3.Object);
					block = VisitExpression(function, block, scope, jsMemberExpression3.Property);
					Emit(block, "to_propkey", jsMemberExpression3);
					Emit(block, "dup3", jsMemberExpression3);
					Emit(block, "get_super_value", jsMemberExpression3);
					num = 3;
				}
				else
				{
					JsMemberExpression jsMemberExpression4 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression4.Object);
					block = VisitExpression(function, block, scope, jsMemberExpression4.Property);
					Emit(block, "to_propkey2", jsMemberExpression4);
					Emit(block, "dup2", jsMemberExpression4);
					Emit(block, "get_array_el", jsMemberExpression4);
					num = 2;
				}
			}
		}
		else
		{
			Emit(block, "scope_make_persistent_ref", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(scope));
			Emit(block, "get_ref_value", jsIdentifierExpression);
			num = 2;
		}
		Emit(block, "dup", assignment.Left);
		if (assignment.Operator == "??=")
		{
			Emit(block, "is_undefined_or_null", assignment.Left);
		}
		IrBlock ecmaIrBlock = block;
		IrBlock ecmaIrBlock2 = NewBlock(function);
		IrBlockId id = ecmaIrBlock2.Id;
		Emit(ecmaIrBlock2, "drop", assignment.Left);
		ecmaIrBlock2 = ((assignment.Left is JsIdentifierExpression jsIdentifierExpression5 && assignment.Right is JsClassExpression { Name: null } jsClassExpression) ? EmitClass(function, _scopeBuilders[function.Id], scope, ecmaIrBlock2, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, jsIdentifierExpression5.Name) : VisitExpression(function, ecmaIrBlock2, scope, assignment.Right));
		if (assignment.Left is JsIdentifierExpression jsIdentifierExpression6 && assignment.Right is JsFunctionExpression { Name: null })
		{
			Emit(ecmaIrBlock2, "set_name", assignment.Right, new AtomOperand(jsIdentifierExpression6.Name));
		}
		switch (num)
		{
		case 1:
			Emit(ecmaIrBlock2, "insert2", assignment);
			break;
		case 2:
			Emit(ecmaIrBlock2, "insert3", assignment);
			break;
		case 3:
			Emit(ecmaIrBlock2, "insert4", assignment);
			break;
		}
		JsExpression left2 = assignment.Left;
		JsExpression jsExpression3 = left2;
		if (!(jsExpression3 is JsIdentifierExpression))
		{
			if (jsExpression3 is JsMemberExpression jsMemberExpression5)
			{
				JsExpression jsExpression4 = jsMemberExpression5.Object;
				if (!(jsExpression4 is JsSuperExpression))
				{
					if (!jsMemberExpression5.Computed)
					{
						JsExpression property2 = jsMemberExpression5.Property;
						if (property2 is JsIdentifierExpression jsIdentifierExpression7)
						{
							Emit(ecmaIrBlock2, "put_field", assignment, new AtomOperand(jsIdentifierExpression7.Name));
						}
					}
					else
					{
						Emit(ecmaIrBlock2, "put_array_el", assignment);
					}
				}
				else
				{
					Emit(ecmaIrBlock2, "put_super_value", assignment);
				}
			}
		}
		else
		{
			Emit(ecmaIrBlock2, "put_ref_value", assignment);
		}
		IrBlock ecmaIrBlock3 = NewBlock(function);
		for (int i = 0; i < num; i++)
		{
			Emit(ecmaIrBlock3, "nip", assignment.Left);
		}
		IrBlock ecmaIrBlock4 = NewBlock(function);
		IrBlock ecmaIrBlock5 = ecmaIrBlock;
		string text = assignment.Operator;
		if (1 == 0)
		{
		}
		IrBranchTerminator terminator = text switch
		{
			"&&=" => new IrBranchTerminator(id, ecmaIrBlock3.Id, Location(assignment)), 
			"||=" => new IrBranchTerminator(ecmaIrBlock3.Id, id, Location(assignment)), 
			"??=" => new IrBranchTerminator(id, ecmaIrBlock3.Id, Location(assignment)), 
			_ => throw new InvalidOperationException("Unknown logical assignment."), 
		};
		if (1 == 0)
		{
		}
		ecmaIrBlock5.Terminator = terminator;
		ecmaIrBlock2.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(assignment));
		ecmaIrBlock3.Terminator = new IrGotoTerminator(ecmaIrBlock4.Id, Location(assignment));
		return ecmaIrBlock4;
		IL_0278:
		throw Unsupported(assignment.Left);
	}

	private IrBlock EmitUpdate(IrFunction function, IrBlock block, IrScopeId scope, JsUpdateExpression update, bool valueUsed, bool foldDiscardedPostfix = false)
	{
		object obj;
		if (!(update.Operator == "++"))
		{
			if (!(update.Operator == "--"))
			{
				throw new NotSupportedException("Unsupported update operator " + update.Operator + ".");
			}
			obj = "dec";
		}
		else
		{
			obj = "inc";
		}
		string text = (string)obj;
		string text2 = ((update.Prefix || (!valueUsed & foldDiscardedPostfix)) ? text : ("post_" + text));
		JsExpression argument = update.Argument;
		JsExpression jsExpression = argument;
		JsExpression property;
		if (jsExpression is JsMemberExpression jsMemberExpression)
		{
			if (jsMemberExpression.Optional)
			{
				goto IL_05a9;
			}
			if (!jsMemberExpression.Computed)
			{
				JsExpression jsExpression2 = jsMemberExpression.Object;
				if (jsExpression2 is JsSuperExpression)
				{
					property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression))
					{
						goto IL_00e8;
					}
					Emit(block, "scope_get_var", jsMemberExpression, new AtomOperand("this"), new IrScopeOperand(scope));
					Emit(block, "scope_get_var", jsMemberExpression, new AtomOperand("home_object"), new IrScopeOperand(scope));
					Emit(block, "get_super", jsMemberExpression);
					EmitStringConstant(function, block, jsIdentifierExpression.Name, jsIdentifierExpression);
					Emit(block, "to_propkey", jsMemberExpression);
					Emit(block, "dup3", jsMemberExpression);
					Emit(block, "get_super_value", jsMemberExpression);
					Emit(block, text2, update);
					if (valueUsed)
					{
						Emit(block, update.Prefix ? "insert4" : "perm5", update);
					}
					Emit(block, "put_super_value", update);
					if (!valueUsed && text2 != text)
					{
						Emit(block, "drop", update);
					}
				}
				else
				{
					property = jsMemberExpression.Property;
					if (!(property is JsIdentifierExpression jsIdentifierExpression2))
					{
						goto IL_00e8;
					}
					JsIdentifierExpression jsIdentifierExpression3 = jsIdentifierExpression2;
					JsMemberExpression jsMemberExpression2 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
					Emit(block, "get_field2", jsMemberExpression2, new AtomOperand(jsIdentifierExpression3.Name));
					Emit(block, text2, update);
					if (valueUsed)
					{
						Emit(block, update.Prefix ? "insert2" : "perm3", update);
					}
					Emit(block, "put_field", update, new AtomOperand(jsIdentifierExpression3.Name));
					if (!valueUsed && text2 != text)
					{
						Emit(block, "drop", update);
					}
				}
			}
			else
			{
				JsMemberExpression jsMemberExpression3 = jsMemberExpression;
				block = VisitExpression(function, block, scope, jsMemberExpression3.Object);
				block = VisitExpression(function, block, scope, jsMemberExpression3.Property);
				Emit(block, "to_propkey2", jsMemberExpression3);
				Emit(block, "dup2", jsMemberExpression3);
				Emit(block, "get_array_el", jsMemberExpression3);
				Emit(block, text2, update);
				if (valueUsed)
				{
					Emit(block, update.Prefix ? "insert3" : "perm4", update);
				}
				Emit(block, "put_array_el", update);
				if (!valueUsed && text2 != text)
				{
					Emit(block, "drop", update);
				}
			}
		}
		else
		{
			if (!(jsExpression is JsIdentifierExpression jsIdentifierExpression4))
			{
				goto IL_05a9;
			}
			Emit(block, "scope_make_ref", jsIdentifierExpression4, new AtomOperand(jsIdentifierExpression4.Name), new IrScopeOperand(scope));
			Emit(block, "get_ref_value", jsIdentifierExpression4);
			string text3 = (update.Prefix ? text : ("post_" + text));
			Emit(block, text3, update);
			if (valueUsed)
			{
				Emit(block, update.Prefix ? "insert3" : "perm4", update);
			}
			Emit(block, "put_ref_value", update);
			if (!valueUsed && text3 != text)
			{
				Emit(block, "drop", update);
			}
		}
		goto IL_05b6;
		IL_05a9:
		throw Unsupported(update.Argument);
		IL_05b6:
		return block;
		IL_00e8:
		if (!(property is JsPrivateIdentifierExpression jsPrivateIdentifierExpression))
		{
			goto IL_05a9;
		}
		JsMemberExpression jsMemberExpression4 = jsMemberExpression;
		block = VisitExpression(function, block, scope, jsMemberExpression4.Object);
		Emit(block, "scope_get_private_field2", jsMemberExpression4, new AtomOperand(jsPrivateIdentifierExpression.Name), new IrScopeOperand(scope));
		Emit(block, update.Prefix ? text : ("post_" + text), update);
		if (!update.Prefix | valueUsed)
		{
			Emit(block, update.Prefix ? "insert2" : "perm3", update);
		}
		Emit(block, "scope_put_private_field", jsMemberExpression4, new AtomOperand(jsPrivateIdentifierExpression.Name), new IrScopeOperand(scope));
		if (!valueUsed && !update.Prefix)
		{
			Emit(block, "drop", update);
		}
		goto IL_05b6;
	}

	private IrBlock EmitCall(IrFunction function, IrBlock block, IrScopeId scope, JsCallExpression call)
	{
		if (call.Callee is JsIdentifierExpression { Name: "eval" } jsIdentifierExpression && !call.DirectOptional)
		{
			block = VisitExpression(function, block, scope, jsIdentifierExpression);
			foreach (JsExpression argument in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument);
			}
			Emit(block, "eval", call, new ImmediateOperand(call.Arguments.Count), new ImmediateOperand(scope.Value));
			return block;
		}
		if (call.Callee is JsSuperExpression node)
		{
			EnsureActivationBinding(function, "this_active_func");
			EnsureActivationBinding(function, "new.target");
			EnsureActivationBinding(function, "this");
			Emit(block, "scope_get_var", node, new AtomOperand("this_active_func"), new IrScopeOperand(scope));
			Emit(block, "get_super", node);
			Emit(block, "scope_get_var", node, new AtomOperand("new.target"), new IrScopeOperand(scope));
			foreach (JsExpression argument2 in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument2);
			}
			Emit(block, "call_constructor", call, new ImmediateOperand(call.Arguments.Count));
			Emit(block, "dup", call);
			Emit(block, "scope_put_var_init", call, new AtomOperand("this"), new IrScopeOperand(scope));
			return EmitClassFieldInitializerCall(function, block, call);
		}
		if (call.Callee is JsMemberExpression { Optional: false, Computed: false } jsMemberExpression && !(jsMemberExpression.Object is JsSuperExpression) && jsMemberExpression.Property is JsIdentifierExpression jsIdentifierExpression2)
		{
			block = VisitExpression(function, block, scope, jsMemberExpression.Object);
			Emit(block, "get_field2", jsMemberExpression, new AtomOperand(jsIdentifierExpression2.Name));
			if (call.Arguments.Any((JsExpression argument) => argument is JsSpreadExpression))
			{
				block = EmitSpreadCallArguments(function, block, scope, call);
				Emit(block, "perm3", call);
				Emit(block, "apply", call, new ImmediateOperand(0L));
				return block;
			}
			foreach (JsExpression argument3 in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument3);
			}
			Emit(block, "call_method", call, new ImmediateOperand(call.Arguments.Count));
			return block;
		}
		if (call.Callee is JsMemberExpression { Optional: false, Computed: false } jsMemberExpression2 && jsMemberExpression2.Object is JsSuperExpression && jsMemberExpression2.Property is JsIdentifierExpression jsIdentifierExpression3)
		{
			block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
			EmitStringConstant(function, block, jsIdentifierExpression3.Name, jsIdentifierExpression3);
			Emit(block, "get_array_el", jsMemberExpression2);
			foreach (JsExpression argument4 in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument4);
			}
			Emit(block, "call_method", call, new ImmediateOperand(call.Arguments.Count));
			return block;
		}
		if (call.Callee is JsMemberExpression { Optional: false, Computed: not false } jsMemberExpression3)
		{
			block = VisitExpression(function, block, scope, jsMemberExpression3.Object);
			block = VisitExpression(function, block, scope, jsMemberExpression3.Property);
			Emit(block, (jsMemberExpression3.Object is JsSuperExpression) ? "get_array_el" : "get_array_el2", jsMemberExpression3);
			if (call.Arguments.Any((JsExpression argument) => argument is JsSpreadExpression))
			{
				block = EmitSpreadCallArguments(function, block, scope, call);
				Emit(block, "perm3", call);
				Emit(block, "apply", call, new ImmediateOperand(0L));
				return block;
			}
			foreach (JsExpression argument5 in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument5);
			}
			Emit(block, "call_method", call, new ImmediateOperand(call.Arguments.Count));
			return block;
		}
		if (call.Callee is JsMemberExpression { Optional: false, Computed: false, Property: JsPrivateIdentifierExpression property } jsMemberExpression4)
		{
			block = VisitExpression(function, block, scope, jsMemberExpression4.Object);
			Emit(block, "scope_get_private_field2", jsMemberExpression4, new AtomOperand(property.Name), new IrScopeOperand(scope));
			foreach (JsExpression argument6 in call.Arguments)
			{
				block = VisitExpression(function, block, scope, argument6);
			}
			Emit(block, "call_method", call, new ImmediateOperand(call.Arguments.Count));
			return block;
		}
		block = VisitExpression(function, block, scope, call.Callee);
		if (call.Arguments.Any((JsExpression argument) => argument is JsSpreadExpression))
		{
			block = EmitSpreadCallArguments(function, block, scope, call);
			Emit(block, "push_undefined", call);
			Emit(block, "swap", call);
			Emit(block, "apply", call, new ImmediateOperand(0L));
			return block;
		}
		foreach (JsExpression argument7 in call.Arguments)
		{
			block = VisitExpression(function, block, scope, argument7);
		}
		Emit(block, "call", call, new ImmediateOperand(call.Arguments.Count));
		return block;
	}

	private IrBlock EmitSpreadCallArguments(IrFunction function, IrBlock block, IrScopeId scope, JsCallExpression call)
	{
		int num = call.Arguments.TakeWhile((JsExpression argument) => !(argument is JsSpreadExpression)).Count();
		for (int num2 = 0; num2 < num; num2++)
		{
			block = VisitExpression(function, block, scope, call.Arguments[num2]);
		}
		Emit(block, "array_from", call, new ImmediateOperand(num));
		Emit(block, "push_i32", call, new ImmediateOperand(num));
		for (int num3 = num; num3 < call.Arguments.Count; num3++)
		{
			if (call.Arguments[num3] is JsSpreadExpression jsSpreadExpression)
			{
				block = VisitExpression(function, block, scope, jsSpreadExpression.Argument);
				Emit(block, "append", jsSpreadExpression);
			}
			else
			{
				block = VisitExpression(function, block, scope, call.Arguments[num3]);
				Emit(block, "define_array_el", call.Arguments[num3]);
				Emit(block, "inc", call.Arguments[num3]);
			}
		}
		Emit(block, "drop", call);
		return block;
	}

	private IrBlock EmitSpreadConstructorArguments(IrFunction function, IrBlock block, IrScopeId scope, JsNewExpression created)
	{
		int num = created.Arguments.TakeWhile((JsExpression argument) => !(argument is JsSpreadExpression)).Count();
		for (int num2 = 0; num2 < num; num2++)
		{
			block = VisitExpression(function, block, scope, created.Arguments[num2]);
		}
		Emit(block, "array_from", created, new ImmediateOperand(num));
		Emit(block, "push_i32", created, new ImmediateOperand(num));
		for (int num3 = num; num3 < created.Arguments.Count; num3++)
		{
			if (created.Arguments[num3] is JsSpreadExpression jsSpreadExpression)
			{
				block = VisitExpression(function, block, scope, jsSpreadExpression.Argument);
				Emit(block, "append", jsSpreadExpression);
			}
			else
			{
				block = VisitExpression(function, block, scope, created.Arguments[num3]);
				Emit(block, "define_array_el", created.Arguments[num3]);
				Emit(block, "inc", created.Arguments[num3]);
			}
		}
		Emit(block, "drop", created);
		return block;
	}

	private static void EmitStringConstant(IrFunction function, IrBlock block, string value, JsAstNode node)
	{
		IrConstantId ecmaConstantId = new IrConstantId(function.Constants.Count);
		function.Constants.Add(new IrStringConstant(ecmaConstantId, value));
		block.Instructions.Add(new IrInstruction("push_const", new ReadOnlySingleElementList<IrOperand>(new IrConstantOperand(ecmaConstantId)), Location(node)));
	}

	private IrBlock EmitArray(IrFunction function, IrBlock block, IrScopeId scope, JsArrayExpression array)
	{
		int i;
		for (i = 0; i < array.Elements.Count && i < 32; i++)
		{
			JsExpression jsExpression = array.Elements[i];
			if ((object)jsExpression == null || jsExpression is JsSpreadExpression)
			{
				break;
			}
			block = VisitExpression(function, block, scope, array.Elements[i]);
		}
		Emit(block, "array_from", array, new ImmediateOperand(i));
		int num = i;
		bool flag = false;
		while (num < array.Elements.Count && !(array.Elements[num] is JsSpreadExpression))
		{
			if ((object)array.Elements[num] == null)
			{
				flag = true;
				num++;
				continue;
			}
			block = VisitExpression(function, block, scope, array.Elements[num]);
			Emit(block, "define_field", array, new AtomOperand(num.ToString(CultureInfo.InvariantCulture)));
			flag = false;
			num++;
		}
		if (num < array.Elements.Count)
		{
			Emit(block, "push_i32", array, new ImmediateOperand(num));
			return EmitDynamicArrayTail(function, block, scope, array, num);
		}
		if (flag)
		{
			Emit(block, "dup1", array);
			Emit(block, "push_i32", array, new ImmediateOperand(array.Elements.Count));
			Emit(block, "put_field", array, new AtomOperand("length"));
		}
		return block;
	}

	private IrBlock EmitDynamicArrayTail(IrFunction function, IrBlock block, IrScopeId scope, JsArrayExpression array, int firstIndex)
	{
		bool flag = false;
		for (int i = firstIndex; i < array.Elements.Count; i++)
		{
			JsExpression jsExpression = array.Elements[i];
			JsExpression jsExpression2 = jsExpression;
			if (!(jsExpression2 is JsSpreadExpression jsSpreadExpression))
			{
				if ((object)jsExpression2 != null)
				{
					block = VisitExpression(function, block, scope, jsExpression2);
					Emit(block, "define_array_el", jsExpression2);
					Emit(block, "inc", jsExpression2);
					flag = false;
				}
				else
				{
					Emit(block, "inc", array);
					flag = true;
				}
			}
			else
			{
				block = VisitExpression(function, block, scope, jsSpreadExpression.Argument);
				Emit(block, "append", jsSpreadExpression);
				flag = false;
			}
		}
		if (flag)
		{
			Emit(block, "dup", array);
			Emit(block, "put_field", array, new AtomOperand("length"));
		}
		else
		{
			Emit(block, "drop", array);
		}
		return block;
	}

	private IrBlock EmitObject(IrFunction function, IrBlock block, IrScopeId scope, JsObjectExpression obj)
	{
		Emit(block, "object", obj);
		foreach (JsObjectProperty property in obj.Properties)
		{
			if (property.Key == "...")
			{
				block = VisitExpression(function, block, scope, property.Value);
				Emit(block, "push_null", property);
				Emit(block, "copy_data_properties", property, new ImmediateOperand(6L));
				Emit(block, "drop", property);
				Emit(block, "drop", property);
			}
			else if (property.Kind != JsObjectPropertyKind.Value && property.Value is JsFunctionExpression jsFunctionExpression)
			{
				if ((object)property.ComputedKey != null)
				{
					block = VisitExpression(function, block, scope, property.ComputedKey);
				}
				IrConstantId ecmaConstantId = new IrConstantId(function.Constants.Count);
				IrFunctionId ecmaFunctionId = new IrFunctionId(_nextFunction++);
				function.Constants.Add(new IrFunctionConstant(ecmaConstantId, ecmaFunctionId));
				JsObjectPropertyKind kind = property.Kind;
				if (1 == 0)
				{
				}
				IrFunctionForm ecmaFunctionForm = kind switch
				{
					JsObjectPropertyKind.Getter => IrFunctionForm.Getter, 
					JsObjectPropertyKind.Setter => IrFunctionForm.Setter, 
					_ => IrFunctionForm.Method, 
				};
				if (1 == 0)
				{
				}
				IrFunctionForm form = ecmaFunctionForm;
				BuildFunction(ecmaFunctionId, null, jsFunctionExpression.Parameters, jsFunctionExpression.Body.Body, form, jsFunctionExpression.Async, jsFunctionExpression.Generator, jsFunctionExpression.DefinedArgCount, jsFunctionExpression.ParameterDefaults, jsFunctionExpression.ParameterPatterns, function.Id, scope, ecmaConstantId, Location(jsFunctionExpression), protectionTags: jsFunctionExpression.ProtectionTags);
				Emit(block, "fclosure", property, new IrConstantOperand(ecmaConstantId));
				JsObjectPropertyKind kind2 = property.Kind;
				if (1 == 0)
				{
				}
				int num = kind2 switch
				{
					JsObjectPropertyKind.Getter => 1, 
					JsObjectPropertyKind.Setter => 2, 
					_ => 0, 
				};
				if (1 == 0)
				{
				}
				int num2 = num | 4;
				if ((object)property.ComputedKey == null)
				{
					Emit(block, "define_method", property, new AtomOperand(property.Key), new ImmediateOperand(num2));
				}
				else
				{
					Emit(block, "define_method_computed", property, new ImmediateOperand(num2));
				}
			}
			else if ((object)property.ComputedKey != null)
			{
				block = VisitExpression(function, block, scope, property.ComputedKey);
				block = ((property.Value is JsClassExpression { Name: null } jsClassExpression) ? EmitClass(function, _scopeBuilders[function.Id], scope, block, string.Empty, jsClassExpression.SuperClass, jsClassExpression.Members, jsClassExpression, isDeclaration: false, null, classNameComputed: true) : VisitExpression(function, block, scope, property.Value));
				if (property.Value is JsFunctionExpression { Name: null })
				{
					Emit(block, "set_name_computed", property);
				}
				Emit(block, "define_array_el", property);
				Emit(block, "drop", property);
			}
			else
			{
				block = ((property.Value is JsClassExpression { Name: null } jsClassExpression2) ? EmitClass(function, _scopeBuilders[function.Id], scope, block, string.Empty, jsClassExpression2.SuperClass, jsClassExpression2.Members, jsClassExpression2, isDeclaration: false, property.Key) : VisitExpression(function, block, scope, property.Value));
				if (property.Value is JsFunctionExpression { Name: null })
				{
					Emit(block, "set_name", property, new AtomOperand(property.Key));
				}
				if (property.IsPrototypeSetter)
				{
					Emit(block, "set_proto", property);
					continue;
				}
				Emit(block, "define_field", property, new AtomOperand(property.Key));
			}
		}
		return block;
	}

	private static string UnaryOperation(string op)
	{
		if (1 == 0)
		{
		}
		string result = op switch
		{
			"+" => "plus", 
			"-" => "neg", 
			"!" => "lnot", 
			"~" => "not", 
			"typeof" => "typeof", 
			"delete" => "delete", 
			_ => throw new NotSupportedException("Unsupported unary operator " + op + "."), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static string BinaryOperation(string op)
	{
		if (1 == 0)
		{
		}
		string result = op switch
		{
			"+" => "add", 
			"-" => "sub", 
			"*" => "mul", 
			"/" => "div", 
			"%" => "mod", 
			"**" => "pow", 
			"<<" => "shl", 
			">>" => "sar", 
			">>>" => "shr", 
			"&" => "and", 
			"|" => "or", 
			"^" => "xor", 
			"==" => "eq", 
			"!=" => "neq", 
			"===" => "strict_eq", 
			"!==" => "strict_neq", 
			"<" => "lt", 
			"<=" => "lte", 
			">" => "gt", 
			">=" => "gte", 
			"in" => "in", 
			"instanceof" => "instanceof", 
			_ => throw new NotSupportedException("Unsupported binary operator " + op + "."), 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	private static void Emit(IrBlock block, string operation, JsAstNode node, params IrOperand[] operands)
	{
		block.Instructions.Add(new IrInstruction(operation, operands, Location(node)));
	}

	internal void EmitForDestructuring(IrBlock block, string operation, JsAstNode node, params IrOperand[] operands)
	{
		Emit(block, operation, node, operands);
	}

	internal IrBlock NewBlockForDestructuring(IrFunction function)
	{
		return NewBlock(function);
	}

	internal IrBlock EmitPatternForDestructuring(IrFunction function, IrBlock block, IrScopeId scope, JsBindingPattern pattern)
	{
		return EmitParameterExpressionPattern(function, block, scope, pattern);
	}

	internal IrBlock EmitExpressionForDestructuring(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression)
	{
		return VisitExpression(function, block, scope, expression);
	}

	internal IrBlock EmitExpressionWithInferredNameForDestructuring(IrFunction function, IrBlock block, IrScopeId scope, JsExpression expression, string? inferredName)
	{
		return EmitExpressionWithInferredName(function, block, scope, expression, inferredName);
	}

	internal SourceLocation LocationForDestructuring(JsAstNode node)
	{
		return Location(node);
	}

	internal IrBlock EmitAssignmentReferenceForDestructuring(IrFunction function, IrBlock block, IrScopeId scope, JsExpression target, out int stackDepth)
	{
		if (!(target is JsIdentifierExpression jsIdentifierExpression))
		{
			if (target is JsMemberExpression { Optional: false } jsMemberExpression)
			{
				if (jsMemberExpression.Computed)
				{
					JsMemberExpression jsMemberExpression2 = jsMemberExpression;
					block = VisitExpression(function, block, scope, jsMemberExpression2.Object);
					block = VisitExpression(function, block, scope, jsMemberExpression2.Property);
					Emit(block, "to_propkey2", jsMemberExpression2);
					stackDepth = 2;
					return block;
				}
				JsExpression property = jsMemberExpression.Property;
				if (property is JsIdentifierExpression)
				{
					stackDepth = 1;
					return VisitExpression(function, block, scope, jsMemberExpression.Object);
				}
			}
			throw Unsupported(target);
		}
		Emit(block, "scope_make_persistent_ref", jsIdentifierExpression, new AtomOperand(jsIdentifierExpression.Name), new IrScopeOperand(scope));
		stackDepth = 2;
		return block;
	}

	internal void EmitAssignmentStoreForDestructuring(IrBlock block, JsExpression target)
	{
		if (!(target is JsIdentifierExpression node))
		{
			if (target is JsMemberExpression { Optional: false } jsMemberExpression)
			{
				if (jsMemberExpression.Computed)
				{
					JsMemberExpression node2 = jsMemberExpression;
					Emit(block, "put_array_el", node2);
					return;
				}
				JsExpression property = jsMemberExpression.Property;
				if (property is JsIdentifierExpression jsIdentifierExpression)
				{
					Emit(block, "put_field", jsMemberExpression, new AtomOperand(jsIdentifierExpression.Name));
					return;
				}
			}
			throw Unsupported(target);
		}
		Emit(block, "put_ref_value", node);
	}

	private static NotSupportedException Unsupported(JsAstNode node)
	{
		return new NotSupportedException("Scope construction does not support " + node.GetType().Name + " yet.");
	}

	private static IEnumerable<string> EnumerateBindings(JsBindingPattern pattern)
	{
		if (1 == 0)
		{
		}
		IEnumerable<string> result = ((pattern is JsIdentifierPattern jsIdentifierPattern) ? new ReadOnlySingleElementList<string>(jsIdentifierPattern.Name) : ((pattern is JsAssignmentPattern jsAssignmentPattern) ? EnumerateBindings(jsAssignmentPattern.Left) : ((pattern is JsRestPattern jsRestPattern) ? EnumerateBindings(jsRestPattern.Argument) : ((pattern is JsArrayPattern jsArrayPattern) ? jsArrayPattern.Elements.Where((JsBindingPattern item) => (object)item != null).SelectMany((JsBindingPattern item) => EnumerateBindings(item)) : ((!(pattern is JsObjectPattern jsObjectPattern)) ? Array.Empty<string>() : jsObjectPattern.Properties.SelectMany((JsObjectBindingProperty property) => EnumerateBindings(property.Value)))))));
		if (1 == 0)
		{
		}
		return result;
	}

	private static bool HasUseStrictDirective(IReadOnlyList<JsStatement> statements)
	{
		return statements.FirstOrDefault() is JsExpressionStatement { Expression: JsLiteralExpression { Kind: JavaScriptTokenKind.String } expression } && expression.Raw == "use strict";
	}

	private static IrInstruction Instruction(string operation, JsAstNode node)
	{
		return new IrInstruction(operation, Array.Empty<IrOperand>(), Location(node));
	}

	private static double ParseNumber(string raw)
	{
		string text = raw.Replace("_", string.Empty, StringComparison.Ordinal);
		double result;
		if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
		{
			if (!text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
			{
				if (!text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
				{
					result = double.Parse(text, CultureInfo.InvariantCulture);
				}
				else
				{
					string text2 = text;
					result = Convert.ToInt64(text2.Substring(2, text2.Length - 2), 8);
				}
			}
			else
			{
				string text2 = text;
				result = Convert.ToInt64(text2.Substring(2, text2.Length - 2), 2);
			}
		}
		else
		{
			string text2 = text;
			result = Convert.ToInt64(text2.Substring(2, text2.Length - 2), 16);
		}
		return result;
	}

	private static SourceLocation Location(JsAstNode node)
	{
		return new SourceLocation(node.Line, node.Column);
	}

	private sealed class ReadOnlyArray<T>(T[] values) : IReadOnlyList<T>
	{
		public T this[int index] => values[index];
		public int Count => values.Length;
		public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => values.GetEnumerator();
	}

	private sealed class ReadOnlySingleElementList<T>(T value) : IReadOnlyList<T>
	{
		public T this[int index] => index == 0 ? value : throw new ArgumentOutOfRangeException(nameof(index));
		public int Count => 1;
		public IEnumerator<T> GetEnumerator() { yield return value; }
		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}
}
