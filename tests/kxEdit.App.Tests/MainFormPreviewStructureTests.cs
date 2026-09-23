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

    /// <summary>
    /// E-1 (最終レビュー): M-23 の価値は<b>順序</b>にある —— 上限判定
    /// (<c>TextLength</c> → <c>ExceedsMaxChars</c>) が全文 string 化 (<c>SnapshotText</c>) より
    /// <b>前</b>にあること。
    /// <para>
    /// <b>なぜ挙動テストで代替できないか</b>: <c>Render</c> 内にも同じ cap が残っているので、
    /// 順序を入れ替えても<b>観測可能な結果は変わらない</b>(投げる例外も文面も同じ)。
    /// 差が出るのは 1G 文字級の文書で <c>SnapshotText</c> 自体が
    /// <c>OutOfMemoryException</c> になるときだけで、それは自動テストで作れない。
    /// 残る手段が IL 走査 (<see cref="IlCallees.Of"/> は IL 出現順のリストを返す)。
    /// </para>
    /// </summary>
    [Fact]
    public void ShowMarkdownPreview_ChecksSizeCapBeforeMaterializingText()
    {
        var callees = IlCallees.Of(ShowMarkdownPreview());

        int textLength = callees.FindIndex(m => m.Name == "get_TextLength");
        int exceeds = callees.FindIndex(m =>
            m.DeclaringType == typeof(MarkdownRenderer)
            && m.Name == nameof(MarkdownRenderer.ExceedsMaxChars)
        );
        int snapshot = callees.FindIndex(m => m.Name == "get_SnapshotText");

        // 陽性対照: 3 つとも実在すること。FindIndex は見つからないと -1 を返すので、
        // 比較だけだと片方が消えた状態が「-1 < n」で空虚に緑になる。
        Assert.True(textLength >= 0, "TextLength の取得が見つからない");
        Assert.True(exceeds >= 0, "ExceedsMaxChars の呼出が見つからない");
        Assert.True(snapshot >= 0, "SnapshotText の取得が見つからない");

        Assert.True(textLength < snapshot, "TextLength は SnapshotText より前で読むこと");
        Assert.True(exceeds < snapshot, "ExceedsMaxChars は SnapshotText より前で判定すること");
    }

    /// <summary>
    /// 2026-09-23 設計書: CSVモード中はプレビューを開かない(キー・メニューとも)。
    /// 抑止時は <c>CsvAnnounceFormatter.BlockedInCsvMode</c> を発声する(最終レビュー I-1)。
    /// ここで固定するのは「開かないこと」と「判定の位置」だけで、発声の文言は対象外。
    /// <para>
    /// <b>なぜ挙動テストで代替できないか</b>: ガードが効いていれば即座に return するが、
    /// <b>ガードが消えた退行では <c>ShowDialog</c>(WebView2 実体)やその初期化失敗の
    /// <c>MessageBox</c> に入り、テストは「落ちる」のではなく「固まる」</b>。
    /// CI を無限に止める網は網にならないので、構造(IL)で固定する。
    /// </para>
    /// <para>
    /// 位置も同時に固定する: CSVモード判定は <c>ExceedsMaxChars</c> / <c>SnapshotText</c> より
    /// <b>前</b>。後ろへ移ると 4M 文字超の CSV で「大きすぎます」ダイアログが先に出てしまい、
    /// 「CSVモード中はプレビューを開かず理由だけを発声する」という要件が崩れる。
    /// </para>
    /// </summary>
    [Fact]
    public void ShowMarkdownPreview_BailsOutInCsvMode_BeforeAnyWork()
    {
        var callees = IlCallees.Of(ShowMarkdownPreview());

        int csvMode = callees.FindIndex(m =>
            m.DeclaringType == typeof(DocumentState) && m.Name == "get_CsvMode"
        );
        int exceeds = callees.FindIndex(m =>
            m.DeclaringType == typeof(MarkdownRenderer)
            && m.Name == nameof(MarkdownRenderer.ExceedsMaxChars)
        );
        int snapshot = callees.FindIndex(m => m.Name == "get_SnapshotText");

        // 陽性対照: 3 つとも実在すること。FindIndex は見つからないと -1 を返すので、
        // 比較だけだと片方が消えた状態が「-1 < n」で空虚に緑になる。
        Assert.True(csvMode >= 0, "CSVモードの判定が見つからない");
        Assert.True(exceeds >= 0, "ExceedsMaxChars の呼出が見つからない");
        Assert.True(snapshot >= 0, "SnapshotText の取得が見つからない");

        Assert.True(csvMode < exceeds, "CSVモード判定は上限判定より前で行うこと");
        Assert.True(csvMode < snapshot, "CSVモード判定は SnapshotText より前で行うこと");
    }
}
