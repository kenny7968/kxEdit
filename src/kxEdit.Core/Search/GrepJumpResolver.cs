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

/// <summary>grep ジャンプの着地点。</summary>
/// <param name="Kind">解決の種別。</param>
/// <param name="BufferLine">
/// 着地行。<b>バッファ空間</b>の <b>0 始まり</b>行番号(<c>GrepHit.LineNumber</c> は
/// <b>ディスク空間</b>の <b>1 始まり</b>で、基数も空間も違う)。<b>診断・テスト用</b>。
/// <b>発声の行番号にこれを使わないこと</b>
/// (着地後の <c>EditorControl.CurrentLine</c> から読み戻す=設計書 §3.2。
/// resolver の意図値を発声すると SelectCharRange 側のクランプ/スナップの不具合が
/// 発声に現れなくなる)。
/// </param>
/// <param name="BufferOffset">選択開始位置。<b>バッファ空間</b>の UTF-16 オフセット。</param>
/// <param name="Length">選択長。Stale では 0(選択しない)。</param>
public sealed record GrepJumpTarget(
    GrepJumpKind Kind,
    int BufferLine,
    int BufferOffset,
    int Length
);

/// <summary>
/// grep ヒットを、その時点のバッファ内容へ照合して着地点を決める(A-18・設計書 §3.1)。
/// </summary>
/// <remarks>
/// <b>この関数は <see cref="GrepHit.AbsoluteOffset"/> を読まない。</b> AbsoluteOffset は
/// grep がディスク上のバイト列を復号した空間の値であり、
/// (1) 未保存編集のあるタブ (2) エディタ(先頭 64KB prefix)と grep(全バイト)の文字コード判定の
/// 割れ (3) grep 実行後のディスク側外部変更 —— のいずれでもバッファ空間と<b>ずれうる</b>。
/// <b>「必ずずれる」ではない</b>((1) ヒットより後ろだけの編集ならオフセットは動かない、
/// (2) 本文が ASCII なら判定が UTF-8 / SJIS で割れても復号結果は同一、(3) 外部変更がヒットより
/// 後ろなら同じ)。<b>たまたま揃うことがある</b>のが厄介で、揃うことを前提にできないのが問題。
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
        // クランプは上下で性格が違う。上限は「到達可能」(grep 後に行が消えたファイル。外すと
        // GetLineStart が ArgumentOutOfRangeException を投げて UI スレッドで落ちる=設計書 §5.3 の
        // 変異 #11a が実測で確認)。下限は、現在唯一の生成元である GrepService.CollectLineHits が
        // lineNumber を ++ してから emit する(=LineNumber >= 1)ため「到達不能」で、別途書いた
        // belt ではなく Math.Clamp という慣用的な 1 呼び出しに付いてくるものにすぎない
        // (上限だけにするには Math.Min へ書き換える必要があり、そちらのほうが不自然)。
        // 到達不能なので網は張らない(Land の「到達不能な belt は書かない」判断と同じ方針)。
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
    /// 長さで篩ってから文字列を実体化する。近傍走査は最大 2×窓回まわるので、不一致行の
    /// string 実体化とアロケーションを避けるのが目的。
    /// <b>ピース木の降下自体は削れない</b>: <c>GetLineStart</c> / <c>GetLineEnd</c> は
    /// 長さ比較より前に無条件で走る(CRLF 行では <c>GetLineEnd</c> が内部で <c>GetChar</c> を
    /// 2 回呼ぶ)。実測でも前フィルタが削るのは全体の <b>4〜5 割</b>程度
    /// (設計書 §6 の実施記録・「20k 行 CRLF・同一長で不一致」34.5 / 27.2 ms に対して
    /// 「20k 行 CRLF・長さ違いで不一致」17.4 / 15.4 ms の対)。
    /// <para>
    /// <b>篩いを通過した行は「幅ぶん」の実体化が要る</b>(設計書 §6 の脆弱性パス追実測):
    /// <c>GetText</c> は行全体を string に起こすので、コストは<b>行数</b>ではなく
    /// <b>窓内で長さ一致した行の幅の総和</b>に比例する。1 呼び出しあたりのアロケーションは
    /// fixture 次第で <b>0.5 MB 〜 458 MB</b>(約 900 倍)まで振れ、42,500 文字を超える行は
    /// LOH 行きになる。<b>窓を広げるなら実体化しない比較 seam が要る</b>。
    /// </para>
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
