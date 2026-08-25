using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Differential probes for declaration instantiation, direct-eval
/// environments, named-expression scopes, and per-iteration bindings.
/// </summary>
public sealed class ScopeEvalReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            // Declaration instantiation and source-order independence.
            yield return Case("function-call-before-declaration", "const result = read(); function read() { return value(); }");
            yield return Case("mutually-recursive-declarations", "function even(n) { return n === 0 || odd(n - 1); } function odd(n) { return n !== 0 && even(n - 1); }");
            yield return Case("var-read-before-initializer", "function read() { observe(value); var value = init(); return value; }");
            yield return Case("function-shadows-var-declaration", "function read() { var value; function value() { return 1; } return value; }");
            yield return Case("block-function-after-use", "function read(flag) { if (flag) { const result = value(); function value() { return 1; } return result; } }");
            yield return Case("nested-function-captures-later-var", "function make() { const read = () => value; var value = init(); return read; }");
            yield return Case("nested-function-captures-later-let", "function make() { const read = () => value; let value = init(); return read; }");
            yield return Case("parameter-shadowed-by-var", "function read(value) { var value = update(value); return value; }");

            // Direct eval forces otherwise optimizable bindings to remain visible.
            yield return Case("eval-reads-parameter", "function read(value) { return eval('value'); }");
            yield return Case("eval-writes-parameter", "function update(value) { eval('value = next()'); return value; }");
            yield return Case("eval-reads-var", "function read() { var value = init(); return eval('value'); }");
            yield return Case("eval-writes-var", "function update() { var value = init(); eval('value = next()'); return value; }");
            yield return Case("eval-reads-let", "function read() { let value = init(); return eval('value'); }");
            yield return Case("eval-writes-let", "function update() { let value = init(); eval('value = next()'); return value; }");
            yield return Case("eval-reads-const", "function read() { const value = init(); return eval('value'); }");
            yield return Case("eval-inside-nested-block", "function read() { let outer = init(1); { let inner = init(2); return eval('[outer, inner]'); } }");
            yield return Case("eval-after-block-exit", "function read() { let value = init(1); { let value = init(2); use(value); } return eval('value'); }");
            yield return Case("eval-in-catch-binding", "function read(task) { try { task(); } catch (error) { return eval('error'); } }");
            yield return Case("eval-in-for-let", "function collect(limit) { const out = []; for (let index = 0; index < limit; index++) out.push(eval('index')); return out; }");
            yield return Case("eval-preserves-function-declaration", "function read() { function local() { return 1; } return eval('local()'); }");

            // Eval nested in arrows inherits the surrounding special bindings.
            yield return Case("arrow-eval-reads-outer-local", "function make(value) { return () => eval('value'); }");
            yield return Case("arrow-eval-reads-this", "function make() { return () => eval('this.value'); }");
            yield return Case("arrow-eval-reads-arguments", "function make(value) { return () => eval('arguments[0]'); }");
            yield return Case("nested-arrow-eval-reads-new-target", "function Factory() { return () => () => eval('new.target'); }");
            yield return Case("method-arrow-eval-reads-super", "class Base { read() { return 1; } } class Child extends Base { make() { return () => eval('super.read()'); } }");
            yield return Case("eval-in-default-parameter", "function read(value = eval('fallback()')) { return value; }");
            yield return Case("arrow-eval-in-default-parameter", "function read(value = (() => eval('fallback()'))()) { return value; }");
            yield return Case("eval-parameter-closure", "function make(value = eval('next()')) { return () => value; }");

            // Indirect eval and shadowed eval are ordinary calls.
            yield return Case("indirect-eval-comma", "function run(source) { return (0, eval)(source); }");
            yield return Case("indirect-eval-alias", "function run(source) { const execute = eval; return execute(source); }");
            yield return Case("shadowed-eval-parameter", "function run(eval, source) { return eval(source); }");
            yield return Case("shadowed-eval-block", "function run(source) { { const eval = execute; return eval(source); } }");

            // Named expressions have private self-bindings distinct from outer names.
            yield return Case("named-function-expression-recursion", "const factorial = function inner(value) { return value < 2 ? 1 : value * inner(value - 1); };");
            yield return Case("named-function-expression-outer-shadow", "const inner = outer(); const fn = function inner() { return inner; };");
            yield return Case("named-function-expression-captured", "const fn = function inner() { return () => inner; };");
            yield return Case("named-class-expression-self-reference", "const Entry = class Inner { static self() { return Inner; } };");
            yield return Case("named-class-expression-field-self-reference", "const Entry = class Inner { value = Inner; };");
            yield return Case("named-class-expression-static-self-reference", "const Entry = class Inner { static value = Inner; };");
            yield return Case("named-class-expression-extends-order", "const Entry = class Inner extends base(Inner) { };");

            // A fresh lexical environment is created for each captured iteration.
            yield return Case("for-let-two-captured-bindings", "function make(limit) { const out = []; for (let left = 0, right = limit; left < right; left++, right--) out.push(() => [left, right]); return out; }");
            yield return Case("for-let-update-closure", "function make(limit) { const out = []; for (let index = 0; index < limit; index = step(index)) out.push(() => index); return out; }");
            yield return Case("for-of-destructured-capture", "function make(entries) { const out = []; for (const [key, value] of entries) out.push(() => [key, value]); return out; }");
            yield return Case("for-of-catch-shadow-capture", "function make(items) { const out = []; for (const item of items) { try { use(item); } catch (item) { out.push(() => item); } } return out; }");
            yield return Case("for-in-body-let-capture", "function make(source) { const out = []; for (const key in source) { let value = source[key]; out.push(() => [key, value]); } return out; }");

            // typeof/delete and labelled blocks have special reference behavior.
            yield return Case("typeof-unbound-inside-function", "function inspect() { return typeof missing; }");
            yield return Case("typeof-member-getter", "function inspect(target) { return typeof target.value; }");
            yield return Case("delete-computed-member-order", "function remove(target, key) { return target()[key()] && delete target()[key()]; }");
            yield return Case("labelled-block-break-value", "function run(flag) { block: { before(); if (flag) break block; after(); } return done(); }");
            yield return Case("nested-labels-same-loop", "function run(items) { first: second: for (const item of items) { if (skip(item)) continue first; if (stop(item)) break second; use(item); } }");
            yield return Case("labelled-switch-through-finally", "function run(tag) { exit: try { switch (tag) { case 0: break exit; default: use(tag); } } finally { cleanup(tag); } }");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_scope_or_eval_boundary(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source)
        => [name, source, $"/tmp/reference-units/scope-eval/{name}.js"];
}
