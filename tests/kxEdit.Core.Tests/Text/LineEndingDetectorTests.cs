using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class LineEndingDetectorTests
{
    [Theory]
    [InlineData("a\r\nb", LineEnding.Crlf)]
    [InlineData("a\nb", LineEnding.Lf)]
    [InlineData("a\rb", LineEnding.Cr)]
    public void Detects_dominant_line_ending(string text, LineEnding expected) =>
        Assert.Equal(expected, LineEndingDetector.Detect(text));

    [Fact]
    public void Mixed_returns_dominant() =>
        Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect("a\nb\nc\r\nd"));

    [Fact]
    public void No_newline_returns_platform_default() =>
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect("abc"));

    // A-9: snapshot 版と string 版で多数決の意味論が一致すること(走査範囲だけが違う)。
    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\nb\nc\r\nd")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("a\r\nb\rc\nd\r\ne")]
    [InlineData("a\r")] // 末尾単独 CR(小さい文書での drain)
    // 多数決の 3 つの >= はいずれも同数のときに効く。同数 fixture が無いと比較演算子の
    // 変異(>= を > へ)が生存するので、3 つの境界を 1 件ずつ当てる(設計書 §7.4)。
    [InlineData("a\r\nb\nc")] // crlf == lf → Crlf
    [InlineData("a\r\nb\rc")] // crlf == cr → Crlf
    [InlineData("a\nb\rc")] // crlf < lf == cr → Lf
    public void Snapshot_overload_matches_string_overload(string text) =>
        Assert.Equal(
            LineEndingDetector.Detect(text),
            LineEndingDetector.Detect(TextBuffer.FromString(text).Current)
        );

    // A-9: CRLF が 4MB チャンク境界を跨いでも CR + LF に割れないこと(pendingCr の持ち越し)。
    // 割れると crlf=1 が cr=1 + lf=1 になり、多数決が Crlf → Lf へ反転する=弁別できる。
    // 改行はこの CRLF 1 つだけにすること。計画案のように末尾へ "tail" + CRLF を足すと、
    // 割れた場合でも crlf=1 / lf=1 / cr=1 で Crlf のままになり網として無意味になる。
    [Fact]
    public void Snapshot_overload_counts_crlf_spanning_chunk_boundary_as_one()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes - 1) + "\r\n";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, lastOfFirst: 0x0D, firstOfSecond: 0x0A); // CR で切れ LF で始まる=境界を跨いでいる
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect(snapshot));
    }

    // A-9(M3): 境界を跨いだ CRLF の LF を二重計上しないこと(持ち越し処理の `i = 1;`)。
    // 二重計上すると crlf=1 / lf=1 が crlf=1 / lf=2 になり Crlf → Lf へ反転する=撃墜。
    // 上の ..._crlf_spanning_chunk_boundary_as_one とは fixture の狙いが違う:
    // あちらは「割れないこと」、こちらは「二重に数えないこと」。改行を 1 つだけにすると
    // 二重計上しても同数のまま Crlf になり弁別できないので、ここでは "x\n" で同数を作る。
    [Fact]
    public void Snapshot_overload_does_not_double_count_boundary_crlf()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes - 1) + "\r\n" + "x\n";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, lastOfFirst: 0x0D, firstOfSecond: 0x0A);
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect(snapshot)); // crlf=1 / lf=1 の同数
    }

    // A-9: 文書末尾の単独 CR が drain されること(foreach 後の `if (pendingCr) cr++`)。
    // drain を落とすと crlf=lf=cr=0 になり既定の Crlf が返る=Cr と弁別できる。
    [Fact]
    public void Snapshot_overload_counts_trailing_lone_cr()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes) + "\r";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, lastOfFirst: (byte)'a', firstOfSecond: 0x0D); // 'a' で切れ CR だけの最終ピース
        Assert.Equal(LineEnding.Cr, LineEndingDetector.Detect(snapshot));
    }

    // A-9: ピース末尾 CR の次が LF 以外だったとき、持ち越し CR を単独 CR として数え、
    // かつ現バイトを落とさずに通常処理へ進めること(pendingCr ブロックの `cr++` と fall-through)。
    // 実ファイルでの再現条件は「4MB チャンク境界がちょうど CR に落ちる CR 単独(旧 Mac)文書」。
    // 上の 2 fact は境界 CR が LF に当たる経路と drain 経路しか通らず、この分岐に入らない。
    //
    // 次バイトが通常文字のケース。`cr++` を落とすと改行 0 件になり既定の Crlf へ倒れる=撃墜。
    // (fall-through を落としても 'x' は改行ではないので結果が変わらない=こちらは殺せない)
    [Fact]
    public void Snapshot_overload_counts_carried_cr_before_non_lf_byte()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes - 1) + "\r" + "x";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, lastOfFirst: 0x0D, firstOfSecond: (byte)'x'); // CR で切れ 'x' で始まる
        Assert.Equal(LineEnding.Cr, LineEndingDetector.Detect(snapshot));
    }

    // 次バイトがまた CR のケース。fall-through を落として現バイトを捨てると、続く CRLF の
    // CR が失われて LF 単独に化け、crlf=1/cr=1 が lf=1/cr=1 になり Crlf → Lf へ反転する=撃墜。
    // (`cr++` を落としても crlf=1 が残り Crlf のまま=こちらは殺せない。2 fixture で 1 行ずつ受け持つ)
    [Fact]
    public void Snapshot_overload_counts_carried_cr_before_crlf()
    {
        string body = new string('a', TextBufferBuilder.TargetChunkBytes - 1) + "\r" + "\r\nx";
        var snapshot = TextBuffer.FromString(body).Current;
        AssertTwoPiecesSplitBetween(snapshot, lastOfFirst: 0x0D, firstOfSecond: 0x0D); // CR で切れ CR で始まる
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect(snapshot));
    }

    // A-9(I-2): 編集で ByteStart != 0 になったピースでも、チャンク全体ではなく
    // ピースの担当範囲だけを走査すること。PieceTree は削除してもチャンクのバイトを
    // 捨てず、ピースの範囲を狭めるだけなので、Slice(ByteStart, ByteLen) を
    // Slice(0, Chunk.ByteLength) にすると削除済みの CRLF 3 件を数え直して Crlf を返す。
    //
    // Detect(TextSnapshot) は public API で、現状の唯一の呼び出し元(LoadAsBufferAuto)は
    // 新規バッファしか渡さないので ByteStart は常に 0。将来「貼り付け後に EOL を再判定する」
    // 等の呼び出しが増えた瞬間に誤判定になるため、ここで固定しておく。
    [Fact]
    public void Snapshot_overload_scans_only_the_piece_range_after_edit()
    {
        var buffer = TextBuffer.FromString("\r\n\r\n\r\nx\ny"); // crlf=3 / lf=1 → Crlf
        Assert.Equal(LineEnding.Crlf, LineEndingDetector.Detect(buffer.Current));
        buffer.Delete(0, 6); // 先頭 CRLF ×3 を削除 → 本文は "x\ny" で lf=1 のみ
        AssertSinglePieceAt(buffer.Current, byteStart: 6, byteLen: 3);
        Assert.Equal(LineEnding.Lf, LineEndingDetector.Detect(buffer.Current));
    }

    /// <summary>
    /// fixture の前提検証。スナップショットがちょうど 1 ピースで、その担当範囲が
    /// チャンクの途中(<paramref name="byteStart"/> != 0)から始まり、チャンクには
    /// 削除済みバイトが残っていることを確かめる。ここが 0 に戻ると
    /// 「チャンク全体を走査する」変異を撃墜できなくなる。
    /// </summary>
    private static void AssertSinglePieceAt(TextSnapshot snapshot, int byteStart, int byteLen)
    {
        var piece = Assert.Single(PieceTree.Enumerate(snapshot.Root).ToList());
        Assert.Equal(byteStart, piece.ByteStart);
        Assert.Equal(byteLen, piece.ByteLen);
        Assert.True(
            piece.ByteLen < piece.Chunk.ByteLength,
            $"チャンクに削除済みバイトが残っていません(ByteLen={piece.ByteLen} / ChunkLen={piece.Chunk.ByteLength})"
        );
    }

    /// <summary>
    /// fixture の前提検証。スナップショットがちょうど 2 ピースで、1 つ目の末尾バイトが
    /// <paramref name="lastOfFirst"/>、2 つ目の先頭バイトが <paramref name="firstOfSecond"/>
    /// であることを確かめる。TextBufferBuilder のチャンク分割規則が変わって境界を跨がなくなった
    /// fixture が黙って通り続けるのを防ぐ。
    /// </summary>
    private static void AssertTwoPiecesSplitBetween(
        TextSnapshot snapshot,
        byte lastOfFirst,
        byte firstOfSecond
    )
    {
        var pieces = PieceTree.Enumerate(snapshot.Root).ToList();
        Assert.Equal(2, pieces.Count);
        Assert.Equal(
            lastOfFirst,
            pieces[0].Chunk.Span[pieces[0].ByteStart + pieces[0].ByteLen - 1]
        );
        Assert.Equal(firstOfSecond, pieces[1].Chunk.Span[pieces[1].ByteStart]);
    }
}
