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
        return reader.Value();
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
        internal void String() { var encoded = Uleb(); _offset += checked((int)(encoded >> 1)); }
        internal uint Atom() => Uleb() >> 1;
        internal Function Value()
        {
            var tag = Byte();
            if (tag == 15) { Atom(); SkipModule(); return Value(); }
            if (tag != 14) throw new InvalidOperationException($"Expected function tag at {_offset - 1}.");
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
            for (var index = 0u; index < constantCount; index++) constants.Add(Value());
            return new Function(offset, bytecodeOffset, codeLength, args, vars, defs, closures, constants);
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
