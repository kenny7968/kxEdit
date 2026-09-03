using System.Reflection;

namespace kxEdit.App.Tests;

/// <summary>
/// メソッドの IL から <c>call</c> / <c>callvirt</c> の対象を集めるヘルパー。
/// 「この経路は<b>この API を呼ばない</b>」を挙動ではなく構造で固定するために使う。
/// <para>
/// Issue #48 の文脈: <c>Path.GetFullPath</c> は正規化後のパスに <c>~</c> が含まれると
/// <c>GetLongPathName</c>(境界の無い実 FS / ネットワーク呼び出し)を呼び、不達共有に対して
/// 約 21 秒 UI を止める。ところが**呼んでも呼ばなくても結果の綴りは同じ**(既に正規化済みの
/// パスに対して冪等)なので、追加された無境界呼出は<b>挙動テストでは一切観測できない</b>。
/// 境界付き seam の呼び出し回数を見る網も、seam を通さない直呼びには反応しない。
/// 残る手段が IL 走査。
/// </para>
/// <para>
/// <b>この型は kxEdit.App.Tests 内で唯一の実装</b>(最終レビュー Q-m-1)。
/// 以前は <c>DocumentManagerTests</c> にも同じ本文の private コピーがあり、
/// S-15 の構造網が同一アセンブリ内で 2 本に分岐していた(片方だけ直る事故の温床)。
/// <c>tests/kxEdit.Core.Tests/Text/RecentFilesListTests.cs</c> にも同じ走査があるが、
/// あちらは<b>別アセンブリ</b>(Core.Tests は App.Tests を参照しない)なので統合できない=
/// 重複したままが正しい。走査の意味を変えるときは 2 アセンブリ分を同時に直すこと。
/// </para>
/// </summary>
internal static class IlCallees
{
    /// <summary>
    /// method の IL から <c>call</c> / <c>callvirt</c> の対象として解決できたメソッドを集める。
    /// オペランドを誤読した偽陽性はメタデータテーブル種別(MethodDef / MemberRef / MethodSpec)と
    /// 解決可否で捨てる。残る偽陽性は「呼んでいないものが混ざる」方向にしか働かないので、
    /// 「呼んでいない」の assert が偽陽性で<b>緑になることはない</b>
    /// (逆に、将来の本体変更で偽陽性が当たれば赤で気付ける)。
    /// </summary>
    public static List<MethodBase> Of(MethodBase method) => Scan(method, includeNewobj: false);

    /// <summary>
    /// <see cref="Of"/> に <c>newobj</c>(オブジェクト生成)を加えたもの。
    /// 「この経路は<b>この型を直接組み立てない</b>」を固定したいときに使う
    /// (例: <c>Program.Main</c> が <c>MainForm</c> を合成点を通さず直に作っていないこと)。
    /// <para>
    /// <see cref="Of"/> と<b>別入口</b>にしてあるのは、既存の呼出側(<c>DocumentManagerTests</c> /
    /// <c>FileControllerTests</c> / <c>GrepControllerTests</c>)が集合へ ctor が混ざることを
    /// 想定していないため。走査本体は <see cref="Scan"/> 1 本で、分岐するのは拾う命令だけ。
    /// </para>
    /// </summary>
    public static List<MethodBase> OfIncludingNewobj(MethodBase method) =>
        Scan(method, includeNewobj: true);

    /// <summary>
    /// <c>async</c> メソッドは、本体がコンパイラ生成の状態機械の <c>MoveNext</c> に入っている。
    /// 元のメソッドを <see cref="Of"/> に掛けても状態機械の起動しか見えないので、走査対象を
    /// <c>MoveNext</c> へ解決する。<c>[AsyncStateMachine]</c> 属性の欠落や改名は
    /// <c>Assert.NotNull</c> で止める(走査ゼロ件が「呼んでいない」と読める形にしない)。
    /// <para>
    /// <see cref="Of"/> と同じ理由でここに置く: 以前は <c>GrepControllerTests</c> の private
    /// コピー 1 本だけだったが、B6 で <c>MarkdownPreviewForm.InitAsync</c> にも同型の構造網が
    /// 要ったため、走査補助を 2 本に分岐させずこの型へ集約した。
    /// </para>
    /// </summary>
    public static MethodInfo AsyncBodyOf(Type type, string name)
    {
        var m = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
        );
        Assert.NotNull(m);
        var attr = (System.Runtime.CompilerServices.AsyncStateMachineAttribute?)
            Attribute.GetCustomAttribute(
                m!,
                typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute)
            );
        Assert.NotNull(attr);
        var move = attr!.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(move);
        return move!;
    }

    private static List<MethodBase> Scan(MethodBase method, bool includeNewobj)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        var typeArgs = method.DeclaringType!.GetGenericArguments();
        var methodArgs = method.GetGenericArguments();
        var result = new List<MethodBase>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call / callvirt / (任意で) newobj。いずれも 4 バイトのトークンを伴う。
            bool isCall = il[i] == 0x28 || il[i] == 0x6F;
            if (!isCall && !(includeNewobj && il[i] == 0x73))
                continue;
            int token = BitConverter.ToInt32(il, i + 1);
            byte table = (byte)((uint)token >> 24);
            if (table != 0x06 && table != 0x0A && table != 0x2B) // MethodDef/MemberRef/MethodSpec
                continue;
            try
            {
                var m = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                if (m is not null)
                    result.Add(m);
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
                // 解決できないトークン=オペランドの誤読。呼出ではないので捨てる。
            }
        }
        return result;
    }
}
