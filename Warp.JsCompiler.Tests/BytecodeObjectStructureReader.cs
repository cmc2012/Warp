namespace Warp.JsCompiler.Tests;

/// <summary>Small test-only reader for inspecting serialized function boundaries and runtime metadata.</summary>
internal static class BytecodeObjectStructureReader
{
    internal sealed record Function(int Offset, int BytecodeOffset, uint BytecodeLength, uint Arguments, uint Variables,
        IReadOnlyList<(uint Atom, uint Scope, uint Next, byte Flags)> VarDefs,
        IReadOnlyList<(uint Atom, uint Parent, byte Flags)> Closures,
        IReadOnlyList<Function> Constants);

    internal static Function ReadRoot(byte[] bytes)
    {
        var reader = new Reader(bytes);
        reader.Byte();
        var atoms = reader.Uleb();
        for (var index = 0u; index < atoms; index++) reader.String();
        return reader.Value() ?? throw new InvalidOperationException("The root object is not a bytecode function.");
    }

    private ref struct Reader(byte[] bytes)
    {
        private readonly byte[] _bytes = bytes;
        private int _offset;
        internal byte Byte() => _bytes[_offset++];
        internal uint Uleb()
        {
            uint value = 0; var shift = 0;
            while (true) { var next = Byte(); value |= (uint)(next & 0x7f) << shift; if ((next & 0x80) == 0) return value; shift += 7; }
        }
        internal void Sleb()
        {
            while ((Byte() & 0x80) != 0) { }
        }
        internal void String()
        {
            var encoded = Uleb();
            var length = checked((int)(encoded >> 1));
            _offset += length * ((encoded & 1) == 0 ? 1 : 2);
        }
        internal uint Atom() => Uleb() >> 1;
        internal Function? Value()
        {
            var tag = Byte();
            switch (tag)
            {
                case 1 or 2 or 3 or 4: return null;
                case 5: Sleb(); return null;
                case 6: _offset += 8; return null;
                case 7: String(); return null;
                case 8: SkipObject(); return null;
                case 9: SkipArray(template: false); return null;
                case 10 or 11 or 12: String(); return null;
                case 13: SkipTemplate(); return null;
                case 15: Atom(); SkipModule(); return Value();
                case 16: Byte(); Uleb(); Uleb(); Value(); return null;
                case 17: _offset += checked((int)Uleb()); return null;
                case 18: Uleb(); _offset += 8; return null;
                case 19 or 20: Value(); return null;
                case 21: Uleb(); return null;
            }
            if (tag != 14) throw new InvalidOperationException($"Unknown bytecode value tag {tag} at {_offset - 1}.");
            var offset = _offset - 1;
            var flags = (uint)(Byte() | Byte() << 8); Byte(); Atom();
            var args = Uleb(); var vars = Uleb(); Uleb(); Uleb(); var closureCount = Uleb(); var constantCount = Uleb(); var codeLength = Uleb();
            var defs = new List<(uint, uint, uint, byte)>();
            for (var count = Uleb(); count != 0; count--) defs.Add((Atom(), Uleb(), Uleb(), Byte()));
            var closures = new List<(uint, uint, byte)>();
            for (var index = 0u; index < closureCount; index++) closures.Add((Atom(), Uleb(), Byte()));
            var bytecodeOffset = _offset;
            _offset += checked((int)codeLength);
            if ((flags & (1u << 10)) != 0) { Atom(); Uleb(); _offset += checked((int)Uleb()); }
            var constants = new List<Function>();
            for (var index = 0u; index < constantCount; index++)
            {
                var constant = Value();
                if (constant is not null) constants.Add(constant);
            }
            return new Function(offset, bytecodeOffset, codeLength, args, vars, defs, closures, constants);
        }
        private void SkipObject()
        {
            for (var count = Uleb(); count != 0; count--) { Atom(); Value(); }
        }
        private void SkipArray(bool template)
        {
            for (var count = Uleb(); count != 0; count--) Value();
            if (template) Value();
        }
        private void SkipTemplate()
        {
            for (var count = Uleb(); count != 0; count--) Value();
            if (Byte() != 13) throw new InvalidOperationException("Malformed template literal constant.");
            for (var count = Uleb(); count != 0; count--) Value();
            Value();
        }
        private void SkipModule()
        {
            var required = Uleb(); for (var i = 0u; i < required; i++) Atom();
            var exports = Uleb(); for (var i = 0u; i < exports; i++) { var kind = Byte(); Uleb(); Atom(); if (kind != 0) Atom(); }
            var stars = Uleb(); for (var i = 0u; i < stars; i++) Uleb();
            var imports = Uleb(); for (var i = 0u; i < imports; i++) { Uleb(); Atom(); Uleb(); }
        }
    }
}
