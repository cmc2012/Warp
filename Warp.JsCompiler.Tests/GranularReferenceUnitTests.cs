using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Small, single-feature reference comparisons.  Keeping every construct in a
/// separate module makes a bytecode mismatch point at one lowering rule rather
/// than at a broad fixture.
/// </summary>
public sealed class GranularReferenceUnitTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // Expressions and assignment targets.
            yield return Case("expr-comma", "let value = (1, 2, 3);", "expr-comma");
            yield return Case("expr-conditional", "let value = flag ? left : right;", "expr-conditional");
            yield return Case("expr-nullish", "let value = left ?? right;", "expr-nullish");
            yield return Case("expr-logical-and", "let value = left && right;", "expr-logical-and");
            yield return Case("expr-logical-or", "let value = left || right;", "expr-logical-or");
            yield return Case("expr-optional-member", "let value = target?.field;", "expr-optional-member");
            yield return Case("expr-optional-call", "let value = fn?.(arg);", "expr-optional-call");
            yield return Case("expr-computed-member", "let value = target[key];", "expr-computed-member");
            yield return Case("expr-post-increment-local", "let value = 1; value++;", "expr-post-increment-local");
            yield return Case("expr-pre-increment-member", "++target.field;", "expr-pre-increment-member");
            yield return Case("expr-delete-member", "delete target.field;", "expr-delete-member");
            yield return Case("expr-typeof-unbound", "let value = typeof missing;", "expr-typeof-unbound");
            yield return Case("expr-template", "let value = `prefix-${name}-suffix`;", "expr-template");
            yield return Case("expr-spread-array", "let value = [first, ...items, last];", "expr-spread-array");
            yield return Case("expr-spread-call", "invoke(first, ...items, last);", "expr-spread-call");
            yield return Case("expr-object-computed", "let value = { fixed: 1, [key]: item };", "expr-object-computed");

            // Branches, scopes, and loop exits.
            yield return Case("control-if-else", "if (flag) left(); else right();", "control-if-else");
            yield return Case("control-nested-branch", "if (outer) { if (inner) hit(); } else miss();", "control-nested-branch");
            yield return Case("control-switch-default", "switch (tag) { case 1: one(); break; default: other(); }", "control-switch-default");
            yield return Case("control-switch-fallthrough", "switch (tag) { case 1: first(); case 2: second(); break; }", "control-switch-fallthrough");
            yield return Case("control-while-break", "while (ready()) { if (stop()) break; work(); }", "control-while-break");
            yield return Case("control-do-while-continue", "do { if (skip()) continue; work(); } while (ready());", "control-do-while-continue");
            yield return Case("control-for-lexical", "for (let index = 0; index < limit; index++) consume(index);", "control-for-lexical");
            yield return Case("control-for-var", "for (var index = 0; index < limit; index++) consume(index);", "control-for-var");
            yield return Case("control-for-in", "for (const key in object) consume(key);", "control-for-in");
            yield return Case("control-for-of", "for (const value of values) consume(value);", "control-for-of");
            yield return Case("control-labelled-break", "outer: for (;;) { for (;;) { break outer; } }", "control-labelled-break");
            yield return Case("control-labelled-continue", "outer: for (const row of rows) { for (const cell of row) { if (cell) continue outer; consume(cell); } }", "control-labelled-continue");
            // Functions, parameter environments, and closures.
            yield return Case("function-declaration", "function identity(value) { return value; }", "function-declaration");
            yield return Case("function-expression-name", "const fn = function named(value) { return value; };", "function-expression-name");
            yield return Case("function-arrow-expression", "const fn = value => value + 1;", "function-arrow-expression");
            yield return Case("function-arrow-this", "const fn = () => this.value;", "function-arrow-this");
            yield return Case("function-default-parameter", "function select(value = fallback()) { return value; }", "function-default-parameter");
            yield return Case("function-rest-parameter", "function collect(first, ...rest) { return rest; }", "function-rest-parameter");
            yield return Case("function-array-parameter", "function first([value]) { return value; }", "function-array-parameter");
            yield return Case("function-object-parameter", "function read({ value }) { return value; }", "function-object-parameter");
            yield return Case("function-nested-closure", "function make(value) { return function inner() { return value; }; }", "function-nested-closure");
            yield return Case("function-recursion", "function count(value) { return value ? count(value - 1) : 0; }", "function-recursion");
            yield return Case("function-generator-yield", "function* values() { yield 1; yield 2; }", "function-generator-yield");
            yield return Case("function-async-await", "async function load() { return await fetchValue(); }", "function-async-await");

            // Exceptions and destructuring have distinct control-flow shapes.
            yield return Case("exception-throw", "function fail() { throw new Error('x'); }", "exception-throw");
            yield return Case("exception-catch-binding", "try { work(); } catch (error) { handle(error); }", "exception-catch-binding");
            yield return Case("exception-finally", "try { work(); } finally { cleanup(); }", "exception-finally");
            yield return Case("exception-catch-finally", "try { work(); } catch (error) { handle(error); } finally { cleanup(); }", "exception-catch-finally");
            yield return Case("destructure-array-default", "const [first = fallback(), second] = values;", "destructure-array-default");
            yield return Case("destructure-array-rest", "const [first, ...rest] = values;", "destructure-array-rest");
            yield return Case("destructure-object-alias", "const { source: local } = value;", "destructure-object-alias");
            yield return Case("destructure-object-default", "const { value = fallback() } = source;", "destructure-object-default");
            yield return Case("destructure-object-rest", "const { first, ...rest } = value;", "destructure-object-rest");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_one_language_unit(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source, string fileStem)
        => new object[] { name, source, $"/tmp/reference-units/granular/{fileStem}.js" };
}
