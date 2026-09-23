using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App.Tests;

/// <summary>
/// 名前のスコープ(設計 2026-09-14 D2 / §4)。
/// Mutex / パイプのどちらも「TS セッション ∩ ユーザー」でスコープする。
/// <c>Local\</c> は TS セッション(<c>Process.SessionId</c>)でしか切られずユーザーをまたぐので、
/// パイプと同じくセッション ID と SID を名前に埋めて手動でスコープを作る。
/// </summary>
public class SingleInstanceNamesTests
{
    [Fact]
    public void MutexName_IsScopedToTerminalServicesSession()
    {
        // Global\ への変異を殺す。Global\ にすると別セッションの kxEdit まで止める(設計 D2)。
        Assert.StartsWith(
            @"Local\",
            SingleInstanceNames.MutexName("S-1-5-21-1-1-1-1001"),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void MutexName_DiffersAcrossUsers()
    {
        // 同一 TS セッションの別ユーザー(runas / 管理者資格情報を入力する UAC 昇格)。
        // 名前を共有すると、別ユーザーが作った Mutex は既定 DACL に自分の ACE が無く
        // UnauthorizedAccessException で起動時に落ちる。
        Assert.NotEqual(
            SingleInstanceNames.MutexName("S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.MutexName("S-1-5-21-1-1-1-1002")
        );
    }

    [Fact]
    public void MutexName_HasExactWireFormat()
    {
        // 名前はビルドをまたぐ互換性契約。別ビルドの kxEdit を ForeignPeer として
        // 検出できるのは、両者が同じ Mutex 名を使うからである(設計 §4)。
        // ドリフトすると 2 インスタンスが黙って並走するので、リテラルで固定する。
        Assert.Equal(
            @"Local\kxEdit.SingleInstance.S-1-5-21-1-1-1-1001",
            SingleInstanceNames.MutexName("S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void CurrentMutexName_MatchesCurrentUser()
    {
        // 実プロセスの値と組み立て関数の配線が切れていないことを見る
        // (CurrentMutexName が定数を返す変異を殺す)。
        using var identity = WindowsIdentity.GetCurrent();
        string expected = SingleInstanceNames.MutexName(identity.User!.Value);
        Assert.Equal(expected, SingleInstanceNames.CurrentMutexName());
    }

    [Fact]
    public void PipeName_DiffersAcrossSessions()
    {
        Assert.NotEqual(
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(2, "S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void PipeName_DiffersAcrossUsers()
    {
        Assert.NotEqual(
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(1, "S-1-5-21-1-1-1-1002")
        );
    }

    [Fact]
    public void PipeName_IsStableForSameInputs()
    {
        Assert.Equal(
            SingleInstanceNames.PipeName(3, "S-1-5-21-1-1-1-1001"),
            SingleInstanceNames.PipeName(3, "S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void PipeName_HasExactWireFormat()
    {
        // MutexName_HasExactWireFormat と同じ理由。区切り文字・トークン順序・
        // プレフィクスまで含めて固定する(どれもビルド間で一致していなければならない)。
        Assert.Equal(
            "kxEdit.SingleInstance.7.S-1-5-21-1-1-1-1001",
            SingleInstanceNames.PipeName(7, "S-1-5-21-1-1-1-1001")
        );
    }

    [Fact]
    public void PipeName_FitsWithinWindowsPipeNameLimit()
    {
        // \\.\pipe\ を含めて 256 文字。ドメインユーザーの SID は長い。
        string name = SingleInstanceNames.PipeName(
            int.MaxValue,
            "S-1-5-21-3623811015-3361044348-30300820-1013"
        );
        Assert.True(name.Length + @"\\.\pipe\".Length <= 256, $"パイプ名が長すぎる: {name.Length}");
    }

    [Fact]
    public void CurrentPipeName_MatchesCurrentSessionAndUser()
    {
        // 実プロセスの値と組み立て関数の配線が切れていないことを見る
        // (CurrentPipeName が定数を返す変異を殺す)。
        using var self = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        string expected = SingleInstanceNames.PipeName(self.SessionId, identity.User!.Value);
        Assert.Equal(expected, SingleInstanceNames.CurrentPipeName());
    }
}
