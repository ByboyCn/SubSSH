// unified diff 输出（对应 Go pmezard/go-difflib GetUnifiedDiffString：context=3，
// 头部 "--- file\n+++ file\n"，无差异时输出空串；行按 SplitLines 保留换行）。
namespace SubSsh.Plugin.Files;

internal static class UnifiedDiff
{
    /// <summary>LCS 动态规划的行数上限；超出时退化为前缀/后缀对齐的整段替换。</summary>
    private const int MaxLcsLines = 3000;

    private enum Tag : byte { Equal, Delete, Insert, Replace }

    public static string Create(string[] a, string[] b, string fromFile, string toFile, int context)
    {
        if (a.SequenceEqual(b, StringComparer.Ordinal)) return ""; // 对应 go-difflib：无差异时无输出
        var ops = a.Length <= MaxLcsLines && b.Length <= MaxLcsLines ? LcsOpcodes(a, b) : PrefixSuffixOpcodes(a, b);
        var sb = new System.Text.StringBuilder();
        foreach (var group in GroupOpcodes(ops, context))
        {
            var first = group[0];
            var last = group[^1];
            sb.Append("@@ -").Append(FormatRange(first.I1, last.I2))
              .Append(" +").Append(FormatRange(first.J1, last.J2))
              .Append(" @@\n");
            foreach (var (tag, i1, i2, j1, j2) in group)
            {
                if (tag == Tag.Equal)
                {
                    for (var i = i1; i < i2; i++) WriteBody(sb, ' ', a[i]);
                }
                else
                {
                    if (tag is Tag.Delete or Tag.Replace)
                        for (var i = i1; i < i2; i++) WriteBody(sb, '-', a[i]);
                    if (tag is Tag.Insert or Tag.Replace)
                        for (var j = j1; j < j2; j++) WriteBody(sb, '+', b[j]);
                }
            }
        }
        if (sb.Length == 0) return "";
        sb.Insert(0, $"--- {fromFile}\n+++ {toFile}\n");
        return sb.ToString();
    }

    private static void WriteBody(System.Text.StringBuilder sb, char sign, string line)
    {
        sb.Append(sign).Append(line);
        if (line.Length == 0 || line[^1] != '\n')
            sb.Append('\n').Append("\\ No newline at end of file\n"); // 对应 go-difflib 的 Eol 补齐
    }

    /// <summary>范围格式（对应 go-difflib formatRangeUnified：单行只写起始号，空范围起始号减一）。</summary>
    private static string FormatRange(int start, int stop)
    {
        var beginning = start + 1;
        var length = stop - start;
        if (length == 1) return beginning.ToString();
        if (length == 0) beginning--;
        return $"{beginning},{length}";
    }

    /// <summary>按 context 分组（对应 go-difflib GetGroupedOpCodes：相隔超过 2*context 的等值段拆组）。</summary>
    private static List<List<(Tag Tag, int I1, int I2, int J1, int J2)>> GroupOpcodes(
        List<(Tag Tag, int I1, int I2, int J1, int J2)> ops, int n)
    {
        var codes = new List<(Tag, int, int, int, int)>(ops);
        if (codes.Count == 0) codes.Add((Tag.Equal, 0, 1, 0, 1));
        if (codes[0].Item1 == Tag.Equal)
            codes[0] = (Tag.Equal, Math.Max(codes[0].Item2, codes[0].Item3 - n), codes[0].Item3,
                Math.Max(codes[0].Item4, codes[0].Item5 - n), codes[0].Item5);
        var lastIdx = codes.Count - 1;
        if (codes[lastIdx].Item1 == Tag.Equal)
            codes[lastIdx] = (Tag.Equal, codes[lastIdx].Item2, Math.Min(codes[lastIdx].Item3, codes[lastIdx].Item2 + n),
                codes[lastIdx].Item4, Math.Min(codes[lastIdx].Item5, codes[lastIdx].Item4 + n));
        var nn = n + n;
        var groups = new List<List<(Tag, int, int, int, int)>>();
        var group = new List<(Tag, int, int, int, int)>();
        foreach (var c in codes)
        {
            var (tag, i1, i2, j1, j2) = c;
            if (tag == Tag.Equal && i2 - i1 > nn)
            {
                group.Add((Tag.Equal, i1, Math.Min(i2, i1 + n), j1, Math.Min(j2, j1 + n)));
                groups.Add(group);
                group = [];
                i1 = Math.Max(i1, i2 - n);
                j1 = Math.Max(j1, j2 - n);
            }
            group.Add((tag, i1, i2, j1, j2));
        }
        if (group.Count > 0 && !(group.Count == 1 && group[0].Item1 == Tag.Equal))
            groups.Add(group);
        return groups;
    }

    /// <summary>LCS 动态规划生成 opcode 序列（等价 go-difflib SequenceMatcher 的 diff 语义）。</summary>
    private static List<(Tag, int, int, int, int)> LcsOpcodes(string[] a, string[] b)
    {
        var n = a.Length; var m = b.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = StringComparer.Ordinal.Equals(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(Tag, int, int, int, int)>();
        var ai = 0; var bi = 0;
        while (ai < n && bi < m)
        {
            if (StringComparer.Ordinal.Equals(a[ai], b[bi]))
            {
                AppendOp(ops, Tag.Equal, ai, ai + 1, bi, bi + 1);
                ai++; bi++;
            }
            else if (lcs[ai + 1, bi] >= lcs[ai, bi + 1])
            {
                AppendOp(ops, Tag.Delete, ai, ai + 1, bi, bi);
                ai++;
            }
            else
            {
                AppendOp(ops, Tag.Insert, ai, ai, bi, bi + 1);
                bi++;
            }
        }
        if (ai < n) AppendOp(ops, Tag.Delete, ai, n, bi, bi);
        if (bi < m) AppendOp(ops, Tag.Insert, ai, ai, bi, m);
        return ops;
    }

    /// <summary>大文件退化：公共前缀/后缀对齐后整段替换（不再做 LCS，行为正确但粒度更粗）。</summary>
    private static List<(Tag, int, int, int, int)> PrefixSuffixOpcodes(string[] a, string[] b)
    {
        var ops = new List<(Tag, int, int, int, int)>();
        int pre = 0;
        while (pre < a.Length && pre < b.Length && StringComparer.Ordinal.Equals(a[pre], b[pre])) pre++;
        int sufA = a.Length, sufB = b.Length;
        while (sufA > pre && sufB > pre && StringComparer.Ordinal.Equals(a[sufA - 1], b[sufB - 1])) { sufA--; sufB--; }
        if (pre > 0) AppendOp(ops, Tag.Equal, 0, pre, 0, pre);
        if (sufA > pre || sufB > pre)
            AppendOp(ops, sufA > pre && sufB > pre ? Tag.Replace : (sufA > pre ? Tag.Delete : Tag.Insert), pre, sufA, pre, sufB);
        if (sufA < a.Length) AppendOp(ops, Tag.Equal, sufA, a.Length, sufB, b.Length);
        return ops;
    }

    /// <summary>相邻同类型 opcode 合并（保持序列紧凑）。</summary>
    private static void AppendOp(List<(Tag, int, int, int, int)> ops, Tag tag, int i1, int i2, int j1, int j2)
    {
        if (i1 == i2 && j1 == j2) return;
        if (ops.Count > 0)
        {
            var (pt, pi1, pi2, pj1, pj2) = ops[^1];
            if (pt == tag && pi2 == i1 && pj2 == j1)
            {
                ops[^1] = (pt, pi1, i2, pj1, j2);
                return;
            }
        }
        ops.Add((tag, i1, i2, j1, j2));
    }
}
