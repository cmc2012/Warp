using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>
/// Differential coverage for resolver rules whose output depends on source
/// order, nested pattern shape, or the final branch layout.
/// </summary>
public sealed class JavaScriptResolverBoundaryReferenceTests
{
    public static IEnumerable<object[]> Cases
    {
        get
        {
            yield return Case("arrow-local-before-this",
                "function make(id) { return value => ({ id, value, owner: this.name }); }");
            yield return Case("arrow-this-before-local",
                "function make(id) { return value => ({ owner: this.name, id, value }); }");
            yield return Case("arrow-super-captures",
                "class Base { set value(v) {} } class Child extends Base { write(key, value) { return () => super[key] = value; } }");
            yield return Case("nested-array-default",
                "function read({ value: [head, ...tail] = [] }) { return [head, tail]; }");
            yield return Case("nested-object-default",
                "function read([{ value = 1 } = {}]) { return value; }");
            yield return Case("static-private-field-only",
                "class Box { static #value = 1; static read() { return this.#value; } }");
            yield return Case("static-private-brand-and-field",
                "class Box { static #value = 1; static #next() { return ++this.#value; } static read() { return this.#next(); } }");
            yield return Case("computed-field-order",
                "const a = key(1), b = key(2); class Box { [a] = init(1); static [b] = init(2); value = init(3); }");
            yield return Case("branch-layout-nested-finally",
                "function run(items) { outer: for (const item of items) { try { if (item.done) break outer; if (item.skip) continue; use(item); } finally { close(item); } } return items; }");
            yield return Case("class-explicit-constructor-source-order",
                "class Box { before() {} constructor(value) { this.value = value; } after() {} }");
            yield return Case("class-instance-init-after-default-constructor",
                "class Box { value = 1; read() { return this.value; } }");
            yield return Case("class-instance-static-init-order",
                "class Box { first = init(1); static second = init(2); third = init(3); static fourth = init(4); }");
            yield return Case("class-methods-before-default-constructor",
                "class Box { static one() {} two() {} static three() {} value = 1; }");
            yield return Case("class-private-accessor-order",
                "class Box { static get #value() { return 1; } static set #value(v) {} static read() { return this.#value; } }");
            yield return Case("nested-object-object-default",
                "function read({ outer: { inner = 1 } = {} } = {}) { return inner; }");
            yield return Case("nested-array-object-array-default",
                "function read([{ values: [head = 1, ...tail] = [] } = {}] = []) { return [head, tail]; }");
            yield return Case("nested-object-rest",
                "function read({ outer: { value, ...rest } = {} }) { return [value, rest]; }");
            yield return Case("nested-array-holes",
                "function read([, [head, , tail] = []]) { return [head, tail]; }");
            yield return Case("class-explicit-constructor-with-fields",
                "class Box { before = init(1); constructor(value = init(2)) { this.value = value; } static after = init(3); }");
            yield return Case("class-private-method-field-constructor-order",
                "class Box { #first = 1; #read() { return this.#first; } constructor() {} #last = 2; value() { return this.#read() + this.#last; } }");
            yield return Case("class-static-private-accessor-with-field",
                "class Box { static #seed = 1; static get #value() { return this.#seed; } static set #value(v) { this.#seed = v; } static read() { return this.#value; } }");
            yield return Case("class-computed-method-field-interleave",
                "class Box { [key(1)]() {} [key(2)] = init(2); static [key(3)]() {} static [key(4)] = init(4); }");
            yield return Case("class-expression-fields-and-methods",
                "const Box = class Named { value = 1; static read() { return Named; } method() { return this.value; } }; export { Box };");
            yield return Case("nested-object-computed-default",
                "function read({ [key()]: { value = init() } = {} }) { return value; }");
            yield return Case("nested-array-rest-object",
                "function read([head, ...[{ value = 1 }]]) { return [head, value]; }");
            yield return Case("nested-object-array-rest",
                "function read({ values: [head, ...tail] = [1] } = {}) { return [head, tail]; }");
            yield return Case("nested-parameter-default-chain",
                "function read({ left: [first = init(1)] = [], right: { second = init(2) } = {} } = {}) { return [first, second]; }");
            yield return Case("arrow-nested-this-capture-order",
                "function make(first, second) { return () => ({ first, nested: () => this.name, second }); }");
            yield return Case("arrow-nested-local-capture-order",
                "function make(first, second) { return () => ({ nested: () => second, first, owner: this.name }); }");
            yield return Case("label-switch-finally-layout",
                "function run(value) { outer: try { switch (value) { case 0: break outer; case 1: return use(value); default: throw value; } } finally { close(value); } return value; }");
            yield return Case("loop-continue-finally-layout",
                "function run(items) { for (let i = 0; i < items.length; i++) { try { if (!items[i]) continue; use(items[i]); } finally { close(i); } } }");
            yield return Case("lexical-array-binding-then-assignment",
                "function update(source, next) { let [value = init(), ...rest] = source; value = next; return [value, rest]; }");
            yield return Case("lexical-object-binding-then-update",
                "function update(source) { let { value = init() } = source; return [value++, ++value, value]; }");
            yield return Case("lexical-postfix-value-through-finally",
                "function update(value) { let index = value; try { return index++; } finally { trace(index); } }");
            yield return Case("lexical-prefix-value-in-expression",
                "function update(value) { let index = value; return consume(++index, index); }");
            yield return Case("for-of-array-binding-update",
                "function collect(entries) { const out = []; for (let [key, value = init()] of entries) { value++; out.push(key, value); } return out; }");
            yield return Case("for-in-lexical-binding-update",
                "function collect(source) { const out = []; for (let key in source) { key += suffix(); out.push(key); } return out; }");
            yield return Case("for-of-nested-binding-closure",
                "function collect(entries) { const out = []; for (const { values: [head, ...tail] = [] } of entries) out.push(() => [head, tail]); return out; }");
            yield return Case("catch-array-binding-update",
                "function run(task) { try { return task(); } catch ([code = fallback(), ...rest]) { code++; return [code, rest]; } }");
            yield return Case("for-of-captured-binding-continue",
                "function collect(items) { const out = []; for (const item of items) { out.push(() => item); if (skip(item)) continue; use(item); } return out; }");
            yield return Case("for-of-captured-binding-break-finally",
                "function collect(items) { const out = []; for (const item of items) { try { out.push(() => item); if (stop(item)) break; } finally { trace(item); } } return out; }");
            yield return Case("for-in-captured-binding",
                "function collect(source) { const out = []; for (const key in source) out.push(() => key); return out; }");
            yield return Case("classic-for-captured-binding",
                "function collect(limit) { const out = []; for (let index = 0; index < limit; index++) out.push(() => index); return out; }");
            yield return Case("for-of-captured-return-finally",
                "function find(items) { for (const item of items) { try { const read = () => item; if (accept(item)) return read; } finally { trace(item); } } }");
            yield return Case("for-of-captured-throw",
                "function run(items) { for (const item of items) { const read = () => item; if (fail(item)) throw read; use(read); } }");
            yield return Case("nested-captured-labelled-continue",
                "function collect(groups) { const out = []; outer: for (const group of groups) { const groupRead = () => group; for (const item of group) { out.push(() => [groupRead(), item]); if (skip(item)) continue outer; } } return out; }");
            yield return Case("classic-for-captured-continue-break",
                "function collect(limit) { const out = []; for (let index = 0; index < limit; index++) { out.push(() => index); if (skip(index)) continue; if (stop(index)) break; } return out; }");
            yield return Case("for-in-captured-break",
                "function collect(source) { const out = []; for (const key in source) { out.push(() => key); if (stop(key)) break; } return out; }");
            yield return Case("captured-block-normal-exit",
                "function make(value) { let read; { const local = value; read = () => local; } return read; }");
            yield return Case("captured-block-return-finally",
                "function make(value) { try { let local = value; return () => local; } finally { trace(value); } }");
            yield return Case("captured-catch-binding",
                "function make(task) { try { task(); } catch (error) { return () => error; } }");
            yield return Case("captured-block-labelled-break",
                "function make(value) { let read; outer: { const local = value; read = () => local; if (stop(value)) break outer; use(local); } return read; }");
            yield return Case("block-shadows-parameter-capture",
                "function make(value) { let read; { let value = init(); read = () => value; } return [value, read]; }");
            yield return Case("sibling-block-same-name-captures",
                "function make() { let first, second; { const value = init(1); first = () => value; } { const value = init(2); second = () => value; } return [first, second]; }");
            yield return Case("catch-shadows-outer-binding",
                "function make(error, task) { let read; try { task(); } catch (error) { read = () => error; } return [error, read]; }");
            yield return Case("captured-catch-object-pattern",
                "function make(task) { try { task(); } catch ({ code, detail: { message } = {} }) { return () => [code, message]; } }");
            yield return Case("switch-lexical-capture",
                "function make(tag) { let read; switch (tag) { case 1: { const value = init(1); read = () => value; break; } default: { const value = init(2); read = () => value; } } return read; }");
            yield return Case("nested-block-shadow-captures",
                "function make() { let outerRead, innerRead; { const value = init(1); outerRead = () => value; { const value = init(2); innerRead = () => value; } } return [outerRead, innerRead]; }");
            yield return Case("block-let-shadows-function-var",
                "function read(value) { var result = value; { let result = init(); use(result); } return result; }");
            yield return Case("nested-let-shadows-outer-let",
                "function read(value) { let current = value; { let current = init(); use(current); } return current; }");
            yield return Case("for-let-shadows-parameter",
                "function collect(index, limit) { const out = []; for (let index = 0; index < limit; index++) out.push(index); return [index, out]; }");
            yield return Case("catch-shadows-outer-lexical",
                "function read(task) { let error = fallback(); try { task(); } catch (error) { use(error); } return error; }");
            yield return Case("switch-shared-lexical-capture",
                "function make(tag) { let read; switch (tag) { case 1: const value = init(); read = () => value; break; case 2: read = () => value; } return read; }");
            yield return Case("switch-shared-lexical-fallthrough",
                "function read(tag) { switch (tag) { case 0: let value = init(); case 1: return value; default: return fallback(); } }");
            yield return Case("switch-case-captures-shared-binding",
                "function make(tag) { let read; switch (tag) { case 0: let value = init(); break; case 1: read = () => value; break; } return read; }");
            yield return Case("block-function-capture",
                "function make() { let read; { function value() { return 1; } read = () => value; } return read; }");
            yield return Case("block-function-shadows-outer",
                "function make(value) { let read; { function value() { return 1; } read = () => value; } return [value, read]; }");
            yield return Case("sibling-block-function-captures",
                "function make() { let first, second; { function value() { return 1; } first = () => value; } { function value() { return 2; } second = () => value; } return [first, second]; }");
            yield return Case("anonymous-class-variable-name",
                "function make() { const Entry = class { static read() { return this.name; } }; return Entry; }");
            yield return Case("anonymous-class-assignment-name",
                "function make() { let Entry; Entry = class { static read() { return this.name; } }; return Entry; }");
            yield return Case("anonymous-class-property-name",
                "function make() { return { Entry: class { static read() { return this.name; } } }; }");
            yield return Case("array-binding-default-function-name",
                "function make(source) { const [read = function() { return 1; }] = source; return read; }");
            yield return Case("object-binding-default-arrow-name",
                "function make(source) { const { read = () => 1 } = source; return read; }");
            yield return Case("parameter-default-function-name",
                "function make(read = function() { return 1; }) { return read; }");
            yield return Case("logical-assignment-arrow-name",
                "function make(read) { read ||= () => 1; return read; }");
            yield return Case("computed-property-function-name",
                "function make() { return { ['read']: function() { return 1; } }; }");
            yield return Case("computed-property-class-name",
                "function make() { return { ['Entry']: class { static read() { return this.name; } } }; }");
            yield return Case("array-assignment-default-function-name",
                "function make(source, read) { [read = function() { return 1; }] = source; return read; }");
            yield return Case("object-assignment-default-arrow-name",
                "function make(source, read) { ({ read = () => 1 } = source); return read; }");
            yield return Case("logical-and-assignment-function-name",
                "function make(read) { read &&= function() { return 1; }; return read; }");
            yield return Case("logical-nullish-assignment-class-name",
                "function make(Entry) { Entry ??= class { static read() { return this.name; } }; return Entry; }");
            yield return Case("computed-expression-function-name",
                "function make(key) { return { [key()]: function() { return 1; } }; }");
            yield return Case("computed-expression-class-name",
                "function make(key) { return { [key()]: class { static read() { return this.name; } } }; }");
            yield return Case("anonymous-class-extends-variable-name",
                "function make(Base) { const Entry = class extends Base { static read() { return this.name; } }; return Entry; }");
            yield return Case("anonymous-class-explicit-constructor-name",
                "function make() { const Entry = class { constructor(value) { this.value = value; } read() { return this.value; } }; return Entry; }");
            yield return Case("anonymous-class-instance-field-name",
                "function make() { const Entry = class { value = 1; read() { return this.value; } }; return Entry; }");
            yield return Case("anonymous-class-explicit-constructor-field-order",
                "function make() { const Entry = class { value = 1; constructor(seed) { this.seed = seed; } read() { return this.value + this.seed; } }; return Entry; }");
            yield return Case("anonymous-class-field-outer-capture",
                "function make(seed) { const Entry = class { value = seed; read() { return this.value; } }; return Entry; }");
            yield return Case("anonymous-class-multiple-instance-fields",
                "function make(seed) { const Entry = class { first = seed; second = this.first + 1; read() { return this.second; } }; return Entry; }");
            yield return Case("anonymous-class-static-field",
                "function make(seed) { const Entry = class { static value = seed; static read() { return this.value; } }; return Entry; }");
            yield return Case("anonymous-class-static-field-after-method",
                "function make(seed) { const Entry = class { static read() { return this.value; } static value = seed; }; return Entry; }");
            yield return Case("anonymous-class-multiple-static-fields",
                "function make(seed) { const Entry = class { static first = seed; static second = seed + 1; static read() { return this.second; } }; return Entry; }");
            yield return Case("named-class-expression-field-self-reference",
                "function make() { const Entry = class Named { value = Named; read() { return this.value; } }; return Entry; }");
            yield return Case("anonymous-class-computed-instance-field",
                "function make(key, seed) { const Entry = class { [key()] = seed; }; return Entry; }");
            yield return Case("anonymous-class-computed-static-field",
                "function make(key, seed) { const Entry = class { static [key()] = seed; }; return Entry; }");
            yield return Case("anonymous-class-computed-field-function-name",
                "function make(key) { const Entry = class { [key()] = function() { return 1; }; }; return Entry; }");
            yield return Case("anonymous-class-computed-field-class-name",
                "function make(key) { const Entry = class { [key()] = class { static read() { return this.name; } }; }; return Entry; }");
            yield return Case("anonymous-class-private-instance-field",
                "function make(seed) { const Entry = class { #value = seed; read() { return this.#value; } }; return Entry; }");
            yield return Case("anonymous-class-private-field-function-name",
                "function make() { const Entry = class { #value = function() { return 1; }; read() { return this.#value; } }; return Entry; }");
            yield return Case("anonymous-class-private-field-class-name",
                "function make() { const Entry = class { #value = class { static read() { return this.name; } }; read() { return this.#value; } }; return Entry; }");
            yield return Case("anonymous-class-private-static-field",
                "function make(seed) { const Entry = class { static #value = seed; static read() { return this.#value; } }; return Entry; }");
            yield return Case("anonymous-class-private-method-brand",
                "function make(seed) { const Entry = class { #value = seed; #read() { return this.#value; } read() { return this.#read(); } }; return Entry; }");
            yield return Case("anonymous-class-private-accessor",
                "function make(seed) { const Entry = class { #value = seed; get #current() { return this.#value; } set #current(value) { this.#value = value; } read() { return this.#current; } }; return Entry; }");
            yield return Case("anonymous-class-static-private-method",
                "function make(seed) { const Entry = class { static #value = seed; static #read() { return this.#value; } static read() { return this.#read(); } }; return Entry; }");
            yield return Case("anonymous-class-static-private-accessor",
                "function make(seed) { const Entry = class { static #value = seed; static get #current() { return this.#value; } static set #current(value) { this.#value = value; } static read() { return this.#current; } }; return Entry; }");
            yield return Case("anonymous-class-private-getter-only",
                "function make(seed) { const Entry = class { #value = seed; get #current() { return this.#value; } read() { return this.#current; } }; return Entry; }");
            yield return Case("anonymous-class-private-setter-only",
                "function make(seed) { const Entry = class { #value = seed; set #current(value) { this.#value = value; } write(value) { this.#current = value; return this.#value; } }; return Entry; }");
            yield return Case("anonymous-derived-class-private-brand",
                "function make(Base, seed) { const Entry = class extends Base { #value = seed; #read() { return this.#value; } constructor(value) { super(value); } read() { return this.#read(); } }; return Entry; }");
            yield return Case("anonymous-class-private-method-closure",
                "function make(seed) { const Entry = class { #value = seed; #read() { return () => this.#value; } read() { return this.#read()(); } }; return Entry; }");
            yield return Case("nested-class-private-name-shadowing",
                "function make(seed) { const Outer = class { #value = seed; create(next) { return class { #value = next; read() { return this.#value; } }; } read() { return this.#value; } }; return Outer; }");
            yield return Case("nested-array-default-class-name",
                "function make(source) { const [[Entry = class { static read() { return this.name; } }]] = source; return Entry; }");
            yield return Case("module-variable-anonymous-class-name",
                "const Entry = class { static read() { return this.name; } }; export { Entry };");
            yield return Case("module-logical-assignment-arrow-name",
                "let read; read ||= () => 1; export { read };");
            yield return Case("module-object-binding-default-function-name",
                "const { read = function() { return 1; } } = source; export { read };");
            yield return Case("module-computed-property-class-name",
                "export default { ['Entry']: class { static read() { return this.name; } } };");
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Emits_reference_bytecode_for_resolver_boundaries(string _, string source, string fileName)
        => GoldenAssert.ReferenceModule(source, fileName);

    private static object[] Case(string name, string source) =>
        [name, source, $"/tmp/reference-resolver-boundaries/{name}.js"];
}
