using System.Text.RegularExpressions;
using Warp.ComponentCompiler.Scripting;
using Warp.Diagnostics;

namespace Warp.ComponentCompiler.Analysis;

public sealed class ConstantTable
{
    private readonly Dictionary<string, ConstDecl> _map = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ConstDecl> Entries => _map;
    public bool TryGet(string name, out ConstDecl decl) => _map.TryGetValue(name, out decl!);

    public static ConstantTable Build(ComponentLogic logic, DiagnosticSink sink)
    {
        var table = new ConstantTable();
        foreach (var c in logic.Consts)
        {
            if (table._map.ContainsKey(c.Name))
            {
                sink.Error($"const '{c.Name}' redeclared");
                continue;
            }
            var folded = TryFold(c.Raw, table, out var isFoldable);
            table._map[c.Name] = c with { Folded = folded, IsFoldable = isFoldable };
        }
        var dataKeys = ExtractDataKeys(logic);
        foreach (var name in table._map.Keys)
            if (dataKeys.Contains(name))
                sink.Warning($"const '{name}' shadows data key");
        return table;
    }

    private static object? TryFold(string raw, ConstantTable table, out bool isFoldable)
    {
        raw = raw.Trim().TrimEnd(';');
        if (raw == "true") { isFoldable = true; return true; }
        if (raw == "false") { isFoldable = true; return false; }
        if (raw == "null") { isFoldable = true; return null; }
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            && Regex.IsMatch(raw, @"^-?\d+(\.\d+)?$"))
        { isFoldable = true; return n; }
        if ((raw.StartsWith('"') && raw.EndsWith('"')) || (raw.StartsWith('\'') && raw.EndsWith('\'')))
        { isFoldable = true; return raw[1..^1]; }

        var expr = raw;
        foreach (var kv in table._map.Where(kv => kv.Value.IsFoldable))
        {
            var val = kv.Value.Folded;
            string rep = val is string s ? $"\"{s}\"" : val is bool b ? (b ? "true" : "false") : val?.ToString() ?? "null";
            if (val is double d) rep = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            expr = Regex.Replace(expr, $@"\b{Regex.Escape(kv.Key)}\b", rep);
        }
        if (!Regex.IsMatch(expr, @"^[\d\s\.\+\-\*/%<>=!&\|\(\)""'truefalsenull]+$")) { isFoldable = false; return null; }
        try
        {
            if (Regex.IsMatch(expr.Trim(), @"^[\d\s\.\+\-\*/%\(\)]+$"))
            {
                var dt = new System.Data.DataTable();
                var res = dt.Compute(expr, "");
                if (res is double || res is int || res is decimal)
                { isFoldable = true; return Convert.ToDouble(res); }
            }
            if (expr.Trim() is "true" or "false") { isFoldable = true; return expr.Trim() == "true"; }
        }
        catch { }
        isFoldable = false;
        return null;
    }

    private static HashSet<string> ExtractDataKeys(ComponentLogic logic)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "data", "protected", "private", "public" })
        {
            var prop = logic.ExportDefault?.Properties.FirstOrDefault(p => p.Name == key);
            if (prop is null) continue;
            foreach (Match m in Regex.Matches(prop.RawValue, @"(\w+)\s*:"))
            {
                var k = m.Groups[1].Value;
                if (k != key) keys.Add(k);
            }
        }
        return keys;
    }
}
