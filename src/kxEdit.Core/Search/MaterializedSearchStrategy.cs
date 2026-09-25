using kxEdit.Core.Buffers;

namespace kxEdit.Core.Search;

/// <summary>
/// 全文を材質化して <see cref="TextSearcher"/> に適用する照合。
/// <b>意味論の「正」</b>であり、他 2 戦略の差異はこの戦略との差として記述される。
/// </summary>
/// <remarks>
/// <para>
/// 材質化した文字列は注入された <see cref="SnapshotTextCache"/> が保持する
/// (判定と安全性の議論はそちらの remarks)。同じキャッシュを複数の戦略(照合条件)で共有してよい。
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
    private readonly SnapshotTextCache _texts;

    /// <summary>
    /// テスト観測用: 注入されたキャッシュの材質化回数(キャッシュを共有していれば、共有先の分も数える)。
    /// 既存のテストはこの戦略だけがキャッシュを使う形で観測している。
    /// </summary>
    internal int MaterializeCountForTest => _texts.MaterializeCountForTest;

    /// <summary>専用のキャッシュで構築する(テスト用)。</summary>
    internal MaterializedSearchStrategy(TextSearcher inner)
        : this(inner, new SnapshotTextCache()) { }

    internal MaterializedSearchStrategy(TextSearcher inner, SnapshotTextCache texts)
    {
        _inner = inner;
        _texts = texts;
    }

    /// <summary>snap の全文(キャッシュ経由)。</summary>
    private string TextOf(TextSnapshot snap) => _texts.TextOf(snap);

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
