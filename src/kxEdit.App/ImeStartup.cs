using System.Reflection;

namespace kxEdit.App;

/// <summary>
/// <see cref="ImeStartup.SuppressWinFormsImeModeInference()"/> の結果。
/// 成否とその根拠をテストから観測できるようにするために返す(void にすると
/// 「1 つも塗れていない」変異が緑のまま生き残る)。
///
/// <para><b>この型を <c>==</c> / <see cref="object.Equals(object)"/> で比較しないこと</b>:
/// record struct の生成 <c>Equals</c> は <see cref="PatchedTables"/> を
/// <c>EqualityComparer&lt;IReadOnlyList&lt;string&gt;&gt;.Default</c> —— つまり参照等値 ——
/// で比べるため、中身が同じでも別インスタンスなら不等になる。比較したいときは
/// <see cref="PatchedTables"/> など個々のメンバを比べる。</para>
///
/// <para><b><c>default</c> を作らないこと</b>: record struct なので <c>default</c> が
/// 構築でき、そのときだけ <see cref="PatchedTables"/> が <c>null</c> になる。
/// 呼び出し側(<c>Program.Main</c> 冒頭)は <c>SetUnhandledExceptionMode</c> も
/// <c>CrashHandler</c> も未装着の区間で <c>string.Join</c> に渡すため、そこへ
/// <c>null</c> が届くと起動不能かつ post-mortem 無しになる。生成は必ず
/// <see cref="Failed(string)"/> / <see cref="Failed(string, IReadOnlyList{string})"/> か
/// 全引数コンストラクタを通し、<b><see cref="PatchedTables"/> は非 null 前提</b>で読む
/// (今日 <c>default</c> を返す経路は 1 つも無い。増やさないこと)。</para>
/// </summary>
internal readonly record struct ImeSuppressionResult(
    bool Succeeded,
    IReadOnlyList<string> PatchedTables,
    string? FailureReason
)
{
    internal static ImeSuppressionResult Failed(string reason) => new(false, [], reason);

    /// <summary>
    /// 途中まで塗れていた事実を残したまま失敗を返す。呼び出し側の Trace が
    /// 「1 つも無効化できていない」と「一部は無効化済み」を取り違えないようにするため。
    /// </summary>
    internal static ImeSuppressionResult Failed(string reason, IReadOnlyList<string> patched) =>
        new(false, patched, reason);
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
/// 変換テーブルを経由しないため、数値入力欄の IME 無効化は従来どおり効く。
/// 一次資料(dotnet/winforms <c>Control.Ime.cs</c>)で確認した内訳:
/// <b>読み側</b>の <c>ImeContext.GetImeMode</c> は <c>ImmGetContext(handle) == 0</c> のとき
/// テーブルを引かず<b>リテラルの</b> <c>ImeMode.Disable</c> を返す。<b>設定側</b>の
/// <c>ImeContext.SetImeStatus</c> は <c>ImeMode.Disable</c> を <c>Disable(handle)</c>
/// (= <c>ImmAssociateContext</c> で入力コンテキストを外す)へ回し、その後の
/// <c>switch</c> でも <c>break</c> するだけでテーブルを読まない。
/// よってテーブルを塗っても <c>ImeMode.Disable</c> による IME の切り離しそのものは無傷。
/// ただし「<c>Disable</c> の欄から戻ったときに WinForms が IME を開き直す」復元動作は
/// 本対策で止まる —— 日本語入力中に「行へ移動」等を使うと戻っても IME が切れたままになる
/// (設計 2026-09-05 §6.2 の受容事項。実測済み)。</para>
/// </summary>
internal static class ImeStartup
{
    /// <summary>
    /// 塗り始める index。
    ///
    /// <para><b>根拠(一次資料 dotnet/winforms <c>Control.Ime.cs</c> を実読)</b>:
    /// フレームワークが変換テーブルを<b>添字で読む箇所は
    /// <c>ImeContext.GetImeMode</c> の 7 か所だけ</b>で、その添字は
    /// <c>ImeModeConversion.ImeClosed</c>(= 3)と
    /// <c>ImeNativeFullHiragana</c>(4)/ <c>ImeNativeHalfHiragana</c>(5)/
    /// <c>ImeNativeFullKatakana</c>(6)/ <c>ImeNativeHalfKatakana</c>(7)/
    /// <c>ImeAlphaFull</c>(8)/ <c>ImeAlphaHalf</c>(9)。
    /// <b>index 0 / 1(<c>ImeDisabled</c>)/ 2(<c>ImeDirectInput</c>)は
    /// フレームワークのどこからも添字として使われない</b>(定数値はいずれも実測)。</para>
    ///
    /// <para>したがって 2 から塗るのは<b>「読まれるセルを 1 つ残らず塗る」を満たす最小かつ
    /// 保守的な選択</b>である。0 や 1 まで広げても推測の挙動は 1 ミリも変わらず、
    /// フレームワーク側の構造セル(<c>ImeMode.Inherit</c> / <c>ImeMode.Disable</c>)を
    /// 無意味に壊すだけなので広げない。</para>
    ///
    /// <para><b>ここを 4 以上へ狭めてはいけない</b>: 読まれる最初のセルは <c>ImeClosed</c>(3)で、
    /// 3 を塗り残すと IME が閉じているときの推測が生き返り、<c>PropagatingImeMode</c> が
    /// 再び記録されて発声が戻る(変異 4 は実測で KILLED)。
    /// 3 は機能的には 2 と等価だが、<c>ImeStartupTests.Suppress_NeutralisesCellsTheFrameworkActuallyReads</c> が
    /// <c>ImeDirectInput</c>(2)まで塗ることを固定しているため、そこでも赤くなる。</para>
    /// </summary>
    private const int FirstInferredCell = 2;

    /// <summary>
    /// 本番の入口。<c>Program.Main</c> の<b>最初</b>で呼ぶ(ウィンドウを 1 つも作る前)。
    /// 遅れて呼ぶと、起動時に既に武装済みの分が 1 回だけ鳴る(実測)。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference()
    {
        Type? conversionType;
        try
        {
            conversionType = typeof(Control).Assembly.GetType(
                "System.Windows.Forms.ImeModeConversion"
            );
        }
        catch (Exception ex)
        {
            // Assembly.GetType は ArgumentException / FileNotFoundException /
            // FileLoadException / BadImageFormatException 等を投げうる(列挙は例示で、
            // いずれにせよ Exception で受ける)。呼び出し位置は Program.Main の最初 =
            // Application.SetUnhandledExceptionMode も CrashHandler もまだ未装着の区間で、
            // ここから漏らすと起動不能かつ post-mortem も残らない。絶対に投げ返さない。
            return ImeSuppressionResult.Failed($"型解決に失敗: {ex.GetType().Name}: {ex.Message}");
        }

        return SuppressWinFormsImeModeInference(conversionType);
    }

    /// <summary>
    /// 型解決を外から差し替えられる seam(テストが異常系を駆動するため)。
    /// <paramref name="conversionType"/> が null / 想定外でも<b>例外を投げず</b>失敗を返す
    /// ——起動を止めないことが最優先で、失敗しても現状動作(発声が出る)に落ちるだけ。
    /// </summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference(Type? conversionType)
    {
        var patched = new List<string>();
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
            foreach (var field in conversionType.GetFields(any))
            {
                // 「型で発見する」という本処理の宣言そのもの(第一次の選別条件)。
                // 下の is パターンとは役割が違い、宣言型が object のフィールドを除外する。
                // ただしこの環境では下の is パターンと結果が一致するため、単独では殺せない。
                if (field.FieldType != typeof(ImeMode[]))
                    continue;
                if (field.GetValue(null) is not ImeMode[] table)
                    continue;
                // 1 セルも塗らないテーブル(空の s_unsupportedTable など)を PatchedTables に
                // 載せないためのガード。UnsupportedTable との参照比較は「今日 1 度も分岐しない
                // 到達不能コード」だったので置かない —— 将来 s_unsupportedTable が非空になったら
                // 塗ったうえで PatchedTables に載り、テストが赤くなって気づける(設計 §6.3)。
                if (table.Length <= FirstInferredCell)
                    continue;
                // 変換テーブルの構造署名。今日の 3 テーブルはいずれも [Inherit, Disable, ...] で
                // 始まる(一次資料 ImeModeConversion で確認)。将来 ImeModeConversion に
                // 「変換テーブルではない ImeMode[] static」(既定モードの一覧など)が増えたとき、
                // 型と長さだけでは素通りして全セルを塗り潰してしまう —— そうなると WinForms が
                // その配列に置いた前提が例外も Trace も無しに静かに壊れる。フィールド名依存へ
                // 戻らずに誤爆面だけを狭めるための、形による第 2 の絞り込みである。
                // 逆に .NET 側で署名が変わって 1 つも塗れなくなった場合は Failed に落ちるだけで
                // (症状が戻る)、ImeStartupTests の件数固定(3 テーブル)が CI で先に赤くなる。
                // 上の 2 つの continue と同じく、今日この行を消す変異は生き残る(現存する
                // ImeMode[] static はすべて署名を満たすため)。将来の誤爆に対する防御であって、
                // 今日の網ではない。
                if (table[0] != ImeMode.Inherit || table[1] != ImeMode.Disable)
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
            // ループ途中で落ちても塗り済みの分は渡す —— 「1 つも無効化できていない」と
            // 「一部は無効化済み」を Trace が取り違えないため。
            //
            // ex.Message の評価を try で包まないのは意図的(Program.cs の CrashHandler 側は
            // ExceptionObject.ToString() を包んでいるので、防御水準が非対称に見える)。
            // あちらは任意の例外オブジェクトを受けるが、ここへ到達しうるのは Assembly /
            // Type / FieldInfo が投げる BCL の例外型に限られ、その Message は投げない。
            // 塗るのはテーブル単位で原子的(内側のループは境界内の値型代入のみで throw
            // しえない)なので、ここに来ても半端に塗られたテーブルは残らない。
            return ImeSuppressionResult.Failed($"{ex.GetType().Name}: {ex.Message}", patched);
        }
    }
}
