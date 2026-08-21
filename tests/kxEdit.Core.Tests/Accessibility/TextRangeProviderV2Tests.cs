using System.Windows.Automation.Text;
using kxEdit.Accessibility;
using Xunit;

namespace kxEdit.Core.Tests.Accessibility;

public class TextRangeProviderV2Tests
{
    private sealed class InMemoryHost : IUiaTextHost
    {
        private readonly string _text;

        public InMemoryHost(string text)
        {
            _text = text;
        }

        public string GetTextRange(int start, int length)
        {
            start = System.Math.Clamp(start, 0, _text.Length);
            length = System.Math.Clamp(length, 0, _text.Length - start);
            return _text.Substring(start, length);
        }

        public int TextLength => _text.Length;

        public (int Start, int End) GetSelection() => (0, 0);

        public void SetSelection(int s, int e) { }

        public int NextChar(int o) => System.Math.Min(o + 1, _text.Length);

        public int PrevChar(int o) => System.Math.Max(o - 1, 0);

        public int LineStartOf(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && _text[i - 1] != '\n')
                i--;
            return i;
        }

        public int LineEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && _text[i] != '\n')
                i++;
            if (i < _text.Length)
                i++;
            return i;
        }

        public int LineEndNoBreakOf(int o)
        {
            int e = LineEnd(o);
            if (e > 0 && _text[e - 1] == '\n')
                e--;
            return e;
        }

        public int WordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1]))
                i--;
            return i;
        }

        public int WordEnd(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i < _text.Length && !char.IsWhiteSpace(_text[i]))
                i++;
            return i;
        }

        public int NextWordStart(int o)
        {
            int i = WordEnd(o);
            while (i < _text.Length && char.IsWhiteSpace(_text[i]))
                i++;
            return i;
        }

        public int PrevWordStart(int o)
        {
            int i = System.Math.Clamp(o, 0, _text.Length);
            while (i > 0 && char.IsWhiteSpace(_text[i - 1]))
                i--;
            while (i > 0 && !char.IsWhiteSpace(_text[i - 1]))
                i--;
            return i;
        }

        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;

        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();

        public int OffsetFromScreenPoint(double x, double y) => 0;

        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => System.Windows.Automation.ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";

        public void SetFocus() { }

        // Task 1a: ScrollIntoView の委譲検証用に引数を記録する。
        public (int Start, int End, bool AlignToTop)? LastScroll { get; private set; }

        public void ScrollRangeIntoView(int start, int end, bool alignToTop) =>
            LastScroll = (start, end, alignToTop);

        // Task 2: 本 stub は viewport を持たないため「文書全体が可視」で固定する。
        public (int Start, int End) GetVisibleRange() => (0, TextLength);
    }

    private static (InMemoryHost Host, TextProviderImplV2 Provider) MakeProviderWithHost(
        string text
    )
    {
        var host = new InMemoryHost(text);
        var root = new TextControlProviderV2(host);
        return (host, new TextProviderImplV2(host, root));
    }

    private static TextProviderImplV2 MakeProvider(string text) =>
        MakeProviderWithHost(text).Provider;

    [Fact]
    public void ExpandToEnclosingUnit_Character_ReturnsOneCodePoint()
    {
        var p = MakeProvider("abcdef");
        var r = new TextRangeProviderV2(p, 2, 2);
        r.ExpandToEnclosingUnit(TextUnit.Character);
        Assert.Equal("c", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Word_ExpandsToWordSpan()
    {
        var p = MakeProvider("hello world");
        var r = new TextRangeProviderV2(p, 3, 3);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal("hello", r.GetText(int.MaxValue));
    }

    /// <summary>
    /// 2026-08-04 Task 4: <c>ExpandToEnclosingUnit(Word)</c> が host に何を尋ね、
    /// それを<b>どう合成するか</b>(<c>WordStart</c> + <c>WordEnd</c>、縮退したら
    /// <c>NextChar</c> フォールバック)を固定する。
    /// </summary>
    /// <remarks>
    /// <b>名前が「起点がキャレットに固定されている」と主張しないのは意図的</b>
    /// (2026-08-04 最終レビュー Minor-2)。下の stub host では anchored-at-caret と
    /// anchored-at-start を<b>原理的に区別できない</b>ので、名乗れるのは合成の形までである。
    ///
    /// <b>本テストは起点変更の差を検出しない</b>(実測で確認済み=変更前後とも全 3 ケース緑)。
    /// 上の <c>InMemoryHost</c> は空白区切りの素朴実装で、原理的に判別できないため:
    /// <c>WordStart(pos)</c> が <c>_start &lt; pos</c> を返すのは <c>[_start, pos)</c> が
    /// <b>すべて非空白</b>のときに限られるので、<c>WordEnd(_start)</c> は必ずその連続を
    /// 走り抜けて <c>WordEnd(pos)</c> と同じ位置に着く。<c>_start == pos</c> なら自明に同じ。
    /// よって任意の text / pos で両者は一致する。
    ///
    /// 判別できる網は Editor 層の end-to-end 側
    /// (<c>UiaWordUnitExpandTests</c>=本物の Core 規則 × 本物のホスト)に置いてある。
    /// ここは「Provider が host の <c>WordStart</c> / <c>WordEnd</c> / <c>NextChar</c> を
    /// どう組むか」の形だけを押さえる役割に留まる。
    ///
    /// <c>"hello    world"</c> pos=7 が <c>" "</c> になるのは stub の規則ゆえ:
    /// pos の左隣も空白なので <c>WordStart(7) == 7</c>、<c>WordEnd(7) == 7</c> で縮退し、
    /// <c>NextChar</c> フォールバックが 1 文字ぶんの空白を返す。
    /// </remarks>
    [Theory]
    [InlineData("hello world", 3, "hello")]
    [InlineData("hello world", 8, "world")]
    [InlineData("hello    world", 7, " ")] // 空白 run の内側 = 縮退 → NextChar フォールバック
    public void ExpandToEnclosingUnit_Word_ComposesWordStartWordEndWithNextCharFallback(
        string text,
        int pos,
        string expected
    )
    {
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, pos, pos);
        r.ExpandToEnclosingUnit(TextUnit.Word);
        Assert.Equal(expected, r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_ExcludesLineBreak()
    {
        var p = MakeProvider("aaa\nbbb\nccc");
        var r = new TextRangeProviderV2(p, 5, 5);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("bbb", r.GetText(int.MaxValue));
    }

    [Fact]
    public void ExpandToEnclosingUnit_Line_EmptyLineHasZeroLength()
    {
        var p = MakeProvider("aaa\n\nbbb");
        var r = new TextRangeProviderV2(p, 4, 4);
        r.ExpandToEnclosingUnit(TextUnit.Line);
        Assert.Equal("", r.GetText(int.MaxValue));
    }

    [Fact]
    public void Move_CharForward_PreservesUnitSpan()
    {
        // 文字歩き挙動: Expand(Char) → Move(Char, 1) → GetText を繰り返し
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 0);
        r.ExpandToEnclosingUnit(TextUnit.Character); // "a"
        int moved = r.Move(TextUnit.Character, 1);
        Assert.Equal(1, moved);
        Assert.Equal("b", r.GetText(int.MaxValue)); // b が読める(退化させない)
    }

    [Fact]
    public void GetText_RangeIsClamped()
    {
        var p = MakeProvider("abc");
        var r = new TextRangeProviderV2(p, 0, 3);
        Assert.Equal("abc", r.GetText(int.MaxValue));
        Assert.Equal("ab", r.GetText(2));
    }

    [Fact]
    public void FindText_LocalSearch_ReturnsSubrange()
    {
        var p = MakeProvider("aabbcc");
        var r = new TextRangeProviderV2(p, 0, 6);
        var found = r.FindText("bb", false, false) as TextRangeProviderV2;
        Assert.NotNull(found);
        Assert.Equal("bb", found!.GetText(int.MaxValue));
    }

    // ---------- PR-G Task 4 (UIA-L-1): chunked FindText ----------
    // 実装は 64 KB chunks / 1 KB overlap で走査する。以下のテストは
    //   ・chunk 境界跨ぎ / 境界丁度 / 複数 chunk 越え (forward)
    //   ・chunk 境界跨ぎ (backward)
    //   ・1 GB 相当の fake buffer で単一 GetTextRange が 64 KB を越えないこと
    // を機械固定する。

    private const int ChunkSize = 64 * 1024; // TextRangeProviderV2.FindText と合わせる
    private const int Overlap = 1024;

    /// <summary>
    /// 指定オフセットに needle を埋め込んだ長さ length のフィラー文字列を作る。
    /// フィラーは 'A' 一色にして、needle と誤ヒットしないようにする。
    /// </summary>
    private static string BuildTextWithNeedleAt(int length, int needleOffset, string needle)
    {
        var sb = new System.Text.StringBuilder(length);
        sb.Append('A', length);
        for (int i = 0; i < needle.Length; i++)
            sb[needleOffset + i] = needle[i];
        return sb.ToString();
    }

    /// <summary>
    /// 指定 2 オフセットに needle を埋め込んだ長さ totalLen のフィラー文字列を作る。
    /// backward 検索が rightmost を返すかを差別化するための fixture。
    /// </summary>
    private static string BuildTextWithTwoNeedlesAt(int pos1, int pos2, string needle, int totalLen)
    {
        var sb = new System.Text.StringBuilder(totalLen);
        sb.Append('A', totalLen);
        for (int i = 0; i < needle.Length; i++)
            sb[pos1 + i] = needle[i];
        for (int i = 0; i < needle.Length; i++)
            sb[pos2 + i] = needle[i];
        return sb.ToString();
    }

    [Fact]
    public void FindText_Forward_MatchAcrossChunkBoundary()
    {
        // needle "needle" (6 chars) が index ChunkSize-2 = 65534 に置かれる。
        // これは 1 chunk 目 [0, 65536) と 2 chunk 目 [64512, 130048) に跨る。
        const string needle = "needle";
        int needleAt = ChunkSize - 2; // 65534
        string text = BuildTextWithNeedleAt(ChunkSize + Overlap + 16, needleAt, needle);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needle, false, false) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needle, found!.GetText(int.MaxValue));
        // CompareEndpoints で絶対 offset を検証(Start endpoint 同士の差 = needleAt)
        var anchor = new TextRangeProviderV2(p, 0, 0);
        int startOffset = found.CompareEndpoints(
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            anchor,
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
        );
        Assert.Equal(needleAt, startOffset);
    }

    [Fact]
    public void FindText_Forward_MatchExactlyAtChunkBoundary()
    {
        // needle が index ChunkSize = 65536 ちょうどに始まる。
        // 1 chunk 目 [0, 65536) には含まれず、2 chunk 目 [64512, 130048) の
        // 内側 offset 1024 に位置する。
        const string needle = "needle";
        int needleAt = ChunkSize; // 65536
        string text = BuildTextWithNeedleAt(ChunkSize * 2, needleAt, needle);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needle, false, false) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needle, found!.GetText(int.MaxValue));
        var anchor = new TextRangeProviderV2(p, 0, 0);
        int startOffset = found.CompareEndpoints(
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            anchor,
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
        );
        Assert.Equal(needleAt, startOffset);
    }

    [Fact]
    public void FindText_Forward_MatchInThirdChunk()
    {
        // needle が index 200000 に位置する = 3 chunk 越え。
        // step = ChunkSize - Overlap = 64512 のため、chunk 開始位置は
        // 0, 64512, 129024, 193536, ... のいずれか。200000 は 193536 chunk 内。
        const string needle = "needle";
        int needleAt = 200000;
        string text = BuildTextWithNeedleAt(needleAt + needle.Length + 128, needleAt, needle);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needle, false, false) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needle, found!.GetText(int.MaxValue));
        var anchor = new TextRangeProviderV2(p, 0, 0);
        int startOffset = found.CompareEndpoints(
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            anchor,
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
        );
        Assert.Equal(needleAt, startOffset);
    }

    [Fact]
    public void FindText_Backward_MatchAcrossChunkBoundary()
    {
        // backward 走査でも chunk 境界跨ぎがヒットすること。
        // needle を index 65534 に置いた 200000 chars の buffer を後方検索する。
        const string needle = "needle";
        int needleAt = ChunkSize - 2; // 65534
        string text = BuildTextWithNeedleAt(200000, needleAt, needle);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needle, true, false) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needle, found!.GetText(int.MaxValue));
        var anchor = new TextRangeProviderV2(p, 0, 0);
        int startOffset = found.CompareEndpoints(
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,
            anchor,
            System.Windows.Automation.Text.TextPatternRangeEndpoint.Start
        );
        Assert.Equal(needleAt, startOffset);
    }

    [Fact]
    public void FindText_Backward_ReturnsRightmostMatch_AcrossChunks()
    {
        // 2 個の needle を配置 (offset 100, 150000) → backward 検索は右側 (150000) を返す。
        // totalLen=200000 での backward chunk 訪問順は [134464,200000) → [69952,135488) → ...
        // rightmost needle 150000 は最初に訪問する chunk 内、leftmost needle 100 は最後に
        // 訪問する chunk 内。backward loop が右→左に走査していない (buggy 左→右) 場合は
        // 100 が返って test が落ちる = backward semantics を機械固定 (UIA-L-1 M-1 fixup)。
        const string needle = "kxedit";
        int leftAt = 100;
        int rightAt = 150_000;
        int totalLen = 200_000;
        string text = BuildTextWithTwoNeedlesAt(leftAt, rightAt, needle, totalLen);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needle, true, false) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needle, found!.GetText(int.MaxValue));
        var anchor = new TextRangeProviderV2(p, 0, 0);
        int startOffset = found.CompareEndpoints(
            TextPatternRangeEndpoint.Start,
            anchor,
            TextPatternRangeEndpoint.Start
        );
        Assert.Equal(rightAt, startOffset);
    }

    [Fact]
    public void FindText_EmptyRange_ReturnsNull()
    {
        // s == e (0-length range) は早期 null 返却 (chunk loop に入らない invariant を pin)。
        var p = MakeProvider("hello world");
        var r = new TextRangeProviderV2(p, 5, 5);
        var found = r.FindText("h", false, false);
        Assert.Null(found);
    }

    [Fact]
    public void FindText_NeedleLongerThanChunkSize_ReturnsNull_WithoutSpinning()
    {
        // text.Length > FindTextChunkSize は必ず null 返却 (10^9 iterations の DoS 回避 = M-1 fixup)。
        // 100 KB のバッファを 'a' で埋め、needle は 70 KB (> 64 KB chunk) の 'a' 連鎖。
        // 早期 return が無いと step=1 で e-s 回近くループするため、test 時間が長引く=間接的にも検知。
        var buffer = new string('a', 100_000);
        var p = MakeProvider(buffer);
        var r = new TextRangeProviderV2(p, 0, buffer.Length);
        var needle = new string('a', 70_000);
        var found = r.FindText(needle, false, false);
        Assert.Null(found);
    }

    [Fact]
    public void FindText_IgnoreCase_StillWorksAcrossChunkBoundary()
    {
        // ignoreCase フラグが chunk 走査でも生きること。
        const string needleUpper = "NEEDLE";
        const string needleLower = "needle";
        int needleAt = ChunkSize - 3;
        string text = BuildTextWithNeedleAt(ChunkSize + Overlap + 16, needleAt, needleLower);
        var p = MakeProvider(text);
        var r = new TextRangeProviderV2(p, 0, text.Length);

        var found = r.FindText(needleUpper, false, true) as TextRangeProviderV2;

        Assert.NotNull(found);
        Assert.Equal(needleLower, found!.GetText(int.MaxValue));
    }

    /// <summary>
    /// 1 GB 相当のフェイクバッファ。GetTextRange の呼び出しごとに
    /// 要求長を記録し、実データは要求長分の 'A' を返す(=needle 'Q' は絶対ヒットしない)。
    /// </summary>
    private sealed class LargeSyntheticHost : IUiaTextHost
    {
        // 同一長の要求を繰り返す chunked scan で毎回 alloc しないよう cache する。
        private static readonly string ChunkFiller = new string('A', 64 * 1024);

        public LargeSyntheticHost(int textLength)
        {
            TextLength = textLength;
        }

        public int MaxRequestedLength { get; private set; }
        public int GetTextRangeCalls { get; private set; }

        public string GetTextRange(int start, int length)
        {
            GetTextRangeCalls++;
            if (length > MaxRequestedLength)
                MaxRequestedLength = length;
            if (length <= 0)
                return string.Empty;
            if (length == ChunkFiller.Length)
                return ChunkFiller;
            if (length < ChunkFiller.Length)
                return ChunkFiller.Substring(0, length);
            // 実装が chunked に切っていれば ここには来ない(64 KB を越える単一要求は仕様外)。
            // ここに来た場合は明示的に失敗させる(大きな alloc の兆候)。
            throw new System.InvalidOperationException(
                $"GetTextRange length={length} exceeds 64 KB chunk"
            );
        }

        public int TextLength { get; }

        public (int Start, int End) GetSelection() => (0, 0);

        public void SetSelection(int s, int e) { }

        public int NextChar(int o) => System.Math.Min(o + 1, TextLength);

        public int PrevChar(int o) => System.Math.Max(o - 1, 0);

        public int LineStartOf(int o) => 0;

        public int LineEnd(int o) => TextLength;

        public int LineEndNoBreakOf(int o) => TextLength;

        public int WordStart(int o) => 0;

        public int WordEnd(int o) => TextLength;

        public int NextWordStart(int o) => TextLength;

        public int PrevWordStart(int o) => 0;

        public System.Windows.Rect BoundingRectangle => System.Windows.Rect.Empty;

        public double[] GetBoundingRectangles(int s, int e) => System.Array.Empty<double>();

        public int OffsetFromScreenPoint(double x, double y) => 0;

        public nint Handle => System.IntPtr.Zero;
        public bool HasFocus => false;
        public int ControlTypeId => System.Windows.Automation.ControlType.Document.Id;
        public string Name => "本文";
        public string AutomationId => "editor";

        public void SetFocus() { }

        public void ScrollRangeIntoView(int start, int end, bool alignToTop) { }

        // Task 2: 本 stub は viewport を持たないため「文書全体が可視」で固定する。
        public (int Start, int End) GetVisibleRange() => (0, TextLength);
    }

    [Fact]
    public void FindText_LargeRange_NoOom_MaxSingleReadIsChunkBounded()
    {
        // 1 GB 相当の fake buffer を全走査しても、単一の GetTextRange 要求長が
        // 64 KB を越えないことを機械固定する(旧実装は host.GetTextRange(0, 1 GB) を
        // 呼び出して OOM に至る)。
        const int OneGiB = 1_073_741_824;
        var host = new LargeSyntheticHost(OneGiB);
        var root = new TextControlProviderV2(host);
        var provider = new TextProviderImplV2(host, root);
        var range = new TextRangeProviderV2(provider, 0, OneGiB);

        var found = range.FindText("Q", false, false);

        Assert.Null(found);
        Assert.True(
            host.MaxRequestedLength <= ChunkSize,
            $"Expected MaxRequestedLength <= {ChunkSize} but got {host.MaxRequestedLength}"
        );
        Assert.True(host.GetTextRangeCalls > 0);
    }

    [Fact]
    public void ScrollIntoView_DelegatesRangeAndAlignToTop_ToHost()
    {
        var (host, p) = MakeProviderWithHost("line0\nline1\nline2\nline3");
        var r = new TextRangeProviderV2(p, 6, 11); // "line1"

        r.ScrollIntoView(alignToTop: true);

        Assert.Equal((6, 11, true), host.LastScroll);
    }

    [Fact]
    public void ScrollIntoView_PassesAlignToTopFalse_Unchanged()
    {
        var (host, p) = MakeProviderWithHost("line0\nline1\nline2\nline3");
        var r = new TextRangeProviderV2(p, 6, 11);

        r.ScrollIntoView(alignToTop: false);

        Assert.Equal((6, 11, false), host.LastScroll);
    }

    [Fact]
    public void ScrollIntoView_DelegatesDegenerateRange_ToHost()
    {
        // 縮退範囲 (start == end) でも委譲される。SR のレビューカーソルは
        // 縮退範囲を歩くため、ここが落ちると実機で効かない。
        var (host, p) = MakeProviderWithHost("line0\nline1");
        var r = new TextRangeProviderV2(p, 3, 3);

        r.ScrollIntoView(alignToTop: true);

        Assert.Equal((3, 3, true), host.LastScroll);
    }
}
