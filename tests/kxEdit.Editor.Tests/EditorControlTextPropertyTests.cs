using System.Windows.Forms;
using kxEdit.Core.Buffers;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlTextPropertyTests
{
    [Fact]
    public void Text_Get_ReturnsBufferSnapshot()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            Assert.Equal("hello", ctrl.Text);
        });
    }

    [Fact]
    public void Text_Set_ReplacesBufferAndResetsCaret()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("original"));
            ctrl.SetCaretCharOffset(4);
            ctrl.Text = "brand new content";
            Assert.Equal("brand new content", ctrl.Text);
            Assert.Equal(0, ctrl.CaretCharOffset); // 差し替えでキャレット先頭
            Assert.False(ctrl.Modified); // 差し替え直後は unmodified
        });
    }

    [Fact]
    public void Text_SetEmpty_ClearsBuffer()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("content"));
            ctrl.Text = string.Empty;
            Assert.Equal(string.Empty, ctrl.Text);
        });
    }

    [Fact]
    public void ReplaceSource_ExistingBuffer_Succeeds()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("first"));
            var buf2 = TextBuffer.FromString("second");
            ctrl.ReplaceSource(buf2);
            Assert.Equal("second", ctrl.Text);
        });
    }

    [Fact]
    public void Text_Get_BeforeSetSource_ReturnsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            Assert.Equal(string.Empty, ctrl.Text);
        });
    }

    [Fact]
    public void Text_Set_BeforeSetSource_UsesSetSourceBranch()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.Text = "initial content";
            Assert.Equal("initial content", ctrl.Text);
        });
    }

    [Fact]
    public void Text_SetNull_TreatedAsEmpty()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("original"));
            ctrl.Text = null!;
            Assert.Equal(string.Empty, ctrl.Text);
        });
    }

    [Fact]
    public void ReplaceSource_Null_Throws()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("hello"));
            Assert.Throws<ArgumentNullException>(() => ctrl.ReplaceSource(null!));
        });
    }
}
