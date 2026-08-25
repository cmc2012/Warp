namespace Warp.JsCompiler.ObjectFormat;

/// <summary>
/// Owns the dynamic part of a bytecode atom table.  Atom ids are assigned by
/// first registration, never by the storage collection that happened to
/// discover a name. Lowering passes register atoms at their source-emission
/// point without coupling serialization to the collection that owns a name.
/// </summary>
internal sealed class DynamicAtomTable
{
    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _indices = new(StringComparer.Ordinal);

    internal IReadOnlyList<string> Names => _names;

    internal int Register(string name)
    {
        if (_indices.TryGetValue(name, out var index)) return index;
        index = _names.Count;
        _names.Add(name);
        _indices.Add(name, index);
        return index;
    }

    internal bool TryGetIndex(string name, out int index) => _indices.TryGetValue(name, out index);
}
