using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// 2026-07-31 文字アクセス seam Task 1: コードポイント単位(サロゲートのみ atomic)と
/// 論理文字単位(サロゲート + CRLF atomic)の 2 系統が、それぞれ独立に正しく歩進することを固定する。
///
/// 2 系統を分けている理由: キャレット / UIA は CRLF を 1 論理文字として扱うが、
/// WordBoundary の内部歩進は CR と LF を別々の LineBreak として数える前提になっている。
/// 統一すると WordBoundary の挙動が変わるため、名前で分離したまま集約する。
/// </summary>
public class TextBoundaryTests
{
    private static TextSnapshot Snap(string s) => TextBuffer.FromString(s).Current;

    // ===== コードポイント単位: サロゲートのみ atomic・CRLF は 1 ずつ =====

    [Fact]
    public void NextCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b"); // CharLength=4
        Assert.Equal(1, TextBoundary.NextCodePoint(s, 0));
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 1)); // 😀 を 1 歩で越える
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 3));
        Assert.Equal(4, TextBoundary.NextCodePoint(s, 4)); // 末尾で no-op
    }

    [Fact]
    public void PrevCodePoint_SkipsSurrogatePair()
    {
        var s = Snap("a😀b");
        Assert.Equal(3, TextBoundary.PrevCodePoint(s, 4));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 1));
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, 0)); // 先頭で no-op
    }

    [Fact]
    public void PrevCodePoint_SurrogatePairAtDocumentStart_IsAtomic()
    {
        // 文書先頭のペア = prev > 0 の境界(prefix 付き fixture "a😀b" では prev == 1 に当たらない)
        Assert.Equal(0, TextBoundary.PrevCodePoint(Snap("😀b"), 2));
    }

    [Fact]
    public void NextCodePoint_DoesNotSkipCrlf()
    {
        // 論理文字版との差を固定する = CRLF は 2 歩かかる
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.NextCodePoint(s, 1)); // CR の前 → CR と LF の間
        Assert.Equal(3, TextBoundary.NextCodePoint(s, 2));
    }

    [Fact]
    public void PrevCodePoint_DoesNotSkipCrlf()
    {
        var s = Snap("a\r\nb");
        Assert.Equal(2, TextBoundary.PrevCodePoint(s, 3));
        Assert.Equal(1, TextBoundary.PrevCodePoint(s, 2));
    }

    [Theory]
    [InlineData("ab", 0, 1)] // BMP
    [InlineData("😀", 0, 2)] // astral
    [InlineData("\r\n", 0, 1)] // CR は単独で 1
    public void CodePointLengthAt_Snapshot(string text, int pos, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(Snap(text), pos));

    [Fact]
    public void CodePointLengthAt_Snapshot_LoneHighSurrogateIsNormalizedByBuffer()
    {
        // バッファ層が孤立サロゲートを U+FFFD へ正規化するため、Snapshot 版では
        // 「high サロゲートの直後に low が続かない」形に到達しない。その前提ごと固定する。
        var lone = Snap("a\ud83d");
        Assert.Equal('\uFFFD', lone.GetChar(1));
        Assert.Equal(1, TextBoundary.CodePointLengthAt(lone, 1));
        // 対が揃っていれば 2
        Assert.Equal(2, TextBoundary.CodePointLengthAt(Snap("a😀"), 1));
    }

    [Fact]
    public void CodePointLengthAt_Snapshot_OutOfRange_Throws()
    {
        var s = Snap("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.CodePointLengthAt(s, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.CodePointLengthAt(s, -1));
    }

    // ===== 論理文字単位: サロゲート + CRLF atomic =====

    [Fact]
    public void NextLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a😀b"), 1));
        Assert.Equal(3, TextBoundary.NextLogicalChar(Snap("a\r\nb"), 1)); // CRLF を 1 歩で越える
    }

    [Fact]
    public void PrevLogicalChar_SkipsSurrogatePairAndCrlf()
    {
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a😀b"), 3));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\r\nb"), 3));
    }

    [Fact]
    public void PrevLogicalChar_SurrogatePairAtDocumentStart_IsAtomic()
    {
        // CRLF 側(LogicalChar_CrlfAtDocumentEdges_IsAtomic)と対称の prev > 0 境界
        Assert.Equal(0, TextBoundary.PrevLogicalChar(Snap("😀b"), 2));
    }

    [Fact]
    public void LogicalChar_LoneCrAndLoneLf_MoveOneStep()
    {
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\rb"), 1));
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\nb"), 1));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\rb"), 2));
        Assert.Equal(1, TextBoundary.PrevLogicalChar(Snap("a\nb"), 2));
        // 改行が EOF に来る文書(Mac 形式 = 末尾が孤立 CR)。CRLF 判定の先読みが
        // CharLength ちょうどを踏まないことを固定する(踏むと GetChar が throw する)。
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\r"), 1));
        Assert.Equal(2, TextBoundary.NextLogicalChar(Snap("a\n"), 1));
    }

    [Fact]
    public void LogicalChar_EmptyDocument_IsNoOp()
    {
        var s = Snap("");
        Assert.Equal(0, TextBoundary.NextLogicalChar(s, 0));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, 0));
    }

    [Fact]
    public void LogicalChar_CrlfAtDocumentEdges_IsAtomic()
    {
        // 文書先頭・末尾の CRLF(前後に文字がない)でも pair として 1 歩で跨ぐ
        var s = Snap("\r\n");
        Assert.Equal(2, TextBoundary.NextLogicalChar(s, 0));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, 2));
    }

    // ===== 中間位置スナップ =====

    [Fact]
    public void SnapToLogicalCharStart_SnapsMidSurrogateAndMidCrlf()
    {
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a😀b"), 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a\r\nb"), 2)); // CR と LF の間
    }

    [Fact]
    public void SnapToLogicalCharStart_LeavesBoundariesAlone()
    {
        var s = Snap("a😀b");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, 0));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 1));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 3));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharStart(s, 4)); // EOF は許可
    }

    [Fact]
    public void SnapToLogicalCharStart_LoneLfIsNotSnapped()
    {
        // 直前が CR でない LF は行頭ではなくそれ自体が論理文字の先頭
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(Snap("a\nb"), 1));
    }

    [Fact]
    public void CrlfRule_NonLfAfterLoneCr_IsNotTreatedAsPairEnd()
    {
        // 孤立 CR(Mac 改行)の直後の通常文字は CRLF pair の後半ではない。
        // SnapToLogicalCharStart_LoneLfIsNotSnapped の鏡像(CR 側から見た no-snap)。
        // これが崩れると キャレットが 'b' から CR へ引き戻され、← が 1 回で 2 文字戻る。
        var s = Snap("a\rb");
        Assert.Equal(2, TextBoundary.SnapToLogicalCharStart(s, 2));
        Assert.Equal(2, TextBoundary.PrevLogicalChar(s, 3));
    }

    [Fact]
    public void CrlfRule_NonCrBeforeLoneLf_IsNotTreatedAsPairStart()
    {
        // 孤立 LF(Unix 改行)の直前の通常文字は CRLF pair の前半ではない。
        // CrlfRule_NonLfAfterLoneCr_IsNotTreatedAsPairEnd の鏡像(前進側)。
        // これが崩れると → が 1 回で 'a' と LF をまとめて飛び越える(LF 改行の文書で常時再現)。
        Assert.Equal(1, TextBoundary.NextLogicalChar(Snap("a\nb"), 0));
    }

    [Fact]
    public void SnapToLogicalCharStart_LfAtDocumentStart_DoesNotReadBeforeStart()
    {
        // 先頭が LF の文書(空行始まり)。pos == 0 は CRLF 判定へ進まず、GetChar(-1) を読まない。
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(Snap("\nabc"), 0));
    }

    [Fact]
    public void SnapToLogicalCharStart_ClampsOutOfRange()
    {
        var s = Snap("abc");
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(s, -5));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(s, 99));
    }

    // ===== 範囲外入力の契約(クラス doc の表を固定する) =====
    // 家族ごとに非対称なのは「進めない側」だけを no-op として許容しているため。
    // クランプで隠すと呼び出し側のスナップ漏れが発見できなくなるので throw 側は throw のまま守る。

    [Fact]
    public void OutOfRange_NextFamily_ThrowsBelowZero_ClampsAboveEnd()
    {
        var s = Snap("ab");
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.NextCodePoint(s, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.NextLogicalChar(s, -1));
        Assert.Equal(2, TextBoundary.NextCodePoint(s, 99));
        Assert.Equal(2, TextBoundary.NextLogicalChar(s, 99));
    }

    [Fact]
    public void OutOfRange_PrevFamily_ClampsBelowZero_ThrowsAboveEnd()
    {
        var s = Snap("ab");
        Assert.Equal(0, TextBoundary.PrevCodePoint(s, -1));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(s, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevCodePoint(s, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevLogicalChar(s, 99));
    }

    [Fact]
    public void OutOfRange_PrevFamily_DegenerateNoReadCase_DoesNotThrow()
    {
        // 実装は必要な位置しか読まない。prev == 0 になる範囲外(空文書 + pos == 1)は
        // 読取が起きないため throw せず 0 を返す。クラス doc の但し書きを固定する。
        var empty = Snap("");
        Assert.Equal(0, TextBoundary.PrevCodePoint(empty, 1));
        Assert.Equal(0, TextBoundary.PrevLogicalChar(empty, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextBoundary.PrevCodePoint(empty, 2));
    }

    // ===== span 版(Layout / 描画) =====

    [Theory]
    [InlineData("ab", 0, 1)]
    [InlineData("😀", 0, 2)]
    [InlineData("a😀", 1, 2)]
    public void CodePointLengthAt_Span(string text, int i, int expected) =>
        Assert.Equal(expected, TextBoundary.CodePointLengthAt(text.AsSpan(), i));

    [Fact]
    public void CodePointLengthAt_Span_HighSurrogateAtEnd_IsOne()
    {
        // 対の相手が span の外にある場合は 1(呼び出し側の無限ループを防ぐ)
        ReadOnlySpan<char> lone = "a😀".AsSpan(0, 2); // 'a' + high サロゲートのみ
        Assert.Equal(1, TextBoundary.CodePointLengthAt(lone, 1));
    }

    [Fact]
    public void CodePointLengthAt_Span_HighSurrogateFollowedByNonLow_IsOne()
    {
        // span 版はバッファ層の U+FFFD 正規化を通らないため、対の揃わない high サロゲートを
        // 構築できる(snapshot 版と違い到達可能=テストで塞げる穴)
        ReadOnlySpan<char> text = "a\ud83db".AsSpan();
        Assert.Equal(1, TextBoundary.CodePointLengthAt(text, 1));
    }

    [Fact]
    public void SnapToCodePointStart_Span_LoneLowSurrogateIsNotSnapped()
    {
        // 直前が high サロゲートでない low サロゲートは pair ではない=動かさない
        ReadOnlySpan<char> text = "a\udc00b".AsSpan();
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 1));
    }

    [Fact]
    public void CodePointLengthAt_Span_OutOfRange_ThrowsIndexOutOfRange()
    {
        // snapshot 版(ArgumentOutOfRangeException)と例外型が違う契約を固定する
        Assert.Throws<IndexOutOfRangeException>(() =>
            TextBoundary.CodePointLengthAt("ab".AsSpan(), 2)
        );
        Assert.Throws<IndexOutOfRangeException>(() =>
            TextBoundary.CodePointLengthAt("ab".AsSpan(), -1)
        );
    }

    [Fact]
    public void SnapToCodePointStart_Span()
    {
        var text = "a😀b".AsSpan();
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 2)); // low サロゲート位置
        Assert.Equal(1, TextBoundary.SnapToCodePointStart(text, 1));
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, 0));
        Assert.Equal(4, TextBoundary.SnapToCodePointStart(text, 4)); // 末尾は動かさない
    }

    [Fact]
    public void SnapToCodePointStart_Span_LowSurrogateAtIndexZero_DoesNotReadBeforeStart()
    {
        // スライスが pair を割って low サロゲートから始まる場合。i == 0 は text[-1] を読まない。
        ReadOnlySpan<char> text = "\udc00b".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, 0));
    }

    [Fact]
    public void SnapToCodePointStart_Span_ClampsOutOfRange()
    {
        var text = "abc".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToCodePointStart(text, -5));
        Assert.Equal(3, TextBoundary.SnapToCodePointStart(text, 99));
    }

    // ===== span 版 SnapToLogicalCharStart(2026-09-01 B2 Task 1) =====
    // snapshot 版と違い CRLF も見る。Core.Search が材質化した本文 string を扱うため。

    [Fact]
    public void SnapToLogicalCharStart_Span_SnapsMidSurrogateAndMidCrlf()
    {
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\U0001F600b".AsSpan(), 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\r\nb".AsSpan(), 2));
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_LeavesBoundariesAlone()
    {
        var text = "a\r\nb".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(text, 0));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(text, 1));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(text, 3));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharStart(text, 4)); // 末尾は許可
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_LoneLfAndLoneLowSurrogateAreNotSnapped()
    {
        // 対を成さない片割れは論理文字を作らないので動かさない(snapshot 版と同じ規則)。
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\nb".AsSpan(), 1));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("a\uDE00b".AsSpan(), 1));
        // high サロゲート 2 連: pos は「サロゲートではあるが low ではない」=ペアを終えない。
        // IsLowSurrogate を IsSurrogate へ緩める変異はここでしか殺せない
        // (snapshot 版は TextBuffer が U+FFFD へ潰すため、この形に到達できない)。
        // 既存 span 族の CodePointLengthAt_Span_HighSurrogateFollowedByNonLow_IsOne と対になる。
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart("\uD83D\uD83D".AsSpan(), 1));
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_ClampsOutOfRange()
    {
        var text = "abc".AsSpan();
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart(text, -5));
        Assert.Equal(3, TextBoundary.SnapToLogicalCharStart(text, 99));
    }

    /// <summary>
    /// 全数の材料: 論理文字境界に関わる 5 code unit だけで長さ 4 以下の全文字列を作る。
    /// 通常文字 / CR / LF / high サロゲート / low サロゲートで、
    /// 「対を成す・成さない」の全組合せが 4 文字以内に現れる。
    /// </summary>
    private static IEnumerable<string> ShortStringsOverBoundaryAlphabet()
    {
        // 絵文字は UTF-16 で 2 code unit なので、この文字列の Length は 5
        // (a / CR / LF / high サロゲート / low サロゲート)。
        const string alphabet = "a\r\n\U0001F600";
        var cur = new List<string> { "" };
        yield return "";
        for (int len = 1; len <= 4; len++)
        {
            var next = new List<string>(cur.Count * alphabet.Length);
            // 外側に波括弧が要る: CSharpier は括弧なしの入れ子 foreach を同じ深さへ畳むため、
            // 括弧を外すと Sonar S3973(条件実行の範囲が見えない)でビルドが落ちる。
            foreach (string s in cur)
            {
                foreach (char c in alphabet)
                    next.Add(s + c);
            }
            foreach (string s in next)
                yield return s;
            cur = next;
        }
    }

    [Fact]
    public void ShortStringsOverBoundaryAlphabet_CoversAllStringsUpToLengthFour_WithoutDuplicates()
    {
        // 「全数で固定してある」という主張そのものをピン留めする。これが無いと、alphabet の typo・
        // ループ境界の編集・yield return "" の削除で被覆が静かに落ちてもテストは緑のままになり、
        // 下 2 本が「全数」を騙る(嘘の安全宣言)。
        var all = ShortStringsOverBoundaryAlphabet().ToList();
        // 5^0 + 5^1 + 5^2 + 5^3 + 5^4 = 1 + 5 + 25 + 125 + 625 = 781
        Assert.Equal(781, all.Count);
        Assert.Equal(781, all.Distinct().Count()); // 重複が水増ししていない
        Assert.Contains("", all); // 空文字列(長さ 0)を落としていない
        Assert.Equal(4, all.Max(s => s.Length)); // 長さ 4 まで届いている
        Assert.Equal(5, all.SelectMany(s => s).Distinct().Count()); // alphabet が 5 code unit
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_LowSurrogateAtIndexZero_DoesNotReadBeforeStart()
    {
        // span の端は無条件に境界とみなす契約(xmldoc)を名前付きで固定する。
        // より大きなテキストの「窓」を渡すと窓外へまたがる pair は見えない=窓の先頭が
        // low サロゲート / LF でも動かさない(text[-1] を読まない)。
        // 既存 span 族の SnapToCodePointStart_Span_LowSurrogateAtIndexZero_DoesNotReadBeforeStart
        // の対応物。CRLF 側は snapshot 版の _LfAtDocumentStart_DoesNotReadBeforeStart と対。
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart("\uDE00b".AsSpan(), 0));
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart("\nabc".AsSpan(), 0));
        // 窓が CRLF を割っている場合(直前の '\r' は窓の外)も動かさない。
        Assert.Equal(0, TextBoundary.SnapToLogicalCharStart("a\r\nb".AsSpan(2, 2), 0));
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_MatchesSnapshotVersion_Exhaustive()
    {
        foreach (string raw in ShortStringsOverBoundaryAlphabet())
        {
            var snap = Snap(raw);
            // 比較は「保存層を通った後の本文」で行う。TextBuffer は UTF-8 で保持するため
            // 孤立サロゲートは U+FFFD へ潰れ、raw と snapshot の中身は一致しない。
            string text = snap.GetText(0, snap.CharLength);
            for (int pos = -2; pos <= text.Length + 2; pos++)
            {
                // Assert.Equal はメッセージを付けられない。781 本 × 全 pos のどれで落ちたかが
                // 判らないと原因究明できないので Assert.True + 明示メッセージにする。
                int fromSnapshot = TextBoundary.SnapToLogicalCharStart(snap, pos);
                int fromSpan = TextBoundary.SnapToLogicalCharStart(text.AsSpan(), pos);
                Assert.True(
                    fromSnapshot == fromSpan,
                    $"snapshot/span mismatch: '{Escape(text)}' pos={pos} "
                        + $"snapshot={fromSnapshot} span={fromSpan}"
                );
            }
        }
    }

    [Fact]
    public void SnapToLogicalCharStart_Span_Exhaustive_NeverMovesForwardAndStaysInRange()
    {
        // snapshot を通さない=孤立サロゲートを含む入力も直接当てる(同値テストが届かない領域)。
        foreach (string text in ShortStringsOverBoundaryAlphabet())
        {
            for (int pos = -2; pos <= text.Length + 2; pos++)
            {
                int got = TextBoundary.SnapToLogicalCharStart(text.AsSpan(), pos);
                int clamped = Math.Max(0, Math.Min(pos, text.Length));
                string where = $"'{Escape(text)}' pos={pos} got={got} clamped={clamped}";
                Assert.True(
                    got >= 0 && got <= text.Length,
                    $"out of range: {where} len={text.Length}"
                );
                Assert.True(got <= clamped, $"forward move: {where}");
                Assert.True(clamped - got <= 1, $"moved more than 1: {where}");
                // (1) 結果は論理文字の内側を指さない。
                if (got > 0 && got < text.Length)
                {
                    Assert.False(
                        char.IsLowSurrogate(text[got]) && char.IsHighSurrogate(text[got - 1]),
                        $"landed inside surrogate pair: {where}"
                    );
                    Assert.False(
                        text[got] == '\n' && text[got - 1] == '\r',
                        $"landed inside CRLF: {where}"
                    );
                }
                // (2) iff の残り半分: 動いたなら、動く必要が実際にあった。
                // これが無いと「動くべきでないときに動く」過剰スナップ系の変異
                // (IsLowSurrogate → IsSurrogate 等)が族ごと素通りする。
                if (got != clamped)
                {
                    bool mustMove =
                        (
                            char.IsLowSurrogate(text[clamped])
                            && char.IsHighSurrogate(text[clamped - 1])
                        ) || (text[clamped] == '\n' && text[clamped - 1] == '\r');
                    Assert.True(mustMove, $"moved but need not: {where}");
                }
            }
        }
    }

    /// <summary>失敗メッセージ用。制御文字とサロゲートを可視化する。</summary>
    private static string Escape(string s) =>
        string.Concat(s.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $@"\u{(int)c:X4}"));

    // ===== SnapToLogicalCharEnd: 論理文字の中間位置を後方(pair 終端)へ寄せる =====

    [Fact]
    public void SnapToLogicalCharEnd_MidCrlf_SnapsForwardToLf()
    {
        // "a\r\nb": 2 は CR と LF の間=論理文字の中間。Start は 1 へ、End は 3 へ寄せる。
        var s = Snap("a\r\nb");
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 2)); // 対であることを同じ fixture で示す
    }

    [Fact]
    public void SnapToLogicalCharEnd_MidSurrogatePair_SnapsForwardToLowEnd()
    {
        // "a😀b": 2 は low サロゲート位置=論理文字の中間。
        var s = Snap("a😀b");
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 2));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharStart(s, 2));
    }

    [Fact]
    public void SnapToLogicalCharEnd_OnBoundary_IsIdentity()
    {
        // 境界上は動かさない(no-change の検証は非既定位置=文書先頭でも末尾でもない 1 と 3 から始める)
        var s = Snap("a\r\nb"); // CharLength=4
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(s, 1)); // CR の前
        Assert.Equal(3, TextBoundary.SnapToLogicalCharEnd(s, 3)); // LF の後
    }

    [Fact]
    public void SnapToLogicalCharEnd_ClampsBothEnds()
    {
        var s = Snap("a\r\nb"); // CharLength=4
        Assert.Equal(0, TextBoundary.SnapToLogicalCharEnd(s, -5));
        Assert.Equal(0, TextBoundary.SnapToLogicalCharEnd(s, 0));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharEnd(s, 4));
        Assert.Equal(4, TextBoundary.SnapToLogicalCharEnd(s, 99));
    }

    [Fact]
    public void SnapToLogicalCharEnd_LoneCrAndLoneLf_AreNotPairs()
    {
        // 単独 CR / 単独 LF は pair を作らない=どの位置も恒等。
        // 「直前が CR か」の判定を落として「\n なら常に前進」にする変異をここで殺す。
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(Snap("a\rb"), 1));
        Assert.Equal(1, TextBoundary.SnapToLogicalCharEnd(Snap("a\nb"), 1));
        Assert.Equal(2, TextBoundary.SnapToLogicalCharEnd(Snap("a\nb"), 2));
    }

    [Fact]
    public void SnapToLogicalCharEnd_LfAtDocumentStart_DoesNotReadBeforeStart()
    {
        // 先頭が LF の文書(空行始まり)。pos == 0 は CRLF 判定へ進まず、GetChar(-1) を読まない。
        // SnapToLogicalCharStart_LfAtDocumentStart_DoesNotReadBeforeStart の鏡像。
        // ClampsBothEnds の fixture("a\r\nb")は pos == 0 が 'a' で述語が短絡するため、
        // 早期 return を落とす変異(pos <= 0 → pos < 0)をそちらでは殺せない。
        Assert.Equal(0, TextBoundary.SnapToLogicalCharEnd(Snap("\nabc"), 0));
    }
}
