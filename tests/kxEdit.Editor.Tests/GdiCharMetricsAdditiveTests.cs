using kxEdit.Core.Layout;
using kxEdit.Editor;

namespace kxEdit.Editor.Tests;

/// <summary>
/// <see cref="GdiCharMetrics.MeasureAdditive"/>(設計書 2026-10-06 §3.2): BMP の幅を配列で引く高速版が、
/// 1 コードポイントずつの <see cref="GdiCharMetrics.MeasureRun"/> の和と同じ値を返すこと。
/// </summary>
public class GdiCharMetricsAdditiveTests
{
    private static int SumOfCodePoints(GdiCharMetrics m, string s)
    {
        int px = 0;
        int i = 0;
        while (i < s.Length)
        {
            int len =
                char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                    ? 2
                    : 1;
            px += m.MeasureRun(s.AsSpan(i, len));
            i += len;
        }
        return px;
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("a\tb")]
    [InlineData("あいう漢字")]
    [InlineData("aあ😀b")]
    [InlineData("ｱｲｳ・ー")]
    public void Matches_the_sum_of_single_code_point_measures(string s) =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            int expected = SumOfCodePoints(m, s);
            Assert.Equal(expected, m.MeasureAdditive(s));
            Assert.Equal(expected, m.MeasureAdditive(s)); // 2 回目は配列のヒット
        });

    /// <summary>単独サロゲート(InlineData に置くと xUnit の ID が衝突するので Fact に分ける)。</summary>
    [Fact]
    public void Matches_the_sum_for_lone_surrogates() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);

            foreach (string s in new[] { "\uD83Dx", "x\uDE00", "\uDE00\uD83D" })
                Assert.Equal(SumOfCodePoints(m, s), m.MeasureAdditive(s));
        });

    [Fact]
    public void Does_not_fall_to_zero_beyond_the_gdi_limit() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
            var m = new GdiCharMetrics(font);
            string s = new('吾', 50_000);

            Assert.Equal(0, m.MeasureRun(s)); // 前提: GDI の一括計測は 43,679 字を超えると 0
            Assert.Equal(50_000 * m.MeasureRun("吾"), m.MeasureAdditive(s));
        });

    [Fact]
    public void Interface_call_reaches_the_fast_override() =>
        Sta.Run(() =>
        {
            using var font = new Font("MS ゴシック", 12f);
#pragma warning disable CA1859 // reason: interface 経由の呼び出しを検証するテストなので interface 型で持つ
            ICharMetrics m = new GdiCharMetrics(font);
#pragma warning restore CA1859
            // 既定実装でも値は同じなので、ここでは interface 経由で呼べて同じ値になることだけを見る。
            Assert.Equal(3 * m.MeasureRun("あ"), m.MeasureAdditive("あああ"));
        });
}
