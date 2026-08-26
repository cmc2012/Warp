using System.Text.RegularExpressions;
using Warp.ComponentSyntax.Ast;
using Warp.Diagnostics;

namespace Warp.ComponentCompiler.Analysis;

public sealed class StaticAnalysis
{
    public sealed record PageBudget(
        int TotalNodes,
        int StaticNodes,
        int DynamicAttrs,
        int NoKeyLists,
        int StringParseSites,
        int FoldedConsts,
        int DeadBranches,
        int ComponentCount,
        int MaxNesting);

    public static PageBudget Analyze(UxDocument doc, ConstantTable constTable, DiagnosticSink sink)
    {
        var totalNodes = CountNodes(doc);
        int dynamicAttrs = 0;
        int noKeyLists = 0;
        int deadBranches = 0;
        int maxNesting = 1;

        void Walk(IReadOnlyList<UxNode> nodes, int depth)
        {
            maxNesting = Math.Max(maxNesting, depth);
            foreach (var n in nodes)
            {
                if (n is UxElement el)
                {
                    foreach (var a in el.Attrs)
                    {
                        if (a.Value is BindingValue or ExprValue)
                        {
                            var isFolded = IsFoldedAttr(a.Value, constTable);
                            if (!isFolded) dynamicAttrs++;
                            // Style {Expr '...;...;'} -> W-PERF-101
                            if (a.Kind == AttrKind.Style && a.Value is ExprValue ev && ev.Expr.Contains(';'))
                                sink.Warning("dynamic Style uses string concat, bind prebuilt object", a.Position);
                        }
                    }
                    Walk(el.Children, depth + 1);
                }
                else if (n is UxListNode l)
                {
                    if (l.Key is null) noKeyLists++;
                    if (l.ItemsSource is BindingValue or ExprValue && !IsFoldedAttr(l.ItemsSource, constTable))
                        dynamicAttrs++;
                    Walk([l.ItemTemplateRoot], depth + 1);
                }
                else if (n is UxIfChain c)
                {
                    foreach (var br in c.Branches)
                    {
                        if (br.Test is not null && !IsFoldedAttr(br.Test, constTable))
                            dynamicAttrs++;
                        if (br.Test is ExprValue ev2 && TryEvalBool(ev2.Expr, constTable) is bool b)
                        {
                            if (!b) deadBranches++;
                        }
                        Walk(br.Children, depth + 1);
                    }
                }
            }
        }

        Walk(doc.Children, 1);

        var staticNodes = Math.Max(0, totalNodes - dynamicAttrs);
        return new PageBudget(totalNodes, staticNodes, dynamicAttrs, noKeyLists, 0,
            constTable.Entries.Count(kv => kv.Value.IsFoldable), deadBranches,
            doc.Imports.Count, maxNesting);
    }

    private static bool IsFoldedAttr(AttrValue v, ConstantTable table)
    {
        string? name = v switch { BindingValue b => b.Path, ExprValue e => ExtractFirstIdent(e.Expr), _ => null };
        if (name is null) return false;
        return table.TryGet(name, out var decl) && decl.IsFoldable;
    }

    private static string? ExtractFirstIdent(string expr)
    {
        var m = Regex.Match(expr.Trim(), @"^([A-Za-z_$][A-Za-z0-9_$]*)\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool? TryEvalBool(string expr, ConstantTable table)
    {
        expr = expr.Trim();
        if (expr == "true") return true;
        if (expr == "false") return false;
        if (table.TryGet(expr, out var d) && d.IsFoldable && d.Folded is bool b) return b;
        return null;
    }

    private static int CountNodes(UxDocument doc)
    {
        return CountList(doc.Children);
    }
    private static int CountList(IReadOnlyList<UxNode> nodes)
    {
        int n = 0;
        foreach (var node in nodes)
        {
            n++;
            if (node is UxElement el) n += CountList(el.Children);
            else if (node is UxListNode l) n += CountList([l.ItemTemplateRoot]);
            else if (node is UxIfChain c) foreach (var b in c.Branches) n += CountList(b.Children);
        }
        return n;
    }
}
