namespace kxEdit.Editor.Tests;

/// <summary>
/// P3 Task 1: EditorControl テスト基盤のスモーク。
/// SetCaretCharOffset のクランプ・サロゲートスナップという P2 で申し送られた
/// 純データ系プロパティ挙動を、WinForms(STA) 経由で検証する最小 3 件。
/// Task 2 以降でここに Move* / Cut/Copy/Paste / Undo/Redo などの契約テストを追加していく。
/// </summary>
public class CaretAndSelectionSmokeTests
{
    [Fact]
    public void SetCaretCharOffset_ClampsToZero_WhenNegative() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle; // ハンドル生成(SetSource 内のキャレット生成経路が
            // 走ることは無い=OnGotFocus 未経由なので _hasFocus=false)
            c.SetSource(TextBuffer.FromString("abc"));
            c.SetCaretCharOffset(2); // 前提: いったん非ゼロ位置に置き、-5→0 の「遷移」を検証
            c.SetCaretCharOffset(-5); // (0→0 の早期 return とクランプ→0 を区別する)
            Assert.Equal(0, c.CaretCharOffset);
        });

    [Fact]
    public void SetCaretCharOffset_ClampsToCharLength_WhenTooLarge() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            var buf = TextBuffer.FromString("abc");
            c.SetSource(buf);
            c.SetCaretCharOffset(9999);
            Assert.Equal(buf.Current.CharLength, c.CaretCharOffset);
        });

    [Fact]
    public void SetCaretCharOffset_SnapsSurrogateLowToHigh() =>
        Sta.Run(() =>
        {
            using var f = new Form();
            using var c = new EditorControl();
            f.Controls.Add(c);
            _ = f.Handle;
            // 😀 = U+1F600 は UTF-16 サロゲートペア(high=D83D, low=DE00)。
            // "abc" が 3 文字 → high は index 3、low は index 4。
            c.SetSource(TextBuffer.FromString("abc😀def"));
            c.SetCaretCharOffset(4);
            Assert.Equal(3, c.CaretCharOffset); // low → high へ前方スナップ
        });
}
