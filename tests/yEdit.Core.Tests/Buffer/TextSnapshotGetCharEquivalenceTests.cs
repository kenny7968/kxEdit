using System.Text;
using yEdit.Core.Buffers;

namespace yEdit.Core.Tests.Buffers;

/// <summary>
/// 2026-07-31 文字アクセス seam Task 3: GetChar をバイト直読みへ再実装するにあたり、
/// 「全位置で GetText(pos, 1)[0] と一致する」ことを参照実装比較で固定する。
///
/// 一番危ないのはサロゲート中間位置(low サロゲートを指す pos)。旧実装は GetSubstring の
/// 「開始が中間なら低い方へスナップ・終端が中間ならコードポイントを丸ごと含めてから Substring」
/// という経路で正しい low サロゲートを返していた。新実装はこれを 1 回のマッピングで導く。
/// </summary>
public class TextSnapshotGetCharEquivalenceTests
{
    // ASCII / CJK(3 バイト)/ 絵文字(4 バイト)/ CRLF・LF・CR 混在をすべて含む
    private const string MixedFixture = "abc\r\nあいう\n😀xy\rz\r\n漢字😀あ\nEnd😀";

    private static void AssertAllPositionsMatch(TextSnapshot snap)
    {
        AssertEveryPieceIsWholeCodePoints(snap);
        for (int pos = 0; pos < snap.CharLength; pos++)
            Assert.Equal(snap.GetText(pos, 1)[0], snap.GetChar(pos));
    }

    /// <summary>
    /// GetChar の <c>DecodeUtf16At</c> は継続バイトの存在を検査せず <c>byteOffset + 1..3</c> を
    /// 読む。その範囲外読み回避は「ピースのバイト範囲が必ず完全なコードポイントの列である」
    /// という 1 点だけに依存するので、各ピースが単独で妥当な UTF-8 であることを直接固定する
    /// (この不変条件が破れると GetChar はピースの外のバイトを読む)。
    /// </summary>
    private static void AssertEveryPieceIsWholeCodePoints(TextSnapshot snap)
    {
        foreach (var p in PieceTree.Enumerate(snap.Root))
        {
            Assert.True(
                System.Text.Unicode.Utf8.IsValid(p.Chunk.Span.Slice(p.ByteStart, p.ByteLen)),
                "ピース範囲が単独で妥当な UTF-8 でない=コードポイントがピースを跨いでいる"
            );
        }
    }

    [Fact]
    public void GetChar_MatchesGetText_AtEveryPosition_FreshBuffer() =>
        AssertAllPositionsMatch(TextBuffer.FromString(MixedFixture).Current);

    [Fact]
    public void GetChar_MatchesGetText_AtEveryPosition_Utf8LengthBoundaries()
    {
        // MixedFixture だけでは足りない: 2 バイト列を 1 つも含まず、3 バイト列も U+8000 未満・
        // 4 バイト列も U+20000 未満のため、デコーダのマスク・シフトの上位ビットが一度も立たない。
        // (2026-07-31 のミューテーション掃引で、この網がないと 11 件の変異が生き残ることを実測)
        int[] codePoints =
        [
            0x0000, // 1 バイトの下端
            0x007F, // 1 バイトの上端(0x80 閾値)
            0x0080, // 2 バイトの下端
            0x07FF, // 2 バイトの上端(先頭 0xDF・継続 0xBF=2 バイト側のマスクを全ビット踏む)
            0x0800, // 3 バイトの下端(先頭バイトがちょうど 0xE0=0xE0 閾値)
            0xFFFD, // Utf8Sanitizer が生成する置換文字(実運用で確実に現れる 3 バイト列)
            0xFFFF, // 3 バイトの上端(先頭 0xEF=先頭マスク 0x0F を全ビット踏む)
            0x10000, // 4 バイトの下端(v=0)
            0xFFFFF, // 4 バイトの継続バイト 3 つのマスクを全ビット踏む(0x10FFFF は bit17 が立たない)
            0x10FFFF, // 4 バイトの上端(先頭バイト 0xF4=先頭マスク 0x07 の bit2 を踏む唯一の値)
        ];
        var sb = new StringBuilder();
        foreach (int cp in codePoints)
            sb.Append(char.ConvertFromUtf32(cp));
        AssertAllPositionsMatch(TextBuffer.FromString(sb.ToString()).Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AtEveryPosition_LargeDocument()
    {
        // 格子(既定 64KB)を何マスも跨ぐ規模にして、格子点前後の境界も踏ませる
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append(MixedFixture);
        AssertAllPositionsMatch(TextBuffer.FromString(sb.ToString()).Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AfterEdits()
    {
        // ピース分割 + AppendBuffer 経由のタイピングを経た木でも一致すること。
        // Task 4 の格子細分化が AppendBuffer の「生成後に同じ配列へ書き続ける」前提を
        // 壊していないかを捕まえる唯一の実質的な網。
        var buf = TextBuffer.FromString(MixedFixture);
        for (int i = 0; i < 500; i++)
        {
            int pos = (i * 7) % (buf.Current.CharLength + 1);
            pos = SnapOffSurrogateMiddle(buf.Current, pos);
            buf.Insert(
                pos,
                i % 3 == 0 ? "あ"
                    : i % 3 == 1 ? "x"
                    : "😀"
            );
        }
        AssertAllPositionsMatch(buf.Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AfterLargeInsert()
    {
        // AppendBuffer の大挿入経路(>32KB は専用チャンク)も踏む
        var buf = TextBuffer.FromString(MixedFixture);
        var big = new StringBuilder();
        for (int i = 0; i < 4000; i++)
            big.Append("あ😀a\r\n");
        buf.Insert(0, big.ToString());
        AssertAllPositionsMatch(buf.Current);
    }

    [Theory]
    [InlineData("é")] // 2 バイト
    [InlineData("あ")] // 3 バイト
    [InlineData("😀")] // 4 バイト
    public void GetChar_MatchesGetText_AcrossAppendBufferBlockBoundary(string multiByte)
    {
        // AppendBuffer は 64KB ブロックの残りに収まらない挿入を「コードポイント境界まで後退
        // スナップして詰める」(AppendBuffer.Append)。このスナップが落ちるとコードポイントが
        // ピースを跨ぎ、DecodeUtf16At が継続バイトを先頭バイトとして読む=同一ブロックの
        // 未書込ゼロ領域を読んで例外なしに誤った char を返す(静かな破壊)。
        // 他の等価テストはここへ到達しない: AfterEdits は総量 1.5KB 程度でブロックに届かず、
        // AfterLargeInsert は LargeInsertBytes 超で専用チャンク経路へ分岐するため。
        for (int gap = 0; gap <= 5; gap++)
        {
            var buf = TextBuffer.FromString("");
            int fill = AppendBuffer.BlockBytes - gap;
            // 1 回の挿入が LargeInsertBytes を超えると専用チャンクへ逃げてブロックを消費しない
            for (int written = 0; written < fill; )
            {
                int n = Math.Min(AppendBuffer.LargeInsertBytes, fill - written);
                buf.Insert(buf.Current.CharLength, new string('a', n));
                written += n;
            }
            buf.Insert(buf.Current.CharLength, multiByte + "xyz");

            var snap = buf.Current;
            AssertEveryPieceIsWholeCodePoints(snap);
            // 境界前後だけ全位置照合する(全 65,000 位置を回すと参照実装側が桁違いに遅いため)
            for (int pos = fill - 4; pos < snap.CharLength; pos++)
                Assert.Equal(snap.GetText(pos, 1)[0], snap.GetChar(pos));
        }
    }

    [Fact]
    public void GetChar_MidSurrogatePosition_ReturnsLowSurrogate()
    {
        var snap = TextBuffer.FromString("a😀b").Current;
        Assert.True(char.IsHighSurrogate(snap.GetChar(1)));
        Assert.True(char.IsLowSurrogate(snap.GetChar(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(999)]
    public void GetChar_OutOfRange_Throws(int pos)
    {
        var snap = TextBuffer.FromString("abcd").Current; // CharLength=4
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.GetChar(pos));
    }

    /// <summary>挿入位置がサロゲート中間に落ちないよう 1 つ手前へ寄せる
    /// (TextBuffer.Split は内部でスナップするが、テストの意図を明示するため)。</summary>
    private static int SnapOffSurrogateMiddle(TextSnapshot snap, int pos)
    {
        if (pos <= 0 || pos >= snap.CharLength)
            return pos;
        return char.IsLowSurrogate(snap.GetChar(pos)) ? pos - 1 : pos;
    }
}
