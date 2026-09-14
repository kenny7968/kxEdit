// SingleInstanceNames.cs
// 設計 2026-09-14 D2 / §4「名前」。
using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App;

/// <summary>
/// 単一インスタンス機構が使う Mutex / パイプの名前。
/// </summary>
/// <remarks>
/// <b>2 つの名前空間の性質が違う</b>のがここの要点である。
/// Mutex は <c>Local\</c> を付ければカーネルがログオンセッション単位に切ってくれるが、
/// <b>名前付きパイプの名前空間は OS 全体で共有され、<c>Local\</c> に相当する仕組みが無い</b>。
/// そのためセッション ID と ユーザー SID を名前に埋めて手動で同じスコープを作る。
/// </remarks>
internal static class SingleInstanceNames
{
    /// <summary><c>Local\</c> = ログオンセッション単位(設計 D2)。</summary>
    internal const string MutexName = @"Local\kxEdit.SingleInstance";

    internal static string PipeName(int sessionId, string userSid) =>
        $"kxEdit.SingleInstance.{sessionId}.{userSid}";

    /// <summary>現在のプロセスのセッション / ユーザーに対応するパイプ名。</summary>
    internal static string CurrentPipeName()
    {
        using var self = Process.GetCurrentProcess();
        var user =
            WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("現在のユーザーの SID を取得できない");
        return PipeName(self.SessionId, user.Value);
    }
}
