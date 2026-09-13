// FrameBuilderSelectionForeTests.cs
// 2026-09-14 設計書: 黒地テーマで選択中テキストが読めない不具合の網。
// ViewportStyle.SelectionFore が非 null のとき、本文 DrawText を選択境界で分割することを固定する。
// 分割しない側(標準テーマ=SelectionFore が null)の不変性も同じ場所で対にして固定する。
using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;

namespace kxEdit.Core.Tests.Layout;

public class FrameBuilderSelectionForeTests
{
    // MonoCharMetrics(halfWidthPx:1, lineHeightPx:10) を共有=決定的な座標。
    // ASCII=1px・サロゲートペア=2px・行高 10px。
    private static readonly MonoCharMetrics M = new(halfWidthPx: 1, lineHeightPx: 10);

    private static readonly PaintColor Fore = new(0x000000);
    private static readonly PaintColor SelFore = new(0xFF00FF);
    private static readonly PaintColor SelBack = new(0xADD8E6);

    private static ViewportStyle Style(PaintColor? selectionFore) =>
        new(
            Foreground: Fore,
            Background: new PaintColor(0xFFFFFF),
            CurrentLineBack: new PaintColor(0x88FF88),
            SelectionBack: SelBack,
            SelectionFore: selectionFore,
            LineNumberFore: new PaintColor(0x777777),
            HighlightOutline: new PaintColor(0xFF8800),
            WhitespaceGlyph: new PaintColor(0xCCCCCC)
        );

    /// <summary>
    /// 本文 DrawText を抽出する。<see cref="Build"/> は既定で行番号マージン 0・空白可視化 OFF なので、
    /// フレーム中の DrawText は本文だけ。<b>色で絞らない</b>のは、想定外の色で増えた余計な op を
    /// フィルタで消さないため(色で絞ると「op が 1 本足りない」という遠い形でしか落ちない)。
    /// 行番号マージンを付けて撃つテストだけは行番号 op が混ざるので、そちらで別途絞る。
    /// </summary>
    private static List<PaintOp> BodyText(Frame frame) =>
        frame.Ops.Where(op => op.Kind == PaintOpKind.DrawText).ToList();

    private static Frame Build(
        string text,
        SelectionRange? selection,
        PaintColor? selectionFore,
        int wrapCols = 0,
        ICharMetrics? metrics = null,
        int lineNumberMarginPx = 0
    )
    {
        var m = metrics ?? M;
        var buf = TextBuffer.FromString(text);
        var rows = ViewportLayout.Build(
            buf.Current,
            topLine: 0,
            topSegment: 0,
            heightPx: 1000,
            wrapColumns: wrapCols,
            m
        );
        return FrameBuilder.Build(
            buf.Current,
            rows,
            clientWidth: 200,
            clientHeight: 100,
            lineNumberMarginPx: lineNumberMarginPx,
            currentLineLogical: -1,
            selection: selection,
            cellHighlight: null,
            showWhitespace: false,
            Style(selectionFore),
            m
        );
    }

    // --- 分割する側 ---

    // fixture "abcdef" + 選択 [2,4) は prefix "ab" と suffix "ef" の両方を持つ
    // (全選択・行頭選択・行末選択のいずれとも区別できる=CLAUDE.md §4-B)。
    [Fact]
    public void Partial_selection_splits_body_text_into_three_runs()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "ab", x: 0, w: 2, Fore),
            op => AssertRun(op, "cd", x: 2, w: 2, SelFore),
            op => AssertRun(op, "ef", x: 4, w: 2, Fore)
        );
    }

    // 行頭からの選択では prefix の空 op を出さない(空 op は無害だが、意図せず増えた
    // op が「分割数」を主張するテストを素通りさせるため、個数まで固定する)。
    [Fact]
    public void Selection_from_row_start_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore),
            op => AssertRun(op, "cdef", x: 2, w: 4, Fore)
        );
    }

    [Fact]
    public void Selection_to_row_end_emits_two_runs()
    {
        var frame = Build("abcdef", new SelectionRange(4, 6), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "abcd", x: 0, w: 4, Fore),
            op => AssertRun(op, "ef", x: 4, w: 2, SelFore)
        );
    }

    // 行まるごとの選択は 1 op だが、色が SelectionFore であることが「分割しない」場合との差。
    [Fact]
    public void Whole_row_selection_emits_single_run_in_selection_color()
    {
        var frame = Build("abcdef", new SelectionRange(0, 6), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, SelFore));
    }

    // 折り返しで複数の視覚行にまたがる選択は、視覚行ごとに分割される。
    // 視覚行 0 は "ab"(y=0)・視覚行 1 は "cd"(y=10)。
    [Fact]
    public void Selection_spanning_wrapped_rows_splits_each_row()
    {
        var frame = Build("abcd", new SelectionRange(1, 3), SelFore, wrapCols: 2);

        var ops = BodyText(frame);

        Assert.Collection(
            ops,
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 論理行をまたぐ選択。改行文字ぶん char オフセットが飛ぶので、行内オフセットへの
    // 変換(SegmentStartChar の減算)を誤ると 2 行目の分割位置がずれる。
    [Fact]
    public void Selection_spanning_logical_lines_splits_each_row()
    {
        var frame = Build("ab\ncd", new SelectionRange(1, 4), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "a", x: 0, w: 1, Fore, y: 0),
            op => AssertRun(op, "b", x: 1, w: 1, SelFore, y: 0),
            op => AssertRun(op, "c", x: 0, w: 1, SelFore, y: 10),
            op => AssertRun(op, "d", x: 1, w: 1, Fore, y: 10)
        );
    }

    // 選択と交差しない行は分割しない(選択が存在する状態で、別の行が巻き込まれないこと)。
    [Fact]
    public void Row_outside_selection_is_not_split()
    {
        var frame = Build("ab\ncd", new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore, y: 0),
            op => AssertRun(op, "cd", x: 0, w: 2, Fore, y: 10)
        );
    }

    // 分割 run も行番号マージンぶん右へずれること。他のテストは行番号マージン 0 で撃つので、
    // bodyX の加算が落ちても 1 本も落ちない = 「黒地テーマ + 行番号表示 ON で本文が行番号の上に
    // 重なって描かれる」という実害のある改変が素通りする(2026-09-14 最終レビューの実測)。
    // 非分割経路は FrameBuilderTests.Line_number_margin_offsets_body_and_emits_right_aligned_numbers
    // が押さえているので、分割経路にも同じ網を張って非対称を解消する。
    [Fact]
    public void Split_runs_are_offset_by_line_number_margin()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore, lineNumberMarginPx: 30);

        // このテストだけは行番号の DrawText が混ざるので、本文色 / 選択色で絞る。
        var body = BodyText(frame).Where(op => op.Fore == Fore || op.Fore == SelFore).ToList();

        Assert.Collection(
            body,
            op => AssertRun(op, "ab", x: 30, w: 2, Fore),
            op => AssertRun(op, "cd", x: 32, w: 2, SelFore),
            op => AssertRun(op, "ef", x: 34, w: 2, Fore)
        );
    }

    // 選択の内側にある空行(SegmentLength == 0)も分割経路に入らず、
    // 従来どおり空 Text の op が 1 本出る(不変条件のもう一方の端点)。
    //
    // このテストは単独で「交差判定の < を <= に緩める」改変を殺している唯一の網でもある
    // (空行では startInRow == endInRow == 0 になり、分割経路へ入ると 3 本とも空区間で弾かれて
    // 本文 op が消える)。折り返しの隣接行を含め、他のテストはこの改変を素通りする。消さないこと。
    [Fact]
    public void Empty_row_inside_selection_keeps_single_empty_run()
    {
        var frame = Build("ab\n\ncd", new SelectionRange(0, 6), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 2, SelFore, y: 0),
            op => AssertRun(op, "", x: 0, w: 0, Fore, y: 10),
            op => AssertRun(op, "cd", x: 0, w: 2, SelFore, y: 20)
        );
    }

    // 空選択(Start == End)は分割しない。
    // 交差計算が空選択を吸収するので、FrameBuilder 側の Start < End ガードを外してもこれは緑のまま
    // (ガードは工程 3 と読み口を揃えるための冗長な明示)。ここが固定しているのは
    // 「キャレットがあるだけの状態で本文の色が変わらない」ことそのもの。
    [Fact]
    public void Empty_selection_does_not_split()
    {
        var frame = Build("abcdef", new SelectionRange(3, 3), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));
    }

    // --- サロゲートペア ---
    // "a" + U+1F600(サロゲートペア=2 code unit・幅 2)+ "b" = 4 code unit・全幅 4。
    // OffsetToPx は pair 先頭へ前方スナップするので、文字の切り出しも同じ位置でスナップしないと
    // x と文字がずれるか pair が割れる。
    // 連結文字列の一致では検証にならない: スナップを外しても "a" / "\uD83D" / "\uDE00b" を
    // 連結すれば元の文字列に等しくなり、pair が割れた状態を素通りさせてしまう(レビュー指摘)。
    // そのため op ごとに Text / X / Width / 色を固定する。
    private const string SurrogateLine = "a😀b";

    // pair の high 側(offset 1)から low 側(offset 2)までの選択は、両端とも pair 先頭へ寄って
    // 空区間になり、選択矩形の幅も 0 になる。選択が 1px も塗られないので分割せず、
    // 選択が無いときと完全に同じ 1 op を出す(pair が割れないのが要点)。
    [Fact]
    public void Selection_within_surrogate_pair_renders_row_unsplit()
    {
        var frame = Build(SurrogateLine, new SelectionRange(1, 2), SelFore);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "a😀b", x: 0, w: 4, Fore));
    }

    // pair の途中で終わる選択は pair 先頭まで縮む(pair は選択外=本文色でまとめて描かれる)。
    [Fact]
    public void Selection_ending_inside_surrogate_pair_snaps_to_pair_start()
    {
        var frame = Build(SurrogateLine, new SelectionRange(0, 2), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "a", x: 0, w: 1, SelFore),
            op => AssertRun(op, "😀b", x: 1, w: 3, Fore)
        );
    }

    // pair の途中で始まる選択は pair 先頭まで広がる(pair 全体が選択色になる)。
    [Fact]
    public void Selection_starting_inside_surrogate_pair_snaps_to_pair_start()
    {
        var frame = Build(SurrogateLine, new SelectionRange(2, 4), SelFore);

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "a", x: 0, w: 1, Fore),
            op => AssertRun(op, "😀b", x: 1, w: 3, SelFore)
        );
    }

    // --- 非加算メトリクス(設計書 §5.2 の最重要制約)---

    // 設計書 §5.2: X と幅は必ず OffsetToPx(行頭からの prefix 計測)の差分で出す。
    // MonoCharMetrics は加算的なので、部分文字列を個別に MeasureRun する実装でも上の全テストが
    // 緑のまま通ってしまう = この制約は加算的メトリクスでは固定できない(レビュー指摘)。
    // 非加算のメトリクスで撃って初めて差が出る。
    [Fact]
    public void Run_x_and_width_come_from_prefix_measurement_not_substring_measurement()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore, metrics: new NonAdditive());

        // prefix 計測: "ab"=19 / "abcd"=37 / "abcdef"=55。差分は 19 / 18 / 18 で合計 55。
        // 部分文字列を個別に測ると "ab"="cd"="ef"=19 となり、幅も X も合わなくなる。
        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "ab", x: 0, w: 19, Fore),
            op => AssertRun(op, "cd", x: 19, w: 18, SelFore),
            op => AssertRun(op, "ef", x: 37, w: 18, Fore)
        );
    }

    // 設計書 §5.2「矩形と文字が必ず一致する」の実証。交差計算(char 境界)は共有しているが、
    // px への変換は 2 経路(矩形側は OffsetToPx の内部スナップ任せ・分割側は明示スナップ)あり、
    // 一致は構造ではなく両者の等価性に依存している。非加算メトリクスで値ごと突き合わせる。
    [Fact]
    public void Selection_run_matches_selection_rect_exactly()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), SelFore, metrics: new NonAdditive());

        var selRun = Assert.Single(BodyText(frame), op => op.Fore == SelFore);
        var selRect = Assert.Single(
            frame.Ops,
            op => op.Kind == PaintOpKind.FillRect && op.Back == SelBack
        );

        // 4 辺すべてを突き合わせる。X / Width だけだと、矩形が別の行に出ている改変が通る。
        Assert.Equal(selRect.X, selRun.X);
        Assert.Equal(selRect.Width, selRun.Width);
        Assert.Equal(selRect.Y, selRun.Y);
        Assert.Equal(selRect.Height, selRun.Height);
    }

    // --- ピクセル幅 0 の選択(結合文字)---

    // 2026-09-14 の L5 実機目視で検出した不具合の回帰網。
    // GDI は結合列("か"+U+3099 等)を合成してマークに advance を与えないため、マークだけを
    // 選択すると OffsetToPx の差分が 0 になり、選択矩形が 1px も塗られない。それでも本文を
    // 分割すると、マークが選択文字色(黒地テーマでは黒)で塗られていない背景の上に描かれ、
    // 背景に溶けて消える(実機では「濁点だけを選択すると濁点が消える」として現れた)。
    // 選択が見えないなら描画も選択なしと完全に同じであること。
    [Fact]
    public void Zero_width_selection_renders_row_unsplit()
    {
        // "e" + U+0301(advance 0 の結合文字)+ "b"。選択は結合文字だけ。
        var frame = Build("éb", new SelectionRange(1, 2), SelFore, metrics: new ZeroWidthMark());

        Assert.Collection(BodyText(frame), op => AssertRun(op, "éb", x: 0, w: 20, Fore));
    }

    // 対: 同じメトリクスでも、幅を持つ区間を含む選択はこれまでどおり分割される
    // (上のガードが「選択が見えていても分割しない」まで広がっていないこと)。
    [Fact]
    public void Visible_selection_still_splits_with_zero_width_marks_present()
    {
        // 結合文字を含めて "e" ごと選択する = 幅 10px の選択。
        var frame = Build("éb", new SelectionRange(0, 2), SelFore, metrics: new ZeroWidthMark());

        Assert.Collection(
            BodyText(frame),
            op => AssertRun(op, "é", x: 0, w: 10, SelFore),
            op => AssertRun(op, "b", x: 10, w: 10, Fore)
        );
    }

    /// <summary>
    /// U+0301(結合アキュート)の advance を 0 にするメトリクス。GDI が結合列を合成して
    /// マークに幅を与えないときの挙動を、GDI 抜きで決定的に再現するための道具。
    /// </summary>
    private sealed class ZeroWidthMark : ICharMetrics
    {
        public int LineHeightPx => 10;

        public int MeasureRun(ReadOnlySpan<char> text)
        {
            int px = 0;
            foreach (char c in text)
            {
                if (c != '́')
                    px += 10;
            }
            return px;
        }
    }

    /// <summary>
    /// 加算的でない <see cref="ICharMetrics"/>。1 文字 10px だが、run の文字数 n に対して
    /// <c>10n - (n - 1)</c> を返す = 部分文字列を個別に測って足すと行全体より広くなる。
    /// <c>GdiCharMetrics</c> が非 ASCII を含む run を一括計測することで生じる非加算性を、
    /// GDI 抜きで決定的に再現するための道具。
    /// </summary>
    private sealed class NonAdditive : ICharMetrics
    {
        public int LineHeightPx => 10;

        public int MeasureRun(ReadOnlySpan<char> text) =>
            text.Length == 0 ? 0 : (text.Length * 10) - (text.Length - 1);
    }

    // --- 分割しない側(標準テーマ相当)---

    // no-change 網。既定状態(選択なし)と区別するため、選択が存在する状態から検証する
    // (CLAUDE.md §4-B)。SelectionFore が null なら本文 op は 1 本のまま=描画不変。
    [Fact]
    public void Null_selection_fore_keeps_single_body_run_even_when_selected()
    {
        var frame = Build("abcdef", new SelectionRange(2, 4), selectionFore: null);

        Assert.Collection(BodyText(frame), op => AssertRun(op, "abcdef", x: 0, w: 6, Fore));

        // アンカー: 選択自体は確かに有効(矩形は塗られている)。これが無いと選択が
        // 効いていない状態でも緑になり、このテストは何も主張しなくなる。
        Assert.Contains(frame.Ops, op => op.Kind == PaintOpKind.FillRect && op.Back == SelBack);
    }

    private static void AssertRun(PaintOp op, string text, int x, int w, PaintColor fore, int y = 0)
    {
        Assert.Equal(PaintOpKind.DrawText, op.Kind);
        Assert.Equal(text, op.Text);
        Assert.Equal(x, op.X);
        Assert.Equal(w, op.Width);
        Assert.Equal(y, op.Y);
        Assert.Equal(fore, op.Fore);
        // Height も見る。RenderFrame では op.Height が縦のクリップ幅なので、0 に化けると
        // 本文が一切描かれない。ここを見ないと lineHeight の受け渡しを落とす改変が素通りする。
        // M / NonAdditive とも LineHeightPx は 10。
        Assert.Equal(10, op.Height);
    }
}
