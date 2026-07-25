namespace yEdit.Editor.Tests;

/// <summary>
/// RaiseUiaSelectionEvents プロパティ受け口の契約テスト(既定 true・読み書き可能)。
/// CSV モード(CsvController)が誤読み抑止に使う温存機能。
/// EmptyLineNavigationTests から移設(個別 SR 対応廃止=CaretEnteredEmptyLine 削除に伴い
/// docs/plans/2026-07-13-pctalker-removal-design.md)。
/// </summary>
public class RaiseUiaSelectionEventsTests
{
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form();
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    [Fact]
    public void RaiseUiaSelectionEvents_DefaultIsTrue() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                Assert.True(c.RaiseUiaSelectionEvents);
            }
        });

    [Fact]
    public void RaiseUiaSelectionEvents_CanBeSet() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.RaiseUiaSelectionEvents = false;
                Assert.False(c.RaiseUiaSelectionEvents);
                c.RaiseUiaSelectionEvents = true;
                Assert.True(c.RaiseUiaSelectionEvents);
            }
        });
}
