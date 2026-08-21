using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

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
/// <b>選択の前提</b>: この戦略は <c>CharLength &lt;= 閾値</c> のときだけ選ばれる。
/// 閾値以下ならパターン種別(<see cref="SearchOptions.UseRegex"/>)は問わない。
/// </para>
/// <para>
/// <b>位置引数は <see cref="ISnapshotSearchStrategy"/> の契約表どおりに届く</b>。
/// 表のうちこの戦略にとって効くのは <see cref="FindPrev"/> の before で、
/// 下限(<c>&gt; 0</c>)は保証されるが<b>上限は保証されない</b>=文書長超がそのまま来る。
/// この戦略はそれをクランプせずに <see cref="TextSearcher"/> へ渡すのが現行挙動であり、
/// そこが閾値超の 2 戦略との意図的な非対称である(理由と反例は契約表・詳細は
/// <see cref="FindPrev"/> の doc)。
/// </para>
/// <para>
/// なお <see cref="TextSearcher"/> 自身も位置引数を自前で正規化する
/// (例: <see cref="TextSearcher.FindNext"/> は from をクランプする)ため、
/// ファサードの正規化と二重になっている箇所がある。<b>等価</b>=
/// 材質化長は常に <see cref="TextSnapshot.CharLength"/> に一致するので、
/// 前段で正規化済みの値に対して後段のクランプは冪等になる。
/// </para>
/// </remarks>
internal sealed class MaterializedSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;

    private TextSnapshot? _cachedSnapshot;
    private string _cachedText = string.Empty;

    /// <summary>
    /// テスト観測用: 実際に材質化した回数。キャッシュが効いていることを assert 化する seam。
    /// <b>消さないこと</b>: <c>Cache_holds_at_most_one_snapshot</c> が「保持は最大 1 本」を
    /// 検証する唯一の手段であり、結果値からは辞書実装(多スロット)と区別できない。
    /// </summary>
    internal int MaterializeCountForTest { get; private set; }

    internal MaterializedSearchStrategy(TextSearcher inner) => _inner = inner;

    /// <summary>snap の全文。同一スナップショットの連続呼び出しでは前回の結果を返す。</summary>
    private string TextOf(TextSnapshot snap)
    {
        if (ReferenceEquals(_cachedSnapshot, snap))
            return _cachedText;
        // 代入順は text が先・snapshot が後(入れ替えないこと)。逆順だと GetText が
        // 例外を投げたときに _cachedSnapshot だけ新しくなり、次回の参照同一性ヒットで
        // 古い本文を新しいスナップショットのものとして返す stale の窓が開く。
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
