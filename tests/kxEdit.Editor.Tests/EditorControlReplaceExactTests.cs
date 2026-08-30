namespace kxEdit.Editor.Tests;

/// <summary>
/// A-14(2026-08-29): <see cref="EditorControl.ReplaceCharRangeExact"/> が、両端が論理文字の
/// 内側を指していても巻き込んだ文字を復元することを固定する。
///
/// 既存 <c>ReplaceCharRange</c> は両端をスナップするため CRLF の LF だけを置換できない。
/// 一括置換(<c>ReplaceInRange</c> + 範囲丸ごと差し替え)は両端が境界に乗るので同じ問題を
/// 踏まず、正しい結果を出している。本 API は単発置換をその結果に揃えるために足した。
/// </summary>
public class EditorControlReplaceExactTests
{
    [Fact]
    public void ReplaceCharRangeExact_LfOfCrlf_KeepsCr() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            int next = ctrl.ReplaceCharRangeExact(4, 1, "X"); // LF だけを置換

            Assert.Equal("abc\rXdef", ctrl.Text);
            // 戻り値=置換文字列の直後。s(3) + 復元した接頭辞 "\r"(1) + "X"(1) = 5。
            Assert.Equal(5, next);
        });

    [Fact]
    public void ReplaceCharRangeExact_LowSurrogateOnly_HighHalfCollapsesToReplacementChar() =>
        Sta.Run(() =>
        {
            // 計画の期待値は "a\uD83DXb"(高位サロゲートが残る)だったが、これは**保存形式の
            // 都合で到達不能**。ピース木は本文を UTF-8 で保持し(TextBuffer.FromString /
            // AppendBuffer.Append はいずれも Encoding.UTF8.GetBytes)、孤立サロゲートは既定の
            // 置換フォールバックで U+FFFD へ潰れる(AppendBuffer.Append の doc に明記済みの
            // 既存契約)。つまりサロゲートペアを割る置換は**どの経路を通っても**半身を救出できない。
            //
            // 本 API の目的は単発置換を一括置換に**揃える**ことなので、ここでの正は
            // 一括置換(範囲丸ごと差し替え)が出す結果そのものとする。下の対照群がそれを示す
            // =U+FFFD 化は本 API 固有の欠陥ではなく保存層の性質である。
            using var exact = new EditorControl();
            using var bulk = new EditorControl();
            exact.SetSource(TextBuffer.FromString("a\U0001F600b")); // "a😀b"
            bulk.SetSource(TextBuffer.FromString("a\U0001F600b"));

            exact.ReplaceCharRangeExact(2, 1, "X"); // low サロゲートだけを置換

            // 対照群: 一括置換の形(断片を組んで範囲を丸ごと差し替える)。
            // 断片は「高位サロゲート + X + b」= 単発置換が組むのと同じ内容。
            bulk.ReplaceCharRange(0, bulk.TextLength, "a\uD83DXb");

            // 生の U+FFFD をソースへ直接置くと不可視で誤読を招くため、コードポイントから組む。
            string fffd = char.ConvertFromUtf32(0xFFFD);
            Assert.Equal("a" + fffd + "Xb", exact.Text); // 高位サロゲートは U+FFFD へ潰れる
            Assert.Equal(exact.Text, bulk.Text); // 単発 = 一括
        });

    [Fact]
    public void ReplaceCharRangeExact_CaretLandsAtEndOfWidenedRange() =>
        Sta.Run(() =>
        {
            // 事後条件の固定。委譲先が "s + text.Length" に置くため、キャレットは
            // 置換文字列の末尾(4)ではなく**広げた範囲の末尾**(5 = 復元した LF の後ろ)に立つ。
            // 次ヒットの探索起点にキャレットを流用できないことを、戻り値との差で示す。
            // CR 側の巻き込み復元(LF を食わない)と戻り値もここで併せて固定する
            // = 同一 fixture・同一主張の _CrOfCrlf_KeepsLf は真部分集合だったので畳んだ。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            int next = ctrl.ReplaceCharRangeExact(3, 1, "X"); // CR だけを置換(LF を復元して書き戻す)

            Assert.Equal("abcX\ndef", ctrl.Text);
            Assert.Equal(5, ctrl.CaretCharOffset); // 広げた範囲の末尾
            Assert.Equal((5, 5), ctrl.GetSelectionCharRange()); // 選択は解除される
            // キャレットは戻り値の代用にならない。復元した suffix("\n")の分だけ先へ行く。
            Assert.Equal(4, next);
            Assert.NotEqual(next, ctrl.CaretCharOffset);
            // 接頭辞 [0, start) は長さ保存で復元される=start(3)より前は不変。
            Assert.Equal("abc", ctrl.Text[..3]);
        });

    [Fact]
    public void ReplaceCharRangeExact_NegativeStart_KeepsRemainingWidth() =>
        Sta.Run(() =>
        {
            // 終端は**生の start** から作る(クランプ後の s0 からではない)。[-1, 2) のうち
            // 文書内に残る幅 [0, 2) が置換対象になる。s0 から作ると [0, 3) になり "Xd" が出る。
            //
            // この設計選択そのものは ClampsOutOfRangeArgs も弁別する(実測: (long)s0 への変異で
            // 両方が赤。(-3, 2) は正しい実装ではゼロ幅だが、変異すると幅 2 の置換になり
            // "Xabcd" が "Xcd" に変わる)。本テストが足すのは**幅が生き残る**ほうの分岐=
            // 始端だけが範囲外で終端は文書内、という非ゼロ幅の入口を通す網。
            // 既存 ReplaceCharRange と同じ流儀であることも対照群で示す。
            using var exact = new EditorControl();
            using var plain = new EditorControl();
            exact.SetSource(TextBuffer.FromString("abcd"));
            plain.SetSource(TextBuffer.FromString("abcd"));

            exact.ReplaceCharRangeExact(-1, 3, "X");
            plain.ReplaceCharRange(-1, 3, "X");

            Assert.Equal("Xcd", exact.Text); // "ab" が消えて "cd" が残る
            Assert.Equal(exact.Text, plain.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_ZeroWidthInsideSurrogatePair_DoesNotSplitIt() =>
        Sta.Run(() =>
        {
            // ゼロ幅(純挿入)を外側へ広げると、断片「高位サロゲート + X + 低位サロゲート」を
            // 組んで書き戻すことになり、UTF-8 保存で両半身が U+FFFD へ潰れる。
            // 広げなければ委譲先が境界へスナップして挿入するだけなので絵文字は無傷。
            // 対照群として既存 ReplaceCharRange を並べる=この入力で新 API が既存 API を
            // 悪化させないことを固定する(悪化させていた退行の網)。
            using var exact = new EditorControl();
            using var plain = new EditorControl();
            exact.SetSource(TextBuffer.FromString("a\U0001F600b")); // "a😀b"
            plain.SetSource(TextBuffer.FromString("a\U0001F600b"));

            int next = exact.ReplaceCharRangeExact(2, 0, "X"); // 低位サロゲートの手前=ペアの内側へゼロ幅挿入
            plain.ReplaceCharRange(2, 0, "X");

            Assert.Equal("aX\U0001F600b", exact.Text); // 絵文字は無傷のまま前へ挿入される
            Assert.Equal(exact.Text, plain.Text); // 新 API = 既存 API(悪化していない)
            // 挿入点が 2 → 1 へ後退するので直後は 2。start + 1 = 3 は絵文字の中間を指してしまう。
            Assert.Equal(2, next);
            Assert.NotEqual(2 + "X".Length, next);
        });

    [Fact]
    public void ReplaceCharRangeExact_ZeroWidthInsideCrlf_DoesNotSplitIt() =>
        Sta.Run(() =>
        {
            // CRLF の内側へのゼロ幅挿入も同じ規則(広げない=割らない)。CR と LF の間に
            // 割り込むと 1 行が 2 行になるため、境界へスナップして CR の手前に挿入する。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            int next = ctrl.ReplaceCharRangeExact(4, 0, "X"); // CR と LF の間へゼロ幅挿入

            Assert.Equal("abcX\r\ndef", ctrl.Text);
            // 挿入点が 4 → 3 へ後退するので、置換文字列の直後は 4。
            Assert.Equal(4, next);
            // **なぜ戻り値が要るのか**の網: 呼び出し側が start + replacement.Length で
            // 導出すると 5 になり、挿入した "X" ではなく CR を飛び越した位置を指す。
            // ゼロ幅では始端自体が動くため、呼び出し側からは正しい値を組めない。
            Assert.NotEqual(4 + "X".Length, next);
        });

    [Fact]
    public void ReplaceCharRangeExact_ExistingReplaceCharRange_SwallowsTheWholeCrlf() =>
        Sta.Run(() =>
        {
            // 対照群: 既存 API の契約(巻き込む)が変わっていないことを同じ入力で示す。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRange(4, 1, "X");

            Assert.Equal("abcXdef", ctrl.Text); // CR ごと消える=既存契約
        });

    [Fact]
    public void ReplaceCharRangeExact_OnLogicalBoundary_MatchesReplaceCharRange() =>
        Sta.Run(() =>
        {
            // 委譲の恒等性: 両端が境界に乗っていれば既存 API と同結果でなければならない。
            using var a = new EditorControl();
            using var b = new EditorControl();
            a.SetSource(TextBuffer.FromString("abc\r\ndef"));
            b.SetSource(TextBuffer.FromString("abc\r\ndef"));

            a.ReplaceCharRangeExact(5, 3, "XY"); // "def" を置換(境界上)
            b.ReplaceCharRange(5, 3, "XY");

            Assert.Equal("abc\r\nXY", a.Text);
            Assert.Equal(a.Text, b.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_ClampsOutOfRangeArgs() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcd"));

            ctrl.ReplaceCharRangeExact(-3, 2, "X"); // 始端が負(終端 -3+2 = -1 も負)

            // 両端とも 0 へクランプ = [0,0) の純挿入。既存 ReplaceCharRange と同じ結果。
            Assert.Equal("Xabcd", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_LengthOverflow_DoesNotWrapToNegative() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcd"));

            ctrl.ReplaceCharRangeExact(2, int.MaxValue, "X"); // start + length が int を溢れる

            Assert.Equal("abX", ctrl.Text); // [2, CharLength) へクランプ(全文置換にならない)
        });

    [Fact]
    public void ReplaceCharRangeExact_StartBeyondEof_AppendsAtEnd() =>
        Sta.Run(() =>
        {
            // 始端の上側クランプ(0, CharLength)が効いていないと、続く終端クランプが
            // min > max になり Math.Clamp が ArgumentException を投げる。その保険。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcd"));

            ctrl.ReplaceCharRangeExact(9999, 5, "X"); // 始端・終端とも文書末尾より後ろ

            Assert.Equal("abcdX", ctrl.Text); // [CharLength, CharLength) の純挿入
        });

    [Fact]
    public void ReplaceCharRangeExact_ReadOnly_NoOp() =>
        Sta.Run(() =>
        {
            // 固定するのは観測可能な契約(ReadOnly なら文書が変わらない)であって、
            // 入口ガードの存在ではない。変異で確認済み: 本メソッドから "|| ReadOnly" を
            // 落としても委譲先 ReplaceCharRange の同じガードが効くため本テストは生存する。
            // 入口の ReadOnly 判定は「no-op のためにスナップショットを読まない」ための
            // 冗長な早期 return であり、単独ではテストで固定できない。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));
            ctrl.ReadOnly = true;

            int next = ctrl.ReplaceCharRangeExact(4, 1, "X");

            Assert.Equal("abc\r\ndef", ctrl.Text);
            // no-op でもクランプ済み始端(=位置は動いていない)を返す規約。番兵値は返さない
            // ので、戻り値だけでは置換の有無を判別できない(doc に明記済みの制約)。
            Assert.Equal(4, next);
        });

    [Fact]
    public void ReplaceCharRangeExact_IsOneUndoUnit() =>
        Sta.Run(() =>
        {
            // 巻き込み復元を「削除 + 挿入」の 2 手でやると Undo が 2 回必要になる。
            // 委譲によって 1 Undo 単位であることを固定する。
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRangeExact(4, 1, "X");
            ctrl.Undo();

            Assert.Equal("abc\r\ndef", ctrl.Text);
        });
}
