using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

public class AtomicReplaceFailedExceptionTests
{
    /// <summary>
    /// 設計 §3.4: この例外を共有/ロック違反と誤認させない。誤認すると
    /// TextFileService の `when (AtomicFile.IsShareOrLockViolation(ex))` が真になり、
    /// 原本が消えた後に in-place 上書きフォールバックへ流れて、AtomicFile の復旧と
    /// 同じ仕事を別経路で二重に行うことになる(どちらが効いたか判らなくなる)。
    /// </summary>
    [Fact]
    public void Is_not_classified_as_share_or_lock_violation()
    {
        // fixture は「非既定の HResult」から始める(CLAUDE.md §4-B)。素の IOException の既定値は
        // 0x80131620 で outer の既定値と同じため、主 ctor に `HResult = replaceError.HResult;`
        // (元のエラーコードを保存しようという善意の変異)を足しても検出できない。
        // 本番(Task 3)で File.Replace が投げる replaceError は共有違反であることが最も多く、
        // それは設計 §3.4 が恐れている当の経路そのもの。
        var replaceError = new IOException("sharing violation")
        {
            HResult = unchecked((int)0x80070020),
        };

        var ex = new AtomicReplaceFailedException(
            @"C:\dir\doc.txt",
            @"C:\dir\doc.txt.abc.tmp",
            replaceError,
            new IOException("recover failed")
        );

        // fixture 自体が非既定であることを固定する(これが false へ退化すると上の網が無力になる)。
        Assert.True(AtomicFile.IsShareOrLockViolation(replaceError));
        // inner が共有違反でも outer は伝播しない = フォールバック条件に当たらない。
        Assert.False(AtomicFile.IsShareOrLockViolation(ex));
    }

    /// <summary>残した tmp のパスと、失われた差替先のパスを呼出側が読めること。</summary>
    [Fact]
    public void Carries_target_and_preserved_paths_and_both_errors()
    {
        var replaceError = new IOException("replace failed");
        var recoveryError = new IOException("recover failed");

        var ex = new AtomicReplaceFailedException(
            @"C:\dir\doc.txt",
            @"C:\dir\doc.txt.abc.tmp",
            replaceError,
            recoveryError
        );

        Assert.Equal(@"C:\dir\doc.txt", ex.TargetPath);
        Assert.Equal(@"C:\dir\doc.txt.abc.tmp", ex.PreservedTempPath);
        Assert.Same(replaceError, ex.InnerException); // 差替そのものの失敗
        Assert.Same(recoveryError, ex.RecoveryError); // 復旧の失敗
        Assert.Contains(@"C:\dir\doc.txt.abc.tmp", ex.Message); // 呼出側が message でも伝えられる
        // tmp パスは targetPath を部分文字列として含む(`…doc.txt` + `.abc.tmp`)ので、上の 1 行
        // だけでは補間から '{targetPath}' を丸ごと削る変異を素通しする。引用符で閉じた形で
        // 突き合わせて、targetPath が単独で message に載っていることを固定する。
        Assert.Contains(@"'C:\dir\doc.txt'", ex.Message);
    }

    /// <summary>RCS1194 準拠の標準 ctor が CS8618 を出さずに使えること(null 参照を作らない)。</summary>
    [Fact]
    public void Standard_constructors_leave_paths_empty_not_null()
    {
        var ex = new AtomicReplaceFailedException();

        Assert.Equal(string.Empty, ex.TargetPath);
        Assert.Equal(string.Empty, ex.PreservedTempPath);
        Assert.Null(ex.RecoveryError);
    }
}
