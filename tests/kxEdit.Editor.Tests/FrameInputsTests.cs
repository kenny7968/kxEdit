using System.Drawing;
using System.Reflection;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;
using SelectionRange = kxEdit.Core.Layout.SelectionRange;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 2026-09-25 性能改善フェーズ 3(設計書 §8.1・§8.4 の網羅性)。<see cref="FrameInputs"/> の
/// メンバーが列挙どおりであることと、各メンバーの違いが等値比較で「変化あり」になることを固定する。
/// メンバーを足したら <see cref="ExpectedMembers"/> と <see cref="Different"/> の両方を直すこと
/// (片方だけだと 1 本目か 2 本目が赤くなる)。
/// </summary>
public class FrameInputsTests
{
    private static readonly string[] ExpectedMembers =
    [
        "Snapshot",
        "TopLine",
        "TopSegment",
        "ScrollX",
        "WrapColumns",
        "ClientSize",
        "PaintWidth",
        "PaintHeight",
        "ShowLineNumbers",
        "LineNumberWidth",
        "CurrentLineLogical",
        "Selection",
        "CellHighlight",
        "ShowWhitespace",
        "Style",
        "Metrics",
        "Font",
        "UnderlineFont",
        "TargetFont",
        "BackColor",
        "Ime",
    ];

    public static TheoryData<string> Members() => [.. ExpectedMembers];

    /// <summary>テスト全体で使い回すフォントと幅メモ(GDI 資源なので作り直さない)。</summary>
    private static readonly Font s_font = new("ＭＳ ゴシック", 12f);
    private static readonly Font s_underline = new(s_font, FontStyle.Underline);
    private static readonly Font s_target = new(s_font, FontStyle.Underline | FontStyle.Bold);

    private static FrameInputs Base(GdiCharMetrics metrics) =>
        new()
        {
            Snapshot = TextBuffer.FromString("abc\r\ndef").Current,
            TopLine = 3,
            TopSegment = 1,
            ScrollX = 5,
            WrapColumns = 0,
            ClientSize = new Size(300, 200),
            PaintWidth = 283,
            PaintHeight = 200,
            ShowLineNumbers = true,
            LineNumberWidth = 28,
            CurrentLineLogical = 4,
            Selection = new SelectionRange(1, 3),
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
            Metrics = metrics,
            Font = s_font,
            UnderlineFont = s_underline,
            TargetFont = s_target,
            BackColor = Color.White,
            Ime = ImeCompositionState.Empty,
        };

    /// <summary>メンバーの値を「描画が変わりうる別の値」にする。参照で比べる型は、値が同じ別インスタンスにする。</summary>
    private static object? Different(object? value) =>
        value switch
        {
            int i => i + 1,
            bool b => !b,
            Size s => new Size(s.Width + 1, s.Height),
            Color c => Color.FromArgb(c.ToArgb() ^ 0x000001),
            TextSnapshot => TextBuffer.FromString("abc\r\ndef").Current, // 同じ本文・別の参照
            SelectionRange r => new SelectionRange(r.Start, r.End + 1),
            null => new SelectionRange(0, 1),
            ViewportStyle st => st with { Foreground = new PaintColor(st.Foreground.Rgb ^ 1) },
            GdiCharMetrics => new GdiCharMetrics(s_font), // 同じフォント・別の参照
            Font f => new Font(f, f.Style), // 同じ値・別の参照
            ImeCompositionState ime => ime with { Text = ime.Text + "あ" },
            _ => throw new InvalidOperationException($"未対応の型: {value.GetType()}"),
        };

    [Fact]
    public void Members_are_exactly_the_enumerated_inputs()
    {
        var names = typeof(FrameInputs)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(ExpectedMembers.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void Same_values_and_same_references_are_equal() =>
        Sta.Run(() =>
        {
            var metrics = new GdiCharMetrics(s_font);
            var a = Base(metrics);
            var b = a with { }; // 全メンバーをそのまま写す
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        });

    [Theory]
    [MemberData(nameof(Members))]
    public void A_difference_in_any_member_is_a_change(string member) =>
        Sta.Run(() =>
        {
            var metrics = new GdiCharMetrics(s_font);
            var a = Base(metrics);
            var prop = typeof(FrameInputs).GetProperty(member)!;
            var b = a with { };
            prop.SetValue(b, Different(prop.GetValue(a))); // init アクセサはリフレクションから呼べる
            Assert.NotEqual(a, b);
        });

    /// <summary>ViewportStyle は record の値比較(ApplyAppearance で同じテーマを作り直しても「変化なし」)。</summary>
    [Fact]
    public void Style_is_compared_by_value() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font));
            var b = a with { Style = a.Style with { } };
            Assert.NotSame(a.Style, b.Style);
            Assert.Equal(a, b);
        });

    /// <summary>BackColor は画素の値で比べる(名前付きの White と、同じ ARGB の無名色は同じ絵になる)。</summary>
    [Fact]
    public void BackColor_is_compared_by_argb() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font));
            var b = a with { BackColor = Color.FromArgb(255, 255, 255, 255) };
            Assert.NotEqual(Color.White, b.BackColor); // 前提: Color.Equals では異なる
            Assert.Equal(a, b);
        });

    /// <summary>未確定の配列は参照で比べる(打鍵ごとに新しい配列 = 「変化あり」= 安全側)。</summary>
    [Fact]
    public void Ime_arrays_are_compared_by_reference() =>
        Sta.Run(() =>
        {
            var a = Base(new GdiCharMetrics(s_font)) with
            {
                Ime = new ImeCompositionState(0, "かな", 2, [0, 0], [0, 2]),
            };
            var b = a with { Ime = a.Ime with { Attrs = [0, 0] } };
            Assert.NotEqual(a, b);
        });
}
