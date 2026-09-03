using kxEdit.Core.Buffers;
using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Core.Editing;

/// <summary>
/// TextSnapshot 上の位置移動関数群(純ロジック)。
/// EditorControl から呼ばれる。範囲外指定時のスナップは呼び出し側の責務で、
/// この関数群は caret ∈ [0, snap.CharLength] かつ code-point 境界を前提とする。
/// </summary>
/// <remarks>
/// 前提違反時(caret が [0, CharLength] を外れる/サロゲート中間)は、
/// TextSnapshot 側(GetChar / GetLineIndexOfChar 等)から
/// <see cref="ArgumentOutOfRangeException"/> が透過的に伝播する。
/// EditorControl 側は SnapAndClamp で必ずスナップしてから呼ぶこと
/// (呼び忘れると即例外でキー入力が消えるため)。
/// </remarks>
public static class NavigationCommands
{
    /// <summary>左に1文字移動。サロゲートペア・CRLF pair は1文字として扱う。先頭では動かない。</summary>
    /// <remarks>2026-07-31: 歩進規則の実体は <see cref="TextBoundary.PrevLogicalChar"/> へ移した
    /// (UIA 側 <c>UiaTextHostAdapter.PrevChar</c> と同じ規則を 2 箇所に書いていたため)。</remarks>
    public static int MoveLeftChar(TextSnapshot s, int caret) =>
        TextBoundary.PrevLogicalChar(s, caret);

    /// <summary>右に1文字移動。サロゲートペア・CRLF pair は1文字として扱う。末尾では動かない。</summary>
    /// <remarks>2026-07-31: 歩進規則の実体は <see cref="TextBoundary.NextLogicalChar"/> へ移した。</remarks>
    public static int MoveRightChar(TextSnapshot s, int caret) =>
        TextBoundary.NextLogicalChar(s, caret);

    /// <summary>現在行の先頭(char offset)。</summary>
    public static int MoveHome(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineStart(line);
    }

    /// <summary>現在行の末尾(break 直前)。</summary>
    public static int MoveEnd(TextSnapshot s, int caret)
    {
        int line = s.GetLineIndexOfChar(caret);
        return s.GetLineEnd(line, includeBreak: false);
    }

    /// <summary>Home キーの移動先(論理行版)。</summary>
    /// <param name="skipIndent">true=行頭の空白を飛ばすスマート挙動(<c>firstNonWs</c> ⇔ <c>lineStart</c>
    /// のトグル)。false=常に行頭(<c>lineStart</c>)。設定 <c>AppSettings.SmartHome</c> に対応する。</param>
    /// <remarks>
    /// <para><paramref name="skipIndent"/> = true のとき:</para>
    /// - キャレットが行頭(lineStart)にいる → 先頭空白の後(firstNonWs)へ
    /// - キャレットが firstNonWs にいる → 行頭(lineStart)へ
    /// - それ以外(本文内) → firstNonWs へ
    /// 空白のみの行では firstNonWs == lineEnd。トグルは lineStart ↔ lineEnd 相当だが問題なし。
    /// <para><paramref name="skipIndent"/> = false のとき: 常に lineStart(トグルしない=
    /// 行頭で押しても動かない)。</para>
    /// 空白判定は半角空白(' ')とタブ('\t')のみ。全角空白(U+3000)や他の Unicode 空白は含めない
    /// (Scintilla 版 M6 と同じ挙動。char.IsWhiteSpace は改行を巻き込むため使わない)。
    /// </remarks>
    public static int MoveLineHome(TextSnapshot s, int caret, bool skipIndent)
    {
        int line = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(line);
        if (!skipIndent)
            return lineStart;
        int lineEnd = s.GetLineEnd(line, includeBreak: false);
        int firstNonWs = lineStart;
        while (firstNonWs < lineEnd)
        {
            char c = s.GetChar(firstNonWs);
            if (c != ' ' && c != '\t')
                break;
            firstNonWs++;
        }
        // すでに firstNonWs にいる → lineStart。それ以外 → firstNonWs
        if (caret == firstNonWs)
            return lineStart;
        return firstNonWs;
    }

    /// <summary>P8-1a: 視覚行(折り返し行)ベースの Home キーの移動先。</summary>
    /// <param name="wrapColumns">折り返し桁数(半角換算)。&lt;=0 で折り返し無し=<see cref="MoveLineHome(TextSnapshot, int, bool)"/> と同じ論理行挙動。</param>
    /// <param name="metrics">文字幅計測(<see cref="LineLayout.Wrap"/> と同じ流儀)。</param>
    /// <param name="skipIndent">true=第 1 視覚セグメントで先頭空白を飛ばすスマート挙動。
    /// false=常に視覚セグメント先頭(論理行頭ではない)。値の意味は
    /// <see cref="MoveLineHome(TextSnapshot, int, bool)"/> の同名パラメータと同じ。</param>
    /// <remarks>
    /// <para>折り返し ON 時: キャレットが属する視覚セグメントの先頭を返す=NVDA/ナレーターが
    /// 視覚行の先頭から読むように App 層キー入力を統一する(P7 チェックリスト N-3=論理行頭に飛んで
    /// 視覚行の先頭から読まれない問題の解消)。<b>この特性は
    /// <paramref name="skipIndent"/> の両値で保たれる</b>(2026-09-04)。</para>
    /// <list type="bullet">
    /// <item>第 1 視覚セグメント(=論理行先頭を含む)は <paramref name="skipIndent"/> = true なら
    /// smart トグル(視覚 seg 内の firstNonWs ⇔ 視覚 seg 先頭=lineStart)、
    /// false なら常に視覚 seg 先頭。</item>
    /// <item>継続視覚セグメント(2 つ目以降)は視覚 seg 先頭に固定=トグルなし
    /// (継続 seg は通常 leading whitespace を持たないため firstNonWs 判定不要)。</item>
    /// <item>空行は視覚セグメントも [(0,0)] 1 個(<see cref="LineLayout.Wrap"/> 契約)=lineStart を返す。</item>
    /// </list>
    /// </remarks>
    public static int MoveLineHome(
        TextSnapshot s,
        int caret,
        int wrapColumns,
        ICharMetrics metrics,
        bool skipIndent
    )
    {
        // wrap OFF は既存論理行挙動へ委譲
        if (wrapColumns <= 0)
            return MoveLineHome(s, caret, skipIndent);

        int line = s.GetLineIndexOfChar(caret);
        int lineStart = s.GetLineStart(line);
        int lineEnd = s.GetLineEnd(line, includeBreak: false);

        // 空行: 論理版と同じ挙動(lineStart 相当・視覚 seg も 1 個)
        if (lineStart == lineEnd)
            return lineStart;

        string lineText = s.GetText(lineStart, lineEnd - lineStart);
        int maxWidthPx = wrapColumns * metrics.MeasureRun("0".AsSpan());
        var segs = LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
        int caretInLine = caret - lineStart;

        // キャレットが属する視覚セグメントを探す(行末位置=最終 seg 扱い)
        var (segIdx, seg) = VisualSegments.FindContaining(segs, caretInLine);
        int visualStart = lineStart + seg.OffsetInLine;
        int visualEnd = lineStart + seg.OffsetInLine + seg.Length;

        // 継続セグメント: 視覚 seg 先頭固定(トグルなし)
        if (segIdx > 0)
            return visualStart;

        // 「常に行頭」モード: 第 1 セグメントでも空白を飛ばさない(2026-09-04)
        if (!skipIndent)
            return visualStart;

        // 第 1 セグメント: smart トグル(視覚 seg 内の firstNonWs ⇔ 視覚 seg 先頭)
        int firstNonWs = visualStart;
        while (firstNonWs < visualEnd)
        {
            char c = s.GetChar(firstNonWs);
            if (c != ' ' && c != '\t')
                break;
            firstNonWs++;
        }
        if (caret == firstNonWs)
            return visualStart;
        return firstNonWs;
    }
}
