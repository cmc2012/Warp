using Warp.JsCompiler.Api;

namespace Warp.JsCompiler.Frontend;

public enum JavaScriptTokenKind { Identifier, String, Punctuation, Number, Regex, Template, End }
internal sealed record JavaScriptToken(JavaScriptTokenKind Kind, string Text, int Line, int Column);

/// <summary>Small, allocation-conscious lexer used for diagnostics and static-import discovery.</summary>
internal sealed class JavaScriptScanner(string text, string fileName)
{
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public IReadOnlyList<JavaScriptToken> Scan()
    {
        var result = new List<JavaScriptToken>();
        while (_index < text.Length)
        {
            SkipTrivia();
            if (_index >= text.Length) break;
            var line = _line; var column = _column; var c = text[_index];
            if (IsIdentifierStart(c)) result.Add(new(JavaScriptTokenKind.Identifier, ReadIdentifier(), line, column));
            else if (c == '#' && _index + 1 < text.Length && IsIdentifierStart(text[_index + 1])) result.Add(new(JavaScriptTokenKind.Identifier, ReadPrivateIdentifier(), line, column));
            else if (c is '\'' or '\"') result.Add(new(JavaScriptTokenKind.String, ReadQuoted(c), line, column));
            else if (c == '`') result.Add(new(JavaScriptTokenKind.Template, ReadTemplate(), line, column));
            else if (char.IsDigit(c) || c == '.' && _index + 1 < text.Length && char.IsDigit(text[_index + 1])) result.Add(new(JavaScriptTokenKind.Number, ReadNumber(), line, column));
            else if (c == '/' && CanStartRegex(result)) result.Add(new(JavaScriptTokenKind.Regex, ReadRegex(), line, column));
            else result.Add(new(JavaScriptTokenKind.Punctuation, ReadPunctuation(), line, column));
        }
        result.Add(new(JavaScriptTokenKind.End, string.Empty, _line, _column));
        return result;
    }

    public static IReadOnlyList<StaticModuleImport> FindStaticImports(IReadOnlyList<JavaScriptToken> tokens)
    {
        var imports = new List<StaticModuleImport>();
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i] is not { Kind: JavaScriptTokenKind.Identifier, Text: "import" }) continue;
            // `import "x"`; and `import ... from "x"`. Dynamic import has `(` next.
            if (tokens[i + 1].Kind == JavaScriptTokenKind.String)
                imports.Add(new(tokens[i + 1].Text, tokens[i].Line, tokens[i].Column));
            else
            {
                for (var j = i + 1; j + 1 < tokens.Count && tokens[j].Text != ";"; j++)
                    if (tokens[j].Kind == JavaScriptTokenKind.Identifier && tokens[j].Text == "from" && tokens[j + 1].Kind == JavaScriptTokenKind.String)
                    { imports.Add(new(tokens[j + 1].Text, tokens[i].Line, tokens[i].Column)); break; }
            }
        }
        return imports;
    }

    public static bool HasModuleSyntax(IReadOnlyList<JavaScriptToken> tokens)
    {
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Kind != JavaScriptTokenKind.Identifier) continue;
            if (tokens[i].Text == "export") return true;
            // Auto-detection follows the driver contract: only a static
            // import declaration selects module compilation.  `import()` is
            // an expression, and `import.meta` is left to the selected
            // grammar to validate rather than changing the source kind.
            if (tokens[i].Text == "import" && tokens[i + 1].Text is not "(" and not ".") return true;
        }
        return false;
    }

    private void SkipTrivia()
    {
        while (_index < text.Length)
        {
            if (char.IsWhiteSpace(text[_index])) { Advance(); continue; }
            if (Peek("//")) { while (_index < text.Length && text[_index] != '\n') Advance(); continue; }
            if (Peek("/*")) { Advance(); Advance(); while (_index < text.Length && !Peek("*/")) Advance(); if (_index >= text.Length) Error("Unterminated block comment."); Advance(); Advance(); continue; }
            break;
        }
    }

    private string ReadIdentifier() { var start = _index; Advance(); while (_index < text.Length && (IsIdentifierStart(text[_index]) || char.IsDigit(text[_index]))) Advance(); return text[start.._index]; }
    private string ReadPrivateIdentifier()
    {
        var start = _index;
        Advance(); // '#'
        Advance(); // identifier start
        while (_index < text.Length && (IsIdentifierStart(text[_index]) || char.IsDigit(text[_index]))) Advance();
        return text[start.._index];
    }
    private string ReadNumber()
    {
        var start = _index;
        if (Peek("0x") || Peek("0X")) { Advance(); Advance(); while (_index < text.Length && (char.IsAsciiHexDigit(text[_index]) || text[_index] == '_')) Advance(); if (_index < text.Length && text[_index] == 'n') Advance(); return text[start.._index]; }
        if (Peek("0b") || Peek("0B")) { Advance(); Advance(); while (_index < text.Length && (text[_index] is '0' or '1' or '_')) Advance(); if (_index < text.Length && text[_index] == 'n') Advance(); return text[start.._index]; }
        if (Peek("0o") || Peek("0O")) { Advance(); Advance(); while (_index < text.Length && (text[_index] is >= '0' and <= '7' || text[_index] == '_')) Advance(); if (_index < text.Length && text[_index] == 'n') Advance(); return text[start.._index]; }
        while (_index < text.Length && (char.IsDigit(text[_index]) || text[_index] == '_')) Advance();
        if (_index < text.Length && text[_index] == '.') { Advance(); while (_index < text.Length && (char.IsDigit(text[_index]) || text[_index] == '_')) Advance(); }
        if (_index < text.Length && text[_index] is 'e' or 'E') { Advance(); if (_index < text.Length && text[_index] is '+' or '-') Advance(); while (_index < text.Length && (char.IsDigit(text[_index]) || text[_index] == '_')) Advance(); }
        if (_index < text.Length && text[_index] == 'n') Advance();
        return text[start.._index];
    }
    private string ReadQuoted(char quote)
    {
        Advance(); var chars = new System.Text.StringBuilder();
        while (_index < text.Length && text[_index] != quote)
        {
            if (text[_index] != '\\') { chars.Append(text[_index]); Advance(); continue; }
            Advance(); if (_index >= text.Length) Error("Unterminated string literal.");
            var escape = text[_index]; Advance();
            chars.Append(escape switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                '0' => '\0', '\n' => string.Empty, '\r' => ConsumeLineContinuation(),
                'x' => ReadHexEscape(2), 'u' => ReadUnicodeEscape(), _ => escape.ToString(),
            });
        }
        if (_index >= text.Length) Error("Unterminated string literal."); Advance(); return chars.ToString();
    }
    private string ConsumeLineContinuation() { if (_index < text.Length && text[_index] == '\n') Advance(); return string.Empty; }
    private string ReadHexEscape(int digits)
    {
        if (_index + digits > text.Length) Error("Invalid hexadecimal escape.");
        var slice = text.AsSpan(_index, digits); if (!uint.TryParse(slice, System.Globalization.NumberStyles.AllowHexSpecifier, null, out var value)) Error("Invalid hexadecimal escape.");
        for (var i = 0; i < digits; i++) Advance(); return ((char)value).ToString();
    }
    private string ReadUnicodeEscape()
    {
        if (_index < text.Length && text[_index] == '{')
        {
            Advance(); var start = _index; while (_index < text.Length && text[_index] != '}') Advance();
            if (_index >= text.Length || !int.TryParse(text.AsSpan(start, _index - start), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var codePoint) || codePoint > 0x10ffff)
            {
                Error("Invalid Unicode escape.");
                return string.Empty;
            }
            Advance();
            // JavaScript strings store UTF-16 code units. A braced escape may
            // intentionally denote a lone surrogate, which .NET's UTF-32 helper
            // rejects even though the JavaScript source is valid.
            return codePoint is >= 0xd800 and <= 0xdfff
                ? ((char)codePoint).ToString()
                : char.ConvertFromUtf32(codePoint);
        }
        return ReadHexEscape(4);
    }
    private string ReadTemplate()
    {
        var start = _index;
        Advance(); // opening backtick
        while (_index < text.Length)
        {
            if (text[_index] == '\\') { Advance(); if (_index < text.Length) Advance(); continue; }
            if (text[_index] == '`') { Advance(); return text[start.._index]; }
            if (Peek("${"))
            {
                Advance(); Advance();
                SkipTemplateExpression();
                continue;
            }
            Advance();
        }
        Error("Unterminated template literal.");
        return string.Empty;
    }

    private void SkipTemplateExpression()
    {
        var depth = 1;
        while (_index < text.Length && depth != 0)
        {
            if (text[_index] is '\'' or '"') { SkipQuoted(text[_index]); continue; }
            if (text[_index] == '`') { _ = ReadTemplate(); continue; }
            if (text[_index] == '\\') { Advance(); if (_index < text.Length) Advance(); continue; }
            if (text[_index] == '{') depth++;
            else if (text[_index] == '}') depth--;
            Advance();
        }
        if (depth != 0) Error("Unterminated template expression.");
    }

    private void SkipQuoted(char quote)
    {
        Advance();
        while (_index < text.Length && text[_index] != quote)
        {
            if (text[_index] == '\\') { Advance(); if (_index < text.Length) Advance(); continue; }
            Advance();
        }
        if (_index >= text.Length) Error("Unterminated string literal.");
        Advance();
    }
    private string ReadRegex()
    {
        var start = _index; Advance(); var inClass = false;
        while (_index < text.Length)
        {
            if (text[_index] == '\\') { Advance(); if (_index < text.Length) Advance(); continue; }
            if (text[_index] == '[') inClass = true;
            else if (text[_index] == ']') inClass = false;
            else if (text[_index] == '/' && !inClass) { Advance(); while (_index < text.Length && char.IsLetter(text[_index])) Advance(); return text[start.._index]; }
            else if (text[_index] is '\r' or '\n') Error("Unterminated regular expression literal.");
            Advance();
        }
        Error("Unterminated regular expression literal."); return string.Empty;
    }
    private string ReadPunctuation()
    {
        foreach (var candidate in new[] { ">>>=", "<<=", ">>=", "===", "!==", "**=", "&&=", "||=", "??=", "...", ">>>", "=>", "==", "!=", "<=", ">=", "++", "--", "&&", "||", "??", "**", "<<", ">>", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "?." })
        {
            if (!Peek(candidate)) continue;
            for (var i = 0; i < candidate.Length; i++) Advance();
            return candidate;
        }
        var value = text[_index].ToString(); Advance(); return value;
    }
    private static bool CanStartRegex(IReadOnlyList<JavaScriptToken> tokens)
    {
        if (tokens.Count == 0) return true;
        var previous = tokens[^1];
        if (previous.Kind == JavaScriptTokenKind.Identifier)
            return previous.Text is "return" or "throw" or "case" or "delete" or "void" or "typeof" or "yield";
        return previous.Text is not ")" and not "]" and not "}" && previous.Kind is not JavaScriptTokenKind.Number and not JavaScriptTokenKind.String and not JavaScriptTokenKind.Regex and not JavaScriptTokenKind.Template;
    }
    private bool Peek(string value) => _index + value.Length <= text.Length && text.AsSpan(_index, value.Length).SequenceEqual(value);
    private static bool IsIdentifierStart(char c) => c is '$' or '_' || char.IsLetter(c) || c > 0x7f;
    private void Advance() { if (text[_index++] == '\n') { _line++; _column = 1; } else _column++; }
    private void Error(string message) => throw new JavaScriptCompilationException(message, fileName, _line, _column, "ECMA1001");
}
