using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// <see cref="SnapshotSearcher"/> の照合方式。文書サイズと照合条件から
/// <see cref="SnapshotSearcher"/> が 1 つ選び、以後の照合を丸ごと委譲する。
/// 選択規則(サイズ → パターン種別の 2 段)は <see cref="SnapshotSearcher"/> のクラス doc を参照。
/// </summary>
/// <remarks>
/// <b>実装者への契約</b>:
/// <list type="bullet">
///   <item>照合条件は有効(<see cref="TextSearcher.IsValid"/>=true)であることが保証される。
///     無効時の短絡は <see cref="SnapshotSearcher"/> 側が持つため、実装で再度ガードしない。</item>
///   <item>オフセットは全て UTF-16 コード単位。</item>
///   <item><b>インスタンスは複数の <see cref="TextSnapshot"/> にまたがって再利用される</b>
///     (ファサードが構築時に 1 個作り、以後すべての呼び出しで使い回す)。
///     snapshot 由来の状態を持つ実装は、参照同一性で無効化すること。
///     また実装は<b>スレッドセーフではない</b>=ファサードごと単一スレッドで使うこと。</item>
///   <item><b>位置引数の正規化は引数ごとに違う</b>(下表)。「未保証」の引数をそのまま
///     <see cref="TextSnapshot.GetText"/> 等へ渡す実装は、自分でガードする責務を負う。</item>
/// </list>
/// <list type="table">
///   <listheader><term>引数</term><description>ファサードが保証する範囲</description></listheader>
///   <item><term><see cref="FindNext"/> の from</term>
///     <description>0 &lt;= from &lt;= snap.CharLength</description></item>
///   <item><term><see cref="FindPrev"/> の before</term>
///     <description><b>下限のみ</b>(before &gt; 0)。<b>上限は未保証</b>= CharLength 超が来る</description></item>
///   <item><term><see cref="ReplaceInRange"/> の start / length</term>
///     <description>0 &lt;= start かつ start + length &lt;= snap.CharLength</description></item>
///   <item><term><see cref="Locate"/> / <see cref="ReplacementAt"/> の span</term>
///     <description><b>未検証</b>。負値・範囲外がそのまま来る(<see cref="MatchSpan"/> は検証を持たない)</description></item>
/// </list>
/// <para>
/// <b>この表はファサードを畳んだ後(Task 5 完了時)の姿</b>。それまでの中間状態では、
/// 材質化戦略に <see cref="FindNext"/> / <see cref="FindPrev"/> の 2 行が適用されない
/// (ファサードの材質化分岐が該当のクランプ・早期 return より手前にあるため)。
/// 詳細は <see cref="MaterializedSearchStrategy"/> のクラス doc を参照。
/// </para>
/// <para>
/// <b><see cref="FindPrev"/> だけ上限が未保証な理由</b>: 材質化戦略は生の before を
/// <see cref="TextSearcher.FindPrev"/> へ渡すのが現行挙動であり、CharLength 超の before と
/// ゼロ幅ヒットの組み合わせで観測可能な差になる(パターン <c>b*</c> / 文書 <c>"ab"</c> で
/// <c>FindPrev(3)</c> は <c>(2,0)</c>・<c>FindPrev(2)</c> は <c>(1,1)</c>)。
/// よってクランプはファサードへ集約できず、<b>必要とする戦略が自分で行う</b>。
/// </para>
/// </remarks>
internal interface ISnapshotSearchStrategy
{
    /// <summary>snap 全体のヒット件数。</summary>
    int Count(TextSnapshot snap);

    /// <summary>from 以降で最初のヒット(折り返しなし)。</summary>
    MatchSpan? FindNext(TextSnapshot snap, int from);

    /// <summary>開始位置が before より厳密に前にある最後のヒット(折り返しなし)。</summary>
    MatchSpan? FindPrev(TextSnapshot snap, int before);

    /// <summary>span が全ヒット中の何件目か(1 始まり, total)。ヒットでなければ null。</summary>
    (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span);

    /// <summary>span が実際のヒットなら置換文字列を返す。違えば null。</summary>
    string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement);

    /// <summary>[start, start+length) に完全に収まるヒットだけ置換した断片と件数。</summary>
    (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    );
}
