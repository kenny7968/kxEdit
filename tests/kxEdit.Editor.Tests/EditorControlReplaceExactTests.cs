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

            ctrl.ReplaceCharRangeExact(4, 1, "X"); // LF だけを置換

            Assert.Equal("abc\rXdef", ctrl.Text);
        });

    [Fact]
    public void ReplaceCharRangeExact_CrOfCrlf_KeepsLf() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef"));

            ctrl.ReplaceCharRangeExact(3, 1, "X"); // CR だけを置換

            Assert.Equal("abcX\ndef", ctrl.Text);
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

            ctrl.ReplaceCharRangeExact(4, 1, "X");

            Assert.Equal("abc\r\ndef", ctrl.Text);
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
