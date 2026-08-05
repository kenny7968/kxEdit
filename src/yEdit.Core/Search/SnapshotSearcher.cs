using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// P6 Task 11: <see cref="TextSnapshot"/> ベースの検索/置換ファサード。
/// 実際の照合は 3 つの <see cref="ISnapshotSearchStrategy"/> 実装が行い、本クラスは
/// <see cref="StrategyFor"/> でそのうち 1 つを選んで丸ごと委譲する。
/// 6 つの public メソッドが自分で行うのは <see cref="IsValid"/> の短絡と位置引数の正規化だけで、
/// 経路の分岐は <see cref="StrategyFor"/> の 1 箇所に集約されている。
/// <para>
/// <b>選択規則</b>(<c>L</c> = <see cref="TextSnapshot.CharLength"/> ・
/// <c>T</c> = 閾値。既定は <see cref="DefaultThresholdChars"/> = 32M chars ≒ 64MB):
/// </para>
/// <list type="table">
///   <listheader><term>条件</term><description>選ばれる戦略</description></listheader>
///   <item><term><c>L &lt;= T</c>(<b>ちょうど一致は閾値以下側</b>)</term>
///     <description><see cref="MaterializedSearchStrategy"/>=全文を材質化して
///       <see cref="TextSearcher"/> へ委譲する。<b>意味論の「正」</b>であり、
///       閾値二層化の前の挙動と 100% 一致する。</description></item>
///   <item><term><c>L &gt; T</c> かつ <see cref="SearchOptions.UseRegex"/>=false</term>
///     <description><see cref="LiteralWindowSearchStrategy"/>=材質化せず、
///       ウィンドウ(既定 <see cref="DefaultWindowSize"/>)+ パターン長 -1 の overlap で走査する。
///       </description></item>
///   <item><term><c>L &gt; T</c> かつ <see cref="SearchOptions.UseRegex"/>=true</term>
///     <description><see cref="RegexPerLineSearchStrategy"/>=1 行ずつ切り出して
///       <see cref="TextSearcher"/> へ適用し、行頭オフセットを足して文書座標へ戻す。</description></item>
/// </list>
/// <para>
/// <b>壊れる契約(設計書§2-8 許容範囲)</b>: 閾値超の 2 戦略は材質化経路と意味論が食い違う
/// (改行跨ぎ・regex アンカー・WholeWord の Unicode 差)。
/// <b>内訳はそれぞれの戦略クラスの remarks が正</b>で、ここには複製しない
/// (二重管理にすると必ず片方が腐る)。呼び出し側が閾値の両側で厳密に同一の挙動を
/// 必要とするなら、選ばれうる戦略の remarks を読むこと。
/// </para>
/// <para>
/// <b>戦略に依らず残る制約</b>: <see cref="ReplaceInRange"/> は閾値超でも置換後 Fragment を
/// string で組み立てる。大容量 ReplaceAll での真の OOM 回避は未対応のまま(設計書 §8 S-1)。
/// </para>
/// <para>
/// <b>スレッドセーフではない</b>=1 インスタンスは単一スレッドからのみ使うこと。
/// 内部の材質化戦略が材質化した全文をスナップショット単位でキャッシュするミュータブルな
/// スロットを持つため、同一 <see cref="TextSnapshot"/> に対する並行読みでも安全ではない
/// (この性質は材質化戦略の抽出で入った=それ以前は不変フィールドのみだった)。
/// 現時点の利用者は <c>SearchController</c> だけで、照合条件ごとに 1 インスタンスを
/// フィールドへ保持し、4 メソッド(件数更新 / 検索 / 置換 / 全置換)がそれを共有する。
/// いずれも UI スレッドから呼ばれる。
/// 件数更新などをバックグラウンドへ逃がすなら、スレッドごとに別インスタンスを持つこと。
/// </para>
/// <para>
/// <b>長寿命に保持するなら参照の寿命は呼び出し側の責任</b>。材質化戦略のキャッシュは
/// 最後に照合した <see cref="TextSnapshot"/> とその全文 string を強参照で持つ
/// (= 背後のピース木・バイト配列ごとピン留めする)。保持側は照合条件の変化・
/// 文書の切替 / クローズ・検索の終了で参照を捨てること
/// (<c>SearchController.DropSearcher</c> がその実装)。
/// </para>
/// </summary>
public sealed class SnapshotSearcher
{
    /// <summary>閾値(UTF-16 文字数)。既定=32M chars(≈64MB)。</summary>
    public const int DefaultThresholdChars = 32 * 1024 * 1024;

    /// <summary>閾値超リテラル窓照合のウィンドウサイズ(UTF-16 文字数)。既定=4096 chars(≈8KB)。</summary>
    public const int DefaultWindowSize = 4 * 1024;

    private readonly SearchOptions _opts;
    private readonly TextSearcher _inner;
    private readonly int _thresholdChars;

    private readonly MaterializedSearchStrategy _materialized;
    private readonly LiteralWindowSearchStrategy _literal;
    private readonly RegexPerLineSearchStrategy _regexPerLine;

    /// <summary>照合条件から SnapshotSearcher を構築する。IsValid/Error は内側 <see cref="TextSearcher"/> と同一。</summary>
    public SnapshotSearcher(SearchOptions options)
        : this(options, DefaultThresholdChars, DefaultWindowSize) { }

    /// <summary>
    /// 閾値・窓サイズを指定して SnapshotSearcher を構築する(テスト注入用)。
    /// 本番コードは既定コンストラクタを使う。閾値・窓サイズは正数でなければならない。
    /// </summary>
    public SnapshotSearcher(SearchOptions options, int thresholdChars, int windowSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        _opts = options;
        _inner = new TextSearcher(options);
        _thresholdChars = thresholdChars;
        // (_windowSize フィールドは持たない: 窓サイズはここで戦略へ渡し、以後は戦略側が保持する)
        _literal = new LiteralWindowSearchStrategy(options, windowSize);
        // 材質化戦略・regex 戦略は内側 TextSearcher を共有する(必ず _inner 代入の後で構築すること)。
        _materialized = new MaterializedSearchStrategy(_inner);
        _regexPerLine = new RegexPerLineSearchStrategy(_inner);
    }

    /// <summary>照合条件が有効(正規表現を構築できた)か。</summary>
    public bool IsValid => _inner.IsValid;

    /// <summary>無効な場合の理由(空パターンや不正な正規表現)。有効なら null。</summary>
    public string? Error => _inner.Error;

    /// <summary>
    /// snap のサイズと照合条件から戦略を選ぶ(分岐はこの 1 箇所だけ)。
    /// <b>private ではなく internal なのはテスト都合</b>=選ばれた戦略を「型」で直接 assert する
    /// <c>StrategyFor_selects_expected_strategy</c> の網がこの可視性に依存している。
    /// 意味論的帰結からの間接観測(改行跨ぎ regex がヒットするか等)だけでは、戦略側の制約が
    /// 将来変わったときに境界の反転を黙って見逃すため、<c>private</c> へ縮めないこと。
    /// </summary>
    /// <remarks>
    /// 3 戦略とも ctor で 1 個ずつ作って使い回す。閾値超の 2 戦略は snapshot 非依存で、
    /// 材質化戦略だけが snapshot 依存の状態(材質化キャッシュ)を持つが、
    /// スナップショット参照の同一性で無効化するので同じく使い回せる。
    /// 閾値判定は「ちょうど一致は閾値以下(材質化経路)」。<c>&lt;</c> にすると
    /// 閾値ちょうどの文書の意味論が変わる = 挙動変更になる
    /// (<c>AtExactThreshold_uses_below_path_not_above</c> が固定)。
    /// </remarks>
    internal ISnapshotSearchStrategy StrategyFor(TextSnapshot snap) =>
        snap.CharLength <= _thresholdChars ? _materialized
        : _opts.UseRegex ? _regexPerLine
        : _literal;

    /// <summary>snap 全体のヒット件数。無効なら 0。</summary>
    public int Count(TextSnapshot snap) => IsValid ? StrategyFor(snap).Count(snap) : 0;

    /// <summary>from 以降で最初のヒット(折り返しなし)。無効なら null。</summary>
    public MatchSpan? FindNext(TextSnapshot snap, int from)
    {
        if (!IsValid)
            return null;
        if (from < 0)
            from = 0;
        if (from > snap.CharLength)
            return null;
        return StrategyFor(snap).FindNext(snap, from);
    }

    /// <summary>開始位置(Index)が before より厳密に前にある最後のヒットを返す(折り返しなし)。</summary>
    /// <remarks>
    /// <c>Math.Min(before, snap.CharLength)</c> を<b>ここへ集約してはいけない</b>。
    /// 閾値以下経路は生の before を <see cref="TextSearcher"/> へ渡すのが現行挙動で、
    /// 文書長を超える before とゼロ幅ヒットの組み合わせで結果が変わる
    /// (パターン <c>b*</c> / 文書 <c>"ab"</c> で <c>FindPrev(3)</c> は <c>(2,0)</c>・
    /// <c>FindPrev(2)</c> は <c>(1,1)</c>)。上限クランプは必要とする 2 戦略が自分で持つ。
    /// </remarks>
    public MatchSpan? FindPrev(TextSnapshot snap, int before) =>
        IsValid && before > 0 ? StrategyFor(snap).FindPrev(snap, before) : null;

    /// <summary>span を全ヒット中の何件目か(1始まり, total)。span がヒットでなければ null。</summary>
    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span) =>
        IsValid ? StrategyFor(snap).Locate(snap, span) : null;

    /// <summary>
    /// span が実際のヒットなら置換文字列を返す(正規表現は $1 等展開・リテラルは素のまま)。違えば null。
    /// </summary>
    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement) =>
        IsValid ? StrategyFor(snap).ReplacementAt(snap, span, replacement) : null;

    /// <summary>
    /// [start, start+length) に完全に収まるヒットだけ置換し、その範囲の置換後断片と件数を返す。
    /// 範囲外・境界をまたぐヒットは対象外。start/length は snap 範囲へクランプする。
    /// 閾値超でも Fragment を string で組み立てる=大容量 ReplaceAll での真の OOM 回避は P7 送り(設計書§2-8 許容)。
    /// </summary>
    public (string Fragment, int Count) ReplaceInRange(
        TextSnapshot snap,
        int start,
        int length,
        string replacement
    )
    {
        // 材質化経路の引数形を他 2 戦略と揃えて (s, end - s) を渡す。生の (start, length) を渡す
        // 旧実装と結果は同一: TextSearcher.ReplaceInRange は s' = Clamp(s, 0, L) /
        // end' = Clamp(s + (end - s), s, L) を再度行うが、下の 2 行で 0 <= s <= end <= L
        // (L = 材質化長 = snap.CharLength) が成り立つため両方とも冪等(s' == s / end' == end)。
        // 網 = ReplaceInRange_ClampsOutOfRangeArgs_below_threshold。
        int s = Math.Clamp(start, 0, snap.CharLength);
        int end = Math.Clamp(start + length, s, snap.CharLength);
        if (!IsValid)
            return (snap.GetText(s, end - s), 0);
        return StrategyFor(snap).ReplaceInRange(snap, s, end - s, replacement);
    }
}
