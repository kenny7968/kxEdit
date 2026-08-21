// UiaTextHostAdapterContractTests.cs
// Phase 3 Task 3d 契約テスト: EditorControl から 12 Uia field が完全移譲されたことと、
// UiaTextHostAdapter がそれらを保持することを reflection で機械固定する。
// Task 3b の CaretControllerContractTests / Task 3c の InputRouterContractTests と同流儀。
using System.Reflection;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class UiaTextHostAdapterContractTests
{
    private static readonly string[] UiaFields =
    {
        "_bufferSnapshot",
        "_boundsSync",
        "_bounds",
        "_clientToScreenX",
        "_clientToScreenY",
        "_lastLineSegs",
        "_hwnd",
        "_provider",
        "_testHook_LastGetObjectServed",
        "_uiaTextChangedCount",
        "_uiaSelectionChangedCount",
        "_uiaFocusChangedCount",
    };

    [Fact]
    public void UiaTextHostAdapter_Owns12UiaFields()
    {
        // (1) EditorControl 側から 12 field が全て消えたことを確認
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in UiaFields)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.True(
                f is null,
                $"EditorControl should no longer own '{name}' (Task 3d は Adapter へ移譲)"
            );
        }

        // (2) UiaTextHostAdapter 型が Editor アセンブリに存在し、12 field を全て持つことを確認
        var adapterType = typeof(EditorControl).Assembly.GetType(
            "kxEdit.Editor.UiaTextHostAdapter"
        );
        Assert.NotNull(adapterType);
        var adapterFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in UiaFields)
        {
            var f = adapterType!.GetField(name, adapterFlags);
            Assert.True(f is not null, $"UiaTextHostAdapter should own '{name}' (Task 3d 移譲先)");
        }
    }

    [Fact]
    public void EditorControl_HoldsAdapter_ByField()
    {
        var f = typeof(EditorControl).GetField(
            "_uia",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(f);
        // 型も検証: UiaTextHostAdapter でなければならない
        var adapterType = typeof(EditorControl).Assembly.GetType(
            "kxEdit.Editor.UiaTextHostAdapter"
        );
        Assert.Equal(adapterType, f!.FieldType);
    }
}
