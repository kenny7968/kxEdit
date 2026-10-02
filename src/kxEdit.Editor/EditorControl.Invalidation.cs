// EditorControl.Invalidation.cs
// 2026-09-27 申し送り回収 フェーズ 6(項目 22)で EditorControl.Paint.cs から移した。
// 描画の入力の差から、無効化する範囲を決める部分(行の帯・画素スクロール・IME の行)。
// フィールドは EditorControl.cs 本体で宣言。
using kxEdit.Core.Layout;

namespace kxEdit.Editor;

public sealed partial class EditorControl
{
    /// <summary>
    /// 今の描画の入力と、画面の絵の入力(<see cref="_lastPaintedInputs"/>)の差から、描き直しが要る行の帯だけを
    /// 無効化する(設計書 §14.1・計画 §0.2)。等しければ何もしない(フェーズ 3 の省略)。
    /// 行の外に効く入力(<see cref="FrameInputs.SameLayoutAs"/>)やスクロール位置が違えば全面。
    /// </summary>
    /// <remarks>
    /// 正しさの根拠は計画 §0.3 の不変条件 3。無効化する帯は、前回の絵と今の絵で画素が違いうる範囲をすべて覆う
    /// (記述子が行の絵を決め尽くす = <see cref="RowPaintKey"/> の doc。IME は原点の行と次の行)。
    /// 描画の入力を変える経路は、必ずこれか <see cref="Control.Invalidate()"/> を自分で呼ぶ(不変条件 2)。
    /// この引数なしの版はスクロールでは画素を移さない = スクロール位置が違えば全面(<c>allowScroll: false</c>)。
    /// </remarks>
    private void InvalidateChangedRows() => InvalidateChangedRows(allowScroll: false);

    /// <summary>
    /// <see cref="InvalidateChangedRows()"/> と同じ。<paramref name="allowScroll"/> が true(スクロールのセッター)なら、
    /// スクロール位置の差を <see cref="IPaintSurface.TryScroll"/> で画素の移動に変え、露出した帯と、
    /// 行の対応をずらして比べたときに記述子が違う行だけを無効化する(設計書 §14.2)。
    /// </summary>
    /// <remarks>
    /// 画素を移した後は <see cref="_lastPaintedInputs"/> を今の入力にする(計画 §0.3 の不変条件 3。
    /// 画面は、これから無効化する範囲を除いて、今の入力から描いた絵と同じになった)。
    /// 全面に落とす条件: 記録がない・行の外に効く入力が違う・縦と横が同時に変わった・IME の未確定がある・
    /// 移動量が可視域以上・行の対応が見つからない・<see cref="IPaintSurface.CanScroll"/> が false・移動に失敗した。
    /// 全面のまま残すもの(設計書 §14.2): クランプ(UpdateVerticalScrollbar など)・ReplaceSource・ApplyAppearance・
    /// WrapColumns は、この経路を通らないか allowScroll が false の経路で呼ばれる。
    /// </remarks>
    private void InvalidateChangedRows(bool allowScroll)
    {
        var now = CaptureFrameInputs();
        var old = _lastPaintedInputs;
        if (now is null || old is null)
        {
            Invalidate();
            return;
        }
        if (now.Equals(old))
            return;
        if (!now.SameLayoutAs(old))
        {
            Invalidate();
            return;
        }
        int lh = now.Metrics.LineHeightPx;
        bool scrolled =
            now.TopLine != old.TopLine
            || now.TopSegment != old.TopSegment
            || now.ScrollX != old.ScrollX;
        if (!scrolled)
        {
            InvalidateBands(
                FrameDiff.DirtyBands(
                    _rowCache.Keys(old),
                    _rowCache.Keys(now),
                    0,
                    lh,
                    now.PaintHeight
                )
            );
            InvalidateImeRows(old, now);
            return;
        }
        if (
            !allowScroll
            || old.Ime.IsActive
            || now.Ime.IsActive
            || !TryPlanScroll(old, now, out int shift, out int dx)
            || !_paintSurface.CanScroll(this)
        )
        {
            Invalidate();
            return;
        }
        var bands = FrameDiff.DirtyBands(
            _rowCache.Keys(old),
            _rowCache.Keys(now),
            shift,
            lh,
            now.PaintHeight
        );
        var area = new Rectangle(0, 0, now.PaintWidth, now.PaintHeight);
        int dy = -shift * lh;
        if (!_paintSurface.TryScroll(this, dx, dy, area, out var uncovered))
        {
            Invalidate();
            return;
        }
        _lastPaintedInputs = now;
        // ScrollWindowEx はシステムキャレットも画素と一緒に動かしうるので、置き直す(設計書 §14.2 の実機確認 1)。
        PositionCaret();
        InvalidateRectIfAny(uncovered);
        InvalidateRectIfAny(ExposedStrip(area, dx, dy));
        InvalidateBands(bands);
    }

    /// <summary>
    /// 行の対応(新しい行 i = 古い行 i + <paramref name="shift"/>)と横の移動量を決める。縦と横が同時に変わるとき・
    /// 移動量が(縦は可視行数以上・横は描画幅(<see cref="FrameInputs.PaintWidth"/>)以上)のとき・
    /// 行の対応が見つからないときは false(全面)。
    /// 行の対応は可視行の (論理行, 視覚行) で探す。対応が誤っていても記述子の比較が違う行を無効化するので、
    /// 正しさには効かない(効率だけ)。
    /// </summary>
    /// <remarks>
    /// 設計書 §14.2: 「移動量が可視行数未満」のときだけ画素スクロールを許す。PageUp/PageDown のように
    /// 可視行数以上動く場合は全面にする。可視行数は <c>PaintHeight / 行高</c>(切り捨て。
    /// <see cref="VisibleRowCount"/> と同じ定義)であり、「移動量(行) × 行高 &lt; PaintHeight」では
    /// ちょうど可視行数ぶんの移動を最後の部分行の余白で誤って通してしまう(2026-09-27 実測の退行:
    /// 可視行数 41・行高 16px・PaintHeight 661px のとき 41*16=656&lt;661 で受理していた)。
    /// </remarks>
    private bool TryPlanScroll(FrameInputs old, FrameInputs now, out int shift, out int dx)
    {
        shift = 0;
        dx = 0;
        bool vertical = now.TopLine != old.TopLine || now.TopSegment != old.TopSegment;
        bool horizontal = now.ScrollX != old.ScrollX;
        if (vertical && horizontal)
            return false;
        if (horizontal)
        {
            dx = old.ScrollX - now.ScrollX;
            return Math.Abs(dx) < now.PaintWidth;
        }
        var oldRows = _rowCache.Rows(old);
        var nowRows = _rowCache.Rows(now);
        if (oldRows.Count == 0 || nowRows.Count == 0)
            return false;
        int k = IndexOfRow(oldRows, nowRows[0]);
        if (k > 0)
            shift = k;
        else
        {
            int j = IndexOfRow(nowRows, oldRows[0]);
            if (j <= 0)
                return false;
            shift = -j;
        }
        // 行高 0 の防御(VisibleRowCount と同じ形): 万一 0 のとき、ガードなしでは 0 除算例外になる。
        int fullRows = now.PaintHeight / Math.Max(1, now.Metrics.LineHeightPx);
        return Math.Abs(shift) < fullRows;
    }

    private static int IndexOfRow(IReadOnlyList<VisualRow> rows, VisualRow target)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (
                rows[i].LogicalLine == target.LogicalLine
                && rows[i].SegmentIndex == target.SegmentIndex
            )
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 画素を移して露出した帯。この帯は必ず描き直しが要る(露出した行に加え、途中で切れていた旧最下行の、
    /// 画面になかった下半分もこの帯に入る。設計書 §14.2)。ScrollWindowEx の prcUpdate(uncovered)も通常はこの帯を含むが、
    /// その中身は実装(と偽の画面)に依存するので、それに頼らず自分で求めた帯も無効化する(保険)。
    /// </summary>
    private static Rectangle ExposedStrip(Rectangle area, int dx, int dy) =>
        dy < 0 ? Rectangle.FromLTRB(area.Left, area.Bottom + dy, area.Right, area.Bottom)
        : dy > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Right, area.Top + dy)
        : dx < 0 ? Rectangle.FromLTRB(area.Right + dx, area.Top, area.Right, area.Bottom)
        : dx > 0 ? Rectangle.FromLTRB(area.Left, area.Top, area.Left + dx, area.Bottom)
        : Rectangle.Empty;

    /// <summary>テスト専用: 画素の移動先を差し替える(偽の画面)。</summary>
    internal static void TestHook_SetPaintSurface(EditorControl c, IPaintSurface surface) =>
        c._paintSurface = surface;

    private void InvalidateBands(List<(int Top, int Bottom)> bands)
    {
        int width = ClientSize.Width;
        foreach (var (top, bottom) in bands)
            InvalidateRectIfAny(new Rectangle(0, top, width, bottom - top));
    }

    /// <summary>
    /// 未確定表示の行を無効化する。未確定表示は行の帯でクリップせずに描くので、原点の行と次の行の 2 行ぶんにする
    /// (太字のディセンダが帯を越える場合の余裕。越えないことは L5 で確かめる。設計書 §14.1 の例外 2)。
    /// </summary>
    private void InvalidateImeRows(FrameInputs old, FrameInputs now)
    {
        if (old.Ime.Equals(now.Ime) && old.ImeOrigin == now.ImeOrigin)
            return;
        int lh = now.Metrics.LineHeightPx;
        int width = ClientSize.Width;
        if (old.ImeOrigin is Point o)
            InvalidateRectIfAny(new Rectangle(0, o.Y, width, 2 * lh));
        if (now.ImeOrigin is Point n)
            InvalidateRectIfAny(new Rectangle(0, n.Y, width, 2 * lh));
    }

    /// <summary>
    /// クライアント領域と交差する部分だけを無効化する。空なら何もしない
    /// (<c>Invalidate(Rectangle.Empty)</c> は全面の無効化に化ける。設計書 §14.1)。
    /// </summary>
    private void InvalidateRectIfAny(Rectangle r)
    {
        r.Intersect(ClientRectangle);
        if (r.Width > 0 && r.Height > 0)
            Invalidate(r);
    }

    /// <summary>
    /// 本文・フォント・テーマを丸ごと差し替える経路の Invalidate。記録を捨てて、古いスナップショットや
    /// フォント・幅メモを次の描画まで握らない(描画されないタブで起きても解放される)。
    /// 捨てた後の比較は必ず「変化あり」になる(安全側)。
    /// </summary>
    private void InvalidateAndForgetPaintedFrame()
    {
        _lastPaintedInputs = null;
        _rowCache.Clear();
        Invalidate();
    }
}
