// SingleInstanceNames.cs
// 設計 2026-09-14 D2 / §4「名前」。
using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App;

/// <summary>
/// 単一インスタンス機構が使う Mutex / パイプの名前。
/// </summary>
/// <remarks>
/// <para>
/// <b>2 つの名前空間の性質が違う</b>のがここの要点である。
/// Mutex は <c>Local\</c> を付ければカーネルが TS セッション(<see cref="Process.SessionId"/>)
/// 単位に切ってくれるが、<b>名前付きパイプの名前空間は OS 全体で共有され、
/// <c>Local\</c> に相当する仕組みが無い</b>。
/// そのためセッション ID と ユーザー SID を名前に埋めて手動で同じスコープを作る。
/// </para>
/// <para>
/// <b>どちらの名前も「TS セッション ∩ ユーザー」でスコープする</b>。設計 D2 の原理は
/// 「フォーカスを移せる範囲 ∩ ファイルが衝突する範囲」をスコープにすることであり、
/// 同一 TS セッションの別ユーザーは前面化はできても <c>%APPDATA%</c> が別なので
/// ファイルは衝突しない = 別インスタンスとして起動してよい。
/// </para>
/// <para>
/// <b>名前はビルドをまたぐ互換性契約である。</b>設計 §4 の <c>ForeignPeer</c> 検出
/// (「別の場所にある kxEdit が既に起動しています」)は、別フォルダの別ビルドが
/// 同じ名前を使うことで成立する。ここの文字列をドリフトさせると新旧ビルドが
/// 互いを検出できず、2 インスタンスが黙って並走する。変更するときは、
/// 旧ビルドとの共存をどう扱うかを設計側で決めてから変えること。
/// </para>
/// </remarks>
internal static class SingleInstanceNames
{
    /// <summary>
    /// <c>Local\</c> = TS セッション単位(<see cref="Process.SessionId"/> で切られる)。
    /// <b>SID も名前に入れる</b>のは、TS セッションがユーザーをまたぐため
    /// (<c>runas</c> や管理者資格情報を入力する UAC 昇格)。名前を共有すると、
    /// 別ユーザーが作った Mutex は既定 DACL に自分の ACE が無く
    /// <see cref="UnauthorizedAccessException"/> になる(実測)。
    /// スコープを「TS セッション ∩ ユーザー」に揃えることで、この経路自体を消す。
    /// </summary>
    internal static string MutexName(string userSid) => $@"Local\kxEdit.SingleInstance.{userSid}";

    /// <summary>現在のユーザーに対応する Mutex 名。</summary>
    internal static string CurrentMutexName() => MutexName(CurrentUserSid());

    /// <summary>
    /// 引き渡し用パイプの名前。パイプの名前空間は OS 全体で共有されるため、
    /// <c>Local\</c> の代わりに TS セッション ID とユーザー SID を名前へ埋めて、
    /// <see cref="MutexName(string)"/> と同じスコープを手動で作る。
    /// </summary>
    /// <param name="sessionId">TS セッション ID(<see cref="Process.SessionId"/>)。</param>
    /// <param name="userSid">ユーザー SID の文字列表現(<c>S-1-5-21-…</c>)。</param>
    internal static string PipeName(int sessionId, string userSid) =>
        $"kxEdit.SingleInstance.{sessionId}.{userSid}";

    /// <summary>現在のプロセスの TS セッション / ユーザーに対応するパイプ名。</summary>
    internal static string CurrentPipeName()
    {
        using var self = Process.GetCurrentProcess();
        return PipeName(self.SessionId, CurrentUserSid());
    }

    /// <summary>現在のユーザー SID。取得できない状況は想定しないので例外にする。</summary>
    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user =
            identity.User
            ?? throw new InvalidOperationException("現在のユーザーの SID を取得できない");
        return user.Value;
    }
}
