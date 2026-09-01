using System.Text;
using System.Text.RegularExpressions;
using kxEdit.Core.Text;

namespace kxEdit.Core.Search;

/// <summary>
/// 単一ファイル検索・置換の照合エンジン。内部を .NET Regex に統一する
/// （リテラルは Regex.Escape、単語単位は \b 境界、大小無視は IgnoreCase）。
/// 大小無視時も含め常に CultureInvariant を用いる（トルコ語 i 問題等の
/// カルチャ依存を避ける安全側）。オフセットは UTF-16 文字位置。
/// 正規表現の不正は例外でなく IsValid/Error で返す。照合実行には 1 秒の
/// matchTimeout を設けており、破滅的バックトラッキング時は照合メソッドが
/// RegexMatchTimeoutException を送出し得る（Core では捕捉せず呼び出し側へ伝播）。
/// </summary>
public sealed class TextSearcher
{
    private readonly Regex? _regex;
    private readonly bool _expand; // 正規表現モードのみ置換文字列の $ を展開

    /// <summary>照合条件が有効（正規表現を構築できた）か。</summary>
    public bool IsValid => _regex is not null;

    /// <summary>無効な場合の理由（空パターンや不正な正規表現）。有効なら null。</summary>
    public string? Error { get; }

    /// <summary>照合条件から照合エンジンを構築する。不正でも例外は投げず IsValid/Error で返す。</summary>
    public TextSearcher(SearchOptions options)
    {
        _expand = options.UseRegex;
        if (string.IsNullOrEmpty(options.Pattern))
        {
            Error = "検索文字列が空です。";
            return;
        }
        string body = options.UseRegex ? options.Pattern : Regex.Escape(options.Pattern);
        if (options.WholeWord)
            body = $@"\b(?:{body})\b";
        var opts = RegexOptions.CultureInvariant;
        if (!options.MatchCase)
            opts |= RegexOptions.IgnoreCase;
        try
        {
            _regex = new Regex(body, opts, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }
    }

    /// <summary>
    /// text 全体のヒット件数。無効なら 0。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public int Count(string text) => _regex is null ? 0 : _regex.Count(text);

    /// <summary>
    /// from 以降で最初のヒット（折り返しなし）。
    /// a*・\b・(?=...) 等のゼロ幅パターンでは Length=0 の MatchSpan を返し得る。
    /// 前方へ歩進する呼び出し側は同位置の無限ループを避けるため、from を
    /// max(1, Length) 分進めること。折り返しはしない。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public MatchSpan? FindNext(string text, int from)
    {
        if (_regex is null)
            return null;
        if (from < 0)
            from = 0;
        if (from > text.Length)
            return null;
        var m = _regex.Match(text, from);
        return m.Success ? new MatchSpan(m.Index, m.Length) : null;
    }

    /// <summary>
    /// 開始位置（Index）が before より厳密に前にある最後のヒットを返す（折り返しなし）。
    /// 開始が before より前で終端が before を越える“またぎ”ヒットも返り得る。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public MatchSpan? FindPrev(string text, int before)
    {
        if (_regex is null)
            return null;
        MatchSpan? last = null;
        foreach (Match m in _regex.Matches(text))
        {
            if (m.Index >= before)
                break;
            last = new MatchSpan(m.Index, m.Length);
        }
        return last;
    }

    /// <summary>
    /// span を全ヒット中の何件目か（1始まり, total）。span がヒットでなければ null。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public (int Ordinal, int Total)? Locate(string text, MatchSpan span)
    {
        if (_regex is null)
            return null;
        int ordinal = 0,
            total = 0;
        bool found = false;
        foreach (Match m in _regex.Matches(text))
        {
            total++;
            if (m.Index == span.Start && m.Length == span.Length)
            {
                ordinal = total;
                found = true;
            }
        }
        return found ? (ordinal, total) : null;
    }

    /// <summary>
    /// span が実際のヒットなら置換文字列を返す（正規表現は $1 等展開・リテラルは素のまま）。違えば null。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public string? ReplacementAt(string text, MatchSpan span, string replacement)
    {
        if (_regex is null)
            return null;
        if (span.Start < 0 || span.Start > text.Length)
            return null;
        var m = _regex.Match(text, span.Start);
        if (!m.Success || m.Index != span.Start || m.Length != span.Length)
            return null;
        return Expand(m, replacement);
    }

    /// <summary>
    /// 全文置換。返すのは置換後の全文（=新断片）と件数。
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public (string Fragment, int Count) ReplaceAll(string text, string replacement) =>
        ReplaceInRange(text, 0, text.Length, replacement);

    /// <summary>
    /// [start, start+length) に完全に収まるヒットだけ置換し、その範囲の置換後断片と件数を返す。
    /// 範囲外・境界をまたぐヒットは対象外。start/length は text 範囲へクランプする。
    /// エディタはこの断片で当該文字範囲を差し替える。
    /// <para>
    /// <b>照合は範囲始端へ再アンカーする</b>(<c>Match(text, scan)</c>)。全文照合の結果を
    /// 捨てるだけだと、範囲直前から始まるヒットが範囲内の文字を食って範囲内のヒットが
    /// 消える(監査 M-29: <c>"aaa"</c> の <c>[1,3)</c> を <c>aa</c> で 0 件)。
    /// <c>startat</c> は<b>入力を切らない</b>ので <c>\b</c>・先読み・後読みは全文文脈のまま。
    /// </para>
    /// <para>
    /// <b>ゼロ幅マッチの挿入点は論理文字の境界まで後退させる</b>
    /// (<c>EditorControl.ReplaceCharRangeExact</c> と同じ規則。規則の所有者は
    /// <see cref="Text.TextBoundary"/>)。後退させないと CRLF やサロゲートペアの内側へ挿入して
    /// しまい、書き戻し時に孤立サロゲートが U+FFFD へ潰れる。後退先が範囲始端より前
    /// / 既に出力した位置より前になるマッチは<b>スキップし件数にも数えない</b>。
    /// 後退の判定は<b>元テキスト</b>に対して行う(単発置換も編集前スナップショットで判定する)。
    /// </para>
    /// 複雑な正規表現では RegexMatchTimeoutException が送出され得る（1秒）。
    /// </summary>
    public (string Fragment, int Count) ReplaceInRange(
        string text,
        int start,
        int length,
        string replacement
    )
    {
        int s = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + length, s, text.Length);
        if (_regex is null)
            return (text.Substring(s, end - s), 0);

        var sb = new StringBuilder();
        int count = 0,
            pos = s,
            scan = s;
        while (scan <= end)
        {
            var m = _regex.Match(text, scan);
            if (!m.Success || m.Index + m.Length > end)
                break;
            int at =
                m.Length == 0
                    ? TextBoundary.SnapToLogicalCharStart(text.AsSpan(), m.Index)
                    : m.Index;
            if (at < pos)
            {
                // 範囲始端より前 / 既に出力した位置より前へは書けない。
                scan = m.Index + 1; // ゼロ幅なので必ず 1 進める(同位置の無限ループ回避)
                continue;
            }
            sb.Append(text, pos, at - pos);
            sb.Append(Expand(m, replacement));
            pos = at + m.Length;
            count++;
            scan = m.Index + Math.Max(1, m.Length);
        }
        sb.Append(text, pos, end - pos);
        return (sb.ToString(), count);
    }

    private string Expand(Match m, string replacement) =>
        _expand ? m.Result(replacement) : replacement;
}
