using System.Diagnostics;
using System.Security.Principal;

namespace kxEdit.App.Tests;

/// <summary>
/// 名前のスコープ(設計 2026-09-14 D2 / §4)。
/// Mutex はログオンセッション単位、パイプは OS 全体の名前空間なので
/// セッション ID と SID を名前に埋めて手動でスコープを作る。
/// </summary>
public class SingleInstanceNamesTests
{
    [Fact]
    public void MutexName_IsScopedToLogonSession()
    {
        // Global\ への変異を殺す。Global\ にすると別ユーザーの kxEdit まで止める(設計 D2)。
        Assert.StartsWith(@"Local\", SingleInstanceNames.MutexName, StringComparison.Ordinal);
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
        string expected = SingleInstanceNames.PipeName(
            self.SessionId,
            WindowsIdentity.GetCurrent().User!.Value
        );
        Assert.Equal(expected, SingleInstanceNames.CurrentPipeName());
    }
}
