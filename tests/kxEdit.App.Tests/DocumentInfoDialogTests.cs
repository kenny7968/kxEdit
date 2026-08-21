namespace kxEdit.App.Tests;

/// <summary>
/// DocumentInfoDialog の構築スモーク。SR 体験の前提(初期フォーカスが TextBox=開いた直後に
/// 内容を読める)と、Dock=Fill / Dock=Bottom の重なり回避、Enter/Esc の双方で閉じる配線を
/// 機械固定する。実発声そのものは L5 実機検証でしか確認できない(CLAUDE.md §2 a11y 鉄則)。
/// </summary>
public class DocumentInfoDialogTests
{
    private const string Sample = "ファイル名: aaa\r\n形式: テキスト(.txt)\r\n文字数: 3";

    /// <summary>フォームを画面外に可視化する(レイアウト確定に Show が要る)。</summary>
    private static DocumentInfoDialog ShowOffScreen(string text)
    {
        var dlg = new DocumentInfoDialog(text)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        dlg.Show();
        return dlg;
    }

    [Fact]
    public void TextBox_receives_initial_focus_and_carries_the_text() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen(Sample);
            var textBox = Assert.IsType<TextBox>(dlg.ActiveControl); // SR が開いた直後に内容を読める
            Assert.Equal(Sample, textBox.Text);
            Assert.True(textBox.ReadOnly);
            Assert.True(textBox.Multiline);
            Assert.False(textBox.WordWrap); // 1 行=1 項目(SR の読み単位)を維持する
        });

    /// <summary>Dock=Fill を先に Add した z-order 契約の kill: 逆順にすると TextBox とボタン列が
    /// 重なる(WinForms は z-order の逆順にドックを確定させるため)。</summary>
    [Fact]
    public void Fill_textbox_and_bottom_button_panel_do_not_overlap() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen(Sample);
            var textBox = dlg.Controls.OfType<TextBox>().Single();
            var panel = dlg.Controls.OfType<FlowLayoutPanel>().Single();

            Assert.False(textBox.Bounds.IntersectsWith(panel.Bounds));
            Assert.Equal(dlg.ClientSize.Height, textBox.Height + panel.Height);
            Assert.Equal(0, dlg.Controls.GetChildIndex(textBox)); // Fill が先に Add されている
        });

    [Fact]
    public void Enter_and_esc_both_close_via_the_same_button() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen(Sample);
            var closeBtn = dlg
                .Controls.OfType<FlowLayoutPanel>()
                .Single()
                .Controls.OfType<Button>()
                .Single();

            Assert.Same(closeBtn, dlg.AcceptButton); // Enter
            Assert.Same(closeBtn, dlg.CancelButton); // Esc
            Assert.Equal(DialogResult.Cancel, closeBtn.DialogResult);
            Assert.Contains("&", closeBtn.Text, StringComparison.Ordinal); // アクセラレータ付き
        });
}
