// UiaTextHostAdapterContractTests.cs
// Phase 3 Task 3d 契約テスト: EditorControl から Uia field が完全移譲されたことと、
// UiaTextHostAdapter がそれらを保持することを reflection で機械固定する。
// Task 3b の CaretControllerContractTests / Task 3c の InputRouterContractTests と同流儀。
// フェーズ 2(S-1・2026-09-25)で座標キャッシュ 4 field(_boundsSync / _bounds /
// _clientToScreenX / _clientToScreenY)を削除した。
using System.Reflection;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class UiaTextHostAdapterContractTests
{
    private static readonly string[] UiaFields =
    {
        "_bufferSnapshot",
        "_lastLineSegs",
        "_hwnd",
        "_provider",
        "_testHook_LastGetObjectServed",
        "_uiaTextChangedCount",
        "_uiaSelectionChangedCount",
        "_uiaFocusChangedCount",
    };

    [Fact]
    public void UiaTextHostAdapter_OwnsUiaFields()
    {
        // (1) EditorControl 側から Uia field が全て消えたことを確認
        var editorFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var name in UiaFields)
        {
            var f = typeof(EditorControl).GetField(name, editorFlags);
            Assert.True(
                f is null,
                $"EditorControl should no longer own '{name}' (Task 3d は Adapter へ移譲)"
            );
        }

        // (2) UiaTextHostAdapter 型が Editor アセンブリに存在し、Uia field を全て持つことを確認
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

    // フェーズ 2(S-1): 座標はその場で求める。キャッシュを戻すと、描画なしのウィンドウ移動で
    // 古い座標を返す不具合(監査 M-10)が戻る(UiaScreenCoordinateTests が挙動側の網)。
    [Fact]
    public void UiaTextHostAdapter_HasNoScreenCoordinateCache()
    {
        var adapterType = typeof(EditorControl).Assembly.GetType(
            "kxEdit.Editor.UiaTextHostAdapter"
        );
        Assert.NotNull(adapterType);
        foreach (
            var name in new[] { "_boundsSync", "_bounds", "_clientToScreenX", "_clientToScreenY" }
        )
            Assert.Null(
                adapterType!.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            );
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
