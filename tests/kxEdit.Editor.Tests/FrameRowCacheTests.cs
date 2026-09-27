using System.Drawing;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-27 フェーズ 9: <see cref="FrameRowCache"/> の引き方(<see cref="FrameInputs.Equals(FrameInputs?)"/>)・
/// 2 件の入れ替え・追い出し・<see cref="FrameRowCache.Clear"/>・記述子の遅延計算を、戻り値の参照で固定する。
/// </summary>
public class FrameRowCacheTests
{
    private static readonly Font s_font = new("ＭＳ ゴシック", 12f);
    private static readonly Font s_underline = new(s_font, FontStyle.Underline);
    private static readonly Font s_target = new(s_font, FontStyle.Underline | FontStyle.Bold);
    private static readonly GdiCharMetrics s_metrics = new(s_font);
    private static readonly TextSnapshot s_snap = TextBuffer
        .FromString("abc\r\ndef\r\nghi")
        .Current;

    /// <summary><paramref name="currentLine"/> だけが違う入力(Equals で区別される)。</summary>
    private static FrameInputs Inputs(int currentLine) =>
        new()
        {
            Snapshot = s_snap,
            TopLine = 0,
            TopSegment = 0,
            ScrollX = 0,
            WrapColumns = 0,
            ClientSize = new Size(300, 200),
            PaintWidth = 283,
            PaintHeight = 200,
            ShowLineNumbers = false,
            LineNumberWidth = 0,
            CurrentLineLogical = currentLine,
            Selection = null,
            CellHighlight = null,
            ShowWhitespace = false,
            Style = new ViewportStyle(
                new PaintColor(0x000000),
                new PaintColor(0xFFFFFF),
                new PaintColor(0xF0F0F0),
                new PaintColor(0xADD8E6),
                null,
                new PaintColor(0x777777),
                new PaintColor(0xD77800),
                new PaintColor(0xCCCCCC)
            ),
            Metrics = s_metrics,
            Font = s_font,
            UnderlineFont = s_underline,
            TargetFont = s_target,
            BackColor = Color.White,
            Ime = ImeCompositionState.Empty,
            ImeOrigin = null,
        };

    [Fact]
    public void Equal_inputs_hit_the_same_rows()
    {
        var cache = new FrameRowCache();
        var a = Inputs(1);
        var rows = cache.Rows(a);
        var equalCopy = a with { };
        Assert.NotSame(a, equalCopy); // 前提: 別インスタンスで、値が等しい
        Assert.Equal(a, equalCopy);
        Assert.Same(rows, cache.Rows(equalCopy));
        Assert.Equal(3, rows.Count); // 前提: 可視行が作られている
    }

    [Fact]
    public void Two_entries_swap_without_rebuilding()
    {
        var cache = new FrameRowCache();
        var a = Inputs(0);
        var b = Inputs(2);
        var rowsA = cache.Rows(a);
        var rowsB = cache.Rows(b);
        Assert.NotSame(rowsA, rowsB); // 前提: 別の入力は別の行
        Assert.Same(rowsA, cache.Rows(a)); // A, B, A: A は残っている
        Assert.Same(rowsB, cache.Rows(b)); // 入れ替えの後も B は残っている
    }

    [Fact]
    public void A_third_input_evicts_the_oldest()
    {
        var cache = new FrameRowCache();
        var a = Inputs(0);
        var b = Inputs(1);
        var c = Inputs(2);
        var rowsA = cache.Rows(a);
        var rowsB = cache.Rows(b);
        var rowsC = cache.Rows(c); // A を追い出す
        Assert.Same(rowsC, cache.Rows(c));
        Assert.Same(rowsB, cache.Rows(b));
        Assert.NotSame(rowsA, cache.Rows(a)); // 作り直し
    }

    [Fact]
    public void Clear_drops_the_entries()
    {
        var cache = new FrameRowCache();
        var a = Inputs(1);
        var b = Inputs(2);
        var rowsA = cache.Rows(a);
        var rowsB = cache.Rows(b);
        cache.Clear();
        Assert.NotSame(rowsA, cache.Rows(a));
        Assert.NotSame(rowsB, cache.Rows(b));
    }

    [Fact]
    public void Keys_are_computed_once_per_entry()
    {
        var cache = new FrameRowCache();
        var a = Inputs(1);
        RowPaintKey[] keys = cache.Keys(a);
        Assert.Same(keys, cache.Keys(a with { }));
        Assert.Equal(3, keys.Length);
        Assert.True(keys[1].IsCurrentLine); // 前提: 入力から作った記述子
        Assert.False(keys[0].IsCurrentLine);
        // 追い出されると作り直す(エントリごとのメモである)。
        cache.Rows(Inputs(0));
        cache.Rows(Inputs(2));
        Assert.NotSame(keys, cache.Keys(a));
    }
}
