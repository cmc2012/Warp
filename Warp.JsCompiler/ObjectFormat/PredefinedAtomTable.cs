namespace Warp.JsCompiler.ObjectFormat;

/// <summary>
/// The fixed atom ABI used by the target runtime. Index zero is reserved;
/// every following position is the serialized atom value. Keep this list in
/// runtime order: it is deliberately data, not a collection of emitter
/// special-cases.
/// </summary>
internal static class PredefinedAtomTable
{
    private static readonly string[] Names =
    [
        "", "null", "false", "true", "if", "else", "return", "var", "this", "delete", "void", "typeof", "new", "in", "instanceof", "do", "while", "for", "break", "continue", "switch", "case", "default", "throw", "try", "catch", "finally", "function", "debugger", "with", "__FILE__", "__DIR__", "class", "const", "enum", "export", "extends", "import", "super", "implements", "interface", "let", "package", "private", "protected", "public", "static", "yield", "await", "",
        "length", "fileName", "lineNumber", "message", "errors", "stack", "name", "toString", "toLocaleString", "valueOf", "eval", "prototype", "constructor", "configurable", "writable", "enumerable", "value", "get", "set", "of", "__proto__", "undefined", "number", "boolean", "string", "object", "symbol", "integer", "unknown", "arguments", "callee", "caller", "<eval>", "<ret>", "<var>", "<arg_var>", "<with>", "lastIndex", "target", "index", "input", "defineProperties", "apply", "join", "concat", "split", "construct", "getPrototypeOf", "setPrototypeOf", "isExtensible", "preventExtensions", "has", "deleteProperty", "defineProperty", "getOwnPropertyDescriptor", "ownKeys", "add", "done", "next", "values", "source", "flags", "global", "unicode", "raw", "new.target", "this.active_func", "<home_object>", "<computed_field>", "<static_computed_field>", "<class_fields_init>", "<brand>", "#constructor", "as", "from", "meta", "*default*", "*", "Module", "then", "resolve", "reject", "promise", "proxy", "revoke", "async", "exec", "groups", "status", "reason", "globalThis", "not-equal", "timed-out", "ok", "toJSON",
        "Object", "Array", "Error", "Number", "String", "Boolean", "Symbol", "Arguments", "Math", "JSON", "Date", "Function", "GeneratorFunction", "ForInIterator", "RegExp", "ArrayBuffer", "SharedArrayBuffer", "Uint8ClampedArray", "Int8Array", "Uint8Array", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array", "Float32Array", "Float64Array", "DataView", "Map", "Set", "WeakMap", "WeakSet", "Map Iterator", "Set Iterator", "Array Iterator", "String Iterator", "RegExp String Iterator", "Generator", "Proxy", "Promise", "PromiseResolveFunction", "PromiseRejectFunction", "AsyncFunction", "AsyncFunctionResolve", "AsyncFunctionReject", "AsyncGeneratorFunction", "AsyncGenerator", "EvalError", "RangeError", "ReferenceError", "SyntaxError", "TypeError", "URIError", "InternalError", "<brand>",
        "Symbol.toPrimitive", "Symbol.iterator", "Symbol.match", "Symbol.matchAll", "Symbol.replace", "Symbol.search", "Symbol.split", "Symbol.toStringTag", "Symbol.isConcatSpreadable", "Symbol.hasInstance", "Symbol.species", "Symbol.unscopables", "Symbol.asyncIterator",
    ];

    static PredefinedAtomTable()
    {
        if (Names.Length != BytecodeTargetAbi.FirstDynamicAtom)
            throw new InvalidOperationException("The predefined atom ABI is incomplete.");
    }

    public static uint? TryGet(string value)
    {
        if (value.Length == 0) return null;
        // The duplicate private-symbol spelling deliberately resolves to its
        // first public atom, matching identifier/property lookup behavior.
        for (var index = 1; index < Names.Length; index++)
            if (string.Equals(Names[index], value, StringComparison.Ordinal))
                return (uint)index;
        return null;
    }
}
