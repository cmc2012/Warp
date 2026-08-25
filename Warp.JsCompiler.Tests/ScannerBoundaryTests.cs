using Warp.JsCompiler.Api;
using Warp.JsCompiler.Frontend;
using Xunit;

namespace Warp.JsCompiler.Tests;

/// <summary>Lexical boundaries for tokens, trivia, literals, and import discovery.</summary>
public sealed class ScannerBoundaryTests
{
    public static IEnumerable<object[]> TokenCases
    {
        get
        {
            yield return Tokens("identifier", "alpha", "Identifier:alpha");
            yield return Tokens("dollar-identifier", "$value", "Identifier:$value");
            yield return Tokens("underscore-identifier", "_value2", "Identifier:_value2");
            yield return Tokens("private-identifier", "#field", "Identifier:#field");
            yield return Tokens("decimal-integer", "12345", "Number:12345");
            yield return Tokens("leading-dot-number", ".125", "Number:.125");
            yield return Tokens("trailing-dot-number", "1.", "Number:1.");
            yield return Tokens("decimal-exponent", "1.5e-3", "Number:1.5e-3");
            yield return Tokens("hex-number", "0xdead_beef", "Number:0xdead_beef");
            yield return Tokens("binary-number", "0b1010_0101", "Number:0b1010_0101");
            yield return Tokens("octal-number", "0o755", "Number:0o755");
            yield return Tokens("single-quoted-string", "'value'", "String:value");
            yield return Tokens("double-quoted-string", "\"value\"", "String:value");
            yield return Tokens("escaped-quote", "'it\\'s'", "String:it's");
            yield return Tokens("escaped-backslash", "'a\\\\b'", "String:a\\b");
            yield return Tokens("hex-string-escape", "'\\x41'", "String:A");
            yield return Tokens("unicode-string-escape", "'\\u0042'", "String:B");
            yield return Tokens("unicode-braced-escape", "'\\u{43}'", "String:C");
            yield return Tokens("static-template", "`value`", "Template:`value`");
            yield return Tokens("interpolated-template", "`before ${value} after`", "Template:`before ${value} after`");
            yield return Tokens("nested-template", "`outer ${`inner ${value}`}`", "Template:`outer ${`inner ${value}`}`");
            yield return Tokens("regexp", "/value+/gi", "Regex:/value+/gi");
            yield return Tokens("regexp-character-class-slash", "/[a/b]+/", "Regex:/[a/b]+/");
            yield return Tokens("regexp-escaped-slash", "/a\\/b/", "Regex:/a\\/b/");
            yield return Tokens("division", "left / right", "Identifier:left", "Punctuation:/", "Identifier:right");
            yield return Tokens("division-assignment", "left /= right", "Identifier:left", "Punctuation:/=", "Identifier:right");
            yield return Tokens("regexp-after-return", "return /value/", "Identifier:return", "Regex:/value/");
            yield return Tokens("regexp-after-throw", "throw /value/", "Identifier:throw", "Regex:/value/");
            yield return Tokens("regexp-after-case", "case /value/", "Identifier:case", "Regex:/value/");
            yield return Tokens("regexp-after-typeof", "typeof /value/", "Identifier:typeof", "Regex:/value/");
            yield return Tokens("strict-equality-longest", "a===b", "Identifier:a", "Punctuation:===", "Identifier:b");
            yield return Tokens("unsigned-shift-assignment-longest", "a>>>=b", "Identifier:a", "Punctuation:>>>=", "Identifier:b");
            yield return Tokens("spread-longest", "[...items]", "Punctuation:[", "Punctuation:...", "Identifier:items", "Punctuation:]");
            yield return Tokens("optional-chain-longest", "value?.field", "Identifier:value", "Punctuation:?.", "Identifier:field");
            yield return Tokens("logical-assignments", "a&&=b||=c??=d", "Identifier:a", "Punctuation:&&=", "Identifier:b", "Punctuation:||=", "Identifier:c", "Punctuation:??=", "Identifier:d");
            yield return Tokens("comments-are-trivia", "left /* middle */ right // tail", "Identifier:left", "Identifier:right");
            yield return Tokens("punctuation-sequence", "{}[](),;:", "Punctuation:{", "Punctuation:}", "Punctuation:[", "Punctuation:]", "Punctuation:(", "Punctuation:)", "Punctuation:,", "Punctuation:;", "Punctuation::");
        }
    }

    public static IEnumerable<object[]> PositionCases
    {
        get
        {
            yield return Position("first-token", "value", 1, 1);
            yield return Position("leading-spaces", "   value", 1, 4);
            yield return Position("leading-tab", "\tvalue", 1, 2);
            yield return Position("second-line", "\nvalue", 2, 1);
            yield return Position("indented-second-line", "\n  value", 2, 3);
            yield return Position("after-line-comment", "// comment\nvalue", 2, 1);
            yield return Position("after-multiline-comment", "/* first\nsecond */value", 2, 10);
            yield return Position("after-string", "'first' value", 1, 9, tokenIndex: 1);
            yield return Position("after-template-newline", "`first\nsecond` value", 2, 9, tokenIndex: 1);
            yield return Position("private-on-second-line", "class Value {\n  #field;\n}", 2, 3, tokenIndex: 3);
        }
    }

    public static IEnumerable<object[]> InvalidLexemes
    {
        get
        {
            yield return Error("unterminated-single-string", "'value");
            yield return Error("unterminated-double-string", "\"value");
            yield return Error("unterminated-string-escape", "'value\\");
            yield return Error("invalid-hex-escape-short", "'\\x1'");
            yield return Error("invalid-hex-escape-text", "'\\xGG'");
            yield return Error("invalid-unicode-escape-short", "'\\u123'");
            yield return Error("invalid-unicode-escape-text", "'\\uZZZZ'");
            yield return Error("invalid-braced-unicode", "'\\u{xyz}'");
            yield return Error("unterminated-braced-unicode", "'\\u{1234'");
            yield return Error("unicode-out-of-range", "'\\u{110000}'");
            yield return Error("unterminated-template", "`value");
            yield return Error("unterminated-template-expression", "`value ${item");
            yield return Error("unterminated-regexp", "/value");
            yield return Error("regexp-line-terminator", "/value\n/");
            yield return Error("unterminated-block-comment", "/* value");
        }
    }

    public static IEnumerable<object[]> ImportCases
    {
        get
        {
            yield return Imports("side-effect", "import 'setup';", "setup");
            yield return Imports("default", "import value from 'source';", "source");
            yield return Imports("named", "import { value } from 'source';", "source");
            yield return Imports("namespace", "import * as api from 'source';", "source");
            yield return Imports("mixed", "import primary, { value } from 'source';", "source");
            yield return Imports("two-imports", "import 'first'; import 'second';", "first", "second");
            yield return Imports("export-from-is-not-scanner-import", "export { value } from 'source';");
            yield return Imports("dynamic-import-excluded", "import('source');");
            yield return Imports("import-in-comment-excluded", "// import 'wrong';\nimport 'right';", "right");
            yield return Imports("import-text-in-string-excluded", "const text = \"import 'wrong'\";");
        }
    }

    public static IEnumerable<object[]> ModuleSyntaxCases
    {
        get
        {
            yield return Module("empty", "", false);
            yield return Module("ordinary-script", "const value = 1;", false);
            yield return Module("dynamic-import", "import('source');", false);
            yield return Module("import-meta", "import.meta.url;", false);
            yield return Module("side-effect-import", "import 'source';", true);
            yield return Module("default-import", "import value from 'source';", true);
            yield return Module("named-export", "export { value };", true);
            yield return Module("default-export", "export default value;", true);
            yield return Module("export-in-comment", "// export default value;", false);
            yield return Module("export-in-string", "const text = 'export default value';", false);
        }
    }

    [Theory]
    [MemberData(nameof(TokenCases))]
    public void Scans_expected_token_sequence(string caseName, string source, string[] expected)
    {
        Assert.NotEmpty(caseName);
        var actual = Scan(source).Where(token => token.Kind != JavaScriptTokenKind.End)
            .Select(token => $"{token.Kind}:{token.Text}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(PositionCases))]
    public void Tracks_token_position(string caseName, string source, int tokenIndex, int line, int column)
    {
        Assert.NotEmpty(caseName);
        var token = Scan(source)[tokenIndex];
        Assert.Equal(line, token.Line);
        Assert.Equal(column, token.Column);
    }

    [Theory]
    [MemberData(nameof(InvalidLexemes))]
    public void Rejects_invalid_lexeme(string caseName, string source)
    {
        Assert.NotEmpty(caseName);
        var error = Assert.Throws<JavaScriptCompilationException>(() => Scan(source));
        Assert.Equal("ECMA1001", error.Code);
        Assert.Equal("scanner-boundary.js", error.FileName);
    }

    [Theory]
    [MemberData(nameof(ImportCases))]
    public void Finds_static_imports(string caseName, string source, string[] expected)
    {
        Assert.NotEmpty(caseName);
        var imports = JavaScriptScanner.FindStaticImports(Scan(source));
        Assert.Equal(expected, imports.Select(import => import.Specifier));
    }

    [Theory]
    [MemberData(nameof(ModuleSyntaxCases))]
    public void Detects_module_syntax(string caseName, string source, bool expected)
    {
        Assert.NotEmpty(caseName);
        Assert.Equal(expected, JavaScriptScanner.HasModuleSyntax(Scan(source)));
    }

    private static IReadOnlyList<JavaScriptToken> Scan(string source)
        => new JavaScriptScanner(source, "scanner-boundary.js").Scan();

    private static object[] Tokens(string name, string source, params string[] expected) => [name, source, expected];
    private static object[] Position(string name, string source, int line, int column, int tokenIndex = 0) => [name, source, tokenIndex, line, column];
    private static object[] Error(string name, string source) => [name, source];
    private static object[] Imports(string name, string source, params string[] expected) => [name, source, expected];
    private static object[] Module(string name, string source, bool expected) => [name, source, expected];
}
