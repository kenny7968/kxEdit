using kxEdit.Core.Buffers;

namespace kxEdit.Core.Text;

/// <summary>
/// 文字境界の判定・歩進を 1 箇所に集約する純ロジック(2026-07-31 新設)。
///
/// <b>このクラスが文字境界規則のオーナー</b>: 呼び出し元で <c>char.IsHighSurrogate</c> や
/// <c>'\r'</c> + <c>'\n'</c> の並び判定を手書きしないこと。2026-07-31 に 15 ファイル・22 箇所へ
/// 散っていた手書き判定をここへ寄せた(うち 1 件は Task 6 のセルフレビューで見つけた棚卸し漏れ)。
/// 規則を変えるときに直す場所が
/// <b>本ファイルの境界述語 6 つ(snapshot 4 + span 2)と、下に挙げる例外 1 つだけ</b>で済む
/// 状態を保つこと。
/// 述語は「前進 / 後退」×「サロゲート / CRLF」の 2×2 で、片方向だけ直して逆方向を落とす失敗
/// (2026-07-24 に実際に起きた)を欠けが見える形で防ぐためにこの構成にしている:
/// <list type="bullet">
/// <item>前進: <see cref="IsSurrogatePairStartingAt"/> / <see cref="IsCrlfStartingAt"/></item>
/// <item>後退・スナップ: <see cref="IsSurrogatePairEndingAt(TextSnapshot, int, char)"/> /
/// <see cref="IsCrlfEndingAt(TextSnapshot, int, char)"/>
/// (span 版: <see cref="IsSurrogatePairEndingAt(ReadOnlySpan{char}, int, char)"/> /
/// <see cref="IsCrlfEndingAt(ReadOnlySpan{char}, int, char)"/>)</item>
/// </list>
/// <b>述語を 1 つでもインライン展開して登録簿から外さないこと。</b> 単一利用でも述語のまま残す
/// (登録簿は完全でなければ「ここだけ直せばよい」という宣言そのものが罠になる)。
/// <b>公開しているのは <see cref="IsSurrogatePairEndingAt(TextSnapshot, int)"/> 1 本だけ</b>
/// (2026-09-01 B2 Task 3)= <c>Snap*</c> が<b>なぜ</b>動いたかを呼び出し側が事後に弁別するための
/// 窓口で、範囲ガードを足して同名 private 版へ委譲するだけ。<b>規則の定義は 6 つのまま</b>
/// (公開版は規則を持たない)。<b>弁別を呼び出し側で手書きさせないための公開</b>である=
/// 手書きされた瞬間に規則がこのクラスの外へ二重化し、この登録簿が嘘になる。
/// <b>span 版も述語を持つ</b>(2026-09-01 B2)。<c>TextSnapshot</c> 版と読み口が違うので
/// <c>snap.GetChar</c> ↔ indexer の間では述語を共有できないが、<b>span ↔ span は共有できる</b>=
/// 後退・スナップ側の 2 規則は
/// <see cref="IsSurrogatePairEndingAt(ReadOnlySpan{char}, int, char)"/> /
/// <see cref="IsCrlfEndingAt(ReadOnlySpan{char}, int, char)"/> に括ってあり、
/// <see cref="SnapToCodePointStart"/> と
/// <see cref="SnapToLogicalCharStart(ReadOnlySpan{char}, int)"/> はこれを呼ぶだけ。
/// <b>残る例外は <see cref="CodePointLengthAt(ReadOnlySpan{char}, int)"/> の前進判定 1 箇所</b>
/// (span 側で前進規則を要るのがここだけなので述語化していない。span 版で前進側を触るコードが
/// もう 1 つ増えたら、そのとき述語へ括ること)。
/// span 版述語は<b>対応する snapshot 版の直後に置く</b>: 離すと Sonar <c>S4136</c> で落ちる。
/// 規則が snapshot / span の 2 実装に分かれる分の drift は、
/// <b>射程の違う全数テスト 2 本</b>(snapshot 版との同値 + snapshot を通さない事後条件)で防ぐ。
/// <b>片方だけでは穴が開く</b>=どちらの網がどの変異を殺すかの実測は
/// <see cref="SnapToLogicalCharStart(ReadOnlySpan{char}, int)"/> の remarks に書いてある。
///
/// <b>本登録簿の適用範囲</b>: <c>TextSnapshot</c> 上 / span 上を<b>キャレット・選択・描画単位で
/// 歩く</b>経路に限る(2026-09-01 B2 まで span 側は「行内」に限ると書いていたが、
/// <c>Core.Search</c> が材質化した本文 string もここへ乗せたので限定を外した)。
/// バイト層(<c>TextChunk</c> / <c>Utf8Scan</c>)と、EOL 変換・整形・計数系
/// (Rune ベース)は対象外。<c>rg IsHighSurrogate src</c> が次にヒットするのは
/// <b>seam の破れではなく意図的な除外</b>(<b>5 ファイル・6 箇所</b>。設計書 §3 は 4 ファイル・
/// 5 箇所だったが、2026-09-01 B2 Task 3 の棚卸しで <c>EditorControl.Input</c> の
/// 登録漏れが 1 件見つかった):
/// <list type="bullet">
/// <item><c>Text.KinsokuFormatter</c>(禁則整形・2 箇所)/ <c>Text.SanitizeForDisplay</c>
/// (表示用サニタイズ)= Rune ベースで流儀が違う</item>
/// <item><c>Documents.CharacterCounter</c> = 文字数の数え上げ(境界歩進ではない)</item>
/// <item><c>kxEdit.App.GrepResultsWindow</c> = 表示文字列の切り詰め</item>
/// <item><c>kxEdit.Editor.EditorControl.Input</c> の <c>OnKeyPress</c> = WM_CHAR で
/// <b>1 文字ずつ届く</b>サロゲートの組み立て(A-20)。判定対象は本文中の位置ではなく
/// 入力イベントの単独 char なので、本クラスの述語(位置と隣接 char を読む)では表現できない</item>
/// </list>
/// <b><c>rg IsLowSurrogate src</c> も同じ集合に収まること</b>(上記のうち
/// <c>KinsokuFormatter</c> / <c>CharacterCounter</c> / <c>EditorControl.Input</c> がヒットする)。
/// 旧記述は <c>IsHighSurrogate</c> だけを棚卸しの網にしていたが、
/// <b>後退・スナップ側の弁別は <c>IsLowSurrogate</c> で書ける</b>ので片方だけでは漏れる
/// (2026-09-01 B2 Task 3 に <c>EditorControl</c> で実際に漏れた)。
/// CRLF 隣接判定も同様に、境界歩進でないものは対象外:
/// <see cref="Buffers.TextSnapshot"/> の <c>GetLineEnd</c> / <c>CountCrlfPairs</c>
/// (加えて前者は本クラスが依存する<b>下位層</b>=寄せると循環参照になる)、
/// <c>UiaTextHostAdapter.LineEndNoBreakOf</c>(行末の改行剥がし)、
/// <c>EditorControl</c> の EOL 変換(ストリーム変換の carry 処理)。
///
/// 典拠: docs/plans/2026-07-31-char-access-seam-design.md §4.1。
/// ただし <c>docs/plans/</c> は策定時スナップショット(CLAUDE.md §8)なので、
/// <b>「現在」の登録簿は本クラス doc が唯一の正</b>。
///
/// <b>2 つの単位を意図的に分けている</b>:
/// <list type="bullet">
/// <item><b>コードポイント単位</b>(<c>*CodePoint*</c>)= サロゲートペアのみ atomic。
/// CR と LF は別々に数える。<see cref="Editing.WordBoundary"/> の内部歩進と
/// Layout / 描画がこちらを使う。</item>
/// <item><b>論理文字単位</b>(<c>*LogicalChar*</c>)= サロゲートペア + CRLF pair が atomic。
/// キャレット / 選択 / UIA(SR の文字単位読み)がこちらを使う
/// (2026-07-24 CRLF atomic caret 設計)。</item>
/// </list>
/// <b>この 2 つを 1 本に統一してはならない。</b> ただし<b>この不変条件はテストで固定できない</b>=
/// <see cref="Editing.WordBoundary"/> の <c>ClassOf</c> が CR と LF を<b>同一の</b>
/// <c>CharClass.LineBreak</c> に写すため、同クラスの内部歩進を CodePoint 系から LogicalChar 系へ
/// 入れ替えても観測可能な差は出ない(2026-07-31 に網羅探索で確認=長さ 5 以下の全文字列 ×
/// 全キャレット位置で差分ゼロ)。LogicalChar が飛ばす CRLF の中間位置はどのループ述語の判定も
/// 変えず、サロゲート側は両系が同じ述語を共有するため原理的に差が出ないから。
/// よって系を分けているのは<b>予防的な措置</b>である: <c>ClassOf</c> が CR と LF を別クラスとして
/// 扱うようになった瞬間に差が出て Ctrl+←→ の単語境界が変わるが、<b>そのときテストは赤くならない</b>。
///
/// 置き場が <c>kxEdit.Core.Text</c> なのは、<c>Core.Editing</c> と <c>Core.Layout</c> の
/// 双方から参照される葉である必要があるため(現状 Editing → Layout の依存があり、
/// 逆向きを足すと循環に見える)。
///
/// <c>TextSnapshot</c> を受ける版は文書全体を、<c>ReadOnlySpan&lt;char&gt;</c> を受ける版は
/// <c>TextSnapshot</c> を持たない呼び出し側(Layout / 描画の行内テキスト・
/// <c>Core.Search</c> が材質化した本文 string)を対象とする。
/// <b>CRLF を見るかどうかは span / snapshot の別ではなく「コードポイント単位か論理文字単位か」で
/// 決まる</b>: <see cref="SnapToCodePointStart"/> はサロゲートだけを見るが、
/// <see cref="SnapToLogicalCharStart(ReadOnlySpan{char}, int)"/> は CRLF も 1 論理文字として見る
/// (2026-09-01 B2: 行内テキスト専用という旧宣言はここで失効した)。
///
/// <b>範囲外入力の契約</b>(置き換え元 <see cref="Editing.NavigationCommands"/> の
/// <c>&lt;remarks&gt;</c> を引き継ぐ)。<b>クランプで隠さず throw させる</b>のが方針=
/// 呼び出し側のスナップ漏れを早期に露出させるため。家族ごとに非対称なのは、
/// それぞれ「進めない側」だけを no-op として許容しているから(<c>Next*</c> は EOF で止まり、
/// <c>Prev*</c> は先頭で止まる)。
/// <list type="table">
/// <listheader><term>家族(<c>TextSnapshot</c> 版)</term><description><c>pos &lt; 0</c> / <c>pos &gt; CharLength</c></description></listheader>
/// <item><term><c>Next*</c></term><description>throw / クランプ(<c>CharLength</c> を返す)</description></item>
/// <item><term><c>Prev*</c></term><description>クランプ(0 を返す) / throw</description></item>
/// <item><term><c>SnapToLogicalCharStart</c></term><description>クランプ / クランプ</description></item>
/// <item><term><c>SnapToLogicalCharEnd</c></term><description>クランプ / クランプ</description></item>
/// </list>
/// throw は <see cref="ArgumentOutOfRangeException"/>(<see cref="TextSnapshot.GetChar"/> 由来)。
/// 実装は必要な位置しか読まないため、読取が発生しない縮退ケースでは範囲外でも throw しない
/// (例: 空文書に <c>PrevCodePoint(snap, 1)</c> は 0 を返す)。呼び出し側はこれに依存せず、
/// <see cref="SnapToLogicalCharStart(TextSnapshot, int)"/> 等で [0, CharLength] に正規化してから
/// 歩進すること。
/// <c>ReadOnlySpan&lt;char&gt;</c> 版は <see cref="CodePointLengthAt(ReadOnlySpan{char}, int)"/> が
/// 範囲外で <see cref="IndexOutOfRangeException"/>(indexer 由来)、
/// <see cref="SnapToCodePointStart"/> と
/// <see cref="SnapToLogicalCharStart(ReadOnlySpan{char}, int)"/> は両端クランプ。
/// </summary>
public static class TextBoundary
{
    // ===== 境界述語(規則の唯一の定義。規則を変えるときはここだけを直す) =====
    // snapshot 版は 前進 / 後退 × サロゲート / CRLF の 2×2 をすべて述語として持つ。
    // span 版は後退・スナップ側の 2 本(span 側で前進規則が要るのは CodePointLengthAt だけなので)。
    // オーバーロードは隣接させること(Sonar S4136)。
    // 片方向だけを直して逆方向を落とす失敗(2026-07-24)を、欠けが見えることで防ぐ。
    // GetChar は安くないため、呼び出し側が既に読んだ char は引数で受け取り再読を避ける。

    /// <summary>
    /// <paramref name="pos"/> から始まるサロゲートペアか(前進側の規則の唯一の定義)。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    private static bool IsSurrogatePairStartingAt(TextSnapshot snap, int pos, char charAtPos) =>
        char.IsHighSurrogate(charAtPos)
        && pos + 1 < snap.CharLength
        && char.IsLowSurrogate(snap.GetChar(pos + 1));

    /// <summary>
    /// <paramref name="pos"/>(= low サロゲート側)で終わるサロゲートペアか
    /// (後退 / スナップ側の規則の唯一の定義)。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    /// <remarks><paramref name="pos"/> &gt; 0 は呼び出し側が保証する
    /// (<c>pos - 1</c> を読むため)。</remarks>
    private static bool IsSurrogatePairEndingAt(TextSnapshot snap, int pos, char charAtPos) =>
        char.IsLowSurrogate(charAtPos) && char.IsHighSurrogate(snap.GetChar(pos - 1));

    /// <summary>
    /// <paramref name="pos"/>(= low サロゲート側)で終わるサロゲートペアか。<b>全域関数</b>=
    /// <paramref name="pos"/> が <c>[1, CharLength)</c> の外なら <c>false</c> を返す
    /// (private 版と違い呼び出し側のガードを要求しない)。規則そのものは private 版が持ち、
    /// これは範囲ガードを足して委譲するだけ。
    /// </summary>
    /// <remarks>
    /// <b>用途は「<c>Snap*</c> がなぜ動いたか」の事後弁別。</b>
    /// <see cref="SnapToLogicalCharStart(TextSnapshot, int)"/> /
    /// <see cref="SnapToLogicalCharEnd(TextSnapshot, int)"/> が位置を動かす要因は
    /// 「サロゲートペアを割った」か「CRLF を割った」かの 2 つしかなく、しかも<b>排他</b>
    /// (low サロゲートは <c>'\n'</c> ではない)。よってスナップ後にこの述語 1 つを当てれば
    /// どちらが起きたかが分かる。
    /// <para>
    /// <c>EditorControl.GetExactChangeCharRange</c> がこれを使う。復元する半身が
    /// <b>孤立サロゲートになる</b>(= UTF-8 往復で U+FFFD へ潰れる)のはサロゲート側だけで、
    /// CRLF 側は <c>\r</c> / <c>\n</c> が無傷で戻るため、両者を弁別しないと
    /// 「内容が変わりうる範囲」を過大に見積もる。
    /// </para>
    /// <para>
    /// <b>呼び出し側で <c>char.IsLowSurrogate</c> + 隣接読みを手書きしないこと</b>
    /// (class doc の宣言)。手書きすると規則がこのクラスの外へ二重化し、登録簿が嘘になる。
    /// 公開しているのはこの 1 本だけで、規則の定義数(6)は増えていない。
    /// </para>
    /// </remarks>
    public static bool IsSurrogatePairEndingAt(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0 || pos >= snap.CharLength)
            return false;
        return IsSurrogatePairEndingAt(snap, pos, snap.GetChar(pos));
    }

    /// <summary>
    /// span 版。<paramref name="pos"/>(= low サロゲート側)で終わるサロゲートペアか。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>text[pos]</c>。</param>
    /// <remarks>
    /// <paramref name="pos"/> &gt; 0 は呼び出し側が保証する(<c>pos - 1</c> を読むため)。
    /// snapshot 版のすぐ隣に置くこと: 離すと Sonar <c>S4136</c>(オーバーロードは隣接)で落ちる。
    /// </remarks>
    private static bool IsSurrogatePairEndingAt(ReadOnlySpan<char> text, int pos, char charAtPos) =>
        char.IsLowSurrogate(charAtPos) && char.IsHighSurrogate(text[pos - 1]);

    /// <summary>
    /// <paramref name="pos"/>(= CR 側)から始まる CRLF pair か(前進側の規則の唯一の定義)。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    private static bool IsCrlfStartingAt(TextSnapshot snap, int pos, char charAtPos) =>
        charAtPos == '\r' && pos + 1 < snap.CharLength && snap.GetChar(pos + 1) == '\n';

    /// <summary>
    /// <paramref name="pos"/>(= LF 側)で終わる CRLF pair か(後退 / スナップ側の規則の唯一の定義)。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    /// <remarks><paramref name="pos"/> &gt; 0 は呼び出し側が保証する
    /// (<c>pos - 1</c> を読むため)。</remarks>
    private static bool IsCrlfEndingAt(TextSnapshot snap, int pos, char charAtPos) =>
        charAtPos == '\n' && snap.GetChar(pos - 1) == '\r';

    /// <summary>
    /// span 版。<paramref name="pos"/>(= LF 側)で終わる CRLF pair か。
    /// </summary>
    /// <param name="charAtPos">呼び出し側が既に読んだ <c>text[pos]</c>。</param>
    /// <remarks>
    /// <paramref name="pos"/> &gt; 0 は呼び出し側が保証する(<c>pos - 1</c> を読むため)。
    /// snapshot 版のすぐ隣に置くこと: 離すと Sonar <c>S4136</c>(オーバーロードは隣接)で落ちる。
    /// </remarks>
    private static bool IsCrlfEndingAt(ReadOnlySpan<char> text, int pos, char charAtPos) =>
        charAtPos == '\n' && text[pos - 1] == '\r';

    // ===== コードポイント単位: サロゲートのみ atomic・CR と LF は別々に数える =====

    /// <summary>
    /// <paramref name="pos"/> のコードポイントが占める code unit 数(1 または 2)。
    /// サロゲートペアが成立するときだけ 2。
    /// </summary>
    /// <remarks><paramref name="pos"/> が [0, CharLength) の外なら
    /// <see cref="ArgumentOutOfRangeException"/>(<see cref="TextSnapshot.GetChar"/> 由来)。</remarks>
    public static int CodePointLengthAt(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        return IsSurrogatePairStartingAt(snap, pos, snap.GetChar(pos)) ? 2 : 1;
    }

    /// <summary>
    /// span 版(Layout / 描画)。<paramref name="i"/> のコードポイントが占める code unit 数
    /// (1 または 2)。対の相手が span の外にある(末尾の孤立 high サロゲート)場合は 1 を返す
    /// = 呼び出し側の前進ループが必ず進む。
    /// </summary>
    /// <remarks><paramref name="i"/> が [0, text.Length) の外なら
    /// <see cref="IndexOutOfRangeException"/>(indexer 由来)。</remarks>
    public static int CodePointLengthAt(ReadOnlySpan<char> text, int i) =>
        char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
            ? 2
            : 1;

    /// <summary>右に 1 コードポイント進む。CRLF は跨がない(CR と LF の間で止まる)。</summary>
    public static int NextCodePoint(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos >= snap.CharLength)
            return snap.CharLength;
        return pos + CodePointLengthAt(snap, pos);
    }

    /// <summary>左に 1 コードポイント戻る。CRLF は跨がない。</summary>
    public static int PrevCodePoint(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        int prev = pos - 1;
        // prev == 0(文書先頭)ならペアは成立しえない=読まずに確定する
        // (PrevLogicalChar と同じ 2 段ガード形。逆方向だけ直して片方を落とす失敗を防ぐため
        //  2 つの Prev 系は形まで揃えておく)
        if (prev <= 0)
            return 0;
        char c = snap.GetChar(prev);
        if (IsSurrogatePairEndingAt(snap, prev, c))
            return prev - 1;
        return prev;
    }

    /// <summary>
    /// span 版(Layout / 描画)。low サロゲート位置なら pair 先頭へ前方スナップする。
    /// [0, text.Length] の外はクランプ(末尾は動かさない)。
    /// </summary>
    public static int SnapToCodePointStart(ReadOnlySpan<char> text, int i)
    {
        if (i <= 0)
            return 0;
        if (i >= text.Length)
            return text.Length;
        // CRLF は見ない(コードポイント単位)ので、サロゲート側の述語だけを呼ぶ。
        return IsSurrogatePairEndingAt(text, i, text[i]) ? i - 1 : i;
    }

    // ===== 論理文字単位: サロゲート + CRLF atomic =====

    /// <summary>右に 1 論理文字進む。サロゲートペアと CRLF pair を 1 単位として越える。</summary>
    public static int NextLogicalChar(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos >= snap.CharLength)
            return snap.CharLength;
        char c = snap.GetChar(pos);
        if (IsSurrogatePairStartingAt(snap, pos, c) || IsCrlfStartingAt(snap, pos, c))
            return pos + 2;
        return pos + 1;
    }

    /// <summary>左に 1 論理文字戻る。サロゲートペアと CRLF pair を 1 単位として越える。</summary>
    public static int PrevLogicalChar(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        int prev = pos - 1;
        // prev == 0(文書先頭)ならどちらの pair も成立しえない=読まずに確定する
        // (PrevCodePoint と同じ 2 段ガード形)
        if (prev <= 0)
            return 0;
        char c = snap.GetChar(prev);
        if (IsSurrogatePairEndingAt(snap, prev, c) || IsCrlfEndingAt(snap, prev, c))
            return prev - 1;
        return prev;
    }

    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方(pair 先頭)へスナップする。CharLength(=EOF)はキャレットが立てる境界なので許可。
    /// </summary>
    public static int SnapToLogicalCharStart(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return snap.CharLength;
        // pos > 0 は前段の早期 return で保証済み(述語が pos - 1 を読める条件)
        char c = snap.GetChar(pos);
        if (IsSurrogatePairEndingAt(snap, pos, c) || IsCrlfEndingAt(snap, pos, c))
            return pos - 1;
        return pos;
    }

    /// <summary>
    /// span 版。<c>TextSnapshot</c> を持たない呼び出し側(<c>Core.Search</c> が材質化した
    /// 本文 string 等)向け。論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 前方(pair 先頭)へスナップする。[0, text.Length] の外はクランプ。
    ///
    /// <b>span の両端は無条件に論理文字境界とみなす</b>(<c>pos == 0</c> で <c>text[-1]</c> を、
    /// <c>pos == text.Length</c> で <c>text[Length]</c> を読まない)。したがって<b>より大きな
    /// テキストの「窓」を渡すと、窓外へまたがる pair は見えない</b>=窓の先頭が low サロゲート /
    /// LF でも動かさない。<see cref="CodePointLengthAt(ReadOnlySpan{char}, int)"/> が末尾の孤立
    /// high サロゲートを 1 と数えるのと同じ流儀。窓を渡す呼び出し側は、<b>論理文字を割らない位置で
    /// 窓を切る責任を持つ</b>(行単位で切るなら改行を含めない=CRLF の内側で切らない)。
    /// </summary>
    /// <remarks>
    /// <see cref="SnapToCodePointStart"/>(サロゲートのみ)と違い <b>CRLF も 1 論理文字として見る</b>。
    /// 判定を述語へ括らずインラインで書いているのは他の span 版 2 メソッドと同じ理由
    /// (indexer 読みなので snapshot 版の述語と共有できない)。
    ///
    /// <b>drift 防止の網は 2 本あり、射程が違う。「同値テストがあるから片方だけ直せば必ず赤くなる」は
    /// 偽</b>(2026-09-01 B2 に変異で実測。素通りを 2 方向とも確認済み):
    /// <list type="bullet">
    /// <item><c>SnapToLogicalCharStart_Span_MatchesSnapshotVersion_Exhaustive</c> = snapshot 版との同値。
    /// 射程は<b>snapshot が表現できる本文に限る</b>=<c>TextBuffer</c> が孤立サロゲートを U+FFFD へ
    /// 潰すため、材質化後の本文に孤立 low サロゲートは 1 つも現れない。よって
    /// <c>IsHighSurrogate(text[pos - 1])</c> を落とす変異は<b>この網を素通りする</b>(不一致 0 件)。</item>
    /// <item><c>SnapToLogicalCharStart_Span_Exhaustive_NeverMovesForwardAndStaysInRange</c> =
    /// snapshot を通さない事後条件。孤立サロゲートを含む入力にも直接当たるので上の変異を殺す
    /// (違反 22 件)。逆に <c>c == '\n'</c> を落とす変異は、戻り先が CR になって「論理文字の内側を
    /// 指さない」を満たしてしまうため<b>この網を素通りし</b>(違反 0 件)、同値テスト側だけが殺す
    /// (不一致 344 件)。</item>
    /// </list>
    /// <b>2 本は相補的。どちらか一方を消すと、対応する側の変異が生存する。</b>
    /// </remarks>
    public static int SnapToLogicalCharStart(ReadOnlySpan<char> text, int pos)
    {
        if (pos <= 0)
            return 0;
        if (pos >= text.Length)
            return text.Length;
        char c = text[pos];
        if (IsSurrogatePairEndingAt(text, pos, c) || IsCrlfEndingAt(text, pos, c))
            return pos - 1;
        return pos;
    }

    /// <summary>
    /// [0, CharLength] にクランプし、論理文字の中間位置(low サロゲート位置 / CR と LF の間)を
    /// 後方(pair 終端)へスナップする。
    /// <see cref="SnapToLogicalCharStart(TextSnapshot, int)"/> の対。
    /// </summary>
    /// <remarks>
    /// 範囲の<b>終端</b>に使う。終端に <see cref="SnapToLogicalCharStart(TextSnapshot, int)"/> を
    /// 掛けると範囲が狭まり、割ってはいけない論理文字ごと範囲から落ちる(例: CRLF の CR にヒットした
    /// <c>[3,4)</c> が <c>[3,3)</c> のゼロ幅に潰れる)。始端に Start・終端に End を掛けると、
    /// 元の範囲を必ず含み、かつ論理文字を割らない最小の範囲になる。
    /// </remarks>
    public static int SnapToLogicalCharEnd(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos <= 0)
            return 0;
        if (pos >= snap.CharLength)
            return snap.CharLength;
        // pos > 0 は前段の早期 return で保証済み(述語が pos - 1 を読める条件)
        char c = snap.GetChar(pos);
        if (IsSurrogatePairEndingAt(snap, pos, c) || IsCrlfEndingAt(snap, pos, c))
            return pos + 1;
        return pos;
    }
}
