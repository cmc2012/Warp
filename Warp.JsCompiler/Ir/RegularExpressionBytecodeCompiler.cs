using System.Globalization;

namespace Warp.JsCompiler.Ir;

/// <summary>
/// Emits the stable byte-string representation consumed by OP_regexp.  This
/// mirrors the reference engine's header and its literal-character path; the
/// compiler keeps this binary payload separate from JavaScript source text.
/// </summary>
internal static class RegularExpressionBytecodeCompiler
{
    private sealed class CompileState
    {
        internal int CaptureCount { get; set; } = 1;
        internal List<string> GroupNames { get; } = [];
    }
    private const byte Global = 1, IgnoreCase = 2, Multiline = 4, DotAll = 8, Utf16 = 16, Sticky = 32;
    private const byte Char = 1, Char32 = 2, Any = 4, Match = 10, SaveStart = 11, SaveEnd = 12;
    private const byte SplitGotoFirst = 8, Goto = 7;
    // libregexp-opcode.h: range follows backward_back_reference, while the
    // simple quantifier is emitted after prev.
    private const byte Range = 21, SimpleGreedyQuantifier = 28;

    internal static (string Pattern, string Flags) SplitLiteral(string literal)
    {
        if (literal.Length < 2 || literal[0] != '/')
            throw new ArgumentException("A regular expression literal must begin with '/'.", nameof(literal));
        var inClass = false;
        for (var index = literal.Length - 1; index > 0; index--)
        {
            if (literal[index] == '/')
            {
                var slashes = 0;
                for (var previous = index - 1; previous >= 0 && literal[previous] == '\\'; previous--) slashes++;
                if ((slashes & 1) == 0 && !inClass)
                    return (literal[1..index], literal[(index + 1)..]);
            }
            if (literal[index] == ']') inClass = true;
            else if (literal[index] == '[') inClass = false;
        }
        throw new ArgumentException("Unterminated regular expression literal.", nameof(literal));
    }

    internal static string Compile(string pattern, string flags)
    {
        var reFlags = ParseFlags(flags);
        var state = new CompileState();
        var code = new List<byte>();
        if ((reFlags & Sticky) == 0)
        {
            EmitU32(code, SplitGotoFirst, 6);
            code.Add(Any);
            EmitU32(code, Goto, -11);
        }
        code.Add(SaveStart); code.Add(0);
        for (var index = 0; index < pattern.Length; index++)
            EmitTerm(code, pattern, ref index, reFlags, state);
        code.Add(SaveEnd); code.Add(0); code.Add(Match);

        if (state.GroupNames.Count != 0) reFlags |= 128;
        var bytes = new List<byte> { reFlags, (byte)state.CaptureCount, 0, 0, 0, 0, 0 };
        var codeLength = code.Count;
        bytes[3] = (byte)codeLength;
        bytes[4] = (byte)(codeLength >> 8);
        bytes[5] = (byte)(codeLength >> 16);
        bytes[6] = (byte)(codeLength >> 24);
        bytes.AddRange(code);
        foreach (var name in state.GroupNames) { bytes.AddRange(name.Select(character => (byte)character)); bytes.Add(0); }
        return new string(bytes.Select(value => (char)value).ToArray());
    }

    private static byte ParseFlags(string flags)
    {
        byte result = 0;
        foreach (var flag in flags)
        {
            var value = flag switch { 'g' => Global, 'i' => IgnoreCase, 'm' => Multiline, 's' => DotAll,
                'u' => Utf16, 'y' => Sticky, _ => throw new ArgumentException($"Invalid regular expression flag '{flag}'.") };
            if ((result & value) != 0) throw new ArgumentException($"Duplicate regular expression flag '{flag}'.");
            result |= value;
        }
        return result;
    }

    private static int ReadCharacter(string pattern, ref int index)
    {
        if (pattern[index] != '\\') return pattern[index];
        if (++index >= pattern.Length) throw new ArgumentException("Trailing regular expression escape.");
        return pattern[index] switch
        {
            'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v', 'f' => '\f',
            // get_class_atom() recognizes a control escape outside a class
            // only when its operand is an ASCII letter.  In non-Unicode
            // patterns an invalid `\c` is parsed as three ordinary
            // characters (backslash, c, following input), so leave the c
            // for the next term after returning the backslash here.
            'c' when index + 1 < pattern.Length && IsAsciiLetter(pattern[index + 1])
                => pattern[++index] & 0x1f,
            'c' => RestoreLiteralBackslash(ref index),
            'x' when index + 2 < pattern.Length => ReadHex(pattern, ref index, 2),
            'u' when index + 4 < pattern.Length => ReadHex(pattern, ref index, 4),
            var value => value,
        };
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static char RestoreLiteralBackslash(ref int index)
    {
        index--;
        return '\\';
    }

    // Keep regexp syntax handling separate from the surrounding JavaScript
    // parser. This follows lre_compile's term-oriented emission: an atom is
    // emitted first, then its quantifier rewrites that byte range.
    private static void EmitTerm(List<byte> code, string pattern, ref int index, byte flags, CompileState state)
    {
        var start = code.Count;
        if (pattern[index] == '(' && index + 2 < pattern.Length && pattern[index + 1] == '?' &&
            pattern[index + 2] is '=' or '!')
        {
            var negative = pattern[index + 2] == '!';
            var close = FindClosingParen(pattern, index);
            var operand = pattern[(index + 3)..close];
            var lookaheadStart = code.Count;
            EmitU32(code, negative ? (byte)24 : (byte)23, 0);
            for (var child = 0; child < operand.Length; child++) EmitTerm(code, operand, ref child, flags, state);
            code.Add(Match);
            WriteI32(code, lookaheadStart + 1, code.Count - (lookaheadStart + 5));
            index = close;
        }
        else if (pattern[index] == '(')
        {
            var close = FindClosingParen(pattern, index);
            var bodyStart = index + 1;
            string? name = null;
            if (bodyStart + 2 < close && pattern[bodyStart] == '?' && pattern[bodyStart + 1] == '<')
            {
                var endName = pattern.IndexOf('>', bodyStart + 2);
                if (endName < 0 || endName >= close) throw new ArgumentException("Invalid named capture group.");
                name = pattern[(bodyStart + 2)..endName]; bodyStart = endName + 1;
                if (state.GroupNames.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Duplicate regular expression group name.");
            }
            var capture = state.CaptureCount++;
            state.GroupNames.Add(name ?? string.Empty);
            code.Add(SaveStart); code.Add((byte)capture);
            var body = pattern[bodyStart..close];
            for (var child = 0; child < body.Length; child++) EmitTerm(code, body, ref child, flags, state);
            code.Add(SaveEnd); code.Add((byte)capture);
            index = close;
        }
        else if (pattern[index] == '.') code.Add((flags & DotAll) != 0 ? Any : (byte)3);
        else if (pattern[index] == '[') EmitClass(code, pattern, ref index, (flags & IgnoreCase) != 0);
        else EmitCharacter(code, ReadCharacter(pattern, ref index));

        if (index + 1 >= pattern.Length || pattern[index + 1] is not ('*' or '+' or '?')) return;
        var quantifier = pattern[++index];
        var min = quantifier == '+' ? 1 : 0;
        var max = quantifier == '?' ? 1 : int.MaxValue;
        if (index + 1 < pattern.Length && pattern[index + 1] == '?') index++; // non-greedy handled below by split form
        // The reference uses simple_greedy_quant for character/range atoms.
        // It contains a copy-length, lower/upper bounds and character count.
        var atomLength = code.Count - start;
        code.Add(Match);
        var payloadLength = code.Count - start;
        var prefix = new List<byte> { SimpleGreedyQuantifier, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        WriteI32(prefix, 1, payloadLength);
        WriteI32(prefix, 5, min);
        WriteI32(prefix, 9, max);
        WriteI32(prefix, 13, 1);
        code.InsertRange(start, prefix);
    }

    private static void EmitClass(List<byte> code, string pattern, ref int index, bool ignoreCase)
    {
        var ranges = new List<(int First, int Last)>();
        var inverted = false;
        if (index + 1 < pattern.Length && pattern[index + 1] == '^') { inverted = true; index++; }
        for (index++; index < pattern.Length && pattern[index] != ']'; index++)
        {
            var first = ReadCharacter(pattern, ref index);
            var last = first;
            if (index + 2 < pattern.Length && pattern[index + 1] == '-' && pattern[index + 2] != ']')
            {
                index += 2;
                last = ReadCharacter(pattern, ref index);
            }
            if (last < first) throw new ArgumentException("Invalid regular expression character range.");
            ranges.Add((first, last));
            // lre_canonicalize is much broader for Unicode; ASCII expansion
            // is the required common subset and remains range based.
            if (ignoreCase && first is >= 'a' and <= 'z' && last is >= 'a' and <= 'z') ranges.Add((first - 32, last - 32));
        }
        if (index >= pattern.Length) throw new ArgumentException("Unterminated regular expression character class.");
        ranges.Sort((left, right) => left.First.CompareTo(right.First));
        ranges = MergeRanges(ranges);
        if (inverted)
        {
            var complement = new List<(int First, int Last)>();
            var cursor = 0;
            foreach (var (first, last) in ranges)
            {
                if (cursor < first) complement.Add((cursor, first - 1));
                cursor = Math.Max(cursor, last + 1);
            }
            if (cursor <= ushort.MaxValue) complement.Add((cursor, ushort.MaxValue));
            ranges = complement;
        }
        code.Add(Range); code.Add((byte)ranges.Count); code.Add((byte)(ranges.Count >> 8));
        foreach (var (first, last) in ranges) { code.Add((byte)first); code.Add((byte)(first >> 8)); code.Add((byte)last); code.Add((byte)(last >> 8)); }
    }

    private static List<(int First, int Last)> MergeRanges(List<(int First, int Last)> ranges)
    {
        var merged = new List<(int First, int Last)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.First > merged[^1].Last + 1) merged.Add(range);
            else merged[^1] = (merged[^1].First, Math.Max(merged[^1].Last, range.Last));
        }
        return merged;
    }

    private static void WriteI32(List<byte> bytes, int index, int value)
    {
        bytes[index] = (byte)value; bytes[index + 1] = (byte)(value >> 8);
        bytes[index + 2] = (byte)(value >> 16); bytes[index + 3] = (byte)(value >> 24);
    }

    private static int FindClosingParen(string pattern, int open)
    {
        var depth = 1;
        for (var index = open + 1; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\') { index++; continue; }
            if (pattern[index] == '(') depth++;
            else if (pattern[index] == ')' && --depth == 0) return index;
        }
        throw new ArgumentException("Unterminated regular expression group.");
    }

    private static int ReadHex(string text, ref int index, int digits)
    {
        var start = ++index;
        index += digits - 1;
        return int.Parse(text.AsSpan(start, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private static void EmitCharacter(List<byte> code, int value)
    {
        if (value <= ushort.MaxValue)
        {
            code.Add(Char); code.Add((byte)value); code.Add((byte)(value >> 8));
        }
        else EmitU32(code, Char32, value);
    }

    private static void EmitU32(List<byte> output, byte opcode, int value)
    {
        output.Add(opcode);
        output.Add((byte)value); output.Add((byte)(value >> 8));
        output.Add((byte)(value >> 16)); output.Add((byte)(value >> 24));
    }
}
