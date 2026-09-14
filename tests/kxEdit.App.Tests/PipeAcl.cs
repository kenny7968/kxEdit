using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace kxEdit.App.Tests;

/// <summary>
/// テストから生の名前付きパイプを立てるときの ACL。
/// </summary>
/// <remarks>
/// <c>SingleInstanceServer.CreatePipe</c> と<b>同じ形</b>にしてあることが要点。
/// 緩いと、クライアント側の <c>PipeOptions.CurrentUserOnly</c> や偽装レベルの指定が
/// 効いているかどうかを、テストの側の都合で見誤る。
/// SingleInstanceChannelTests / SingleInstanceGateTests の両方から使う
/// (逐語的に同一のコピーが 2 つあったものをここへ寄せた)。
/// </remarks>
internal static class PipeAcl
{
    /// <summary>現在のユーザーだけを許可する ACL。</summary>
    internal static PipeSecurity CurrentUserOnly()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow
            )
        );
        return security;
    }
}
