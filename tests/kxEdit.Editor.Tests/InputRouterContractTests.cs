using System.Reflection;

namespace kxEdit.Editor.Tests;

/// <summary>
/// Phase 3 Task 3c の契約テスト: <c>EditorControl</c> が <c>InputRouter</c> インスタンスを
/// フィールドで保持し、Router 自身は state を持たない pure dispatcher であることを reflection で確認する。
/// </summary>
public class InputRouterContractTests
{
    [Fact]
    public void EditorControl_HoldsInputRouter_ByField()
    {
        var f = typeof(EditorControl).GetField(
            "_input",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(f);
    }

    [Fact]
    public void InputRouter_HasNoInstanceStateFields()
    {
        var routerType = typeof(EditorControl).Assembly.GetType("kxEdit.Editor.InputRouter");
        Assert.NotNull(routerType);
        var mutableFields = routerType!
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => !f.IsInitOnly)
            .ToList();
        Assert.Empty(mutableFields);
    }
}
