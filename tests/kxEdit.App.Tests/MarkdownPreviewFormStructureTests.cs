namespace kxEdit.App.Tests;

/// <summary>
/// V-2 + PR #57 申し送り: <c>MarkdownPreviewForm.InitAsync</c> の構造網。
/// <para>
/// <b>なぜ挙動テストで代替できないか</b>: <c>InitAsync</c> は <c>CoreWebView2</c> の実体を
/// 要求するので unit test から通せない(WebView2 ランタイム依存)。そのうえここで守りたい
/// 退行は、いずれも<b>結果を 1 ビットも変えない</b>種類のものだ:
/// <list type="bullet">
/// <item>seam(<c>RemoteAwareDirectory.Exists</c>)を残したまま、その手前へ「念のため」
/// <c>Directory.Exists(_baseDir)</c> を 1 行足す —— A-17 そのものの再導入
/// (PR #57 申し送り)。不達共有で UI が 21 秒止まるが、返り値を捨てれば観測差はゼロ。</item>
/// <item><c>await Task.Run(...)</c> を外して seam を UI スレッドで直接呼ぶ ——
/// 実在確認を UI スレッドから外すという設計書 §13.2 の中核が崩れる(最悪 5 秒ブロック)。
/// 返る値は同じなので、やはり観測差はゼロ。</item>
/// </list>
/// 残る手段が IL 走査 = <c>GrepControllerTests.FolderCheckEntryPoints_DoNotTouchFileSystemOutsideTheSeam</c>
/// と同型の網(A-17 seam の 3 つ目の呼出点として本メソッドが増えたのに、ここだけ網が無かった)。
/// </para>
/// <para>
/// <b>陽性対照を必ず添える</b>: 「呼んでいない」だけを並べた網は、走査対象の解決に失敗した
/// (改名・非 async 化・属性欠落)ときも緑になる。<see cref="IlCallees.AsyncBodyOf"/> 内の
/// <c>Assert.NotNull</c> に加えて、実際に拾えているはずの呼出を 3 本 assert して二重化する。
/// </para>
/// </summary>
public class MarkdownPreviewFormStructureTests
{
    [Fact]
    public void InitAsync_DoesNotTouchFileSystemOutsideTheSeam()
    {
        var callees = IlCallees.Of(IlCallees.AsyncBodyOf(typeof(MarkdownPreviewForm), "InitAsync"));

        // --- 陽性対照(走査が実際に呼出を拾えている) ---
        // マッピングは常に張る = V-2 の中核。Apply ごと消える退行を検出する。
        Assert.Contains(
            callees,
            m =>
                m.DeclaringType == typeof(PreviewVirtualHostMapping)
                && m.Name == nameof(PreviewVirtualHostMapping.Apply)
        );
        // 本文の描画。ここまで到達する経路であることの確認。
        Assert.Contains(
            callees,
            m =>
                m.DeclaringType == typeof(Microsoft.Web.WebView2.Core.CoreWebView2)
                && m.Name == nameof(Microsoft.Web.WebView2.Core.CoreWebView2.NavigateToString)
        );
        // 実在確認を退避スレッドへ逃がしている証拠。下の「seam を直接呼ばない」と対で意味を持つ。
        Assert.Contains(
            callees,
            m =>
                m.DeclaringType == typeof(System.Threading.Tasks.Task)
                && m.Name == nameof(System.Threading.Tasks.Task.Run)
        );

        // --- 守りたい不在 ---
        // (1) A-17 の再導入(PR #57 申し送り)。seam の手前の「念のため」1 行。
        Assert.DoesNotContain(
            callees,
            m =>
                m.DeclaringType == typeof(System.IO.Directory)
                && m.Name == nameof(System.IO.Directory.Exists)
        );
        // (2) seam を UI スレッドで直接呼ぶ形への退行。正しい形では
        //     RemoteAwareDirectory.Exists は Task.Run へ渡すラムダの中にあり、
        //     MoveNext からの直接の呼出には現れない(設計書 §13.2)。
        Assert.DoesNotContain(
            callees,
            m =>
                m.DeclaringType == typeof(RemoteAwareDirectory)
                && m.Name == nameof(RemoteAwareDirectory.Exists)
        );
        // (3) 境界付き I/O を 1 操作 2 回にしない(RemoteAwareDirectory の doc / Task 4 の制約)。
        Assert.DoesNotContain(
            callees,
            m =>
                m.DeclaringType == typeof(IReachabilityProbe)
                && m.Name == nameof(IReachabilityProbe.NormalizePathWithTimeout)
        );
    }
}
