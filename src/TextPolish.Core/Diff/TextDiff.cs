using System.Text;

namespace TextPolish.Core.Diff;

public enum DiffKind
{
    Unchanged,
    Removed,
    Added,
}

/// <summary>差分の一区切り。表示側はこれを順に並べて色分けする。</summary>
public sealed record DiffSegment(DiffKind Kind, string Text);

public sealed class DiffResult
{
    public IReadOnlyList<DiffSegment> Segments { get; init; } = Array.Empty<DiffSegment>();

    /// <summary>
    /// 書き換え率(0.0〜1.0)。原文と結果の長い方に対して、変化しなかった文字の割合を引いた値。
    /// 翻訳のように文字が一切重ならない場合は 1.0 に近づく。
    /// </summary>
    public double RewriteRatio { get; init; }

    /// <summary>テキストが長すぎて差分を計算しなかった場合に true。</summary>
    public bool IsTooLong { get; init; }
}

/// <summary>
/// 原文と整形結果の文字単位の差分を求める。
///
/// 外部ライブラリを使わずLCS(最長共通部分列)で実装している。
/// このプロジェクトは外部NuGet依存ゼロを維持しており、
/// 用途も「短い選択テキストの変更箇所を色分けする」ことに限られるため。
/// </summary>
public static class TextDiff
{
    /// <summary>
    /// 差分を計算する上限文字数。LCSのDP表は原文長×結果長のメモリを使うため制限している。
    /// 音声入力で選択する範囲は通常これより十分短い。
    /// </summary>
    public const int MaxLength = 2000;

    public static DiffResult Compute(string? original, string? result)
    {
        original ??= string.Empty;
        result ??= string.Empty;

        if (original.Length > MaxLength || result.Length > MaxLength)
        {
            return new DiffResult { IsTooLong = true, RewriteRatio = 1.0 };
        }

        // 共通の先頭・末尾を先に切り出しておく。誤変換の修正のように大部分が一致する場合、
        // DP表のサイズが大きく縮む。
        var prefixLength = CommonPrefixLength(original, result);
        var suffixLength = CommonSuffixLength(original, result, prefixLength);

        var middleA = original[prefixLength..(original.Length - suffixLength)];
        var middleB = result[prefixLength..(result.Length - suffixLength)];

        var segments = new List<DiffSegment>();

        if (prefixLength > 0)
        {
            segments.Add(new DiffSegment(DiffKind.Unchanged, original[..prefixLength]));
        }

        segments.AddRange(DiffMiddle(middleA, middleB));

        if (suffixLength > 0)
        {
            segments.Add(new DiffSegment(DiffKind.Unchanged, original[^suffixLength..]));
        }

        var merged = MergeAdjacent(segments);

        var unchangedLength = merged
            .Where(s => s.Kind == DiffKind.Unchanged)
            .Sum(s => s.Text.Length);

        var denominator = Math.Max(original.Length, result.Length);
        var ratio = denominator == 0 ? 0.0 : 1.0 - ((double)unchangedLength / denominator);

        return new DiffResult { Segments = merged, RewriteRatio = ratio };
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private static int CommonSuffixLength(string a, string b, int prefixLength)
    {
        // 先頭で消費した分を食い合わないよう、残り長さを上限にする。
        var max = Math.Min(a.Length, b.Length) - prefixLength;
        var i = 0;
        while (i < max && a[a.Length - 1 - i] == b[b.Length - 1 - i])
        {
            i++;
        }

        return i;
    }

    private static List<DiffSegment> DiffMiddle(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return new List<DiffSegment>();
        }

        var ops = new List<(DiffKind Kind, char Ch)>();

        if (a.Length == 0)
        {
            foreach (var c in b)
            {
                ops.Add((DiffKind.Added, c));
            }

            return ToSegments(ops);
        }

        if (b.Length == 0)
        {
            foreach (var c in a)
            {
                ops.Add((DiffKind.Removed, c));
            }

            return ToSegments(ops);
        }

        // dp[i, j] = a[i..] と b[j..] のLCS長。末尾から埋めることで、
        // 復元を先頭から前向きに進められる(結果を反転させる必要がない)。
        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                ops.Add((DiffKind.Unchanged, a[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                ops.Add((DiffKind.Removed, a[x]));
                x++;
            }
            else
            {
                ops.Add((DiffKind.Added, b[y]));
                y++;
            }
        }

        while (x < a.Length)
        {
            ops.Add((DiffKind.Removed, a[x]));
            x++;
        }

        while (y < b.Length)
        {
            ops.Add((DiffKind.Added, b[y]));
            y++;
        }

        return ToSegments(ops);
    }

    /// <summary>
    /// 文字単位の操作列を区切りにまとめる。
    /// 置換箇所では削除・追加が交互に現れることがあるため、
    /// 変更のかたまりごとに「削除をまとめて → 追加をまとめて」の順に並べ直している
    /// (赤と緑が細かく交互に出ると読めないため)。
    /// </summary>
    private static List<DiffSegment> ToSegments(List<(DiffKind Kind, char Ch)> ops)
    {
        var segments = new List<DiffSegment>();
        var removed = new StringBuilder();
        var added = new StringBuilder();
        var unchanged = new StringBuilder();

        void FlushChange()
        {
            if (removed.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Removed, removed.ToString()));
                removed.Clear();
            }

            if (added.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Added, added.ToString()));
                added.Clear();
            }
        }

        void FlushUnchanged()
        {
            if (unchanged.Length > 0)
            {
                segments.Add(new DiffSegment(DiffKind.Unchanged, unchanged.ToString()));
                unchanged.Clear();
            }
        }

        foreach (var op in ops)
        {
            if (op.Kind == DiffKind.Unchanged)
            {
                FlushChange();
                unchanged.Append(op.Ch);
            }
            else
            {
                FlushUnchanged();
                if (op.Kind == DiffKind.Removed)
                {
                    removed.Append(op.Ch);
                }
                else
                {
                    added.Append(op.Ch);
                }
            }
        }

        FlushChange();
        FlushUnchanged();

        return segments;
    }

    private static List<DiffSegment> MergeAdjacent(List<DiffSegment> segments)
    {
        var merged = new List<DiffSegment>();

        foreach (var segment in segments)
        {
            if (segment.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Kind == segment.Kind)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + segment.Text };
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }
}
