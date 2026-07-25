using System.Windows;

namespace yEdit.Accessibility;

/// <summary>
/// 自作 EditorControl が実装する UIA プロバイダのバックエンド抽象(v2・範囲ベース化)。
/// 旧 v1(P7 で撤去)は全文 string 経路だったのに対し、v2 は
/// 位置歩き + 範囲テキスト + 座標 API を持つ。
///
/// スレッド: RPC スレッドから呼ばれ得る。実装側は不変スナップショット参照 +
/// キャッシュ値で応答すること(<see cref="SetSelection"/> / <see cref="SetFocus"/> /
/// <see cref="ScrollRangeIntoView"/> のみ UI マーシャリング)。
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

    /// <summary>offset を含む単語の左端(Core WordBoundary 委譲)。</summary>
    int WordStart(int offset);

    /// <summary>offset を含む単語の右端(Core WordBoundary 委譲)。</summary>
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

    // ---------- スクロール ----------

    /// <summary>
    /// [start, end) を可視域へスクロールする(実装は UI スレッドへマーシャリング)。
    /// <paramref name="alignToTop"/> が true なら範囲**先頭**を、false なら範囲**末尾**を
    /// 対象にする(UIA <c>ITextRangeProvider.ScrollIntoView</c> の意味論)。
    /// 選択・キャレットは変更しない(装飾スクロール)。
    /// 対象が既に可視なら何もしない=SR がテキストを歩くたびに画面が飛ぶのを防ぐ。
    /// </summary>
    void ScrollRangeIntoView(int start, int end, bool alignToTop);

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
