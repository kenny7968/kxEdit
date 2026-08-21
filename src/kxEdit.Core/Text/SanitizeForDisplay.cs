using System.Globalization;
using System.Text;

namespace kxEdit.Core.Text;

/// <summary>
/// 攻撃者制御の可能性がある文字列を UI/SR/ログへ載せる前段で無害化する。
/// 対象は Unicode 制御文字全般(BiDi/format 系・C0/C1・null・DEL・BOM・U+2028/U+2029 の
/// LINE/PARAGRAPH SEPARATOR 等)。
/// XML/HTML エスケープや scheme 検証は本ヘルパの担当外(呼び出し側で組み合わせる)。
///
/// 設計背景(2026-07-19 再監査 <see href="docs/plans/2026-07-19-security-hardening-medium-low.md">docs/plans/2026-07-19-security-hardening-medium-low.md</see> の
/// 横断的パターン④):
///   - BK-L-4 RestoreDialog の U+202E RLO によるファイル名スプーフィング
///   - UIA-M-1 CsvAnnounceFormatter の攻撃 CSV → SR ソーシャルエンジニアリング
///   - BK-L-5 Trace.TraceWarning の CRLF injection
///   - CSV-L-5 IUserPrompt.Warn に生パスを載せた時の RLO / 改行注入
/// を単一 API で塞ぐための共通依存。
/// </summary>
public static class SanitizeForDisplay
{
    /// <summary>
    /// 1 行表示用に無害化する。CR / LF / TAB を含む全 C0 制御・C1 制御・DEL は単一空白へ置換し、
    /// 連続空白は 1 個へ畳む。BiDi/format 系(RLO/LRE/PDF/…)と ZWSP/ZWNJ/ZWJ/BOM、および
    /// U+2028 LINE SEPARATOR(Zl)/ U+2029 PARAGRAPH SEPARATOR(Zp)は除去する。先頭空白は保つが
    /// 末尾空白は trim する。<paramref name="maxLength"/> を超える場合は末尾を "…"(U+2026)で
    /// 省略する(既定は無制限)。<paramref name="value"/> が null または空なら空文字列を返す。
    /// <paramref name="maxLength"/> &lt; 1 の場合も空文字列(定義域外の防御)。
    /// </summary>
    /// <remarks>
    /// maxLength はサニタイズ「後」の長さ(UTF-16 code unit 数)で判定する。切詰め時に
    /// surrogate pair の分断を避けるため、末尾が高サロゲート単独になる場合は 1 code unit 戻す。
    /// </remarks>
    public static string OneLine(string? value, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (maxLength < 1)
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool lastWasSpace = false;

        foreach (var rune in value.EnumerateRunes())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(rune.Value);
            if (
                cat == UnicodeCategory.Format
                || cat == UnicodeCategory.LineSeparator
                || cat == UnicodeCategory.ParagraphSeparator
            )
            {
                // BiDi override / LRM / RLM / ZWSP / ZWNJ / ZWJ / BOM (Format) と、
                // U+2028 (Zl) / U+2029 (Zp) はすべて drop。改行意図があれば呼び出し側が
                // CR/LF を直接使う設計とし、1 行整合を Zl/Zp から守る。
                continue;
            }

            bool isWhitespaceLike = cat == UnicodeCategory.Control || rune.Value == ' ';
            if (isWhitespaceLike)
            {
                if (lastWasSpace)
                    continue;
                sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                AppendRune(sb, rune);
                lastWasSpace = false;
            }
        }

        // 末尾空白 trim(先頭空白は仕様上残す)
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ')
            sb.Length--;

        if (sb.Length <= maxLength)
            return sb.ToString();

        // "…"(U+2026)は 1 code unit。maxLength - 1 code units + "…"。
        int cutTo = maxLength - 1;
        // 末尾が高サロゲート単独になる場合は 1 戻して pair を丸ごと落とす。
        if (cutTo > 0 && char.IsHighSurrogate(sb[cutTo - 1]))
            cutTo--;
        return sb.ToString(0, cutTo) + "…";
    }

    /// <summary>
    /// 複数行表示用に無害化する。CR / LF / TAB は保持するが、それ以外の C0 制御・C1 制御・DEL・
    /// BiDi/format 系(RLO/LRE/…)と ZWSP/ZWNJ/ZWJ/BOM・U+2028 LINE SEPARATOR(Zl)・
    /// U+2029 PARAGRAPH SEPARATOR(Zp)を除去する。空白の畳み込みや末尾 trim は行わない
    /// (ログ/複数行 UI で行構造を壊さないため)。<paramref name="value"/> が null または空なら
    /// 空文字列を返す。
    /// </summary>
    public static string MultiLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            int cp = rune.Value;
            var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (
                cat == UnicodeCategory.Format
                || cat == UnicodeCategory.LineSeparator
                || cat == UnicodeCategory.ParagraphSeparator
            )
            {
                // BiDi / ZW / BOM (Format) と U+2028 (Zl) / U+2029 (Zp) を drop。
                // Zl/Zp は「CR/LF/TAB のみ通す」invariant を守るため通さない。
                continue;
            }
            if (cat == UnicodeCategory.Control)
            {
                // CR (U+000D) / LF (U+000A) / TAB (U+0009) のみ通す。他 C0 / C1 / DEL は drop。
                if (cp == '\r' || cp == '\n' || cp == '\t')
                {
                    sb.Append((char)cp);
                }
                continue;
            }
            AppendRune(sb, rune);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> に「サニタイズ対象の制御文字」(<see cref="OneLine"/> /
    /// <see cref="MultiLine"/> が除去または空白へ置換する rune) が 1 個以上含まれるかを判定する。
    /// 判定対象は UnicodeCategory の Format / LineSeparator / ParagraphSeparator / Control
    /// (BiDi/format 系・ZW*・BOM・U+2028/U+2029・C0/C1/DEL・CR/LF/TAB 含む)。
    /// null または空文字列は false。
    /// </summary>
    /// <remarks>
    /// UIA-M-1 で「攻撃 CSV セルにマーカーを付けるか否か」を明確に切り分けるために追加。
    /// OneLine/MultiLine とロジックが 1:1 対応するよう同じカテゴリ集合で判定する。
    /// </remarks>
    public static bool ContainsSanitizableControlChar(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var rune in value.EnumerateRunes())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(rune.Value);
            if (
                cat == UnicodeCategory.Format
                || cat == UnicodeCategory.LineSeparator
                || cat == UnicodeCategory.ParagraphSeparator
                || cat == UnicodeCategory.Control
            )
            {
                return true;
            }
        }
        return false;
    }

    private static void AppendRune(StringBuilder sb, Rune rune)
    {
        Span<char> buf = stackalloc char[2];
        int n = rune.EncodeToUtf16(buf);
        sb.Append(buf[..n]);
    }
}
