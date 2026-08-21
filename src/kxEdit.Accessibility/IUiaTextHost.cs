using System.Windows;

namespace kxEdit.Accessibility;

/// <summary>
/// 自作 EditorControl が実装する UIA プロバイダのバックエンド抽象(v2・範囲ベース化)。
/// 旧 v1(P7 で撤去)は全文 string 経路だったのに対し、v2 は
/// 位置歩き + 範囲テキスト + 座標 API を持つ。
///
/// スレッド: RPC スレッドから呼ばれ得る。実装側は次の 3 分類で応答すること。
/// <list type="bullet">
/// <item>書き込み系(<see cref="SetSelection"/> / <see cref="SetFocus"/> /
/// <see cref="ScrollRangeIntoView"/>)は <c>BeginInvoke</c>(戻り値が不要=RPC スレッドを待たせない)。</item>
/// <item>UI スレッド専用状態を要する読み取り(<see cref="GetBoundingRectangles"/> /
/// <see cref="OffsetFromScreenPoint"/> / <see cref="GetVisibleRange"/>)は同期 <c>Invoke</c>。</item>
/// <item>それ以外は不変スナップショット参照 + キャッシュ値で応答(マーシャリングしない)。</item>
/// </list>
/// </summary>
public interface IUiaTextHost
{
    // ---------- テキスト(範囲ベース) ----------

    /// <summary>[start, start+length) の UTF-16 部分文字列。範囲外は clamp。RPC スレッド安全。</summary>
    string GetTextRange(int start, int length);

    /// <summary>本文長(UTF-16 コード単位)。</summary>
    int TextLength { get; }

    // ---------- 選択 ----------

    /// <summary>現在の選択(Start &lt;= End、End 排他)。キャレットのみのときは Start==End。</summary>
    (int Start, int End) GetSelection();

    /// <summary>選択/キャレットを設定(実装は UI スレッドへマーシャリング)。</summary>
    void SetSelection(int start, int end);

    // ---------- 位置歩き(全て純関数=RPC スレッド安全) ----------

    /// <summary>offset の次の code-point 位置(サロゲート考慮)。EOF なら TextLength。</summary>
    int NextChar(int offset);

    /// <summary>offset の前の code-point 位置(サロゲート考慮)。BOF なら 0。</summary>
    int PrevChar(int offset);

    /// <summary>offset を含む行の開始位置(P8-1c: 折り返し ON なら視覚行=折り返し行の先頭・OFF なら論理行先頭)。</summary>
    int LineStartOf(int offset);

    /// <summary>offset を含む行の終端(改行を含まない・P8-1c: 折り返し ON なら視覚行末=折り返し境界)。空行では LineStartOf と一致し len=0。</summary>
    int LineEndNoBreakOf(int offset);

    /// <summary>
    /// offset を含む行の終端(次視覚行の開始・P8-1c)。継続セグメントは改行を跨がない=次 seg 先頭。
    /// 論理行の最終視覚行のみ改行を含めて次論理行の開始 or TextLength を返す。
    /// </summary>
    int LineEnd(int offset);

    /// <summary>
    /// offset を含む単語の左端(Core WordBoundary 委譲)。単語は<b>文字クラス規則</b>
    /// (Latin / Digit / Hiragana / Katakana / Han / Other 等)で区切られる=空白区切りではない。
    /// <b>非空白 run については</b>「同一クラスの連続 = 1 単語」。
    /// </summary>
    /// <remarks>
    /// Whitespace / LineBreak クラスは<b>単語を成さない</b>。空白 run の上での挙動
    /// (返り値が offset を含まないこと・走査上限の窓の単位と意味)は
    /// <c>kxEdit.Core.Editing.WordBoundary</c> クラスの <c>&lt;remarks&gt;</c>
    /// 「窓についてよくある誤読」が正本。ここでは UIA 層に固有の帰結だけを書く:
    /// 同じ offset を <see cref="WordEnd"/> へ渡すと offset がそのまま返るため、
    /// 両者を組むと<b>空スパンになりうる</b>(例: <c>"    hello"</c> の offset=0 は 0 / 0)。
    /// 空スパンは <c>TextRangeProviderV2.ExpandToEnclosingUnit</c> の
    /// <see cref="NextChar"/> フォールバックが 1 文字へ膨らませる。
    ///
    /// <b>同じ offset を渡した場合に限り</b>、<see cref="WordEnd"/> と対でダブルクリック単語選択と
    /// 同じスパンになる(目に見える選択と SR が読むスパンが一致する)。2026-08-04 Task 4 で
    /// <c>TextRangeProviderV2.ExpandToEnclosingUnit</c> が両者へ同じ offset を渡すようになったので、
    /// 空白の上でもダブルクリックと一致する(<c>"ab    cd"</c> の offset=4 は両方 <c>[0,4)</c>。
    /// Task 4 以前は UIA だけ <c>[0,2)</c> だった)。
    /// </remarks>
    int WordStart(int offset);

    /// <summary>
    /// offset を含む単語の右端(Core WordBoundary 委譲・<see cref="WordStart"/> と同じ文字クラス規則)。
    /// <b>末尾の空白は含まない</b>(単語の直後に続く空白 run は返り値の外)。
    /// offset が空白 / 改行の上にあるときは offset をそのまま返す
    /// (<c>WordEnd(offset) &gt;= offset</c> は不変条件=反転レンジを UIA へ出さないため)。
    /// </summary>
    int WordEnd(int offset);

    /// <summary>Ctrl+→ 相当の「次の単語の先頭」。EOF なら TextLength。</summary>
    int NextWordStart(int offset);

    /// <summary>Ctrl+← 相当の「前の単語の先頭」。BOF なら 0。</summary>
    int PrevWordStart(int offset);

    // ---------- 座標 ----------

    /// <summary>コントロール全体のスクリーン座標矩形(UI スレッドで更新したキャッシュ値)。</summary>
    Rect BoundingRectangle { get; }

    /// <summary>[start, end) の各行スクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。空なら長さ 0。</summary>
    double[] GetBoundingRectangles(int start, int end);

    /// <summary>スクリーン座標 (x, y) 直下の文字オフセット(HitTest 相当)。範囲外は clamp。</summary>
    int OffsetFromScreenPoint(double x, double y);

    // ---------- スクロール / 可視範囲 ----------

    /// <summary>
    /// [start, end) を可視域へスクロールする(実装は UI スレッドへマーシャリング)。
    /// <paramref name="alignToTop"/> が true なら範囲**先頭**を、false なら範囲**末尾**を
    /// 対象にする(UIA <c>ITextRangeProvider.ScrollIntoView</c> の意味論)。
    /// 選択・キャレットは変更しない(装飾スクロール)。
    /// 対象が既に可視なら何もしない=SR がテキストを歩くたびに画面が飛ぶのを防ぐ。
    ///
    /// <b>範囲外 / stale な offset は実装側で clamp すること</b>(<see cref="GetTextRange"/> と同じ流儀)。
    /// 呼び出し元の <c>TextRangeProviderV2</c> は ctor でしか clamp しないため、SR が範囲を掴んだまま
    /// 文書が縮むと stale な offset がそのまま届く。ここで clamp しないと行番号解決が
    /// 例外を投げ、UI スレッドへマーシャリング済みなら未処理例外=アプリ落ちになる。
    /// </summary>
    void ScrollRangeIntoView(int start, int end, bool alignToTop);

    /// <summary>
    /// 現在ビューポートに見えている本文の範囲 [Start, End)。
    /// 末尾行の改行は含めない(<see cref="LineEndNoBreakOf"/> と同じ流儀)。
    /// 水平スクロールで横に隠れている部分は「可視」に含める(行単位で報告する)。
    /// 実装は UI スレッド専用状態を要するため同期マーシャリングする。
    /// バッファ未設定 / Handle 未生成では (0, 0)。
    /// </summary>
    (int Start, int End) GetVisibleRange();

    // ---------- 属性 ----------

    /// <summary>ウィンドウハンドル(キャッシュ値)。</summary>
    nint Handle { get; }

    /// <summary>フォーカス状態(キャッシュ値)。</summary>
    bool HasFocus { get; }

    /// <summary>報告する ControlType Id(本番は Document=P0 で確定)。</summary>
    int ControlTypeId { get; }

    /// <summary>UIA の Name プロパティ("本文")。</summary>
    string Name { get; }

    /// <summary>UIA の AutomationId プロパティ("editor")。</summary>
    string AutomationId { get; }

    /// <summary>コントロールにフォーカスを与える(UI スレッドへマーシャリング)。</summary>
    void SetFocus();
}
