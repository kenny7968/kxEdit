using yEdit.Core.Buffers;
using yEdit.Core.Text;

namespace yEdit.Core.Editing;

/// <summary>
/// 文字クラス(単語境界判定用)。
/// - Whitespace: 半角空白(' ')/ タブ('\t')。全角空白(U+3000)は Other 扱い(NavigationCommands.SmartHome と同方針)。
/// - LineBreak: '\r' / '\n'。CRLF は連続 LineBreak として自然にまとまる。
/// - Latin: [A-Za-z_]。アンダースコアも識別子扱い。
/// - Digit: [0-9]。
/// - Hiragana / Katakana / Han: BMP 範囲のみ。拡張漢字 A/B..は Other 扱い。
/// - Other: 記号・拡張漢字・サロゲート・上記以外。
/// </summary>
internal enum CharClass
{
    Whitespace,
    LineBreak,
    Latin,
    Digit,
    Hiragana,
    Katakana,
    Han,
    Other,
}

/// <summary>
/// Ctrl+←→(単語ナビ)用の単語境界検出(純ロジック)。
/// Unicode カテゴリ大分類で文字種を 8 クラスに分類し、「同じ文字種の連続 = 1 単語」とする。
/// 空白(' ' '\t')・改行(CR / LF)はスキップ扱い(単語には含まない)。
/// </summary>
/// <remarks>
/// 前提違反時(caret が [0, CharLength] を外れる/サロゲート中間)は、TextSnapshot 側から
/// <see cref="ArgumentOutOfRangeException"/> が透過的に伝播する(NavigationCommands と同方針)。
/// EditorControl 側は SnapAndClamp で必ずスナップしてから呼ぶこと。
///
/// 2026-07-31: 内部の code-point 歩進は <see cref="Text.TextBoundary"/> の <c>*CodePoint*</c> 系へ
/// 移した(キャレット / UIA が使う <c>*LogicalChar*</c> 系ではない)。CodePoint 系は CRLF を
/// atomic に扱わず、CR と LF の間で止まる。
///
/// <b>ただしこの選択は現状テストで固定できない。</b> <c>ClassOf</c> が CR と LF を<b>同一の</b>
/// <c>CharClass.LineBreak</c> に写すため、内部歩進を LogicalChar 系へ丸ごと入れ替えても
/// 観測可能な差は出ない(2026-07-31 に網羅探索で確認=長さ 5 以下の全文字列 × 全キャレット位置で
/// 差分ゼロ)。LogicalChar が飛ばす CRLF の中間位置はどのループ述語の判定も変えず、
/// サロゲート側は両系が同じ述語を共有するため原理的に差が出ないから。
///
/// よって CodePoint 系を使うのは<b>予防的な措置</b>である。差が出るのは <c>ClassOf</c> が
/// CR と LF を別クラスとして扱うようになったとき=そのとき Ctrl+←→ の単語境界が変わるが、
/// <b>テストは赤くならない</b>。<c>ClassOf</c> の改変時はこの注意書きだけが防壁になる。
///
/// 2026-08-04: public API 4 本すべてに走査上限 <c>maxScan</c> を必須引数として足した。
/// 空白ゼロの長大行で 1 回の走査が行全体(500K 文字で約 1.4〜2.8 秒)を舐めるのを止めるため
/// (docs/plans/2026-08-03-uia-word-unit-design.md §2.4)。省略可能引数にしていないのは、
/// 上限なしを<b>明示的に選ばせる</b>ため(既定が無制限だと新しい呼び出しが黙って無制限になる)。
/// </remarks>
public static class WordBoundary
{
    /// <summary>
    /// 走査上限なしを表す番兵。<b>新しい本番呼び出しでこれを使ってはならない</b> —
    /// 上限なしの走査は空白ゼロ長大行で行全体を舐める(2026-08-03-uia-word-unit-design.md §2.4)。
    /// テストと、上限を意図的に外すことに理由がある場所だけが使う。
    /// </summary>
    public const int NoScanLimit = int.MaxValue;

    /// <summary>
    /// 単語走査の既定上限(code point 数)。1 回の呼び出しがこの歩数を超えて走らない。
    /// SR の読み上げスパン(<c>UiaTextHostAdapter</c>)と Ctrl+←→(<c>InputRouter</c>)の両方が使う。
    /// </summary>
    /// <remarks>
    /// 値の根拠は docs/plans/2026-08-04-uia-word-unit-fix.md Task 6 の実測。
    /// 上限に当たると単語の途中で切れる = SR は run の一部だけを読み、キャレットも run の
    /// 途中で止まる。これは「500K 文字を 1 単語として読ませない」ための意図的な打ち切りである。
    /// </remarks>
    public const int DefaultMaxScan = 256; // Task 6 で確定させる暫定値

    /// <summary>次の単語の先頭に進む。EOF に達したら CharLength を返す。</summary>
    /// <remarks>
    /// 動作:
    /// 1. caret が CharLength なら CharLength を返す(EOF)
    /// 2. 現在位置の class が Whitespace/LineBreak → その連続をスキップして到達位置を返す
    /// 3. 現在位置の class が非空白 → 同 class の連続をスキップ → その先の空白/改行連続もスキップして到達位置を返す
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="caret">走査開始位置。</param>
    /// <param name="maxScan">
    /// 1 呼び出しで進める最大 code point 数。上限に当たった位置で打ち切る(= 単語の途中で止まる)。
    /// 上限なしは <see cref="NoScanLimit"/>。本メソッドでは単語 run のスキップと空白 run のスキップを
    /// <b>通した合計予算</b>である(空白 run をまたいでも予算は合算で消費される)。
    /// <b>契約として <c>maxScan &gt;= 1</c> を要求する</b>(0 以下は未規定=正規化しない。0 は
    /// 1 歩も進まず、負値は <c>budget--</c> が unchecked で折り返して実質無制限になりうる)。
    /// </param>
    public static int NextWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        if (caret >= snap.CharLength)
            return snap.CharLength;
        int budget = maxScan;
        int pos = caret;
        var start = ClassOf(snap, pos);
        if (start == CharClass.Whitespace || start == CharClass.LineBreak)
        {
            // 空白/改行から始まる場合は連続をスキップ → 次の非空白の頭
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak,
                ref budget
            );
        }
        else
        {
            // 非空白 class の連続をスキップ → その先の空白/改行連続もスキップ
            pos = SkipForwardWhile(snap, pos, cls => cls == start, ref budget);
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak,
                ref budget
            );
        }
        return pos;
    }

    /// <summary>前の単語の先頭に戻る。BOF に達したら 0 を返す。</summary>
    /// <remarks>
    /// 動作:
    /// 1. caret が 0 なら 0 を返す
    /// 2. 1 code-point 左へ移動(サロゲート考慮)
    /// 3. 空白/改行の後方連続をスキップ(pos が非空白 class に到達するまで左へ)
    /// 4. その class の後方連続をスキップ(左隣が同 class の間、左へ)
    /// 5. 到達位置を返す
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="caret">走査開始位置。</param>
    /// <param name="maxScan">
    /// 1 呼び出しで進める最大 code point 数。上限に当たった位置で打ち切る(= 単語の途中で止まる)。
    /// 上限なしは <see cref="NoScanLimit"/>。手順 2 の<b>最初の 1 歩も予算に数える</b>ので、
    /// <c>maxScan</c> が 1 なら 1 code point 左へ移動した位置がそのまま返る。空白 run のスキップと
    /// 単語 run のスキップを通した合計予算である(手順 3 で予算を使い切ると手順 4 は 1 歩も進まない)。
    /// <b>契約として <c>maxScan &gt;= 1</c> を要求する</b>(0 以下は未規定=正規化しない。0 でも
    /// 手順 2 の 1 歩だけは進む非対称があり、負値は <c>budget--</c> が unchecked で折り返して
    /// 実質無制限になりうる)。
    /// </param>
    public static int PrevWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        if (caret <= 0)
            return 0;
        int budget = maxScan;
        int pos = TextBoundary.PrevCodePoint(snap, caret);
        budget--;
        // 左隣を空白/改行としてスキップ(後方=空白の直前まで)
        while (budget > 0 && pos > 0)
        {
            var cls = ClassOf(snap, pos);
            if (cls != CharClass.Whitespace && cls != CharClass.LineBreak)
                break;
            pos = TextBoundary.PrevCodePoint(snap, pos);
            budget--;
        }
        // 位置 pos の class を単語 class として、その連続をさらに左へ
        var wordCls = ClassOf(snap, pos);
        while (budget > 0 && pos > 0)
        {
            int prev = TextBoundary.PrevCodePoint(snap, pos);
            if (ClassOf(snap, prev) != wordCls)
                break;
            pos = prev;
            budget--;
        }
        return pos;
    }

    /// <summary>
    /// <paramref name="pos"/> を含む単語の左端。ダブルクリック単語選択と SR の読み上げスパンが共有する。
    /// </summary>
    /// <remarks>
    /// <b>規則は <see cref="PrevWordStart"/> の組み合わせで表現される</b>(新しい規則を発明しない)。
    /// <c>pos + 1</c> を渡すことで「pos 自身を含むクラス連続の左端」になる。
    /// 2026-08-04 に <c>InputRouter.PrevWordBoundary</c> から bit-perfect 移設した。
    ///
    /// pos が空白の上にあるときは左の空白 run を越えて<b>前の単語の頭</b>を返す(= スパンが
    /// キャレットを含まない)。これは移設元からの現行仕様で、
    /// <c>MouseInputTests.DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun</c> が固定している。
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="pos">単語の左端を求めたい位置。</param>
    /// <param name="maxScan">
    /// 1 呼び出しで進める最大 code point 数(<see cref="PrevWordStart"/> にそのまま渡る)。
    /// 上限に当たった位置で打ち切る(= 単語の途中で止まる)。上限なしは <see cref="NoScanLimit"/>。
    /// <b><paramref name="pos"/> 基準の窓は左側だけ 1 狭い</b> = 下限は
    /// <c>pos - (maxScan - 1)</c> である(<see cref="PrevWordStart"/> に <c>pos + 1</c> を渡すため
    /// 最初の 1 歩が pos へ戻るのに消費される)。右側の <see cref="WordEnd"/> は
    /// <c>pos + maxScan</c> まで走るので<b>両者は非対称</b>。cap の較正時にこの 1 のズレが効く。
    /// <b>契約として <c>maxScan &gt;= 1</c> を要求する</b>(0 以下は未規定=正規化しない)。
    /// </param>
    public static int WordStart(TextSnapshot snap, int pos, int maxScan)
    {
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return PrevWordStart(snap, pos, maxScan);
        return PrevWordStart(snap, pos + 1, maxScan);
    }

    /// <summary>
    /// <paramref name="pos"/> の word run の終端。末尾の空白は含めない。
    /// </summary>
    /// <remarks>
    /// <see cref="NextWordStart"/> は「単語末尾 + 空白列をスキップして次単語の頭」を返すため、
    /// 返り値から左へ戻して空白/改行以外の最初の位置を求める。後方スキャンは
    /// <c>nextWordStart &gt; pos</c> でガードするので pos より左には決して戻らない。
    /// 2026-08-04 に <c>InputRouter.NextWordBoundary</c> から bit-perfect 移設した。
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="pos">word run の終端を求めたい位置。</param>
    /// <param name="maxScan">
    /// 1 呼び出しで進める最大 code point 数(<see cref="NextWordStart"/> にそのまま渡る)。
    /// 上限に当たった位置で打ち切る(= 単語の途中で止まる)。上限なしは <see cref="NoScanLimit"/>。
    /// 末尾空白の巻き戻しは <see cref="NextWordStart"/> が進んだ範囲の内側でしか動かないため、
    /// 追加の予算を消費しない。上限は <c>pos + maxScan</c>(<see cref="WordStart"/> 側の下限は
    /// <c>pos - (maxScan - 1)</c> で 1 狭い=非対称)。
    /// <b>契約として <c>maxScan &gt;= 1</c> を要求する</b>(0 以下は未規定=正規化しない)。
    /// </param>
    public static int WordEnd(TextSnapshot snap, int pos, int maxScan)
    {
        if (pos >= snap.CharLength)
            return snap.CharLength;
        int nextWordStart = NextWordStart(snap, pos, maxScan);
        while (nextWordStart > pos)
        {
            char c = snap.GetChar(nextWordStart - 1);
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                break;
            nextWordStart--;
        }
        return nextWordStart;
    }

    // ===== ヘルパ =====

    private static CharClass ClassOf(TextSnapshot snap, int pos)
    {
        if (pos >= snap.CharLength)
            return CharClass.Other;
        char c = snap.GetChar(pos);
        if (c == '\r' || c == '\n')
            return CharClass.LineBreak;
        if (c == ' ' || c == '\t')
            return CharClass.Whitespace;
        if (c >= '0' && c <= '9')
            return CharClass.Digit;
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_')
            return CharClass.Latin;
        if (c >= 0x3040 && c <= 0x309F)
            return CharClass.Hiragana;
        if (c >= 0x30A0 && c <= 0x30FF)
            return CharClass.Katakana;
        if (c >= 0x4E00 && c <= 0x9FFF)
            return CharClass.Han;
        return CharClass.Other;
    }

    /// <summary>pred が真の間、code-point 単位で右へ進む。予算 <paramref name="budget"/> を消費する。</summary>
    private static int SkipForwardWhile(
        TextSnapshot snap,
        int pos,
        Func<CharClass, bool> pred,
        ref int budget
    )
    {
        while (budget > 0 && pos < snap.CharLength && pred(ClassOf(snap, pos)))
        {
            pos = TextBoundary.NextCodePoint(snap, pos);
            budget--;
        }
        return pos;
    }
}
