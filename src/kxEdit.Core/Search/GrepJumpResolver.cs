using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>grep ヒットを live バッファへ解決した結果の種別。</summary>
public enum GrepJumpKind
{
    /// <summary>grep 時の行番号にヒット行がそのままあった。</summary>
    Exact,

    /// <summary>行がずれていたが、近傍に同一内容の行が見つかった。</summary>
    Nearby,

    /// <summary>ヒット行が見つからない(内容が変わった)。行頭へ寄せるだけで選択しない。</summary>
    Stale,
}

/// <summary>grep ジャンプの着地点。<paramref name="Line"/> は 0-based。</summary>
public sealed record GrepJumpTarget(GrepJumpKind Kind, int Line, int Offset, int Length);

/// <summary>
/// grep ヒットを、その時点のバッファ内容へ照合して着地点を決める(A-18・設計書 §3.1)。
/// </summary>
/// <remarks>
/// <b>この関数は <see cref="GrepHit.AbsoluteOffset"/> を読まない。</b> AbsoluteOffset は
/// grep がディスク上のバイト列を復号した空間の値であり、
/// (1) 未保存編集のあるタブ (2) エディタ(先頭 64KB prefix)と grep(全バイト)の文字コード判定の
/// 割れ (3) grep 実行後のディスク側外部変更 —— のいずれでもバッファ空間とずれる。
/// A-18 はこれを選択位置に流用していたことによる「別の行を正しい行として読み上げる」不具合。
/// <b>AbsoluteOffset をこの経路へ戻さないこと</b>(<c>GrepJumpResolverTests</c> が固定している)。
/// </remarks>
public static class GrepJumpResolver
{
    /// <summary>
    /// 行がずれていたときに前後へ探しにいく行数。UI スレッド上の走査なので有界にする。
    /// 実測に基づく値ではない設計値(設計書 §6 申し送り)。
    /// </summary>
    internal const int NearbyLineWindow = 1000;

    /// <summary>
    /// <paramref name="hit"/> の行番号+行内容を <paramref name="snap"/> へ照合し、着地点を返す。
    /// 1) 行番号どおりの行が一致 → <see cref="GrepJumpKind.Exact"/>
    /// 2) 近い順に ±<see cref="NearbyLineWindow"/> 行を探して一致 → <see cref="GrepJumpKind.Nearby"/>
    /// 3) 見つからない / 行内容が空 → <see cref="GrepJumpKind.Stale"/>(選択せず行頭へ)
    /// </summary>
    public static GrepJumpTarget Resolve(GrepHit hit, TextSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentNullException.ThrowIfNull(snap);

        int lineCount = snap.LineCount; // 空文字でも 1
        int origin = Math.Clamp(hit.LineNumber - 1, 0, lineCount - 1);

        if (LineEquals(snap, origin, hit.LineText))
            return Land(GrepJumpKind.Exact, snap, origin, hit);

        // 行内容が空だと照合材料がゼロで、近傍の任意の空行に一致してしまう。無関係な空行へ
        // 黙って着地して正常であるかのように発声するより、Stale として明示するほうが誠実
        // (設計書 §3.1)。
        if (hit.LineText.Length > 0)
        {
            for (int d = 1; d <= NearbyLineWindow; d++)
            {
                int up = origin - d;
                int down = origin + d;
                // 同距離なら上を先に採る(タイブレークの規約)。
                if (up >= 0 && LineEquals(snap, up, hit.LineText))
                    return Land(GrepJumpKind.Nearby, snap, up, hit);
                if (down < lineCount && LineEquals(snap, down, hit.LineText))
                    return Land(GrepJumpKind.Nearby, snap, down, hit);
                if (up < 0 && down >= lineCount)
                    break; // 両端に到達=窓を使い切る前に探索終了
            }
        }

        return new GrepJumpTarget(GrepJumpKind.Stale, origin, snap.GetLineStart(origin), 0);
    }

    /// <summary>
    /// 一致した行の着地点を組み立てる。
    /// </summary>
    /// <remarks>
    /// <b>行内へのクランプは置かない。</b> <see cref="LineEquals"/> が
    /// 「行の長さ == <c>hit.LineText.Length</c>」を保証し、grep 側が
    /// 「<c>MatchStartInLine + MatchLength &lt;= LineText.Length</c>」を保証するので、
    /// 選択が行外へ食み出す経路が存在しない(=書いても到達不能な belt になる)。
    /// 範囲外の最終防衛は <c>EditorControl.SelectCharRange</c> の契約が担う。
    /// </remarks>
    private static GrepJumpTarget Land(
        GrepJumpKind kind,
        TextSnapshot snap,
        int line,
        GrepHit hit
    ) => new(kind, line, snap.GetLineStart(line) + hit.MatchStartInLine, hit.MatchLength);

    /// <summary>
    /// <paramref name="line"/> の行内容(改行を含まない)が <paramref name="text"/> と序数一致するか。
    /// 文字列を実体化する前に長さで篩う(近傍走査でピース木の走査を最大 2×窓回まわすため)。
    /// </summary>
    private static bool LineEquals(TextSnapshot snap, int line, string text)
    {
        int start = snap.GetLineStart(line);
        int length = snap.GetLineEnd(line, includeBreak: false) - start;
        if (length != text.Length)
            return false;
        return length == 0
            || string.Equals(snap.GetText(start, length), text, StringComparison.Ordinal);
    }
}
