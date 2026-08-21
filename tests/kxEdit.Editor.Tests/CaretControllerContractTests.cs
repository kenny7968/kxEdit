using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// Phase 3 Task 3b の契約テスト: <c>_caret</c>/<c>_anchor</c>/<c>_desiredXpx</c> の所有権が
/// <c>EditorControl</c> から <c>CaretController</c> へ完全移譲されたことを reflection で確認する。
/// PR4 C-6 (S2292) で <c>_desiredXpx</c> は auto-property <c>DesiredXpx</c> に集約されたため、
/// CaretController 側の存在確認は property 名(<c>DesiredXpx</c>)で行う(compiler 生成の
/// backing field 名 <c>&lt;DesiredXpx&gt;k__BackingField</c> は semantic 等価)。
/// </summary>
public class CaretControllerContractTests
{
    [Fact]
    public void CaretController_Fields_AreOwnedByController()
    {
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        string[] removed = { "_caret", "_anchor", "_desiredXpx" };
        foreach (var name in removed)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.Null(f);
        }
        // EditorControl 側に property DesiredXpx が漏れていないことも確認(auto-property 化に伴う追加検査)。
        Assert.Null(
            typeof(EditorControl).GetProperty(
                "DesiredXpx",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            )
        );

        var ctrlFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        var ctrlType = typeof(EditorControl).Assembly.GetType("kxEdit.Editor.CaretController");
        Assert.NotNull(ctrlType);
        // _caret / _anchor は field のまま = 従来検査を維持。
        Assert.NotNull(ctrlType!.GetField("_caret", ctrlFlags));
        Assert.NotNull(ctrlType!.GetField("_anchor", ctrlFlags));
        // _desiredXpx は auto-property DesiredXpx に置換=property 存在で所有権を確認。
        Assert.NotNull(
            ctrlType!.GetProperty(
                "DesiredXpx",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            )
        );
    }

    [Fact]
    public void EditorControl_HoldsCaretController_ByField()
    {
        var f = typeof(EditorControl).GetField(
            "_caretCtrl",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(f);
    }
}
