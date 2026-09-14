using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class FrameBuilderTests
{
    // MonoCharMetrics(halfWidthPx:1, lineHeightPx:10) を全テストで共有=決定的な座標
    private static MonoCharMetrics M => new(halfWidthPx: 1, lineHeightPx: 10);

    // テスト用スタイル: 全フィールドを識別可能な RGB で埋める。
    // (実装が style から色を拾わずに default を返しているとテストが落ちる)
    // SelectionFore は null = 標準テーマ相当(選択中も本文色のまま=本文 op を分割しない)。
    // 分割側の網は FrameBuilderSelectionForeTests が持つ。
    private static ViewportStyle TestStyle() =>
        new(
            Foreground: new PaintColor(0x000000),
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: new PaintColor(0xADD8E6),
            SelectionFore: null,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );

    private static IReadOnlyList<VisualRow> BuildRows(
        TextSnapshot snap,
        int wrapCols = 0,
        int height = 1000
    ) =>
        ViewportLayout.Build(
            snap,
            topLine: 0,
            topSegment: 0,
            heightPx: height,
            wrapColumns: wrapCols,
            M
        );

    // ---------- 仕様 1: 背景塗り ----------
    [Fact]
    public void Background_fill_is_first_op_and_covers_full_client()
    {
        var buf = TextBuffer.FromString("ab");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        var first = frame.Ops[0];
        Assert.Equal(PaintOpKind.FillRect, first.Kind);
        Assert.Equal(0, first.X);
        Assert.Equal(0, first.Y);
        Assert.Equal(200, first.Width);
        Assert.Equal(100, first.Height);
        Assert.Equal(style.Background, first.Back);
        Assert.Equal(200, frame.ClientWidth);
        Assert.Equal(100, frame.ClientHeight);
    }

    // ---------- 仕様 2: 行描画 ----------
    [Fact]
    public void Two_lines_produce_two_body_drawtext_ops_at_expected_y()
    {
        var buf = TextBuffer.FromString("ab\ncd");
        var rows = BuildRows(buf.Current, wrapCols: 0, height: 20);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        var bodyTexts = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText)
            .Where(op => op.Text is "ab" or "cd")
            .OrderBy(op => op.Y)
            .ToList();

        Assert.Equal(2, bodyTexts.Count);
        Assert.Equal("ab", bodyTexts[0].Text);
        Assert.Equal(0, bodyTexts[0].Y);
        Assert.Equal(0, bodyTexts[0].X);
        Assert.Equal(style.Foreground, bodyTexts[0].Fore);
        Assert.Equal("cd", bodyTexts[1].Text);
        Assert.Equal(10, bodyTexts[1].Y);
        Assert.Equal(0, bodyTexts[1].X);
    }

    // ---------- 仕様 3: 現在行強調 ----------
    [Fact]
    public void Current_line_fillrect_precedes_body_text_for_that_row()
    {
        var buf = TextBuffer.FromString("ab\ncd");
        var rows = BuildRows(buf.Current, height: 30);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 30,
            lineNumberMarginPx: 0,
            currentLineLogical: 1, // "cd" 行
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        int currentLineRectIdx = -1;
        int cdTextIdx = -1;
        for (int i = 0; i < frame.Ops.Count; i++)
        {
            var op = frame.Ops[i];
            if (
                op.Kind == PaintOpKind.FillRect
                && op.Y == 10
                && op.Height == 10
                && op.X == 0
                && op.Width == 100
                && op.Back == style.CurrentLineBack
            )
            {
                currentLineRectIdx = i;
            }
            if (op.Kind == PaintOpKind.DrawText && op.Text == "cd")
                cdTextIdx = i;
        }

        Assert.NotEqual(-1, currentLineRectIdx);
        Assert.NotEqual(-1, cdTextIdx);
        Assert.True(
            currentLineRectIdx < cdTextIdx,
            "現在行強調は本文テキストより前に配置されている必要がある"
        );
    }

    // ---------- 仕様 4: 選択 ----------
    [Fact]
    public void Selection_1_to_3_of_abcd_creates_fillrect_before_body_text()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: new SelectionRange(1, 3),
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        int selRectIdx = -1;
        int abcdTextIdx = -1;
        for (int i = 0; i < frame.Ops.Count; i++)
        {
            var op = frame.Ops[i];
            if (
                op.Kind == PaintOpKind.FillRect
                && op.X == 1
                && op.Width == 2 // OffsetToPx(1)=1, OffsetToPx(3)=3, W=2
                && op.Y == 0
                && op.Height == 10
                && op.Back == style.SelectionBack
            )
            {
                selRectIdx = i;
            }
            if (op.Kind == PaintOpKind.DrawText && op.Text == "abcd")
                abcdTextIdx = i;
        }

        Assert.NotEqual(-1, selRectIdx);
        Assert.NotEqual(-1, abcdTextIdx);
        Assert.True(selRectIdx < abcdTextIdx);
    }

    // ---------- 仕様 5: 行番号マージン ----------
    [Fact]
    public void Line_number_margin_offsets_body_and_emits_right_aligned_numbers()
    {
        var buf = TextBuffer.FromString("ab\ncd");
        var rows = BuildRows(buf.Current, height: 30);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 30,
            lineNumberMarginPx: 30,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        // 本文が X=30 で描かれる
        var body = frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && (op.Text == "ab" || op.Text == "cd")
            )
            .OrderBy(op => op.Y)
            .ToList();
        Assert.Equal(2, body.Count);
        Assert.All(body, op => Assert.Equal(30, op.X));

        // 行番号 "1"/"2" が LineNumberFore で描かれる(右寄せ・X < 30)
        var numbers = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText && (op.Text == "1" || op.Text == "2"))
            .OrderBy(op => op.Y)
            .ToList();
        Assert.Equal(2, numbers.Count);
        Assert.Equal("1", numbers[0].Text);
        Assert.Equal(0, numbers[0].Y);
        Assert.Equal(style.LineNumberFore, numbers[0].Fore);
        Assert.Equal("2", numbers[1].Text);
        Assert.Equal(10, numbers[1].Y);
        // 右寄せ: 数字の右端が (marginPx - padding) に収まる。X + width <= 30 - padding は緩めの検査。
        Assert.All(numbers, op => Assert.True(op.X >= 0 && op.X + op.Width <= 30));
    }

    // ---------- 仕様 6: セルハイライト ----------
    [Fact]
    public void Cell_highlight_produces_four_drawline_and_semi_transparent_fillrect()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: new SelectionRange(1, 3),
            showWhitespace: false,
            style,
            M
        );

        // 半透明 FillRect(Alpha=60・SelectionBack ではなく HighlightOutline 由来の色)
        var hlBack = frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.FillRect
                && op.Y == 0
                && op.Height == 10
                && op.X == 1
                && op.Width == 2
                && op.Back.Alpha == 60
            )
            .ToList();
        Assert.Single(hlBack);

        // 枠 DrawLine が 4 本(上下左右)
        var lines = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawLine && op.Fore == style.HighlightOutline)
            .ToList();
        Assert.Equal(4, lines.Count);

        // 4 本の内訳: 上下=Height 0・左右=Width 0
        Assert.Equal(2, lines.Count(l => l.Height == 0));
        Assert.Equal(2, lines.Count(l => l.Width == 0));
    }

    // ---------- 仕様 7: 空白可視化 ----------
    [Fact]
    public void Show_whitespace_true_emits_glyph_drawtext_for_space_and_tab()
    {
        // "a b\tc": ' ' が index 1、'\t' が index 3
        var buf = TextBuffer.FromString("a b\tc");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: true,
            style,
            M
        );

        // 空白グリフは WhitespaceGlyph 色で描画される個別 DrawText
        var glyphs = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore == style.WhitespaceGlyph)
            .ToList();
        Assert.Equal(2, glyphs.Count);

        // 座標: OffsetToPx で計算(halfWidthPx=1 なら index==pixel)
        var spaceGlyph = glyphs.Single(g => g.X == 1);
        var tabGlyph = glyphs.Single(g => g.X == 3);
        Assert.False(string.IsNullOrEmpty(spaceGlyph.Text));
        Assert.False(string.IsNullOrEmpty(tabGlyph.Text));
        Assert.NotEqual(spaceGlyph.Text, tabGlyph.Text); // スペース/タブは違うグリフ
    }

    // ---------- 仕様 7 対偶: 空白可視化 OFF ----------
    // Task 11 の受け口(EditorControl.ShowWhitespace)が false のとき FrameBuilder が
    // グリフ DrawText を発行しないことを固定する。回帰時、仕様 7 と対で意図が明確になる。
    [Fact]
    public void Show_whitespace_false_emits_no_glyph_drawtext()
    {
        var buf = TextBuffer.FromString("a b\tc");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        var glyphs = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore == style.WhitespaceGlyph)
            .ToList();
        Assert.Empty(glyphs);
    }

    // ---------- 仕様 8: 空フレーム ----------
    [Fact]
    public void Empty_document_yields_background_and_at_least_one_empty_body_text()
    {
        var buf = TextBuffer.FromString(string.Empty);
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 50,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        Assert.Equal(PaintOpKind.FillRect, frame.Ops[0].Kind);
        Assert.Equal(style.Background, frame.Ops[0].Back);

        // 空視覚行1個ぶんの本文 DrawText("") が存在(Y=0)
        var emptyBody = frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && op.Text == string.Empty && op.Y == 0
            )
            .ToList();
        Assert.Single(emptyBody);
    }

    // ---------- 追加境界: 選択が視覚行を跨ぐ ----------
    [Fact]
    public void Selection_spanning_two_visual_rows_creates_two_rects()
    {
        // "ab\ncd" 論理2行。選択 [1, 4) → line0 の 'b' + line1 の 'c'
        // (改行位置=2、line1 開始=3、line1 の 'c' は絶対 offset 3)
        var buf = TextBuffer.FromString("ab\ncd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: new SelectionRange(1, 4),
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        var selRects = frame
            .Ops.Where(op => op.Kind == PaintOpKind.FillRect && op.Back == style.SelectionBack)
            .ToList();
        Assert.Equal(2, selRects.Count);

        // Y の重複なし・視覚行ごとに 1 矩形
        Assert.Contains(selRects, r => r.Y == 0);
        Assert.Contains(selRects, r => r.Y == 10);
    }

    // ---------- 追加境界: 選択なし・現在行なし ----------
    [Fact]
    public void No_selection_and_no_current_line_produces_no_such_rects()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        Assert.DoesNotContain(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.CurrentLineBack
        );
        Assert.DoesNotContain(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.SelectionBack
        );
    }

    // ---------- 追加境界: 行番号マージン=0 ----------
    [Fact]
    public void Line_number_margin_zero_omits_line_numbers_and_body_x_is_zero()
    {
        var buf = TextBuffer.FromString("ab\ncd");
        var rows = BuildRows(buf.Current, height: 30);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 30,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        // "1"/"2" の DrawText がない
        Assert.DoesNotContain(
            frame.Ops,
            op => op.Kind == PaintOpKind.DrawText && (op.Text == "1" || op.Text == "2")
        );

        // 本文は X=0
        var body = frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && (op.Text == "ab" || op.Text == "cd")
            )
            .ToList();
        Assert.Equal(2, body.Count);
        Assert.All(body, op => Assert.Equal(0, op.X));
    }

    // ---------- 追加境界: cellHighlight=null ----------
    [Fact]
    public void Cell_highlight_null_produces_no_border_lines()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        Assert.DoesNotContain(frame.Ops, op => op.Kind == PaintOpKind.DrawLine);
    }

    // ---------- 追加: 重なり順(背景→現在行→選択→本文) ----------
    [Fact]
    public void Ordering_background_before_current_line_before_selection_before_body()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 20,
            lineNumberMarginPx: 0,
            currentLineLogical: 0,
            selection: new SelectionRange(1, 3),
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        int bgIdx = IndexOf(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.Background
        );
        int curIdx = IndexOf(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.CurrentLineBack
        );
        int selIdx = IndexOf(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.SelectionBack
        );
        int bodyIdx = IndexOf(
            frame.Ops,
            op => op.Kind == PaintOpKind.DrawText && op.Text == "abcd"
        );

        Assert.True(bgIdx >= 0);
        Assert.True(curIdx >= 0);
        Assert.True(selIdx >= 0);
        Assert.True(bodyIdx >= 0);
        Assert.True(bgIdx < curIdx, $"背景({bgIdx})は現在行({curIdx})より前");
        Assert.True(curIdx < selIdx, $"現在行({curIdx})は選択({selIdx})より前");
        Assert.True(selIdx < bodyIdx, $"選択({selIdx})は本文({bodyIdx})より前");
    }

    private static int IndexOf(IReadOnlyList<PaintOp> ops, Func<PaintOp, bool> predicate)
    {
        for (int i = 0; i < ops.Count; i++)
            if (predicate(ops[i]))
                return i;
        return -1;
    }

    // ---------- 追加: SelectionRange invariant ----------
    [Fact]
    public void SelectionRange_throws_when_start_greater_than_end()
    {
        Assert.Throws<ArgumentException>(() => new SelectionRange(5, 3));
    }

    // 非負も invariant。FrameBuilder は交差を End - rowStart で行内オフセットへ落とすので、
    // 大きく負の値は unchecked で正へラップし、行の長さを超える添字になって原因から遠い場所で
    // 落ちる。範囲の invariant は入口で守る(2026-09-14 の脆弱性レビュー指摘)。
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(int.MinValue, int.MinValue + 5)]
    public void SelectionRange_throws_when_start_is_negative(int start, int end)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SelectionRange(start, end));
    }

    [Fact]
    public void SelectionRange_allows_equal_start_end_for_empty_range()
    {
        var s = new SelectionRange(3, 3);
        Assert.Equal(3, s.Start);
        Assert.Equal(3, s.End);
    }

    // ---------- 追加: 現在行の行番号は Foreground 色(Task 8) ----------
    [Fact]
    public void Current_line_number_uses_foreground_color()
    {
        var buf = TextBuffer.FromString("a\nb\nc");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 30,
            currentLineLogical: 1, // 2番目の行(0始まり)を現在行に
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        // 現在行の行番号 "2" は Foreground 色(マージン内=X<30)
        var currentLineNumber = frame.Ops.FirstOrDefault(op =>
            op.Kind == PaintOpKind.DrawText && op.Text == "2" && op.X < 30
        );
        Assert.NotEqual(default(PaintOp), currentLineNumber);
        Assert.Equal(style.Foreground, currentLineNumber.Fore);

        // 他行の行番号 "1"/"3" は LineNumberFore
        var otherLineNumbers = frame
            .Ops.Where(op =>
                op.Kind == PaintOpKind.DrawText && (op.Text == "1" || op.Text == "3") && op.X < 30
            )
            .ToList();
        Assert.Equal(2, otherLineNumbers.Count);
        Assert.All(otherLineNumbers, op => Assert.Equal(style.LineNumberFore, op.Fore));
    }

    // ---------- 追加(Task 9): FrameBuilder の契約 = 選択と現在行の両方が渡されれば両方描く ----------
    // 「選択中は現在行を出さない」判断は EditorControl 層の責務であり、FrameBuilder は
    // 与えられた引数どおりに両方を発行する。この契約が崩れると EditorControl 側の
    // "選択がある間は currentLineLogical=-1 を渡す" 分岐が意味を失うため固定する。
    [Fact]
    public void When_selection_and_current_line_both_active_both_fillrects_are_emitted()
    {
        var buf = TextBuffer.FromString("abcd");
        var rows = BuildRows(buf.Current);
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 100,
            clientHeight: 30,
            lineNumberMarginPx: 0,
            currentLineLogical: 0,
            selection: new SelectionRange(1, 3),
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        Assert.Contains(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.CurrentLineBack
        );
        Assert.Contains(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == style.SelectionBack
        );
    }

    // ---------- 追加: 折り返し 2 段目以降は行番号を出さない(Task 8 で仕様維持) ----------
    [Fact]
    public void Wrap_row_second_segment_has_no_line_number()
    {
        // 折り返し ON: 論理行 "abcdef" を wrap=3 → 2 視覚行(seg0="abc"/seg1="def")
        var buf = TextBuffer.FromString("abcdef");
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 1000,
            wrapColumns: 3,
            M
        );
        var style = TestStyle();

        var frame = FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 30,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );

        // 行番号 "1" は seg0 のみ(1 個)
        var lineNumbers = frame
            .Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Text == "1" && op.X < 30)
            .ToList();
        Assert.Single(lineNumbers);
    }

    // ===== F-1(2026-09-14 の L5 で検出): 長大行の本文 DrawText を長さで分割する =====

    /// <summary>本文の DrawText op(Fore が Foreground のもの)だけを拾う。</summary>
    private static List<PaintOp> BodyTextOps(Frame frame, PaintColor fore) =>
        frame.Ops.Where(op => op.Kind == PaintOpKind.DrawText && op.Fore == fore).ToList();

    private static Frame BuildForLine(string line, ViewportStyle style)
    {
        var buf = TextBuffer.FromString(line);
        var rows = BuildRows(buf.Current);
        return FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: 0,
            currentLineLogical: -1,
            selection: null,
            cellHighlight: null,
            showWhitespace: false,
            style,
            M
        );
    }

    /// <summary>
    /// 上限ちょうどの行は分割しない = 分割導入前と同じ 1 op のまま。
    /// (上限の比較を &lt;= から &lt; へ緩める変異はここで死ぬ)
    /// </summary>
    [Fact]
    public void Body_text_op_is_not_split_at_exactly_the_limit()
    {
        var style = TestStyle();
        string line = new('a', FrameBuilder.MaxCharsPerTextOp);

        var body = BodyTextOps(BuildForLine(line, style), style.Foreground);

        Assert.Single(body);
        Assert.Equal(line, body[0].Text);
    }

    /// <summary>
    /// 上限を 1 文字超えると分割され、<b>連結すると元の行と一致する</b>。
    /// GDI は上限超過で何も描かないので、ここが F-1 の本体
    /// (1 文字も落とさない・重複しないことを連結で固定する)。
    /// </summary>
    [Fact]
    public void Body_text_op_is_split_beyond_the_limit_and_concatenates_back()
    {
        var style = TestStyle();
        string line = new('a', FrameBuilder.MaxCharsPerTextOp + 1);

        var body = BodyTextOps(BuildForLine(line, style), style.Foreground);

        Assert.Equal(2, body.Count);
        Assert.Equal(line, string.Concat(body.Select(op => op.Text)));
    }

    /// <summary>分割後も X は単調増加し、先頭は本文原点(行番号なしなので 0)から始まる。</summary>
    [Fact]
    public void Split_body_ops_advance_x_monotonically_from_body_origin()
    {
        var style = TestStyle();
        string line = new('a', FrameBuilder.MaxCharsPerTextOp * 2 + 5);

        var body = BodyTextOps(BuildForLine(line, style), style.Foreground);

        Assert.Equal(3, body.Count);
        Assert.Equal(0, body[0].X);
        for (int i = 1; i < body.Count; i++)
            Assert.True(body[i].X > body[i - 1].X, $"op[{i}].X={body[i].X} <= op[{i - 1}].X");
        // ASCII なので MeasureRun は加算的 = 幅の合計が行全体の幅と一致する
        Assert.Equal(M.MeasureRun(line), body.Sum(op => op.Width));
    }

    /// <summary>
    /// 分割境界にサロゲートペアが跨っても<b>割らない</b>。
    /// (SnapToCodePointStart を落とす変異はここで死ぬ —— 割ると片割れだけの
    /// 不正な文字列を描くことになる)
    /// </summary>
    [Fact]
    public void Split_does_not_break_a_surrogate_pair_at_the_boundary()
    {
        var style = TestStyle();
        const string pair = "\uD842\uDFB7"; // U+20BB7(𠮷)
        // 上限の 1 つ手前からペアを置く = 分割したい位置が必ずペアの内側に落ちる
        string line =
            new string('a', FrameBuilder.MaxCharsPerTextOp - 1) + pair + new string('b', 10);

        var body = BodyTextOps(BuildForLine(line, style), style.Foreground);

        Assert.Equal(2, body.Count);
        Assert.Equal(line, string.Concat(body.Select(op => op.Text)));
        foreach (var op in body)
        {
            string t = op.Text!;
            Assert.False(char.IsHighSurrogate(t[^1]), "op が高位サロゲートで終わっている");
            Assert.False(char.IsLowSurrogate(t[0]), "op が低位サロゲートで始まっている");
        }
        Assert.Contains(body, op => op.Text!.Contains(pair, StringComparison.Ordinal));
    }
}
