using System.Text.RegularExpressions;
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
/// <para>
/// <b>一致位置表(2026-09-25 フェーズ 5 P-14)</b>: 直近に試みた 1 スナップショットぶんの
/// 全ヒットの位置表(<see cref="MatchPositions"/>)を持ち、<see cref="Locate"/> と <see cref="FindPrev"/>
/// を表の二分探索で答える(F3 / Shift+F3 のたびの全件列挙をやめる)。
/// 構築の契機はこの 2 メソッドだけで、<see cref="Count"/> は構築済みの表を使うだけで構築を始めない。
/// 構築の失敗(件数が上限 <see cref="DefaultMaxCachedMatches"/> 超え・タイムアウト)もスナップショットごとに
/// 記憶し、同じスナップショットでは作り直さずに従来の経路(全件列挙)で答える。
/// 表は全文キャッシュと違って searcher(照合条件)ごとに持ち、共有しない(表は正規表現に依存する)。
/// 表は次の <see cref="Locate"/> / <see cref="FindPrev"/> で作り直すまで、試みたスナップショットを
/// 強参照し続ける(文書を編集した後も、編集前のスナップショットを掴むことがある)。
/// 寿命は searcher の寿命の内側に収まる=searcher を捨てれば一緒に離れる。
/// </para>
/// </remarks>
internal sealed class MaterializedSearchStrategy : ISnapshotSearchStrategy
{
    private readonly TextSearcher _inner;
    private readonly SnapshotTextCache _texts;

    /// <summary>一致位置表を作る件数の上限(設計書 §10.3)。超えたら表を作らず従来の経路で答える(配列の肥大化を防ぐ)。</summary>
    internal const int DefaultMaxCachedMatches = 1_000_000;

    private readonly int _maxCachedMatches;

    // 一致位置表(P-14)。_positionsSnapshot は「表を試みたスナップショット」で、_positions が null なら
    // 未構築ではなく「作れなかった」(上限超え・タイムアウト)。同じスナップショットでは作り直さない。
    // 全文キャッシュと違って searcher(照合条件)ごとに持つ(表は正規表現に依存する)。
    private TextSnapshot? _positionsSnapshot;
    private MatchPositions? _positions;

    /// <summary>テスト観測用: 表の構築を試みた回数(失敗も数える)。</summary>
    internal int BuildCountForTest { get; private set; }

    /// <summary>テスト観測用: 直近に試みたスナップショットの表があるか。</summary>
    internal bool HasPositionsForTest => _positions is not null;

    /// <summary>
    /// テスト観測用: 注入されたキャッシュの材質化回数(キャッシュを共有していれば、共有先の分も数える)。
    /// 既存の <c>Cache_holds_at_most_one_snapshot</c> 等はこれを経由する。専用キャッシュの ctor
    /// (1 引数)で作ったときだけ、戦略単位の材質化回数と一致する。
    /// </summary>
    internal int MaterializeCountForTest => _texts.MaterializeCountForTest;

    /// <summary>専用のキャッシュで構築する(テスト用)。</summary>
    internal MaterializedSearchStrategy(TextSearcher inner)
        : this(inner, new SnapshotTextCache()) { }

    internal MaterializedSearchStrategy(
        TextSearcher inner,
        SnapshotTextCache texts,
        int maxCachedMatches = DefaultMaxCachedMatches
    )
    {
        _inner = inner;
        _texts = texts;
        _maxCachedMatches = maxCachedMatches;
    }

    /// <summary>snap の全文(キャッシュ経由)。</summary>
    private string TextOf(TextSnapshot snap) => _texts.TextOf(snap);

    /// <summary>
    /// snap の一致位置表。初めてのスナップショットなら構築を試みる。作れなければ null
    /// (呼び出し側は従来の経路=全件列挙で答える)。
    /// </summary>
    private MatchPositions? PositionsOf(TextSnapshot snap, string text)
    {
        if (ReferenceEquals(_positionsSnapshot, snap))
            return _positions;
        BuildCountForTest++;
        MatchPositions? built;
        try
        {
            built = _inner.CollectMatches(text, _maxCachedMatches);
        }
        catch (RegexMatchTimeoutException)
        {
            // 表を確定させずに従来の経路へ戻す。従来の経路が同じくタイムアウトすれば、
            // 例外はそちらから従来どおり伝播する(旧 FindPrev は早く break して成功することもある)。
            built = null;
        }
        // 代入順は表が先・スナップショットが後(SnapshotTextCache.TextOf と同じ理由。入れ替えないこと)。
        _positions = built;
        _positionsSnapshot = snap;
        return built;
    }

    /// <summary>
    /// 表が構築済みなら表の件数、未構築なら従来どおり <c>Regex.Count</c>。
    /// <b>ここから構築を始めない</b>: 検索語の打鍵では searcher が作り直されるので表は再利用されず、
    /// 構築(Matches の全列挙と配列の確保)は Match を作らない Regex.Count より重い(設計書 §10.3)。
    /// </summary>
    public int Count(TextSnapshot snap)
    {
        string text = TextOf(snap);
        return ReferenceEquals(_positionsSnapshot, snap) && _positions is { } p
            ? p.Count
            : _inner.Count(text);
    }

    /// <summary>
    /// <b>表で置き換えない</b>: <c>Regex.Match(text, from)</c> の結果は <c>Matches</c> の集合と一致しない
    /// (<c>"aaa"</c> を <c>"aa"</c> で探すと <c>Matches</c> は (0,2) だけだが、<c>Match(text, 1)</c> は
    /// (1,2) を返す)。
    /// </summary>
    public MatchSpan? FindNext(TextSnapshot snap, int from) => _inner.FindNext(TextOf(snap), from);

    /// <summary>
    /// <b>before をクランプしない</b>のがこの戦略の現行挙動であり、他 2 戦略との意図的な非対称。
    /// CharLength 超の before はゼロ幅ヒットと組み合わせると観測可能な差になる
    /// (反例は <see cref="ISnapshotSearchStrategy"/> の契約表)。「3 経路が同じ形だから」で
    /// クランプを足さないこと。
    /// </summary>
    public MatchSpan? FindPrev(TextSnapshot snap, int before)
    {
        string text = TextOf(snap);
        return PositionsOf(snap, text) is { } p
            ? p.FindPrev(before)
            : _inner.FindPrev(text, before);
    }

    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)
    {
        string text = TextOf(snap);
        return PositionsOf(snap, text) is { } p ? p.Locate(span) : _inner.Locate(text, span);
    }

    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement) =>
        _inner.ReplacementAt(TextOf(snap), span, replacement);

    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    ) => _inner.ReplaceInRange(TextOf(snap), start, length, replacement);
}
