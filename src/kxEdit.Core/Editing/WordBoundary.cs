using System.Diagnostics;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;

namespace kxEdit.Core.Editing;

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
///
/// <c>maxScan</c> は 1 呼び出し全体の予算で、単語 run と空白 run を<b>またいでも合算</b>で
/// 消費される。契約は <c>maxScan &gt;= 1</c>(各 API 冒頭の <c>Debug.Assert</c> で検証)。
/// 上限なしは <see cref="NoScanLimit"/>。<b>予算を使い切ったらその位置でそのまま返す</b>
/// (単語の途中でも切る=SR は run の一部だけを読み、キャレットも run の途中で止まる)。
/// 各 API が触れる窓(<b>単位は code point 数</b>。下の表の <c>maxScan</c> も同じ):
///
/// <list type="table">
/// <listheader><term>API</term><description>窓</description></listheader>
/// <item><term><see cref="NextWordStart"/>(caret)</term>
///   <description><c>[caret, caret + maxScan]</c></description></item>
/// <item><term><see cref="PrevWordStart"/>(caret)</term>
///   <description><c>[caret - maxScan, caret]</c></description></item>
/// <item><term><see cref="WordStart"/>(pos)</term>
///   <description><c>[pos - (maxScan - 1), pos]</c> ← <b>左だけ 1 狭い</b></description></item>
/// <item><term><see cref="WordEnd"/>(pos)</term>
///   <description><c>[pos, pos + maxScan]</c></description></item>
/// </list>
///
/// <see cref="WordStart"/> だけ 1 狭いのは <see cref="PrevWordStart"/> へ <c>pos + 1</c> を渡すため=
/// 最初の 1 歩が pos へ戻るのに消費される。cap の較正時にこの 1 のズレが効く。
///
/// <b>窓についてよくある誤読 2 つは、この節が正本である。</b>
/// <c>TextRangeProviderV2</c> / <c>IUiaTextHost.WordStart</c> / <c>UiaWordUnitExpandTests</c> /
/// <c>EditorControlUiaHostTests</c> は 1 行でここを参照するだけにしてある(2026-08-04 の
/// 最終レビュー Minor-3: 同じ説明が 4〜5 箇所へ写されていて、規則が変わったとき
/// 食い違う形になっていた)。規則を変えるときはここを直すこと。
///
/// <b>(1) 窓の単位は code point 数であって char オフセットではない。</b> 引数 <c>pos</c> /
/// <c>caret</c> と返り値は char オフセットなので、非 BMP(サロゲートペア)の run では
/// <b>char オフセットでの窓幅が最大 2 倍になる</b>。したがって
/// <c>WordStart(pos)</c>〜<c>WordEnd(pos)</c> のスパンは、ASCII / BMP で
/// <c>2 * maxScan - 1</c> char、非 BMP 一色で最大 <c>4 * maxScan - 2</c> char まで伸びる
/// (実測: 絵文字 run・<c>maxScan = 256</c> で 1022 char)。cap の較正で
/// 「SR が 1 回に読む最大文字数 = 2 × cap」と読まないこと — 頭打ちになるのは
/// <b>走査回数</b>であって、スパンの char 幅ではない。
///
/// <b>(2)「窓がキャレット中心」なのは「走査」であって、スパンの包含ではない。</b>
/// <see cref="WordStart"/>(pos) と <see cref="WordEnd"/>(pos) へ<b>同じ pos</b> を渡せば
/// 走査の窓は <c>[pos - (maxScan - 1), pos + maxScan]</c> とキャレットを中心に据わる。
/// だが返るスパンが pos の文字を含むとは限らない: pos が空白 run の上にあると
/// <see cref="WordStart"/> は左の空白 run を越えて前の単語の頭まで戻り、
/// <see cref="WordEnd"/> は pos をそのまま返すので、スパン <c>[start, pos)</c> は
/// <b>キャレット位置の文字を含まない</b>(<c>"ab    cd"</c> の pos=4 → <c>"ab  "</c> が実例)。
/// 担保されるのは <c>WordStart(pos) &lt;= pos &lt;= WordEnd(pos)</c> まで=
/// <b>反転レンジは出さない</b>ことである。
///
/// 上限に当たったかどうかは呼び出し側が <c>end - pos == maxScan</c> で判定できる
/// (誤検出は run 長がちょうど cap のときのみ)。ゆえに <c>out bool truncated</c> は足さない。
///
/// 将来オーバーロードを足す場合、既定値は <see cref="NoScanLimit"/> ではなく
/// <see cref="DefaultMaxScan"/> 側にすること。<b>引数を省略可能にすると、新しい呼び出しが
/// 黙って上限なしになる</b>のを止められなくなる。「本番経路に <c>NoScanLimit</c> が
/// 残っていない」ことは <c>rg -n "NoScanLimit" src/ -g '!**/WordBoundary.cs'</c> が
/// 空になることで確認する(定義ファイル自身の <c>const</c> 宣言と xmldoc の参照を除外しないと、
/// 修正が正しく入っていても<b>このファイルの doc だけで数 hit して空にならない</b>)。
/// ただし<b>これは自動ゲートではない</b> — <c>tools/</c> にも <c>.github/</c> にも
/// この検査は無い。実体は docs/plans/2026-08-04-uia-word-unit-fix.md の
/// 完了条件(DoD)にある手動チェック 1 行である。
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
    /// SR の読み上げスパン(<c>UiaTextHostAdapter</c>)・Ctrl+←→ / ダブルクリック単語選択
    /// (<c>InputRouter</c>)の 3 経路が<b>同じ値</b>を渡す(片方だけ広げると F-3 の再導入になる)。
    /// </summary>
    /// <remarks>
    /// <b>名前の <c>Default</c> は「経路ごとに変えてよい既定値」の意味ではない。</b>
    /// 上の 3 経路は<b>必ず同じ値</b>を渡さなければならず、片方だけ別の cap にすることは
    /// 許されない(同じ位置で「目に見える選択」と「SR が聞くスパン」が食い違う=
    /// このブランチが直した F-3 の再導入になる)。「本番経路に別 cap を渡す呼び出しを
    /// 足さない」ことがこの定数の契約である。<c>Default</c> なのは、将来
    /// 「意図的に上限を外す / 変える理由がある特殊な呼び出し」が現れたときに
    /// <see cref="NoScanLimit"/> や明示値を渡す余地を残しているという意味に留まる。
    /// 2026-08-04 の最終レビュー Minor-6 で改名も検討したが、実装計画・設計書・テストを
    /// 巻き込む churn に見合わないと判断して<b>この注記で受容した</b>。
    ///
    /// <b>128 は 2026-08-04 の実測から選び、ユーザー承認を得て確定した値</b>である。採取は
    /// <c>tests/kxEdit.Editor.Smoke --wordunit</c>(<c>WordUnitBench</c>)、経緯と生データは
    /// docs/plans/2026-08-04-uia-word-unit-fix.md Task 6。
    ///
    /// <b>単語らしさ側</b>: リポジトリ内の実ファイル 6 本(日本語散文 2・C# コード・yml・
    /// csproj・README)で最長のクラス run は <b>57 code point</b> = 設計書中に引用された C# 識別子
    /// <c>DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun</c>。128 はその約 2.2 倍で、
    /// 6 本すべてで切り詰め 0 件だった(64 でも 0 件だが最長 run に対する余裕が 12% しかない)。
    ///
    /// <b>速度側</b>: 空白ゼロ・単一クラスの 500K 行で expand 1 回(<see cref="WordStart"/> +
    /// <see cref="WordEnd"/>)が最悪位相 1.28 ms / 典型位相 0.06 ms、Ctrl+← 1 回
    /// (<see cref="PrevWordStart"/>)が最悪位相 0.67 ms。上限なしだと同じ操作が 2,785 ms かかる。
    ///
    /// <b>上の ms は「ファイル読み込み直後のバッファ」の値で、編集中のバッファを代表していない。</b>
    /// <c>AppendBuffer</c> 由来 piece(大きめの貼り付け直後など)では <c>TextChunk.CharToByte</c> の
    /// 線形走査が効いて同じ expand 1 回が<b>約 17 倍の 22.5 ms</b> になる(2026-08-04 最終レビュー
    /// 脆弱性パス V-3 の実測)。同条件の上限なしは 3,101 ms なので、これは本 cap による悪化ではなく
    /// <b>138 倍の改善</b>である。ブロック長 64KB で頭打ちになるため、行長に比例して伸び続けはしない。
    ///
    /// <b>現実のテキストでは cap はそもそも効かない。</b> クラス境界が数文字ごとに来るので
    /// 走査は cap に達する前に止まる(ベンチの jamix 行が cap 32〜4096 で ms もスパン幅も
    /// 不変であることで実証)。cap が効くのは空白ゼロ単一クラス長大行という病的条件だけである。
    ///
    /// <b>これ以上大きくしない理由</b>: 病的行で SR が 1 単語として読む長さは
    /// <c>2 * cap - 1</c> code point になる。128 なら 255 で、256 にすると 511 = 発話が倍長くなる。
    ///
    /// <b>許容範囲は [64, 256]</b>。<c>WordBoundaryTests.DefaultMaxScan_StaysWithinCalibratedRange</c>
    /// がこの範囲を固定している(値そのものではなく上の 2 つの推論を符号化したもの。
    /// <c>Assert.Equal(128, …)</c> は定数のミラーで無価値なので置かない)。下限 64 は
    /// <b>単語らしさ側</b>の帰結=実測した現実のテキストの最長クラス run が 57 code point で、
    /// 64 を下回ると普通の文章で切り詰めが起きはじめる。上限 256 は<b>速度側 / 発話長</b>の帰結=
    /// 256 で発話が <c>2 * cap - 1 = 511</c> code point になり、これ以上は病的行での
    /// 1 単語が長すぎる。範囲を動かすなら、先に <c>--wordunit</c> で採り直して
    /// この 2 つの根拠を書き換えること。
    ///
    /// 上限に当たると単語の途中で切れる = SR は run の一部だけを読み、キャレットも run の
    /// 途中で止まる。これは「500K 文字を 1 単語として読ませない」ための意図的な打ち切りである。
    ///
    /// <b>ms の絶対値は参考値</b>(同一マシンでも負荷で 2 倍振れる)。壁時計は cap に単純比例せず
    /// <c>TextChunk</c> の格子内走査の<b>位相</b>で数倍振れる(掃引して決めたのはこのため)ので、
    /// 主張は倍率と「何に比例するか」の側に置くこと。
    /// </remarks>
    public const int DefaultMaxScan = 128;

    /// <summary>
    /// <c>maxScan &gt;= 1</c> 契約違反の <c>Debug.Assert</c> メッセージ。
    /// </summary>
    /// <remarks>
    /// 3 引数版の <c>Debug.Assert</c> を使うのは、2 引数版の message が
    /// <c>[CallerArgumentExpression]</c> 付きで明示指定が S3236 になるため
    /// (<c>TextSnapshot.DecodeUtf16At</c> と同じ流儀)。
    /// </remarks>
    private const string MaxScanContract =
        "maxScan は 1 以上でなければならない(0 以下は未規定=正規化しない)";

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
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。
    /// 上限なしは <see cref="NoScanLimit"/>。
    /// </param>
    public static int NextWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
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
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。手順 2 の
    /// <b>最初の 1 歩も予算に数える</b>。上限なしは <see cref="NoScanLimit"/>。
    /// </param>
    public static int PrevWordStart(TextSnapshot snap, int caret, int maxScan)
    {
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (caret <= 0)
            return 0;
        // 手順 2(最初の 1 歩)ぶんを先に引いた予算。`budget = maxScan; budget--;` と書くと
        // maxScan == int.MinValue のとき unchecked で int.MaxValue へ化けて上限が消える
        // (実測: 'a' x 200,000 の caret=100,000 が 0 を返して 964 ms。cap=128 なら 0.5 ms)。
        // 上限の導入自体が DoS 対策なので、特定の入力値でそれが無効化される形は残さない。
        // maxScan >= 1 では maxScan - 1 と完全に等価・0 以下はすべて「1 歩も走らない」に収束する
        // (2026-08-04 最終レビュー 脆弱性パス V-1)。
        int budget = maxScan > 0 ? maxScan - 1 : 0;
        int pos = TextBoundary.PrevCodePoint(snap, caret);
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
    /// 3 分岐の意味:
    /// <list type="number">
    /// <item><c>pos &lt;= 0</c>: 0。</item>
    /// <item><c>pos &gt;= CharLength</c>(EOF): <see cref="PrevWordStart"/>(pos) に委譲=
    /// 末尾が空白なら空白を左スキップして直前の単語まで戻る=末尾に近い単語の頭を返す。
    /// <c>pos + 1</c> を足さないのは、EOF 位置に「pos 自身の文字」が無いため。</item>
    /// <item>それ以外: <see cref="PrevWordStart"/>(pos + 1) を呼ぶことで
    /// 「pos 自身を含むクラス連続の左端」を得る。</item>
    /// </list>
    /// 2026-08-04 に <c>InputRouter.PrevWordBoundary</c> から bit-perfect 移設した
    /// (移設元の xmldoc は Task 2 で原本ごと消えるため、3 分岐の説明をここへ引き継いでいる。
    /// その原本は <c>EditorControl.Input.cs</c> から責務分離 Phase 3 Task 3c で InputRouter へ
    /// 移されたもの。以後この 1 本が唯一の定義)。
    ///
    /// pos が空白の上にあるときは左の空白 run を越えて<b>前の単語の頭</b>を返す(= スパンが
    /// キャレットを含まない)。これは移設元からの現行仕様で、
    /// <c>MouseInputTests.DoubleClick_OnWhitespace_SelectsPrevWordPlusWhitespaceRun</c> が固定している。
    /// 詳細はクラス <c>&lt;remarks&gt;</c> の「窓についてよくある誤読」(2) を参照。
    ///
    /// <b>窓が左だけ 1 狭い(<c>[pos - (maxScan - 1), pos]</c>)のを対称化しない理由</b>
    /// (2026-08-04 Task 1 品質レビューで次の 2 案とも棄却した。最終レビュー Minor-7 で記録):
    /// <list type="number">
    /// <item>内部で <c>PrevWordStart(snap, pos + 1, maxScan + 1)</c> を渡す案 →
    /// <see cref="NoScanLimit"/> == <c>int.MaxValue</c> のとき <c>maxScan + 1</c> が
    /// <c>int.MinValue</c> へオーバーフローする。</item>
    /// <item><see cref="PrevWordStart"/> の最初の 1 歩を無料にする案 → 今度は
    /// <see cref="PrevWordStart"/> 自身が <c>maxScan + 1</c> 歩戻るようになり、
    /// <see cref="NextWordStart"/> との対称が壊れる。Ctrl+← / Ctrl+→ は同じ cap で交互に
    /// 使われるので、<b>そちらの対称の方が観測されやすい</b>。</item>
    /// </list>
    /// よって 1 のズレは受容し、cap の較正時に勘定へ入れる(クラス <c>&lt;remarks&gt;</c> の窓の表)。
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="pos">
    /// 単語の左端を求めたい位置。契約は <c>[0, CharLength]</c> で、<b>正規化は呼び出し側の責務</b>。
    /// <c>pos &gt; CharLength</c> は契約違反=クラス <c>&lt;remarks&gt;</c> の前提どおり
    /// <c>TextSnapshot</c> 側から <see cref="ArgumentOutOfRangeException"/> が透過する
    /// (<c>UiaTextHostAdapter</c> は <c>Math.Clamp(offset, 0, snap.CharLength)</c> してから呼ぶ)。
    /// <b><see cref="WordEnd"/> とは非対称</b>: あちらは <c>pos &gt;= CharLength</c> を
    /// CharLength で受け止めるので投げない。上限側だけを見て「クランプ不要」と判断しないこと。
    /// </param>
    /// <param name="maxScan">
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表=<b>左だけ 1 狭い</b>)。
    /// 上限なしは <see cref="NoScanLimit"/>。
    /// </param>
    public static int WordStart(TextSnapshot snap, int pos, int maxScan)
    {
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
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
    /// <see cref="NextWordStart"/> は<b>Ctrl+→ 用に</b>「単語末尾 + 空白列をスキップして次単語の頭」を
    /// 返す設計。ダブルクリック単語選択と SR の読み上げスパンでは<b>末尾の空白を含めたくない</b>ため、
    /// 返り値から左へ戻して空白/改行以外の最初の位置を求める。後方スキャンは
    /// <c>nextWordStart &gt; pos</c> でガードするので pos より左には決して戻らない
    /// (= <c>WordEnd(pos) &gt;= pos</c> が常に成り立つ。これを破ると
    /// <c>TextRangeProviderV2.ExpandToEnclosingUnit</c> が未対応の反転レンジを UIA へ出す。
    /// <c>WordEnd_OnWhitespaceRun_NeverReturnsBeforePos</c> が固定している)。
    /// 2026-08-04 に <c>InputRouter.NextWordBoundary</c> から bit-perfect 移設した
    /// (その原本は <c>EditorControl.Input.cs</c> から責務分離 Phase 3 Task 3c で InputRouter へ
    /// 移されたもの。以後この 1 本が唯一の定義)。
    ///
    /// 巻き戻しの空白判定は <c>ClassOf</c> に寄せてある(literal の空白集合を第 2 の定義として
    /// 持たない)。将来 U+3000 を <see cref="CharClass.Whitespace"/> へ移した場合に、
    /// <see cref="NextWordStart"/> はスキップするのに本メソッドは削らない、という食い違いを防ぐため。
    /// </remarks>
    /// <param name="snap">走査対象のスナップショット。</param>
    /// <param name="pos">word run の終端を求めたい位置。</param>
    /// <param name="maxScan">
    /// 走査上限(契約 <c>&gt;= 1</c>・窓はクラス <c>&lt;remarks&gt;</c> の表)。末尾空白の巻き戻しは
    /// <see cref="NextWordStart"/> が進んだ範囲の内側でしか動かないため追加の予算を消費しない。
    /// 上限なしは <see cref="NoScanLimit"/>。
    /// </param>
    public static int WordEnd(TextSnapshot snap, int pos, int maxScan)
    {
        Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));
        if (pos >= snap.CharLength)
            return snap.CharLength;
        int nextWordStart = NextWordStart(snap, pos, maxScan);
        while (nextWordStart > pos)
        {
            if (
                ClassOf(snap, nextWordStart - 1)
                is not (CharClass.Whitespace or CharClass.LineBreak)
            )
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
