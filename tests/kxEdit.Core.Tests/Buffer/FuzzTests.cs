using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Tests.Buffers;

/// <summary>
/// ランダム編集ファズ: 素朴string実装(モデル)との突合。
/// モデルのスナップ規則はバッファ実装をコピーせず char.IsLowSurrogate で独立実装。
/// モデルのUndoは全文スタックで単純化(毎操作 BreakUndoCoalescing。coalescing の
/// 正しさは UndoTests の決定的テストが担保)。
/// 操作数は環境変数 KXEDIT_FUZZ_OPS で増量可(既定3,000)。
/// </summary>
public class FuzzTests
{
    private const int MaxDocLength = 300_000; // これを超えたらDelete優先(ファズ時間の抑制)
    private const int ModelUndoCap = 256; // モデル側の全文スタックのメモリ抑制

    // collection-expression `=> [1, 2, 3, 42, 20260705]` は CA1825 が誤検出するため
    // (analyzer が TheoryData<int> の CollectionBuilder パスを見誤り「長さ 0 の配列」と判断する)
    // 明示 initializer で回避する。挙動は完全同一(TheoryData<int>.Add を各要素で呼ぶ)。
    public static TheoryData<int> Seeds => new() { 1, 2, 3, 42, 20260705 };

    private static readonly string[] Pool =
    [
        "word",
        "hello world ",
        "test123",
        "ひらがな漢字とカナ",
        "😀🈴",
        "😀",
        "\n",
        "\r",
        "\r\n",
        "line1\nline2\r\nline3\r",
        "あiうeお😀\r\n混合テキスト",
        "x",
    ];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_edits_match_naive_model(int seed)
    {
        int ops =
            int.TryParse(Environment.GetEnvironmentVariable("KXEDIT_FUZZ_OPS"), out int v) && v > 0
                ? v
                : 3000;
        var rnd = new Random(seed);

        const string initial = "初期文書\r\nsecond line\n😀\r";
        var buffer = TextBuffer.FromString(initial);
        var initialSnap = buffer.Current;

        string text = initial;
        var undoStack = new List<string>();
        var redoStack = new List<string>();

        for (int op = 1; op <= ops; op++)
        {
            int roll = rnd.Next(100);
            if (text.Length > MaxDocLength)
                roll = 45; // Delete優先

            if (roll < 40)
            { // Insert 40%
                int pos = rnd.Next(text.Length + 1);
                string ins = Material(rnd);
                buffer.Insert(pos, ins);
                text = ModelSplice(text, pos, 0, ins, undoStack, redoStack);
            }
            else if (roll < 70)
            { // Delete 30%
                int pos = rnd.Next(text.Length + 1);
                int len = Math.Min(rnd.Next(1, 31), text.Length - pos);
                buffer.Delete(pos, len);
                text = ModelSplice(text, pos, len, "", undoStack, redoStack);
            }
            else if (roll < 80)
            { // Replace 10%
                int pos = rnd.Next(text.Length + 1);
                int len = Math.Min(rnd.Next(1, 31), text.Length - pos);
                string ins = Material(rnd);
                buffer.Replace(pos, len, ins);
                text = ModelSplice(text, pos, len, ins, undoStack, redoStack);
            }
            else if (roll < 90)
            { // Undo 10%(モデル側が戻せるときのみ=キャップ超で捨てた深部へは行かない)
                if (undoStack.Count > 0)
                {
                    Assert.True(buffer.CanUndo);
                    Assert.NotNull(buffer.Undo());
                    redoStack.Add(text);
                    text = undoStack[^1];
                    undoStack.RemoveAt(undoStack.Count - 1);
                }
            }
            else if (roll < 95)
            { // Redo 5%
                if (redoStack.Count > 0)
                {
                    Assert.True(buffer.CanRedo);
                    Assert.NotNull(buffer.Redo());
                    undoStack.Add(text);
                    text = redoStack[^1];
                    redoStack.RemoveAt(redoStack.Count - 1);
                }
            }
            else
            { // MarkSaved 5%
                buffer.MarkSaved();
            }

            buffer.BreakUndoCoalescing();

            // 毎操作: 長さ・行数の一致
            Assert.Equal(text.Length, buffer.Current.CharLength);
            Assert.Equal(NaiveBreakEnds(text).Count + 1, buffer.Current.LineCount);

            if (op % 25 == 0)
                DeepVerify(text, buffer.Current, rnd);
        }

        // 永続性: 開始時に取った初期スナップショットが終了時も初期内容のまま
        Assert.Equal(initial, initialSnap.GetText(0, initialSnap.CharLength));
    }

    /// <summary>モデル側splice(バッファとは独立のスナップ規則実装)。</summary>
    private static string ModelSplice(
        string text,
        int pos,
        int delLen,
        string ins,
        List<string> undoStack,
        List<string> redoStack
    )
    {
        int start = pos > 0 && pos < text.Length && char.IsLowSurrogate(text[pos]) ? pos - 1 : pos;
        int endRaw = pos + delLen;
        int end =
            endRaw > 0 && endRaw < text.Length && char.IsLowSurrogate(text[endRaw])
                ? endRaw - 1
                : endRaw;
        if (end < start)
            end = start;
        if (start == end && ins.Length == 0)
            return text; // 無変化はUndoエントリなし

        undoStack.Add(text);
        if (undoStack.Count > ModelUndoCap)
            undoStack.RemoveAt(0);
        redoStack.Clear();
        return text[..start] + ins + text[end..];
    }

    private static string Material(Random rnd)
    {
        int target = rnd.Next(1, 21);
        var sb = new StringBuilder();
        while (sb.Length < target)
            sb.Append(Pool[rnd.Next(Pool.Length)]);
        return sb.ToString();
    }

    private static List<int> NaiveBreakEnds(string s)
    {
        var ends = new List<int>();
        for (int i = 0; i < s.Length; i++)
            if (s[i] == '\n' || (s[i] == '\r' && (i + 1 == s.Length || s[i + 1] != '\n')))
                ends.Add(i);
        return ends;
    }

    private static void DeepVerify(string text, TextSnapshot snap, Random rnd)
    {
        Assert.Equal(text, snap.GetText(0, snap.CharLength));

        var ends = NaiveBreakEnds(text);
        int lineCount = ends.Count + 1;
        int line = rnd.Next(lineCount);
        Assert.Equal(line == 0 ? 0 : ends[line - 1] + 1, snap.GetLineStart(line));
        bool isLast = line == lineCount - 1;
        Assert.Equal(
            isLast ? text.Length : ends[line] + 1,
            snap.GetLineEnd(line, includeBreak: true)
        );
        Assert.Equal(
            isLast
                ? text.Length
                : ends[line]
                    - (
                        text[ends[line]] == '\n' && ends[line] > 0 && text[ends[line] - 1] == '\r'
                            ? 1
                            : 0
                    ),
            snap.GetLineEnd(line, includeBreak: false)
        );

        int pos = rnd.Next(text.Length + 1);
        Assert.Equal(ends.Count(e => e < pos), snap.GetLineIndexOfChar(pos));

        int a = rnd.Next(text.Length + 1),
            b = rnd.Next(text.Length + 1);
        if (a > b)
            (a, b) = (b, a);
        Assert.Equal(text.Substring(a, b - a), snap.GetText(a, b - a));
    }
}
