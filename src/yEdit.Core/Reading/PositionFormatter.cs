namespace yEdit.Core.Reading;

/// <summary>現在位置・文字数の読み上げ文字列を組み立てる純ロジック（UI 非依存・テスト可能）。</summary>
public static class PositionFormatter
{
    /// <summary>
    /// 「行 L / 全 N、桁 C、文字数 M」を組み立てる。selectionLength&gt;0 なら「、選択 K 文字」、
    /// overtype 時は「、上書き」を付ける（挿入/上書きモードを照会でも分かるようにする）。
    /// line/column は 1 始まり、totalChars/selectionLength は論理文字数（CRLF=1・サロゲート=2 で数える。
    /// 2026-07-24 に UTF-16 code unit 数から変更=CRLF を 1 論理文字として扱う統一）。
    /// </summary>
    public static string Format(
        int line,
        int totalLines,
        int column,
        int totalChars,
        int selectionLength,
        bool overtype = false
    )
    {
        string s = $"行 {line} / 全 {totalLines}、桁 {column}、文字数 {totalChars}";
        if (selectionLength > 0)
            s += $"、選択 {selectionLength} 文字";
        if (overtype)
            s += "、上書き";
        return s;
    }
}
