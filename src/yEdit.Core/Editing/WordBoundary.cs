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
/// 2026-07-31: 内部の code-point 歩進は <see cref="Text.TextBoundary"/> へ移した。
/// <b>本クラスは CRLF を atomic に扱わない</b>(CR と LF を別々の LineBreak として数える設計)。
/// 論理文字単位が要るキャレット / UIA 系とは意図的に別の API を使っている。
/// </remarks>
public static class WordBoundary
{
    /// <summary>次の単語の先頭に進む。EOF に達したら CharLength を返す。</summary>
    /// <remarks>
    /// 動作:
    /// 1. caret が CharLength なら CharLength を返す(EOF)
    /// 2. 現在位置の class が Whitespace/LineBreak → その連続をスキップして到達位置を返す
    /// 3. 現在位置の class が非空白 → 同 class の連続をスキップ → その先の空白/改行連続もスキップして到達位置を返す
    /// </remarks>
    public static int NextWordStart(TextSnapshot snap, int caret)
    {
        if (caret >= snap.CharLength)
            return snap.CharLength;
        int pos = caret;
        var start = ClassOf(snap, pos);
        if (start == CharClass.Whitespace || start == CharClass.LineBreak)
        {
            // 空白/改行から始まる場合は連続をスキップ → 次の非空白の頭
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak
            );
        }
        else
        {
            // 非空白 class の連続をスキップ → その先の空白/改行連続もスキップ
            pos = SkipForwardWhile(snap, pos, cls => cls == start);
            pos = SkipForwardWhile(
                snap,
                pos,
                cls => cls == CharClass.Whitespace || cls == CharClass.LineBreak
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
    public static int PrevWordStart(TextSnapshot snap, int caret)
    {
        if (caret <= 0)
            return 0;
        int pos = TextBoundary.PrevCodePoint(snap, caret);
        // 左隣を空白/改行としてスキップ(後方=空白の直前まで)
        while (pos > 0)
        {
            var cls = ClassOf(snap, pos);
            if (cls != CharClass.Whitespace && cls != CharClass.LineBreak)
                break;
            pos = TextBoundary.PrevCodePoint(snap, pos);
        }
        // 位置 pos の class を単語 class として、その連続をさらに左へ
        var wordCls = ClassOf(snap, pos);
        while (pos > 0)
        {
            int prev = TextBoundary.PrevCodePoint(snap, pos);
            if (ClassOf(snap, prev) != wordCls)
                break;
            pos = prev;
        }
        return pos;
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

    /// <summary>pred が真の間、code-point 単位で右へ進む。</summary>
    private static int SkipForwardWhile(TextSnapshot snap, int pos, Func<CharClass, bool> pred)
    {
        while (pos < snap.CharLength && pred(ClassOf(snap, pos)))
            pos = TextBoundary.NextCodePoint(snap, pos);
        return pos;
    }
}
