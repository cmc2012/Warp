namespace Warp.Lsp;

/// <summary>
/// Produces LSP semantic tokens for WXAML source. Keeping this in the language
/// server means editor integrations do not need to duplicate WXAML lexing.
/// </summary>
public static class WxamlSemanticTokens
{
    public static readonly string[] TokenTypes = ["comment", "type", "property", "string", "variable", "operator", "keyword", "markup", "bracket"];

    private const int Comment = 0;
    private const int Type = 1;
    private const int Property = 2;
    private const int String = 3;
    private const int Variable = 4;
    private const int Operator = 5;
    private const int Keyword = 6;
    private const int Markup = 7;
    private const int Bracket = 8;

    public static int[] Encode(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            if (StartsWith(text, index, "<!--"))
            {
                var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                AddRange(tokens, text, index, end < 0 ? text.Length : end + 3, Comment);
                index = end < 0 ? text.Length : end + 3;
                continue;
            }

            if (text[index] != '<') { index++; continue; }
            index = ScanTag(text, index, tokens);
        }

        return Encode(tokens);
    }

    private static int ScanTag(string text, int start, List<Token> tokens)
    {
        var index = start + 1;
        if (index < text.Length && text[index] == '/') index++;
        // Keep the delimiters in the same color family as the element name;
        // treating them as generic metadata made WXAML look visually broken.
        AddRange(tokens, text, start, index, Markup);
        SkipWhitespace(text, ref index);
        var nameStart = index;
        while (index < text.Length && IsNamePart(text[index])) index++;
        if (index > nameStart) AddRange(tokens, text, nameStart, index, Type);

        while (index < text.Length)
        {
            if (text[index] == '>')
            {
                AddRange(tokens, text, index, index + 1, Markup);
                return index + 1;
            }
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '>')
            {
                AddRange(tokens, text, index, index + 2, Markup);
                return index + 2;
            }
            if (char.IsWhiteSpace(text[index])) { index++; continue; }

            var attributeStart = index;
            while (index < text.Length && IsNamePart(text[index])) index++;
            if (index == attributeStart) { index++; continue; }
            AddRange(tokens, text, attributeStart, index, Property);
            SkipWhitespace(text, ref index);
            if (index >= text.Length || text[index] != '=') continue;
            AddRange(tokens, text, index, index + 1, Operator);
            index++;
            SkipWhitespace(text, ref index);
            if (index >= text.Length) break;

            var valueStart = index;
            if (text[index] is '\'' or '"')
            {
                var quote = text[index++];
                while (index < text.Length && text[index] != quote) index++;
                if (index < text.Length) index++;
                var isBinding = index - valueStart >= 4 && text[valueStart + 1] == '{' && text[index - 2] == '}';
                if (isBinding) AddBindingValue(tokens, text, valueStart, index, quote);
                else AddRange(tokens, text, valueStart, index, String);
            }
            else if (text[index] == '{')
            {
                var depth = 0;
                do
                {
                    if (text[index] == '{') depth++;
                    else if (text[index] == '}') depth--;
                    index++;
                } while (index < text.Length && depth > 0);
                AddBindingValue(tokens, text, valueStart, index, null);
            }
        }
        return index;
    }

    private static void AddBindingValue(List<Token> tokens, string text, int start, int end, char? quote)
    {
        var valueStart = quote is null ? start : start + 1;
        var valueEnd = quote is null ? end : end - 1;
        if (quote is not null) AddRange(tokens, text, start, start + 1, String);
        AddRange(tokens, text, valueStart, valueStart + 1, Bracket);

        var keywordStart = valueStart + 1;
        while (keywordStart < valueEnd && char.IsWhiteSpace(text[keywordStart])) keywordStart++;
        var keywordEnd = keywordStart;
        while (keywordEnd < valueEnd && char.IsLetter(text[keywordEnd])) keywordEnd++;
        if (keywordEnd > keywordStart) AddRange(tokens, text, keywordStart, keywordEnd, Keyword);
        var expressionStart = keywordEnd;
        while (expressionStart < valueEnd - 1 && char.IsWhiteSpace(text[expressionStart])) expressionStart++;
        if (expressionStart < valueEnd - 1) AddBindingPath(tokens, text, expressionStart, valueEnd - 1);

        AddRange(tokens, text, valueEnd - 1, valueEnd, Bracket);
        if (quote is not null) AddRange(tokens, text, end - 1, end, String);
    }

    private static void AddBindingPath(List<Token> tokens, string text, int start, int end)
    {
        var index = start;
        while (index < end)
        {
            if (text[index] == '.')
            {
                AddRange(tokens, text, index, index + 1, Operator);
                index++;
                continue;
            }
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            var partStart = index;
            while (index < end && IsNamePart(text[index])) index++;
            if (index > partStart) AddRange(tokens, text, partStart, index, Variable);
            else index++;
        }
    }

    private static int[] Encode(List<Token> tokens)
    {
        var result = new List<int>(tokens.Count * 5);
        var previousLine = 0;
        var previousColumn = 0;
        foreach (var token in tokens.OrderBy(token => token.Line).ThenBy(token => token.Column))
        {
            result.Add(token.Line - previousLine);
            result.Add(token.Line == previousLine ? token.Column - previousColumn : token.Column);
            result.Add(token.Length);
            result.Add(token.Type);
            result.Add(0);
            previousLine = token.Line;
            previousColumn = token.Column;
        }
        return result.ToArray();
    }

    private static void AddRange(List<Token> tokens, string text, int start, int end, int type)
    {
        var line = 0;
        var column = 0;
        for (var i = 0; i < start; i++)
        {
            if (text[i] == '\n') { line++; column = 0; }
            else column++;
        }
        for (var i = start; i < end;)
        {
            var lineEnd = i;
            while (lineEnd < end && text[lineEnd] != '\n') lineEnd++;
            if (lineEnd > i) tokens.Add(new Token(line, column, lineEnd - i, type));
            if (lineEnd == end) break;
            i = lineEnd + 1;
            line++;
            column = 0;
        }
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
    }

    private static bool IsNamePart(char value) => char.IsLetterOrDigit(value) || value is '_' or ':' or '-' or '.';
    private static bool StartsWith(string text, int index, string value) => index + value.Length <= text.Length && text.AsSpan(index, value.Length).SequenceEqual(value);
    private sealed record Token(int Line, int Column, int Length, int Type);
}
