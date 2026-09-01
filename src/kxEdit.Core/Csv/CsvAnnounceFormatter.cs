using kxEdit.Core.Text;

namespace kxEdit.Core.Csv;

/// <summary>CSV モードの読み上げ文字列を組み立てる純ロジック（UI 非依存・テスト可能）。</summary>
public static class CsvAnnounceFormatter
{
    /// <summary>マーカー付き発話で先頭に載せるサニタイズ済み値の最大長 (UTF-16 code units)。</summary>
    private const int MarkerHeadMaxLength = 60;

    /// <summary>制御文字を検出したセル/ヘッダ発話の接頭辞 (末尾スペースまで含む)。</summary>
    private const string ControlCharMarker = "制御文字を含みます: ";

    /// <summary>
    /// セル移動時の読み上げ。内容→位置の順（例「田中 2行2列」）。空セルは「ブランク」。row/col は 1 始まり。
    /// <para>
    /// UIA-M-1: <paramref name="value"/> に制御文字 / RLO / BOM / U+2028/U+2029 等が含まれる場合は
    /// 「制御文字を含みます: {先頭 60 文字}」の形式で発話し攻撃気付きを優先する。
    /// クリーンな値は切詰めせず全文を発話する (SR ユーザが住所・備考欄を全文読む正当な操作を維持)。
    /// </para>
    /// </summary>
    public static string Cell(string value, int row, int col)
    {
        if (string.IsNullOrEmpty(value))
            return $"ブランク {row}行{col}列";

        if (SanitizeForDisplay.ContainsSanitizableControlChar(value))
        {
            var head = SanitizeForDisplay.OneLine(value, MarkerHeadMaxLength);
            return $"{ControlCharMarker}{head} {row}行{col}列";
        }

        return $"{value} {row}行{col}列";
    }

    /// <summary>
    /// 見出し読み上げ。空なら「ブランク」。
    /// <para>
    /// UIA-M-1: <see cref="Cell"/> と同じルールで制御文字を含めばマーカー形式、なければ pass-through。
    /// </para>
    /// </summary>
    public static string Header(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "ブランク";

        if (SanitizeForDisplay.ContainsSanitizableControlChar(value))
        {
            var head = SanitizeForDisplay.OneLine(value, MarkerHeadMaxLength);
            return $"{ControlCharMarker}{head}";
        }

        return value;
    }

    /// <summary>CSV モードをオンにしたときの読み上げ。</summary>
    public const string ModeOn = "CSVモード オン";

    /// <summary>CSV モードをオフにしたときの読み上げ。</summary>
    public const string ModeOff = "CSVモード オフ";

    /// <summary>オープン時に CSV として解析できず、テキストとして開いたときの読み上げ。</summary>
    public const string OpenParseFailed = "CSVとして解析できませんでした。テキストとして開きます";

    /// <summary>操作時に CSV として解析できないときの読み上げ。</summary>
    public const string ParseError = "CSVとして解析できません";

    /// <summary>CSV だが行データが無いときの読み上げ。</summary>
    public const string NoData = "データがありません";

    /// <summary>移動先セルが取得できない異常時のフォールバック読み上げ。</summary>
    public const string CannotMove = "移動できません";

    /// <summary>セル指定移動で範囲外を指定したときの読み上げ。</summary>
    public const string OutOfRange = "範囲外です";

    /// <summary>セル指定の書式が不正なときの読み上げ。</summary>
    public const string BadCellFormat = "書式が不正です。行,列 の形式で入力してください";

    /// <summary>CSVモード中に本文変更コマンド（置換/折り返し整形等）を試みたときの読み上げ。</summary>
    public const string BlockedInCsvMode = "CSVモード中は実行できません";

    /// <summary>F2 セル編集の確定時に、編集対象のセルが別物へ変わっていた/消えていたときの読み上げ。
    /// 本文へは一切書かずにこれだけを発声する(2026-09-01 設計書 §4.2)。</summary>
    public const string CommitTargetChanged = "本文が変わったため確定できません";

    /// <summary>左端で左移動したときの読み上げ。</summary>
    public const string LeftEdge = "左端です";

    /// <summary>右端で右移動したときの読み上げ。</summary>
    public const string RightEdge = "右端です";

    /// <summary>先頭行で上移動したときの読み上げ。</summary>
    public const string TopEdge = "先頭行です";

    /// <summary>最終行で下移動したときの読み上げ。</summary>
    public const string BottomEdge = "最終行です";
}
