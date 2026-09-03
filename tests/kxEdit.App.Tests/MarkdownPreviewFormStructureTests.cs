using System.Reflection;

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

    /// <summary>
    /// E-3 (最終レビュー): seam が<b>実際に呼ばれている</b>こと。
    /// <para>
    /// 上のテストは「MoveNext から直接呼んでいない」しか見ていないので、プローブを丸ごと
    /// 落とす退化(<c>&amp;&amp; await Task.Run(() =&gt; true)</c>)は 3 つの
    /// <c>DoesNotContain</c> も <c>Task.Run</c> の陽性対照も<b>すべて満たして緑になる</b>。
    /// 実在確認が消えれば、不達な共有でも <c>SetVirtualHostNameToFolderMapping</c> へ
    /// パスが渡り、UI スレッドが 21 秒止まる(設計書 §13.1 の実測)。
    /// </para>
    /// <para>
    /// 走査対象は <see cref="MarkdownPreviewForm"/> 自身の宣言メソッド<b>と入れ子型</b>。
    /// 正しい形では <c>RemoteAwareDirectory.Exists</c> はコンパイラ生成のラムダ
    /// (<c>&lt;InitAsync&gt;b__…</c>・<see cref="MarkdownPreviewForm"/> のインスタンスメソッド)に
    /// ちょうど 1 本ある。<c>Task.Run</c> を外すと呼出は状態機械の <c>MoveNext</c>(入れ子型)へ
    /// 移動するので、「<c>MoveNext</c> ではないこと」の assert がその退行も捕まえる。
    /// </para>
    /// </summary>
    [Fact]
    public void InitAsync_ActuallyCallsTheSeam_OutsideTheStateMachine()
    {
        var holders = DeclaredMethodsIncludingNested(typeof(MarkdownPreviewForm))
            .Where(m =>
                IlCallees
                    .Of(m)
                    .Any(c =>
                        c.DeclaringType == typeof(RemoteAwareDirectory)
                        && c.Name == nameof(RemoteAwareDirectory.Exists)
                    )
            )
            .ToList();

        Assert.Single(holders);
        Assert.NotEqual("MoveNext", holders[0].Name);
    }

    /// <summary>
    /// V-G (最終レビュー): <b>マッピングしてから描画する</b>順序を固定する。
    /// <para>
    /// V-2 の要はこれ。逆順にすると <c>NavigateToString</c> の直後に走る初回サブリソース要求が
    /// 未マップ状態に当たり、絶対化済みの <c>https://kxedit.preview/…</c> が
    /// <b>実 DNS 解決</b>へ出る(監査 §9 V-2)。ところが WebView2 実体が要るのでその差は
    /// unit test では観測できない —— IL 上の呼出順で固定する
    /// (<see cref="IlCallees.Of"/> は IL 出現順のリストを返す)。
    /// </para>
    /// </summary>
    [Fact]
    public void InitAsync_MapsVirtualHostBeforeNavigating()
    {
        var callees = IlCallees.Of(IlCallees.AsyncBodyOf(typeof(MarkdownPreviewForm), "InitAsync"));

        int apply = callees.FindIndex(m =>
            m.DeclaringType == typeof(PreviewVirtualHostMapping)
            && m.Name == nameof(PreviewVirtualHostMapping.Apply)
        );
        int navigate = callees.FindIndex(m =>
            m.DeclaringType == typeof(Microsoft.Web.WebView2.Core.CoreWebView2)
            && m.Name == nameof(Microsoft.Web.WebView2.Core.CoreWebView2.NavigateToString)
        );

        // 陽性対照: 両方が実在すること(FindIndex は不在で -1 を返すので、比較だけでは
        // 片方が消えた状態が空虚に緑になる)。
        Assert.True(apply >= 0, "PreviewVirtualHostMapping.Apply の呼出が見つからない");
        Assert.True(navigate >= 0, "CoreWebView2.NavigateToString の呼出が見つからない");
        Assert.True(apply < navigate, "仮想ホストのマッピングは NavigateToString より前に張ること");
    }

    /// <summary>
    /// 型自身の宣言メソッド + 入れ子型(コンパイラ生成の状態機械・クロージャ)の宣言メソッド。
    /// <para>
    /// 本体を持たないもの(abstract / extern)は IL 走査できないので除く。コンストラクタも
    /// 対象外にしてある —— <see cref="IlCallees.Of"/> は <c>MethodBase.GetGenericArguments</c>
    /// を呼ぶが、<c>ConstructorInfo</c> はこれを <c>NotSupportedException</c> で拒む(実測)。
    /// 走査したい呼出(ラムダ / 状態機械の <c>MoveNext</c>)はいずれもメソッド側にあるので
    /// 実害は無い。
    /// </para>
    /// </summary>
    private static IEnumerable<MethodBase> DeclaredMethodsIncludingNested(Type type)
    {
        const BindingFlags Flags =
            BindingFlags.DeclaredOnly
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static;

        var types = new List<Type> { type };
        types.AddRange(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
        return types
            .SelectMany(t => t.GetMethods(Flags))
            .Where(m => m.GetMethodBody() is not null)
            .Cast<MethodBase>();
    }
}
