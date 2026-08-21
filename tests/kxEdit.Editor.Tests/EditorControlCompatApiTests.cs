using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlCompatApiTests
{
    [Fact]
    public void SnapshotText_ReturnsFullText()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            Assert.Equal("hello world", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void SelectCharRange_LengthVersion_SelectsRange()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.SelectCharRange(6, 5); // "world"
            Assert.Equal((6, 11), ctrl.GetSelectionCharRange());
        });
    }

    [Fact]
    public void MoveCaretCharOffset_MovesCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.MoveCaretCharOffset(5);
            Assert.Equal(5, ctrl.CaretCharOffset);
        });
    }

    [Fact]
    public void SelectCharRange_NegativeLength_CollapsesToEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello world"));
            ctrl.SelectCharRange(6, -3);
            Assert.Equal((6, 6), ctrl.GetSelectionCharRange());
        });
    }

    [Fact]
    public void LineCount_ReturnsBufferLineCount()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            Assert.Equal(3, ctrl.LineCount);
        });
    }

    [Fact]
    public void GoToLine_MovesCaretToLineStart()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.GoToLine(2); // 0-based=2 → 3行目 "ccc"
            Assert.Equal(8, ctrl.CaretCharOffset);
        });
    }

    [Fact]
    public void SetCaretByLineColumn_MovesToLineStart_WhenColumnIsZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\ndef\r\nghi"));
            ctrl.SetCaretByLineColumn(1, 0);
            Assert.Equal(1, ctrl.CurrentLine);
            Assert.Equal(0, ctrl.GetColumn(ctrl.CurrentPosition));
        });
    }

    [Fact]
    public void SetCaretByLineColumn_MovesToColumnWithinLine()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abcdef\r\nghijkl"));
            ctrl.SetCaretByLineColumn(0, 3);
            Assert.Equal(0, ctrl.CurrentLine);
            Assert.Equal(3, ctrl.GetColumn(ctrl.CurrentPosition));
        });
    }

    [Fact]
    public void SetCaretByLineColumn_ClampsColumn_WhenExceedingLineWidth()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r\nghijkl"));
            // 行 0 は "abc"(3 chars)。col=999 → 3 に clamp(改行手前・次行に食み出さない)
            ctrl.SetCaretByLineColumn(0, 999);
            Assert.Equal(0, ctrl.CurrentLine);
            Assert.Equal(3, ctrl.GetColumn(ctrl.CurrentPosition));
        });
    }

    [Fact]
    public void SetCaretByLineColumn_ClampsLine_WhenExceedingLineCount()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb\r\nc"));
            ctrl.SetCaretByLineColumn(999, 0);
            Assert.Equal(2, ctrl.CurrentLine); // 最終行にクランプ
        });
    }

    [Fact]
    public void SetCaretByLineColumn_ClampsNegative_ToZero()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            ctrl.SetCaretByLineColumn(-5, -3);
            Assert.Equal(0, ctrl.CurrentLine);
            Assert.Equal(0, ctrl.GetColumn(ctrl.CurrentPosition));
        });
    }

    [Fact]
    public void SetCaretByLineColumn_EmptyBuffer_DoesNotThrow()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl(); // SetSource 未実施
            // Assert.Null(Record.Exception(...)) パターンで S2699 を満たす(no-op で例外なし)。
            Assert.Null(Record.Exception(() => ctrl.SetCaretByLineColumn(0, 0)));
        });
    }

    [Fact]
    public void CurrentPosition_MatchesCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            ctrl.SetCaretCharOffset(3);
            Assert.Equal(3, ctrl.CurrentPosition);
        });
    }

    [Fact]
    public void SavePointReached_Fires_WhenMarkSaved()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            ctrl.ReplaceCharRange(0, 5, "xxx"); // Modified=true
            int fired = 0;
            ctrl.SavePointReached += (_, _) => fired++;
            ctrl.SetSavePoint();
            Assert.Equal(1, fired);
            Assert.False(ctrl.Modified);
        });
    }

    [Fact]
    public void SavePointLeft_Fires_WhenModifiedAfterSave()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            int fired = 0;
            ctrl.SavePointLeft += (_, _) => fired++;
            ctrl.ReplaceCharRange(0, 5, "xxx"); // Modified=false → true
            Assert.Equal(1, fired);
        });
    }

    [Fact]
    public void UpdateUI_Fires_OnCaretMove()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            int fired = 0;
            ctrl.UpdateUI += (_, _) => fired++;
            ctrl.SetCaretCharOffset(3);
            Assert.Equal(1, fired);
        });
    }

    [Fact]
    public void SavePointLeft_FiresOnce_PerSaveEditCycle()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            int fired = 0;
            ctrl.SavePointLeft += (_, _) => fired++;
            ctrl.ReplaceCharRange(0, 5, "xxx"); // Modified false→true → fire
            ctrl.ReplaceCharRange(0, 3, "yyy"); // stays Modified=true → no fire
            Assert.Equal(1, fired);
        });
    }

    // P6 レビュー I-1 回帰: Undo で保存点へ戻ると SavePointReached が発火する
    [Fact]
    public void SavePointReached_Fires_WhenUndoReturnsToSavePoint()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            ctrl.SetSavePoint(); // 保存点=空編集履歴
            int reachedFires = 0;
            ctrl.SavePointReached += (_, _) => reachedFires++;
            ctrl.ReplaceCharRange(0, 5, "xxx"); // Modified true → タブ「*」表示
            Assert.True(ctrl.Modified);
            ctrl.Undo(); // 保存点=Modified false へ戻る
            Assert.False(ctrl.Modified);
            Assert.Equal(1, reachedFires); // タブラベル「*」を消せる
        });
    }

    // バックアップ復元の dirty 化: 保存点破棄で Modified=true になり SavePointLeft が 1 回だけ発火し、
    // 直後の編集で(_wasModified が陳腐化した誤検出による)二重発火をしない
    [Fact]
    public void ClearSavePoint_MakesModified_FiresSavePointLeftOnce()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            Assert.False(ctrl.Modified); // fresh バッファ=クリーン
            int leftFires = 0;
            ctrl.SavePointLeft += (_, _) => leftFires++;
            ctrl.ClearSavePoint();
            Assert.True(ctrl.Modified); // 編集なしでも dirty(タブ「*」表示へ)
            Assert.Equal(1, leftFires);
            ctrl.ReplaceCharRange(0, 5, "xxx"); // Modified true のまま → 追加発火なし
            Assert.Equal(1, leftFires);
        });
    }

    [Fact]
    public void ClearSavePoint_BeforeSetSource_IsNoOp()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            int leftFires = 0;
            ctrl.SavePointLeft += (_, _) => leftFires++;
            ctrl.ClearSavePoint(); // dirty にすべき本文が存在しない=何も起きない
            Assert.False(ctrl.Modified);
            Assert.Equal(0, leftFires);
        });
    }

    // -------- Task 10: CurrentBuffer --------

    [Fact]
    public void CurrentBuffer_ReturnsSameReference_AfterSetSource()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = TextBuffer.FromString("hello");
            ctrl.SetSource(buf);
            Assert.Same(buf, ctrl.CurrentBuffer);
        });
    }

    [Fact]
    public void CurrentBuffer_NotNull_BeforeSetSource_ReturnsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var buf = ctrl.CurrentBuffer;
            Assert.NotNull(buf);
            Assert.Equal(0, buf.Current.CharLength);
        });
    }

    [Fact]
    public void CurrentBuffer_BeforeSetSource_ReturnsSameReference_OnRepeatedCalls()
    {
        // Task 10 レビュー M-2: null 経路(SetSource 前)は静的キャッシュ共有=連続呼びで参照同一。
        // 毎回 new すると Assert.Same が意図せず失敗する反直観挙動になるのを防ぐ。
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            var a = ctrl.CurrentBuffer;
            var b = ctrl.CurrentBuffer;
            Assert.Same(a, b);
        });
    }

    [Fact]
    public void CurrentBuffer_ReflectsReplaceSource()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            var replaced = TextBuffer.FromString("world");
            ctrl.ReplaceSource(replaced);
            Assert.Same(replaced, ctrl.CurrentBuffer);
        });
    }
}
