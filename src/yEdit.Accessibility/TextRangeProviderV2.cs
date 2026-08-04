using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;

namespace yEdit.Accessibility;

/// <summary>
/// UIA テキスト範囲(v2)。[Start, End) のオフセット対で表現。
/// v1 の Move スパン保持ロジックを踏襲(文字歩きが動く条件)。
/// テキストアクセスは全て <see cref="_owner"/>.Host の v2 メンバ経由。
/// </summary>
internal sealed class TextRangeProviderV2 : ITextRangeProvider
{
    private readonly TextProviderImplV2 _owner;
    private int _start;
    private int _end;

    public TextRangeProviderV2(TextProviderImplV2 owner, int start, int end)
    {
        _owner = owner;
        int len = owner.Host.TextLength;
        start = System.Math.Clamp(start, 0, len);
        end = System.Math.Clamp(end, 0, len);
        if (start > end)
            (start, end) = (end, start);
        _start = start;
        _end = end;
    }

    public ITextRangeProvider Clone() => new TextRangeProviderV2(_owner, _start, _end);

    public bool Compare(ITextRangeProvider range) =>
        range is TextRangeProviderV2 o && o._start == _start && o._end == _end;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint
    )
    {
        var o = (TextRangeProviderV2)targetRange;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        return a - b;
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var host = _owner.Host;
        int len = host.TextLength;
        int pos = System.Math.Clamp(_start, 0, len);
        switch (unit)
        {
            case TextUnit.Character:
                _start = pos;
                _end = host.NextChar(pos);
                break;
            case TextUnit.Word:
            case TextUnit.Format:
                // 2026-08-04: WordEnd の起点は _start ではなく pos(キャレット位置)。
                // 旧起点 (_start) だと、pos が空白 run の上にあるとき WordStart が run を
                // 飛び越えて左の単語まで戻り、WordEnd(_start) がその単語末で止まるため、
                // スパンがキャレットから cap 文字ぶん離れる。pos 起点なら走査の窓が
                // [pos-(cap-1), pos+cap] とキャレットを中心に据わり、これは構造的に起きない。
                // 典拠 2026-08-03-uia-word-unit-design.md §2.5 の候補 B 欠陥 1 /
                // 2026-08-04-uia-word-unit-fix.md Task 4 §3(導出はそちら)。
                // 「キャレット中心」が指すのは<走査の窓>であってスパンの包含ではない
                // = WordBoundary クラスの <remarks>「窓についてよくある誤読」(2) が正本。
                // 網は UiaWordUnitExpandTests.ExpandToEnclosingUnit_Word_NeverProducesReversedRange。
                _start = host.WordStart(pos);
                _end = host.WordEnd(pos);
                if (_end == _start)
                    _end = host.NextChar(_start);
                break;
            case TextUnit.Line:
            case TextUnit.Paragraph:
                _start = host.LineStartOf(pos);
                _end = host.LineEndNoBreakOf(pos);
                break;
            default: // Page / Document
                _start = 0;
                _end = len;
                break;
        }
        // 2026-08-04 最終レビュー 脆弱性パス V-2: 反転レンジを UIA へ出さないための最終ガード。
        // 単一スナップショット内では host 側の不変条件(WordStart(pos) <= pos <= WordEnd(pos) 等)
        // が成り立つので no-op。効くのは跨スナップショットの競合で、host.WordStart(pos) と
        // host.WordEnd(pos) は別々の RPC 呼び出しなので、その間に UI スレッドが文書を縮めると
        // _end < _start になり得る(実測 _start=773, _end=2)。この形は旧実装でも同じで
        // 本ブランチ由来ではないが、NeverProducesReversedRange が「反転しない」を不変条件として
        // 主張している以上コードを主張へ合わせる。兄弟の MoveEndpointByUnit /
        // MoveEndpointByRange は元から同じ 1 行を持っており、ここだけが持っていなかった。
        if (_end < _start)
            _end = _start;
    }

    public ITextRangeProvider FindAttribute(int attribute, object value, bool backward) => null!;

    // PR-G Task 4 (UIA-L-1): 全範囲を一括で string 化すると本文が巨大(数百 MB〜GB)なとき
    // OOM に至るため、64 KB chunks / 1 KB overlap で走査する。
    // overlap は「UIA 経由の検索語 max 512 chars 想定」の 2 倍余裕。
    // 検索語が overlap を越える異常入力に対しては step を text.Length - 1 まで広げて
    // straddling match を保証する。needle が chunk サイズを越える場合は FindText 冒頭で
    // 早期 null 返却する (UIA-L-1 M-1 fixup: 10^9 iterations DoS 回避)。
    private const int FindTextChunkSize = 64 * 1024;
    private const int FindTextOverlap = 1024;

    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        if (string.IsNullOrEmpty(text) || s >= e)
            return null!;
        // 検索語が chunk サイズを超える場合、どの 1 chunk にも収まらないため必ずマッチ不能。
        // オーバーサイズ needle を明示的に弾くことで、`text.Length > ChunkSize` 時に step=1 に
        // 落ちて 10^9 回ループする DoS を防ぐ (UIA-L-1 M-1 fixup)。
        if (text.Length > FindTextChunkSize)
            return null!;
        var cmp = ignoreCase
            ? System.StringComparison.OrdinalIgnoreCase
            : System.StringComparison.Ordinal;

        // chunk 境界を跨ぐヒットを取りこぼさないため、隣接 chunk と max(Overlap, text.Length-1) 分だけ
        // オーバーラップさせる。step が正になる範囲であることは Math.Max で保証。
        int overlap = System.Math.Max(FindTextOverlap, text.Length - 1);
        int step = FindTextChunkSize - overlap;
        // 上のオーバーサイズ guard で text.Length <= FindTextChunkSize は保証済のため、
        // step = FindTextChunkSize - max(Overlap, text.Length-1) >= 1 が数学的に成立する。
        // 以下は belt-and-suspenders (万一定数が改変された時の前進停止防止)。
        if (step <= 0)
            step = 1;

        if (!backward)
        {
            int pos = s;
            while (pos < e)
            {
                int chunkEnd = System.Math.Min(e, pos + FindTextChunkSize);
                int chunkLen = chunkEnd - pos;
                string chunk = host.GetTextRange(pos, chunkLen);
                int idx = chunk.IndexOf(text, cmp);
                if (idx >= 0)
                {
                    int hitStart = pos + idx;
                    int hitEnd = hitStart + text.Length;
                    if (hitEnd <= e)
                        return new TextRangeProviderV2(_owner, hitStart, hitEnd);
                    return null!;
                }
                if (chunkEnd >= e)
                    break;
                pos += step;
            }
            return null!;
        }
        else
        {
            int pos = e;
            while (pos > s)
            {
                int chunkStart = System.Math.Max(s, pos - FindTextChunkSize);
                int chunkLen = pos - chunkStart;
                string chunk = host.GetTextRange(chunkStart, chunkLen);
                int idx = chunk.LastIndexOf(text, cmp);
                if (idx >= 0)
                {
                    int hitStart = chunkStart + idx;
                    int hitEnd = hitStart + text.Length;
                    if (hitStart >= s && hitEnd <= e)
                        return new TextRangeProviderV2(_owner, hitStart, hitEnd);
                    return null!;
                }
                if (chunkStart <= s)
                    break;
                pos -= step;
            }
            return null!;
        }
    }

    public object GetAttributeValue(int attribute) => AutomationElement.NotSupported;

    public double[] GetBoundingRectangles() => _owner.Host.GetBoundingRectangles(_start, _end);

    public IRawElementProviderSimple GetEnclosingElement() => _owner.RootProvider;

    public string GetText(int maxLength)
    {
        var host = _owner.Host;
        int s = System.Math.Clamp(_start, 0, host.TextLength);
        int e = System.Math.Clamp(_end, 0, host.TextLength);
        int count = e - s;
        if (count < 0)
            count = 0;
        if (maxLength >= 0 && count > maxLength)
            count = maxLength;
        return host.GetTextRange(s, count);
    }

    public int Move(TextUnit unit, int count)
    {
        var host = _owner.Host;
        bool wasDegenerate = _start == _end;

        int pos = _start;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++)
            {
                int n = StepForward(host, pos, unit);
                if (n == pos)
                    break;
                pos = n;
                moved++;
            }
        else if (count < 0)
            for (int i = 0; i < -count; i++)
            {
                int p = StepBackward(host, pos, unit);
                if (p == pos)
                    break;
                pos = p;
                moved++;
            }
        _start = _end = pos;

        // v1 実装の Move スパン保持を踏襲(文字歩き=Expand(Char)→Move(Char,1)→GetText で
        // 2 文字目以降が空にならないように、非退化だった元の状態を復元)
        if (!wasDegenerate)
            ExpandToEnclosingUnit(unit);

        return count < 0 ? -moved : moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var host = _owner.Host;
        int pos = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = 0;
        if (count > 0)
            for (int i = 0; i < count; i++)
            {
                int n = StepForward(host, pos, unit);
                if (n == pos)
                    break;
                pos = n;
                moved++;
            }
        else if (count < 0)
            for (int i = 0; i < -count; i++)
            {
                int p = StepBackward(host, pos, unit);
                if (p == pos)
                    break;
                pos = p;
                moved++;
            }

        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = pos;
            if (_end < _start)
                _end = _start;
        }
        else
        {
            _end = pos;
            if (_start > _end)
                _start = _end;
        }
        return count < 0 ? -moved : moved;
    }

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider targetRange,
        TextPatternRangeEndpoint targetEndpoint
    )
    {
        var o = (TextRangeProviderV2)targetRange;
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? o._start : o._end;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            _start = target;
            if (_end < _start)
                _end = _start;
        }
        else
        {
            _end = target;
            if (_start > _end)
                _start = _end;
        }
    }

    public void Select() => _owner.Host.SetSelection(_start, _end);

    public void AddToSelection() { /* SupportedTextSelection.Single のため無効 */
    }

    public void RemoveFromSelection() { /* SupportedTextSelection.Single のため無効 */
    }

    /// <summary>
    /// 範囲を可視域へスクロールする。<paramref name="alignToTop"/> が true なら範囲先頭を、
    /// false なら範囲末尾を対象にする。判断(可視判定・整列・クランプ)は host 側に集約し、
    /// ここは純委譲に留める。選択・キャレットは変更しない。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.1。
    /// </summary>
    public void ScrollIntoView(bool alignToTop) =>
        _owner.Host.ScrollRangeIntoView(_start, _end, alignToTop);

    public IRawElementProviderSimple[] GetChildren() =>
        System.Array.Empty<IRawElementProviderSimple>();

    // ---------- 単位ステップ(host v2 メンバ経由) ----------

    private static int StepForward(IUiaTextHost host, int pos, TextUnit unit) =>
        unit switch
        {
            TextUnit.Character => host.NextChar(pos),
            TextUnit.Word or TextUnit.Format => host.NextWordStart(pos),
            TextUnit.Line or TextUnit.Paragraph => host.LineEnd(pos),
            _ => host.TextLength,
        };

    private static int StepBackward(IUiaTextHost host, int pos, TextUnit unit) =>
        unit switch
        {
            TextUnit.Character => host.PrevChar(pos),
            TextUnit.Word or TextUnit.Format => host.PrevWordStart(pos),
            TextUnit.Line or TextUnit.Paragraph => LineBackward(host, pos),
            _ => 0,
        };

    private static int LineBackward(IUiaTextHost host, int pos)
    {
        int ls = host.LineStartOf(pos);
        if (ls < pos)
            return ls; // 行頭でなければ行頭へ
        if (ls == 0)
            return 0;
        return host.LineStartOf(ls - 1); // 行頭なら前行の行頭へ
    }
}
