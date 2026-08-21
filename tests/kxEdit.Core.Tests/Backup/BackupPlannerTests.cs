using kxEdit.Core.Backup;
using Xunit;

namespace kxEdit.Core.Tests.Backup;

public class BackupPlannerTests
{
    [Fact]
    public void Dirty_with_changed_content_writes() =>
        Assert.Equal(
            BackupAction.Write,
            BackupPlanner.Decide(
                modified: true,
                currentSig: 2,
                lastSig: 1,
                hasBackup: false,
                forceWrite: false
            )
        );

    [Fact]
    public void Dirty_with_unchanged_content_does_nothing() =>
        Assert.Equal(
            BackupAction.None,
            BackupPlanner.Decide(
                modified: true,
                currentSig: 5,
                lastSig: 5,
                hasBackup: true,
                forceWrite: false
            )
        );

    [Fact]
    public void Dirty_unchanged_but_forced_writes() =>
        Assert.Equal(
            BackupAction.Write,
            BackupPlanner.Decide(
                modified: true,
                currentSig: 5,
                lastSig: 5,
                hasBackup: false,
                forceWrite: true
            )
        );

    [Fact]
    public void Clean_with_backup_deletes() =>
        Assert.Equal(
            BackupAction.Delete,
            BackupPlanner.Decide(
                modified: false,
                currentSig: 5,
                lastSig: 5,
                hasBackup: true,
                forceWrite: false
            )
        );

    [Fact]
    public void Clean_without_backup_does_nothing() =>
        Assert.Equal(
            BackupAction.None,
            BackupPlanner.Decide(
                modified: false,
                currentSig: 5,
                lastSig: 1,
                hasBackup: false,
                forceWrite: false
            )
        );

    [Fact]
    public void Newly_registered_dirty_document_writes_immediately()
    {
        // 登録直後（hasBackup=false・lastSig=現内容）でも、既に dirty なら書く＝起動時無題タブの保護窓を塞ぐ。
        long sig = ContentSignature.Of("メモ");
        // 登録時の初回判定では lastSig をベースライン化する前に Modified を見て Write を選べること。
        Assert.Equal(
            BackupAction.Write,
            BackupPlanner.Decide(
                modified: true,
                currentSig: sig,
                lastSig: sig,
                hasBackup: false,
                forceWrite: true
            )
        );
    }

    [Fact]
    public void Signature_differs_for_different_content_and_is_stable()
    {
        Assert.Equal(ContentSignature.Of("abc"), ContentSignature.Of("abc"));
        Assert.NotEqual(ContentSignature.Of("abc"), ContentSignature.Of("abd"));
        Assert.NotEqual(ContentSignature.Of(""), ContentSignature.Of("a"));
        // 長さ混合により、入れ替えで衝突しやすい組も分離。
        Assert.NotEqual(ContentSignature.Of("ab"), ContentSignature.Of("ba"));
    }
}
