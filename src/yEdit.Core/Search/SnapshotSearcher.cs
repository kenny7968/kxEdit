using yEdit.Core.Buffers;

namespace yEdit.Core.Search;

/// <summary>
/// P6 Task 11: <see cref="TextSnapshot"/> ベースの検索/置換ファサード。
/// 内部で 64MB 閾値(<see cref="ThresholdChars"/>=UTF-16 で 32M chars)により
/// 全文 string 材質化 vs 窓照合を切り替える。
/// <para>
/// 閾値以下は <see cref="TextSearcher"/> にそのまま委譲=既存挙動 100% 一致。
/// 閾値超はリテラル=窓照合(<see cref="WindowSize"/> ウィンドウ + パターン長 overlap)、
/// regex=行単位適用。
/// </para>
/// <para>
/// <b>壊れる契約(設計書§2-8 許容範囲)</b>:
/// <list type="bullet">
///   <item>閾値超 &amp; regex は「改行を跨ぐパターンは絶対にヒットしない」
///     (行単位で <see cref="TextSearcher"/> に委譲するため)。</item>
///   <item>P7 I-5 追記: 閾値超 &amp; regex アンカー(<c>^</c> / <c>$</c> /
///     <c>\A</c> / <c>\Z</c> / <c>\G</c>)は「文書の先頭/末尾」ではなく
///     「行の先頭/末尾」に anchor される(閾値以下の <see cref="TextSearcher"/>
///     は文書全体をひとつの入力として扱うため、閾値境界でアンカー挙動が変わる)。
///     行単位マッチという性質上の必然=呼び出し側が閾値超と閾値以下で厳密に
///     同一挙動を必要とするなら regex アンカーは使わない設計にすること
///     (<c>SnapshotSearcherRegexAnchorTests</c> が挙動を凍結)。</item>
///   <item>閾値超 &amp; WholeWord はエンジン内蔵の Unicode \b ではなく
///     ASCII 単純判定(<see cref="LiteralWindowSearchStrategy"/> の IsWordChar)=
///     全角英数境界で差異が出うる。</item>
///   <item>閾値超 &amp; <see cref="ReplaceInRange"/> は依然として置換後 Fragment を
///     string で組み立てる(大容量 ReplaceAll での真の OOM 回避は P7 送り)。</item>
/// </list>
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
        // regex 戦略は内側 TextSearcher を共有する(必ず _inner 代入の後で構築すること)。
        _regexPerLine = new RegexPerLineSearchStrategy(_inner);
    }

    /// <summary>照合条件が有効(正規表現を構築できた)か。</summary>
    public bool IsValid => _inner.IsValid;

    /// <summary>無効な場合の理由(空パターンや不正な正規表現)。有効なら null。</summary>
    public string? Error => _inner.Error;

    /// <summary>snap 全体のヒット件数。無効なら 0。</summary>
    public int Count(TextSnapshot snap)
    {
        if (!IsValid)
            return 0;
        if (!IsLarge(snap))
            return _inner.Count(Materialize(snap));
        return _opts.UseRegex ? _regexPerLine.Count(snap) : _literal.Count(snap);
    }

    /// <summary>from 以降で最初のヒット(折り返しなし)。無効なら null。</summary>
    public MatchSpan? FindNext(TextSnapshot snap, int from)
    {
        if (!IsValid)
            return null;
        if (!IsLarge(snap))
            return _inner.FindNext(Materialize(snap), from);
        if (from < 0)
            from = 0;
        if (from > snap.CharLength)
            return null;
        return _opts.UseRegex ? _regexPerLine.FindNext(snap, from) : _literal.FindNext(snap, from);
    }

    /// <summary>開始位置(Index)が before より厳密に前にある最後のヒットを返す(折り返しなし)。</summary>
    public MatchSpan? FindPrev(TextSnapshot snap, int before)
    {
        if (!IsValid)
            return null;
        if (!IsLarge(snap))
            return _inner.FindPrev(Materialize(snap), before);
        if (before <= 0)
            return null;
        int b = Math.Min(before, snap.CharLength);
        return _opts.UseRegex ? _regexPerLine.FindPrev(snap, b) : _literal.FindPrev(snap, b);
    }

    /// <summary>span を全ヒット中の何件目か(1始まり, total)。span がヒットでなければ null。</summary>
    public (int Ordinal, int Total)? Locate(TextSnapshot snap, MatchSpan span)
    {
        if (!IsValid)
            return null;
        if (!IsLarge(snap))
            return _inner.Locate(Materialize(snap), span);
        return _opts.UseRegex ? _regexPerLine.Locate(snap, span) : _literal.Locate(snap, span);
    }

    /// <summary>
    /// span が実際のヒットなら置換文字列を返す(正規表現は $1 等展開・リテラルは素のまま)。違えば null。
    /// </summary>
    public string? ReplacementAt(TextSnapshot snap, MatchSpan span, string replacement)
    {
        if (!IsValid)
            return null;
        if (!IsLarge(snap))
            return _inner.ReplacementAt(Materialize(snap), span, replacement);
        return _opts.UseRegex
            ? _regexPerLine.ReplacementAt(snap, span, replacement)
            : _literal.ReplacementAt(snap, span, replacement);
    }

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
        int s = Math.Clamp(start, 0, snap.CharLength);
        int end = Math.Clamp(start + length, s, snap.CharLength);
        if (!IsValid)
            return (snap.GetText(s, end - s), 0);
        if (!IsLarge(snap))
            return _inner.ReplaceInRange(Materialize(snap), start, length, replacement);
        return _opts.UseRegex
            ? _regexPerLine.ReplaceInRange(snap, s, end - s, replacement)
            : _literal.ReplaceInRange(snap, s, end - s, replacement);
    }

    private bool IsLarge(TextSnapshot snap) => snap.CharLength > _thresholdChars;

    private static string Materialize(TextSnapshot snap) => snap.GetText(0, snap.CharLength);
}
