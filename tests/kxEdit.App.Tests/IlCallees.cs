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
    public static List<MethodBase> Of(MethodBase method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        var typeArgs = method.DeclaringType!.GetGenericArguments();
        var methodArgs = method.GetGenericArguments();
        var result = new List<MethodBase>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) // call / callvirt(いずれも 4 バイトのトークンを伴う)
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
