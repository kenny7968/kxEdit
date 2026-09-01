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
        var ex = new AtomicReplaceFailedException(
            @"C:\dir\doc.txt",
            @"C:\dir\doc.txt.abc.tmp",
            new IOException("replace failed"),
            new IOException("recover failed")
        );

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
