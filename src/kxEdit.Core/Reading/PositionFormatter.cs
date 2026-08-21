namespace kxEdit.Core.Reading;

/// <summary>現在位置(行/桁)の読み上げ文字列を組み立てる純ロジック(UI 非依存・テスト可能)。
/// 2026-07-25: 文書情報ダイアログ導入に伴い、文字数(totalChars)と選択(selectionLength)の
/// 引数を削除した。文字数の詳細は [ファイル]&gt;文書情報 ダイアログへ集約する
/// (位置照会=編集位置の指標・文書情報=文書全体の内容量の指標という棲み分け)。</summary>
public static class PositionFormatter
{
    /// <summary>
    /// 「行 L / 全 N、桁 C」を組み立てる。overtype 時は「、上書き」を付ける
    /// (挿入/上書きモードを照会でも分かるようにする)。line/column は 1 始まり。
    /// </summary>
    public static string Format(int line, int totalLines, int column, bool overtype = false)
    {
        string s = $"行 {line} / 全 {totalLines}、桁 {column}";
        if (overtype)
            s += "、上書き";
        return s;
    }
}
