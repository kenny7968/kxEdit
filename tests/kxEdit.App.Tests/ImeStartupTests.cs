using System;
using System.Linq;
using System.Reflection;

namespace kxEdit.App.Tests;

/// <summary>
/// WinForms の IME モード推測を無効化する起動処理を固定する
/// (設計 2026-09-05 §3)。
///
/// <para><b>なぜこれが要るか</b>: WinForms は <c>ImeContext.GetImeMode()</c> が読む
/// <c>ImeModeConversion</c> の変換テーブルからしか「今の IME モード」を推測しない。
/// 推測セルが常に <c>NoControl</c> を返せば <c>Control.PropagatingImeMode</c> の setter が
/// 値を記録しなくなり、<c>WmImeKillFocus</c> が IME を開いて閉じる動作(= SR の
/// 「文字変換」「変換停止」)ごと止まる。</para>
///
/// <para><b>テーブルへの到達経路は本番と別にしてある</b>: 本番はフィールド<b>型</b>で発見するが、
/// ここではフィールド<b>名</b>で引く。同じ経路で引くと「本番が探せていない」ことをテストも
/// 同時に見失う(循環)。.NET 更新で名前が変わればこのテストが赤くなり、CI が気づく。</para>
///
/// <para><b>本テストはプロセス全体の static 状態を変える</b>(製品と同じ状態にする)。
/// 順序依存の偽陽性を避けるため、各テストは<b>非 NoControl の番兵値を書いてから</b>呼ぶ。</para>
///
/// 実際の発声が消えたかは自動テストでは確認できない(CLAUDE.md §2 a11y 鉄則)。L5 で見る。
/// </summary>
public class ImeStartupTests
{
    private const BindingFlags Any =
        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    private static Type ConversionType =>
        typeof(Control).Assembly.GetType("System.Windows.Forms.ImeModeConversion")
        ?? throw new InvalidOperationException("ImeModeConversion が見つからない");

    /// <summary>本番と別経路(フィールド名)で日本語テーブルを引く。</summary>
    private static ImeMode[] JapaneseTable =>
        (ImeMode[])(
            ConversionType.GetField("s_japaneseTable", Any)?.GetValue(null)
            ?? throw new InvalidOperationException("s_japaneseTable が見つからない")
        );

    /// <summary>推測セルを非 NoControl の番兵で埋め、先頭 2 セルを既定値に戻す。</summary>
    private static void Arm(ImeMode[] table)
    {
        table[0] = ImeMode.Inherit;
        table[1] = ImeMode.Disable;
        for (int i = 2; i < table.Length; i++)
            table[i] = ImeMode.Off;
    }

    /// <summary>
    /// 番兵で汚したテーブルを、テストが失敗しても呼び出し前の内容へ戻す。
    /// これが無いと Suppress の失敗時に、後続テスト全部が製品に存在しない状態
    /// (IME 推測が Off 固定)で走る。
    /// </summary>
    private sealed class TableGuard : IDisposable
    {
        private readonly ImeMode[] _table;
        private readonly ImeMode[] _saved;

        internal TableGuard(ImeMode[] table)
        {
            _table = table;
            _saved = (ImeMode[])table.Clone();
        }

        public void Dispose() => Array.Copy(_saved, _table, _saved.Length);
    }

    [Fact]
    public void 推測セルだけが_NoControl_に塗られる()
    {
        var table = JapaneseTable;
        using var guard = new TableGuard(table);
        Arm(table);
        Assert.Equal(ImeMode.Off, table[^1]); // 番兵が効いていることの確認(前提の検査)

        var result = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(result.Succeeded, result.FailureReason);
        // 先頭 2 セルは構造的な値なので不変。ループ開始位置を 0 に変える変異はここで死ぬ。
        Assert.Equal(ImeMode.Inherit, table[0]);
        Assert.Equal(ImeMode.Disable, table[1]);
        // 末尾まで塗れていること。範囲を縮める変異はここで死ぬ。
        Assert.All(table.Skip(2), m => Assert.Equal(ImeMode.NoControl, m));
    }

    /// <summary>
    /// 設計 §5.3 は「現在の入力言語に依存しない(全 CJK テーブルを塗るため CI が en-US でも
    /// 成立する)」と宣言している。日本語テーブルだけを見ていると
    /// 「s_japaneseTable だけ塗る」変異が生き残るので、3 テーブル全部と総数を固定する。
    /// 総数の固定は .NET 側でテーブルが増えたときの検知も兼ねる(設計 §6.3)。
    /// </summary>
    [Fact]
    public void CJK_3テーブルが対象になり_UnsupportedTable_は含まれない()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains("s_japaneseTable", result.PatchedTables);
        Assert.Contains("s_koreanTable", result.PatchedTables);
        Assert.Contains("s_chineseTable", result.PatchedTables);
        Assert.Equal(3, result.PatchedTables.Count);
        Assert.DoesNotContain(
            result.PatchedTables,
            n => n.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
        );
    }

    /// <summary>
    /// 本対策の核心は「IME が閉じているときに <c>ImeContext.GetImeMode</c> が読むセル
    /// (<c>ImeClosed</c>)が中和されていること」。ここが <c>NoControl</c> でなければ
    /// <c>PropagatingImeMode</c> が記録され、発声が戻る。
    /// 添字の出典はフレームワーク側の定数に取る(手書きの 2 / 3 を信じない)。
    /// </summary>
    [Fact]
    public void フレームワークが実際に読むセルが中和される()
    {
        int imeClosed = (int)ConversionType.GetField("ImeClosed", Any)!.GetValue(null)!;
        int imeDirectInput = (int)ConversionType.GetField("ImeDirectInput", Any)!.GetValue(null)!;

        var table = JapaneseTable;
        using var guard = new TableGuard(table);
        Arm(table);
        var result = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(result.Succeeded, result.FailureReason);
        // IME が閉じているとき GetImeMode が返すセル = 本対策の核心。
        Assert.Equal(ImeMode.NoControl, table[imeClosed]);
        // 塗り始めの位置が、読まれる最初のセルより後ろへずれていないこと。
        // (FirstInferredCell を imeClosed より大きくする変異がここで死ぬ)
        Assert.True(imeDirectInput <= imeClosed);
        Assert.Equal(ImeMode.NoControl, table[imeDirectInput]);
    }

    [Fact]
    public void 二回呼んでも成功する()
    {
        var table = JapaneseTable;
        using var guard = new TableGuard(table);
        Arm(table);

        var first = ImeStartup.SuppressWinFormsImeModeInference();
        var second = ImeStartup.SuppressWinFormsImeModeInference();

        Assert.True(first.Succeeded, first.FailureReason);
        Assert.True(second.Succeeded, second.FailureReason);
        Assert.Equal(first.PatchedTables, second.PatchedTables);
    }

    [Fact]
    public void 型を解決できなくても例外を投げず失敗を返す()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference(null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.PatchedTables);
    }

    [Fact]
    public void 変換テーブルを持たない型を渡しても失敗を返すだけで落ちない()
    {
        var result = ImeStartup.SuppressWinFormsImeModeInference(typeof(string));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(result.PatchedTables);
    }
}
