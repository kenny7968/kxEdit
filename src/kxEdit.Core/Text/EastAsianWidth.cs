namespace kxEdit.Core.Text;

/// <summary>
/// コードポイントの半角換算表示幅（Wide/Fullwidth=2, 結合/ゼロ幅=0, その他=1）。
/// Unicode East Asian Width の実用的近似（厳密テーブルは将来精緻化）。
/// </summary>
public static class EastAsianWidth
{
    public static int ColumnWidth(int cp)
    {
        if (IsZeroWidth(cp))
            return 0;
        return IsWide(cp) ? 2 : 1;
    }

    private static bool IsZeroWidth(int cp) =>
        cp
            is (>= 0x0300 and <= 0x036F) // 結合分音記号
                or (>= 0x1AB0 and <= 0x1AFF)
                or (>= 0x1DC0 and <= 0x1DFF)
                or (>= 0x20D0 and <= 0x20FF)
                or (>= 0xFE20 and <= 0xFE2F)
                or 0x200B
                or 0x200C
                or 0x200D
                or 0xFEFF; // ゼロ幅スペース/接合子/BOM

    private static bool IsWide(int cp) =>
        cp
            is (>= 0x1100 and <= 0x115F) // Hangul Jamo
                or (>= 0x2E80 and <= 0x303E) // CJK部首補助〜CJK記号(全角句読点・全角スペース含む)
                or (>= 0x3041 and <= 0x33FF) // かな〜CJK互換
                or (>= 0x3400 and <= 0x4DBF) // 拡張A
                or (>= 0x4E00 and <= 0x9FFF) // 統合漢字
                or (>= 0xA000 and <= 0xA4CF) // イ文字
                or (>= 0xAC00 and <= 0xD7A3) // Hangul音節
                or (>= 0xF900 and <= 0xFAFF) // 互換漢字
                or (>= 0xFE30 and <= 0xFE4F) // CJK互換形
                or (>= 0xFF00 and <= 0xFF60) // 全角形（半角カナ FF61〜 は除外）
                or (>= 0xFFE0 and <= 0xFFE6) // 全角記号
                or (>= 0x1F300 and <= 0x1FAFF) // 絵文字（概ね全角）
                or (>= 0x20000 and <= 0x3FFFD); // CJK拡張B〜（astral）
}
