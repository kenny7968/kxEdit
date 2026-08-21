using kxEdit.App.Speech;
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 選択範囲(無ければ全文)を WrapColumn 桁で禁則整形する(実改行挿入・1 Undo)。
/// CSV モード中は本文が読取専用のため抑止し、誤成功通知を防ぐ。
/// Stage 8 で MainForm.FormatWithKinsoku から抽出(挙動不変・機械的移動)。
/// </summary>
/// <remarks>
/// 依存: <see cref="DocumentManager"/>(アクティブ文書解決)+<see cref="IAnnouncer"/>(SR 通知)。
/// <see cref="AppSettings"/> は OpenSettings で参照差し替わる可能性があるため ctor 注入ではなく
/// <see cref="Run"/> 引数(呼び出し時解決)。Stage 6 <c>ICellPicker</c> と同型パターン。
/// </remarks>
public sealed class KinsokuFormatController
{
    private readonly DocumentManager _docs;
    private readonly IAnnouncer _announcer;

    public KinsokuFormatController(DocumentManager docs, IAnnouncer announcer)
    {
        _docs = docs;
        _announcer = announcer;
    }

    /// <summary>
    /// アクティブ文書の選択範囲(または全文)を禁則整形する。
    /// AppSettings は呼び出し時解決(OpenSettings で参照差し替わるため Controller にキャッシュしない)。
    /// </summary>
    public void Run(AppSettings settings)
    {
        var doc = _docs.Active;
        var ed = doc?.Editor;
        if (ed is null)
            return;
        // CSVモード中は本文が読取専用で整形が無反映になるため抑止(誤成功通知を防ぐ)。
        if (doc!.State.CsvMode)
        {
            _announcer.Say(CsvAnnounceFormatter.BlockedInCsvMode);
            return;
        }

        string text = ed.SnapshotText;
        var (selStart, selEnd) = ed.GetSelectionCharRange();
        bool whole = selStart == selEnd;
        int start = whole ? 0 : selStart;
        int len = whole ? text.Length : selEnd - selStart;
        if (len <= 0)
            return;

        string target = text.Substring(start, len);
        string eol = doc!.State.LineEnding.ToEolString();
        string formatted = KinsokuFormatter.Format(
            target,
            settings.WrapColumn,
            settings.KinsokuLineStartChars,
            settings.KinsokuLineEndChars,
            settings.KinsokuHangChars,
            eol,
            settings.TabWidth
        ); // タブ幅は表示設定と連動(画面の見た目どおりに整形する)

        if (formatted == target)
        {
            _announcer.Say("変更なし");
            return;
        }
        ed.ReplaceCharRange(start, len, formatted); // 1 Undo で置換
        // 部分選択なら変化箇所を選択して提示。全文整形では全選択を避け、先頭へキャレットを置く。
        if (whole)
            ed.SelectCharRange(0, 0);
        else
            ed.SelectCharRange(start, formatted.Length);
        ed.Focus();
        _announcer.Say("整形しました");
    }
}
