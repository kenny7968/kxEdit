using System.Linq;
using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// <c>MainForm.ShowMarkdownPreview</c> の構造網。
/// <para>
/// <b>なぜ挙動テストで代替できないか</b>: <c>MainForm</c> は WinForms のフォーム本体で、
/// この経路は <c>MessageBox.Show</c> と <c>MarkdownPreviewForm.ShowDialog</c> (WebView2 実体)
/// を含むため unit test から通せない。守りたい退行はいずれも
/// <b>「例外の逃がし方」</b>で、成功パスの出力を 1 ビットも変えない。
/// </para>
/// </summary>
public class MainFormPreviewStructureTests
{
    private static MethodInfo ShowMarkdownPreview()
    {
        var m = typeof(MainForm).GetMethod(
            "ShowMarkdownPreview",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(m); // 改名で走査ゼロ件=「無い」と読めるのを防ぐ
        return m!;
    }

    /// <summary>
    /// B (最終レビュー): <c>Render</c> が投げうる想定内例外を<b>すべて</b>捕まえていること。
    /// <para>
    /// <c>DocumentTooLargeException</c> だけを捕まえていた時期、Markdig のネスト深度上限
    /// 超過 (<c>"&gt; " × 200</c> = 400 バイトで発火・実測) が
    /// <c>Application.ThreadException</c> → <c>CrashHandler</c> → <b>アプリ終了</b>になっていた。
    /// </para>
    /// <para>
    /// <c>ArgumentException</c> を捕まえて<b>いない</b>ことも同時に固定する: baseHref の
    /// allow-list 違反 (MD-L-4) は呼び出し側の実装バグなので握り潰してはならない。
    /// </para>
    /// </summary>
    [Fact]
    public void ShowMarkdownPreview_CatchesExpectedRenderFailures_ButNotImplementationBugs()
    {
        var catchTypes = ShowMarkdownPreview()
            .GetMethodBody()!
            .ExceptionHandlingClauses.Where(c => c.Flags == ExceptionHandlingClauseOptions.Clause)
            .Select(c => c.CatchType)
            .ToList();

        Assert.Contains(typeof(DocumentTooLargeException), catchTypes);
        Assert.Contains(typeof(MarkdownTooComplexException), catchTypes);
        // 実装バグを握り潰す形への退行 (catch (Exception) / catch (ArgumentException))。
        Assert.DoesNotContain(typeof(ArgumentException), catchTypes);
        Assert.DoesNotContain(typeof(Exception), catchTypes);
    }
}
