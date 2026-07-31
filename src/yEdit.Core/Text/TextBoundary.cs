using yEdit.Core.Buffers;

namespace yEdit.Core.Text;

/// <summary>
/// 文字境界の判定・歩進を 1 箇所に集約する純ロジック(2026-07-31 新設)。
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
/// </summary>
public static class TextBoundary
{
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
        char c = snap.GetChar(pos);
        return
            char.IsHighSurrogate(c)
            && pos + 1 < snap.CharLength
            && char.IsLowSurrogate(snap.GetChar(pos + 1))
            ? 2
            : 1;
    }

    /// <summary>
    /// span 版(Layout / 描画)。<paramref name="i"/> のコードポイントが占める code unit 数
    /// (1 または 2)。対の相手が span の外にある(末尾の孤立 high サロゲート)場合は 1 を返す
    /// = 呼び出し側の前進ループが必ず進む。
    /// </summary>
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
        if (
            prev > 0
            && char.IsLowSurrogate(snap.GetChar(prev))
            && char.IsHighSurrogate(snap.GetChar(prev - 1))
        )
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
        if (
            char.IsHighSurrogate(c)
            && pos + 1 < snap.CharLength
            && char.IsLowSurrogate(snap.GetChar(pos + 1))
        )
            return pos + 2;
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
        if (
            prev > 0
            && char.IsLowSurrogate(snap.GetChar(prev))
            && char.IsHighSurrogate(snap.GetChar(prev - 1))
        )
            return prev - 1;
        if (prev > 0 && snap.GetChar(prev) == '\n' && snap.GetChar(prev - 1) == '\r')
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
        // pos > 0 は前段の早期 return で保証済み
        char c = snap.GetChar(pos);
        if (char.IsLowSurrogate(c) && char.IsHighSurrogate(snap.GetChar(pos - 1)))
            return pos - 1;
        if (c == '\n' && snap.GetChar(pos - 1) == '\r')
            return pos - 1;
        return pos;
    }
}
