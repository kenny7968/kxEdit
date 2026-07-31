using yEdit.Core.Buffers;

namespace yEdit.Core.Text;

/// <summary>
/// 文字境界の判定・歩進を 1 箇所に集約する純ロジック(2026-07-31 新設)。
///
/// <b>このクラスが文字境界規則のオーナー</b>: 呼び出し元で <c>char.IsHighSurrogate</c> や
/// <c>'\r'</c> + <c>'\n'</c> の並び判定を手書きしないこと。2026-07-31 時点で src 配下
/// 14 ファイル・21 箇所へ散っていたものをここへ集約した。規則を変えるときに直す場所が
/// <b>本ファイルの境界述語 3 つ</b>(<c>IsSurrogatePairStartingAt</c> /
/// <c>IsSurrogatePairEndingAt</c> / <c>IsCrlfEndingAt</c>)<b>と span 版 2 メソッドだけ</b>で
/// 済む状態を保つこと(span 版は <c>TextSnapshot</c> ではなく indexer で読むため述語を共有できない)。
///
/// 典拠: docs/plans/2026-07-31-char-access-seam-design.md §4.1。
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
/// <b>この 2 つを 1 本に統一してはならない。</b> 統一すると
/// <see cref="Editing.WordBoundary"/> が CR と LF を別クラスとして数える前提が崩れ、
/// Ctrl+←→ の単語境界が変わる。
///
/// 置き場が <c>yEdit.Core.Text</c> なのは、<c>Core.Editing</c> と <c>Core.Layout</c> の
/// 双方から参照される葉である必要があるため(現状 Editing → Layout の依存があり、
/// 逆向きを足すと循環に見える)。
///
/// <c>TextSnapshot</c> を受ける版は文書全体を、<c>ReadOnlySpan&lt;char&gt;</c> を受ける版は
/// Layout / 描画が扱う行内テキスト(改行を含まない=CRLF 概念が不要)を対象とする。
///
/// <b>範囲外入力の契約</b>(置き換え元 <see cref="Editing.NavigationCommands"/> の
/// <c>&lt;remarks&gt;</c> を引き継ぐ)。<b>クランプで隠さず throw させる</b>のが方針=
/// 呼び出し側のスナップ漏れを早期に露出させるため。家族ごとに非対称なのは、
/// それぞれ「進めない側」だけを no-op として許容しているから(<c>Next*</c> は EOF で止まり、
/// <c>Prev*</c> は先頭で止まる)。
/// <list type="table">
/// <listheader><term>家族</term><description><c>pos &lt; 0</c> / <c>pos &gt; CharLength</c></description></listheader>
/// <item><term><c>Next*</c></term><description>throw / クランプ(<c>CharLength</c> を返す)</description></item>
/// <item><term><c>Prev*</c></term><description>クランプ(0 を返す) / throw</description></item>
/// <item><term><c>SnapToLogicalCharStart</c></term><description>クランプ / クランプ</description></item>
/// </list>
/// throw は <see cref="ArgumentOutOfRangeException"/>(<see cref="TextSnapshot.GetChar"/> 由来)。
/// 実装は必要な位置しか読まないため、読取が発生しない縮退ケースでは範囲外でも throw しない
/// (例: 空文書に <c>PrevCodePoint(snap, 1)</c> は 0 を返す)。呼び出し側はこれに依存せず、
/// <see cref="SnapToLogicalCharStart"/> 等で [0, CharLength] に正規化してから歩進すること。
/// <c>ReadOnlySpan&lt;char&gt;</c> 版は <see cref="CodePointLengthAt(ReadOnlySpan{char}, int)"/> が
/// 範囲外で <see cref="IndexOutOfRangeException"/>(indexer 由来)、
/// <see cref="SnapToCodePointStart"/> は両端クランプ。
/// </summary>
public static class TextBoundary
{
    // ===== 境界述語(規則の唯一の定義。規則を変えるときはここだけを直す) =====
    // GetChar は安くないため、呼び出し側が既に読んだ char は引数で受け取り再読を避ける。

    /// <summary>
    /// <paramref name="pos"/> から始まるサロゲートペアか(前進側の規則の唯一の定義)。
    /// </summary>
    /// <param name="c">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    private static bool IsSurrogatePairStartingAt(TextSnapshot snap, int pos, char c) =>
        char.IsHighSurrogate(c)
        && pos + 1 < snap.CharLength
        && char.IsLowSurrogate(snap.GetChar(pos + 1));

    /// <summary>
    /// <paramref name="pos"/>(= low サロゲート側)で終わるサロゲートペアか
    /// (後退 / スナップ側の規則の唯一の定義)。
    /// </summary>
    /// <param name="c">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    /// <remarks><paramref name="pos"/> &gt; 0 は呼び出し側が保証する
    /// (<c>pos - 1</c> を読むため)。</remarks>
    private static bool IsSurrogatePairEndingAt(TextSnapshot snap, int pos, char c) =>
        char.IsLowSurrogate(c) && char.IsHighSurrogate(snap.GetChar(pos - 1));

    /// <summary><paramref name="pos"/>(= LF 側)で終わる CRLF pair か(規則の唯一の定義)。</summary>
    /// <param name="c">呼び出し側が既に読んだ <c>snap.GetChar(pos)</c>。</param>
    /// <remarks><paramref name="pos"/> &gt; 0 は呼び出し側が保証する
    /// (<c>pos - 1</c> を読むため)。</remarks>
    private static bool IsCrlfEndingAt(TextSnapshot snap, int pos, char c) =>
        c == '\n' && snap.GetChar(pos - 1) == '\r';

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
        if (prev > 0 && IsSurrogatePairEndingAt(snap, prev, snap.GetChar(prev)))
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
        return char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]) ? i - 1 : i;
    }

    // ===== 論理文字単位: サロゲート + CRLF atomic =====

    /// <summary>右に 1 論理文字進む。サロゲートペアと CRLF pair を 1 単位として越える。</summary>
    public static int NextLogicalChar(TextSnapshot snap, int pos)
    {
        ArgumentNullException.ThrowIfNull(snap);
        if (pos >= snap.CharLength)
            return snap.CharLength;
        char c = snap.GetChar(pos);
        if (IsSurrogatePairStartingAt(snap, pos, c))
            return pos + 2;
        // CRLF は前進側で 1 箇所しか判定しないため述語を切り出していない
        if (c == '\r' && pos + 1 < snap.CharLength && snap.GetChar(pos + 1) == '\n')
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
        if (prev <= 0)
            return prev;
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
}
