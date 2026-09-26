using System.Reflection;
using kxEdit.Core.Buffers;
using kxEdit.Core.Settings;

namespace kxEdit.Editor.Tests;

/// <summary>
/// 性能改善フェーズ 11(P-16 前半): <c>ApplyAppearance</c> は、前回適用したフォントの要求値
/// (既定値の補完後の名前とサイズ)と同じならフォントと <c>GdiCharMetrics</c>(幅メモ)を作り直さない。
/// 使い回しの判定は <see cref="EditorControl.Metrics"/> と描画フォント(private <c>_font</c>)の参照で見る。
/// </summary>
public class ApplyAppearanceFontReuseTests
{
    private static (Form f, EditorControl c) MakeControl()
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString("abc 日本語"));
        return (f, c);
    }

    /// <summary>描画フォント(private フィールド)。リネームで静かに緑になる事故を防ぐため名前つきで落とす。</summary>
    private static Font DrawFont(EditorControl c)
    {
        var fi = typeof(EditorControl).GetField(
            "_font",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.True(fi is not null, "EditorControl に private フィールド _font が見つからない");
        var font = fi!.GetValue(c) as Font;
        Assert.True(font is not null, "_font が Font として取り出せない");
        return font!;
    }

    [Fact]
    public void ApplyAppearance_SameFontTwice_ReusesFontAndMetrics() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                // 既定(ＭＳ ゴシック 12pt)ではない状態から始める(既定値と区別する)。
                var s = new AppSettings { FontName = "Consolas", FontSize = 15f };
                c.ApplyAppearance(s);
                var metrics = c.Metrics;
                var font = DrawFont(c);

                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });

                Assert.Same(metrics, c.Metrics);
                Assert.Same(font, DrawFont(c));
            }
        });

    [Fact]
    public void ApplyAppearance_FirstCall_AlwaysRecreatesEvenIfSameAsCtorFont() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                var ctorMetrics = c.Metrics;
                var ctorFont = DrawFont(c);

                // ctor と同じ文字列(半角「MS ゴシック」12pt)。ctor のフォントは要求値として記録されていない。
                c.ApplyAppearance(new AppSettings { FontName = "MS ゴシック", FontSize = 12f });

                Assert.NotSame(ctorMetrics, c.Metrics);
                Assert.NotSame(ctorFont, DrawFont(c));
            }
        });

    [Fact]
    public void ApplyAppearance_FontChangedAndBack_Recreates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });
                var first = c.Metrics;
                c.ApplyAppearance(new AppSettings { FontName = "Arial", FontSize = 15f });
                var second = c.Metrics;
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 15f });

                Assert.NotSame(first, second);
                Assert.NotSame(second, c.Metrics); // 前回(Arial)と比べる
                Assert.NotSame(first, c.Metrics); // 最初の Consolas を使い回さない(破棄済み)
            }
        });

    [Fact]
    public void ApplyAppearance_SizeOnlyChanged_Recreates() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 12f });
                var metrics = c.Metrics;
                int height = c.LineHeightPx;

                c.ApplyAppearance(new AppSettings { FontName = "Consolas", FontSize = 24f });

                Assert.NotSame(metrics, c.Metrics);
                Assert.True(
                    c.LineHeightPx > height,
                    $"行の高さが大きくならない({height} → {c.LineHeightPx})"
                );
            }
        });

    [Fact]
    public void ApplyAppearance_SameFontDifferentTheme_ReusesFontButAppliesTheme() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = "Consolas",
                        FontSize = 15f,
                        Theme = "default",
                    }
                );
                var metrics = c.Metrics;
                Assert.Equal(Color.White.ToArgb(), c.BackColor.ToArgb()); // 前提

                c.ApplyAppearance(
                    new AppSettings
                    {
                        FontName = "Consolas",
                        FontSize = 15f,
                        Theme = "white-on-black",
                    }
                );

                Assert.Same(metrics, c.Metrics);
                Assert.Equal(Color.Black.ToArgb(), c.BackColor.ToArgb()); // テーマは反映される
            }
        });

    [Fact]
    public void ApplyAppearance_EmptyNameAndZeroSize_EqualToExplicitDefaults_Reuses() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl();
            using (f)
            using (c)
            {
                c.ApplyAppearance(new AppSettings { FontName = "ＭＳ ゴシック", FontSize = 12f });
                var metrics = c.Metrics;

                // 補完後は「ＭＳ ゴシック」12pt=同じフォントが作られるので使い回す。
                c.ApplyAppearance(new AppSettings { FontName = "", FontSize = 0f });

                Assert.Same(metrics, c.Metrics);
            }
        });
}
