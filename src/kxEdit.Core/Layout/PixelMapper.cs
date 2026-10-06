using kxEdit.Core.Text;

namespace kxEdit.Core.Layout;

/// <summary>
/// 折り返し済みセグメント内での char↔pixel マッピング(設計書 §2-3)。
/// セグメント先頭を x=0 とする純関数。P2 のキャレット位置決め・P3 のマウス衝突判定で使う。
/// </summary>
/// <remarks>
/// <b>長い行</b>(長さが <see cref="LongRowThreshold"/> を超えるセグメント)は、X をコードポイント幅の足し算
/// (<see cref="ICharMetrics.MeasureAdditive"/>)で求める(docs/plans/2026-10-06-long-row-geometry-design.md §3.3)。
/// GDI の一括計測は 43,679 字を超えると幅 0 を返し、また長い行を毎回一括で測ると行の長さに比例する GDI 計測が
/// 残るためである。短い行は従来どおり一括計測(座標は変えない)。判定はセグメント全体の長さで行う
/// (prefix の長さではない)ので、同じ行のキャレット・選択・描画は必ず同じ規則で測られる。
/// </remarks>
internal static class PixelMapper
{
    /// <summary>
    /// これを超える長さのセグメントを「長い行」とする。F-1 の区切り(<see cref="FrameBuilder.MaxCharsPerTextOp"/>)と
    /// 同じ値 = 通常の行では絶対に切り替わらない。
    /// </summary>
    internal const int LongRowThreshold = FrameBuilder.MaxCharsPerTextOp;

    /// <summary>窓にかかる文字範囲 [<paramref name="Start"/>, <paramref name="End"/>) と、Start の X(<see cref="SliceForWindow"/>)。</summary>
    internal readonly record struct RowSlice(int Start, int End, int StartPx);

    internal static bool IsLongRow(ReadOnlySpan<char> segment) => IsLongRow(segment.Length);

    /// <summary>長さ <paramref name="length"/> のセグメントが長い行か(本文を取る前に長さだけで判定したいとき用)。</summary>
    internal static bool IsLongRow(int length) => length > LongRowThreshold;

    /// <summary>
    /// segment 内の charOffset(0..segment.Length)を pixel(0..)にマップ。
    /// - charOffset&lt;=0 → 0 / charOffset&gt;=segment.Length → 全幅
    /// - low サロゲート位置に落ちた場合は前方スナップ(pair 先頭 = charOffset-1 に寄せる)
    /// - 長い行は足し算(クラスの remarks)
    /// </summary>
    /// <remarks>
    /// <b>このスナップ方針に外部が依存している</b>:
    /// <c>FrameBuilder.EmitBodyTextWithSelection</c> は選択境界で本文テキストを切り出す際に
    /// <see cref="TextBoundary.SnapToCodePointStart"/> を<b>自前で</b>掛け、その結果が本メソッドの
    /// 内部スナップと一致することを前提に x と文字を合わせている。
    /// スナップ方針を変える(後方スナップにする・論理文字単位にする等)ときは、
    /// <b>必ずあちらも一緒に直すこと</b>。放置すると選択矩形と文字が黙ってずれる。
    /// </remarks>
    public static int OffsetToPx(ReadOnlySpan<char> segment, int charOffset, ICharMetrics metrics)
    {
        if (charOffset <= 0)
            return 0;
        if (charOffset > segment.Length)
            charOffset = segment.Length;

        // low サロゲート位置なら pair 先頭へ前方スナップ
        charOffset = TextBoundary.SnapToCodePointStart(segment, charOffset);

        var prefix = segment[..charOffset];
        return IsLongRow(segment) ? metrics.MeasureAdditive(prefix) : metrics.MeasureRun(prefix);
    }

    /// <summary>
    /// x(px)に最も近い code-point 境界のオフセットを返す。
    /// - x&lt;=0 → 0 / x&gt;=全幅 → segment.Length
    /// - code-point に px が食い込む場合はその code-point の直後を返す
    ///   (=「入れば含める」・選択拡張の直観に合わせる)
    /// - サロゲートペアの中間には落ちない(常に pair の直後)
    /// </summary>
    /// <remarks>
    /// 長い行では、先頭の全幅の一括計測を行わない(43,679 字を超えると 0 になり、どの px でも行末を返してしまう)。
    /// 1 コードポイントずつ歩く処理だけで求め、歩き切ったら行末を返す(全幅以上の px と同じ結果)。
    /// 1 コードポイントの幅は <see cref="ICharMetrics.MeasureAdditive"/> で測る(契約上 <see cref="ICharMetrics.MeasureRun"/>
    /// と同じ値で、GDI の実装では表を引くぶん速い)。累積は long で足す(幅の和が int を超える行でも回り込まない)。
    /// </remarks>
    public static int PxToOffset(ReadOnlySpan<char> segment, int px, ICharMetrics metrics)
    {
        if (segment.IsEmpty)
            return 0;
        if (px <= 0)
            return 0;

        bool longRow = IsLongRow(segment);
        if (!longRow)
        {
            int total = metrics.MeasureRun(segment);
            if (px >= total)
                return segment.Length;
        }

        int i = 0;
        long accumulated = 0;
        while (i < segment.Length)
        {
            // 次の code-point を切り出す(サロゲートペアは 2 code-unit 分)
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);

            var cp = segment.Slice(i, cpLen);
            int cpWidth = longRow ? metrics.MeasureAdditive(cp) : metrics.MeasureRun(cp);

            // 累積 + この code-point の幅が px 以上なら、この code-point を含めた直後を返す
            if (accumulated + cpWidth >= px)
                return i + cpLen;

            accumulated += cpWidth;
            i += cpLen;
        }

        // 短い行では早期リターンで捕捉されるはずだが安全網。長い行では「全幅以上の px」がここに来る。
        return segment.Length;
    }

    /// <summary>
    /// セグメントの全幅(横スクロールバーの幅の計算用)。<see cref="OffsetToPx"/> で行末を測ったのと同じ値。
    /// 短い行は一括計測、長い行は足し算。
    /// </summary>
    internal static int RowWidthPx(ReadOnlySpan<char> segment, ICharMetrics metrics) =>
        IsLongRow(segment) ? metrics.MeasureAdditive(segment) : metrics.MeasureRun(segment);

    /// <summary>
    /// 窓 [<paramref name="leftPx"/>, <paramref name="rightPx"/>)(セグメント先頭が x = 0)にかかる文字範囲を返す
    /// (長い行の描画用。設計書 2026-10-06 §3.3)。X は足し算で測る。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Start: X の範囲 [x, x + w) が leftPx を含むコードポイントの先頭。leftPx &lt;= 0 なら 0。
    /// 幅 0 のコードポイント(結合文字など)からは始めない(基底文字のない結合文字を描かないため)。</item>
    /// <item>End: X の開始が rightPx 以上になる最初のコードポイントの、次のコードポイントの先頭
    /// (右端からはみ出すグリフのための 1 つの余裕)。行末を超えない。</item>
    /// <item>窓が行末より右なら Start = End = 行末(空)。</item>
    /// <item>StartPx は <c>OffsetToPx(segment, Start)</c>(長い行)と同じ値。境界はコードポイントの境界。</item>
    /// <item>費用は O(End)。窓より右は歩かない。</item>
    /// <item>X は long で足す(幅の和が int を超える行でも回り込まない)。StartPx は leftPx 以下なので int に収まる。</item>
    /// </list>
    /// </remarks>
    internal static RowSlice SliceForWindow(
        ReadOnlySpan<char> segment,
        int leftPx,
        int rightPx,
        ICharMetrics metrics
    )
    {
        int i = 0;
        long x = 0;
        if (leftPx > 0)
        {
            while (i < segment.Length)
            {
                int cpLen = TextBoundary.CodePointLengthAt(segment, i);
                int w = metrics.MeasureAdditive(segment.Slice(i, cpLen));
                if (x + w > leftPx)
                    break;
                x += w;
                i += cpLen;
            }
        }
        int start = i;
        int startPx = (int)x;
        while (i < segment.Length && x < rightPx)
        {
            int cpLen = TextBoundary.CodePointLengthAt(segment, i);
            x += metrics.MeasureAdditive(segment.Slice(i, cpLen));
            i += cpLen;
        }
        if (i < segment.Length && i > start)
            i += TextBoundary.CodePointLengthAt(segment, i);
        return new RowSlice(start, i, startPx);
    }
}
