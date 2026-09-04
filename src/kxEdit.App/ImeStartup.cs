using System.Reflection;

namespace kxEdit.App;

/// <summary>
/// <see cref="ImeStartup.SuppressWinFormsImeModeInference()"/> の結果。
/// 成否とその根拠をテストから観測できるようにするために返す(void にすると
/// 「1 つも塗れていない」変異が緑のまま生き残る)。
/// </summary>
internal readonly record struct ImeSuppressionResult(
    bool Succeeded,
    IReadOnlyList<string> PatchedTables,
    string? FailureReason
)
{
    internal static ImeSuppressionResult Failed(string reason) => new(false, [], reason);
}

/// <summary>
/// WinForms の <c>ImeMode</c> 推測機構を起動時に無効化する(設計 2026-09-05)。
///
/// <para><b>なぜ要るか</b>: WinForms は <c>Control.WmImeKillFocus</c> で
/// <c>ImeContext.SetImeStatus(PropagatingImeMode, ...)</c> を呼ぶ。<c>PropagatingImeMode</c> が
/// <c>ImeMode.Off</c>(= ユーザーが IME を切っている通常状態)のとき、その実装は
/// <b>「一度 IME を開いてから閉じる」</b>ため <c>ImmSetOpenStatus</c> が 2 回走り、
/// 日本語スクリーンリーダーが「文字変換」「変換停止」と読み上げる。
/// タブを閉じたときとモーダルダイアログを閉じたときに条件が成立する(設計 §2.2)。</para>
///
/// <para><b>なぜテーブルを塗るのか</b>: WinForms が「今の IME モード」を<b>推測する入口は
/// <c>ImeModeConversion</c> の変換テーブル 1 つだけ</b>で、<c>Control.PropagatingImeMode</c> の
/// setter は <c>NoControl</c> と <c>Disable</c> を<b>記録しない</b>。推測結果が常に
/// <c>NoControl</c> になれば <c>s_propagatingImeMode</c> は永久に <c>Inherit</c> のままとなり、
/// <c>WmImeKillFocus</c> も <c>UpdateImeContextMode</c> も no-op になる。
/// コントロール単位の opt-out は<b>すべて無効だった</b>(実測・設計 §4)——
/// 状態がプロセス全体の static で、<c>TabControl</c> / <c>TabPage</c> / <c>Form</c> や
/// ダイアログ側のコントロールが供給源になるため。</para>
///
/// <para><b>この機構は Microsoft 自身が「Windows 8 以降 <c>ImeMode</c> は無視される」と
/// 文書化した遺物</b>(VB6 の <c>IMEMode</c> プロパティの移植)。それでも実装は残っていて
/// グローバルな IME 状態を実際に叩き続けている。</para>
///
/// <para><b>明示的な <c>ImeMode</c> 設定は生きている</b>: <c>ImeMode.Disable</c> は
/// 変換テーブルを経由しないため、数値入力欄の IME 無効化は従来どおり効く(実測)。</para>
/// </summary>
internal static class ImeStartup
{
    /// <summary>
    /// 塗り始める index。0 = <c>ImeMode.Inherit</c> / 1 = <c>ImeMode.Disable</c> は
    /// 「IME 状態から推測した値」ではなく構造的な定数なので<b>触らない</b>。
    /// ここを 0 にすると <c>ImeContext.GetImeMode</c> が「無効」を返せなくなる。
    /// </summary>
    private const int FirstInferredCell = 2;

    /// <summary>
    /// 本番の入口。<c>Program.Main</c> の<b>最初</b>で呼ぶ(ウィンドウを 1 つも作る前)。
    /// 遅れて呼ぶと、起動時に既に武装済みの分が 1 回だけ鳴る(実測)。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference() =>
        SuppressWinFormsImeModeInference(
            typeof(Control).Assembly.GetType("System.Windows.Forms.ImeModeConversion")
        );

    /// <summary>
    /// 型解決を外から差し替えられる seam(テストが異常系を駆動するため)。
    /// <paramref name="conversionType"/> が null / 想定外でも<b>例外を投げず</b>失敗を返す
    /// ——起動を止めないことが最優先で、失敗しても現状動作(発声が出る)に落ちるだけ。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference(Type? conversionType)
    {
        try
        {
            if (conversionType is null)
                return ImeSuppressionResult.Failed("ImeModeConversion 型を解決できない");

            // reason: WinForms の IME モード推測を止める唯一の入口が internal な
            // ImeModeConversion の static テーブルであり、非公開メンバへの到達は不可避
            // (コントロール単位の公開 API による opt-out はすべて無効だった・設計 §4)。
            // 対象は自プロセスにロード済みの System.Windows.Forms.dll のみで、外部入力は
            // 一切関与しない。解決に失敗しても例外を投げず起動を続ける。
#pragma warning disable S3011 // Make sure that this accessibility bypass is safe here
            const BindingFlags any =
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
#pragma warning restore S3011

            // フィールド名ではなく型で発見する(内部名が変わっても壊れにくくする)。
            var unsupported =
                conversionType.GetProperty("UnsupportedTable", any)?.GetValue(null) as ImeMode[];

            var patched = new List<string>();
            foreach (var field in conversionType.GetFields(any))
            {
                if (field.FieldType != typeof(ImeMode[]))
                    continue;
                if (field.GetValue(null) is not ImeMode[] table)
                    continue;
                // 空の UnsupportedTable(長さ 0)はここで落ちる。参照比較は二重の保険。
                if (table.Length <= FirstInferredCell)
                    continue;
                if (unsupported is not null && ReferenceEquals(table, unsupported))
                    continue;

                for (int i = FirstInferredCell; i < table.Length; i++)
                    table[i] = ImeMode.NoControl;
                patched.Add(field.Name);
            }

            return patched.Count == 0
                ? ImeSuppressionResult.Failed("ImeMode 変換テーブルが 1 つも見つからない")
                : new ImeSuppressionResult(true, patched, null);
        }
        catch (Exception ex)
        {
            // 起動処理なのでここで投げ返さない。握り潰した事実は呼び出し側が Trace へ落とす。
            return ImeSuppressionResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
