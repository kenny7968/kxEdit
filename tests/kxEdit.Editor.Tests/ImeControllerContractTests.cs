// ImeControllerContractTests.cs
// Phase 3 (Task 3a) 契約テスト: EditorControl の _ime フィールドが ImeController に完全移譲され、
// ImeController は IImeContext を ctor 経由で受け取る (=pure テスト可能) ことを反射で機械固定する。
using System.Reflection;
using kxEdit.Editor.Abstractions;

namespace kxEdit.Editor.Tests;

public class ImeControllerContractTests
{
    [Fact]
    public void ImeController_UsesIImeContext_ByCtor()
    {
        var ctrlType = typeof(EditorControl).Assembly.GetType("kxEdit.Editor.ImeController");
        Assert.NotNull(ctrlType);
        var ctors = ctrlType!.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );
        Assert.NotEmpty(ctors);
        bool hasImeContextParam = ctors.Any(c =>
            c.GetParameters()
                .Any(p =>
                    p.ParameterType == typeof(Func<IImeContext>)
                    || p.ParameterType == typeof(IImeContext)
                )
        );
        Assert.True(hasImeContextParam, "ImeController must accept IImeContext via ctor");
    }

    [Fact]
    public void EditorControl_ImeField_Removed()
    {
        var f = typeof(EditorControl).GetField(
            "_ime",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.Null(f);
    }

    [Fact]
    public void EditorControl_HoldsImeController_ByField()
    {
        var f = typeof(EditorControl).GetField(
            "_imeCtrl",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(f);
    }
}
