using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

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

    /// <summary>
    /// AppendBuffer 探針ブロック(<see cref="AppendProbeBlock"/>)の ASCII 部。
    /// 周期 11・全文字相異なので「2 文字ずれ」が必ず別の文字として現れる。
    /// '\n' を含むのは、格子が CharOff と同時に BreaksTo もキャッシュするため。
    /// </summary>
    private const string ProbePattern = "0123456789\n";

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
        // 格子(既定 4KB)を何マスも跨ぐ規模にして、格子点前後の境界も踏ませる
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append(MixedFixture);
        AssertAllPositionsMatch(TextBuffer.FromString(sb.ToString()).Current);
    }

    [Fact]
    public void GetChar_MatchesGetText_AfterEdits()
    {
        // ピース分割 + AppendBuffer 経由のタイピングを経た木でも一致すること
        // (AppendBuffer の格子表が空である前提の網は
        //  GetChar_MatchesSourceText_AcrossWholeAppendBufferBlock が担う。
        //  本テストは GetChar と GetText が同じ木で一致することだけを見る)。
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
    public void GetChar_MatchesSourceText_AcrossWholeAppendBufferBlock()
    {
        // AppendBuffer が共有ブロックを gridBytes: BlockBytes で包んでいること
        // (= 格子表が先頭 1 エントリだけ)を固定する。ctor 側の指定を守る。
        var buf = TextBuffer.FromString("");
        string src = AppendProbeBlock(buf);
        AssertProbeTailMatchesSource(buf.Current, src);
    }

    [Fact]
    public void GetChar_MatchesSourceText_AcrossSecondAppendBufferBlock()
    {
        // 姉妹テスト。AppendBuffer は 1 ブロックを使い切ると _block を再確保して
        // TextChunk を作り直すので、明示指定は ctor と繰上げの 2 箇所に必要になる。
        // 照合対象は 2 ブロック目の末尾だけなので、落ちたら繰上げ側の指定が原因と分かる
        // (1 ブロック = 64KB なので連続タイピングで現実に到達する経路)。
        var buf = TextBuffer.FromString("");
        string first = AppendProbeBlock(buf); // 1 ブロック目を使い切って繰上げさせる
        string second = AppendProbeBlock(buf);
        AssertProbeTailMatchesSource(buf.Current, first + second);
    }

    /// <summary>
    /// AppendBuffer の 1 ブロック(64KB)をちょうど埋める探針テキストを末尾へ書き込み、
    /// 書き込んだ文字列を返す。末尾への連続挿入は TextBuffer.Splice の隣接マージで
    /// 1 ピースへ融合するので、ブロック全域を覆う 1 ピースができる。
    ///
    /// 先頭 1 文字だけ 3 バイト(あ)にするのが要点。以降ブロック全域で
    /// 「バイト位置 = 文字位置 + 2」となる。TextChunk 構築時ブロックは全ゼロなので、
    /// 細かい格子はゼロ領域から求めた「1 バイト = 1 文字 / break 0 個」を格子点に焼き付ける。
    /// 実際の内容はそこから常に 2 文字ずれているため、<b>格子幅がいくつであっても</b>
    /// 格子点へ飛んだ瞬間に 2 文字数え過ぎ、2 文字手前のバイトを返す。
    /// </summary>
    private static string AppendProbeBlock(TextBuffer buf)
    {
        var sb = new StringBuilder(AppendBuffer.BlockBytes);
        sb.Append('あ'); // 3 バイト 1 文字
        while (sb.Length < AppendBuffer.BlockBytes - 2)
            sb.Append(ProbePattern[sb.Length % ProbePattern.Length]);
        string text = sb.ToString(); // 文字数 = BlockBytes-2 / バイト数 = BlockBytes ちょうど

        // 1 回の挿入が LargeInsertBytes を超えると専用チャンクへ逃げてブロックを消費しない
        const int ChunkChars = 32_000;
        for (int off = 0; off < text.Length; off += ChunkChars)
            buf.Insert(
                buf.Current.CharLength,
                text.Substring(off, Math.Min(ChunkChars, text.Length - off))
            );
        return text;
    }

    /// <summary>
    /// 探針ブロックの<b>末尾側だけ</b>を元文字列(ground truth)と照合する。
    ///
    /// 末尾を見るのは、問い合わせ文字位置が最大になる点だから: 格子幅がいくつでも
    /// 「その位置以下の格子点」が必ず存在するので、1 箇所で全格子幅を捕まえられる。
    /// 全位置照合にしないのは、正しい実装(格子 1 エントリ)だと 1 回の GetChar が
    /// O(pos) 走査になり全位置で O(n²) になるため。
    ///
    /// 参照が GetText ではなく元文字列であることも要。GetText は同じ
    /// TextChunk.CharToByte を通るので、格子が壊れれば同じだけ壊れて一致してしまう
    /// (= この不変条件は GetChar と GetText の相互比較では原理的に検出できない)。
    /// </summary>
    private static void AssertProbeTailMatchesSource(TextSnapshot snap, string src)
    {
        AssertEveryPieceIsWholeCodePoints(snap);
        Assert.Equal(src.Length, snap.CharLength);
        for (int pos = src.Length - 16; pos < src.Length; pos++)
            Assert.Equal(src[pos], snap.GetChar(pos));
        // 格子は CharOff と同時に BreaksTo もゼロ領域から焼き付ける。行検索は
        // TextChunk.NthBreakEndChar 経由でそれを読むので、最終行頭でまとめて捕まえる。
        Assert.Equal(src.LastIndexOf('\n') + 1, snap.GetLineStart(snap.LineCount - 1));
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
