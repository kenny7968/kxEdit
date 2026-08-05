using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// 全文を材質化して <see cref="TextSearcher"/> に適用する照合。
/// <b>意味論の「正」</b>であり、他 2 戦略の差異はこの戦略との差として記述される。
/// </summary>
/// <remarks>
/// <para>
/// 材質化した文字列は<b>スナップショット単位で保持</b>する。
/// <see cref="TextSnapshot"/> は不変(構築時のルート参照を包むだけ)で、
/// <see cref="TextBuffer.Current"/> は編集・Undo・Redo のときだけ差し替わるフィールド返しなので、
/// 参照同一性が「文書が変わっていない」の正当な signal になる。
/// 参照同一性を同種の signal に使う idiom は <see cref="TextBuffer.Modified"/> が既に採用している
/// (あちらが比べるのはスナップショットではなくピース木のルート参照)。
/// </para>
/// <para>
/// 誤りは<b>安全な側にしか倒れない</b>: 内容が同じでもインスタンスが別なら
/// (Undo で同じルートへ戻った直後など)材質化をやり直すだけで、古い本文を返すことはない。
/// 逆向き=「同じインスタンスなのに内容が違う」は <see cref="TextSnapshot"/> が不変である限り起こらない。
/// 保持するのは常に最大 1 本で、スナップショットが変われば古い文字列は参照が切れる。
/// </para>
/// <para>
/// <b>位置引数は正規化されていない</b>(<see cref="ISnapshotSearchStrategy"/> の契約表が
/// 保証している範囲より弱い)。ファサードの材質化分岐はすべてのクランプ・早期 return より
/// <b>手前</b>にあり、呼び出し側の生の値がそのまま届く。正規化は委譲先の
/// <see cref="TextSearcher"/> 自身が行い、それが現行挙動である
/// (例: <see cref="TextSearcher.FindNext"/> は from を自前でクランプする)。
/// Task 5 でファサードの三重分岐を畳むときに、クランプをこの戦略の手前へ移すと
/// <see cref="FindPrev"/> の挙動が変わる(下記)。
/// </para>
/// </remarks>
internal sealed class MaterializedSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;

    private TextSnapshot? _cachedSnapshot;
    private string _cachedText = string.Empty;

    /// <summary>テスト観測用: 実際に材質化した回数。キャッシュが効いていることを assert 化する seam。</summary>
    internal int MaterializeCountForTest { get; private set; }

    internal MaterializedSearchStrategy(TextSearcher inner) => _inner = inner;

    /// <summary>snap の全文。同一スナップショットの連続呼び出しでは前回の結果を返す。</summary>
    private string TextOf(TextSnapshot snap)
    {
        if (ReferenceEquals(_cachedSnapshot, snap))
            return _cachedText;
        _cachedText = snap.GetText(0, snap.CharLength);
        _cachedSnapshot = snap;
        MaterializeCountForTest++;
        return _cachedText;
    }

    public int Count(TextSnapshot snap) => _inner.Count(TextOf(snap));

    public MatchSpan? FindNext(TextSnapshot snap, int from) => _inner.FindNext(TextOf(snap), from);

    /// <summary>
    /// <b>before をクランプしない</b>のがこの戦略の現行挙動であり、他 2 戦略との意図的な非対称。
    /// CharLength 超の before はゼロ幅ヒットと組み合わせると観測可能な差になる
    /// (反例は <see cref="ISnapshotSearchStrategy"/> の契約表)。「3 経路が同じ形だから」で
    /// クランプを足さないこと。
    /// </summary>
    public MatchSpan? FindPrev(TextSnapshot snap, int before) =>
        _inner.FindPrev(TextOf(snap), before);

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span) =>
        _inner.Locate(TextOf(snap), span);

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement) =>
        _inner.ReplacementAt(TextOf(snap), span, replacement);

    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    ) => _inner.ReplaceInRange(TextOf(snap), start, length, replacement);
}
