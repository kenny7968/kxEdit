using Xunit;
using yEdit.Core.Backup;
using yEdit.Core.Session;

namespace yEdit.Core.Tests.Session;

/// <summary>
/// LegacySessionConverter(PR #22 形式 → 統合復元入力への一回限り変換・設計 §8/§10)の
/// 純関数テスト。BufferKey の合成 BackupRecord 化・旧 E4 意味論(本文の無い無題は skip)の保存・
/// Normalize 通過を固定する。
/// </summary>
public class LegacySessionConverterTests
{
    private static readonly DateTime Now = new(2026, 7, 23, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>正規形(GUID N・lowercase 32 桁 hex)の BufferKey を生成する(旧形式(PR #22)の書込側と同形)。</summary>
    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static SessionTabRecord Tab(
        string? path = null,
        int untitledNumber = 0,
        string? bufferKey = null,
        bool isActive = false,
        int caretLine = 0,
        int caretColumn = 0,
        int codePage = 0,
        bool hasBom = false,
        int lineEnding = 0,
        bool wasModified = false
    ) =>
        new(
            path,
            untitledNumber,
            bufferKey,
            isActive,
            caretLine,
            caretColumn,
            codePage,
            hasBom,
            lineEnding,
            wasModified
        );

    private static LastSessionSnapshot Snap(params SessionTabRecord[] tabs) =>
        new(new List<SessionTabRecord>(tabs));

    [Fact]
    public void Convert_DirtyPathRecord_SynthesizesBackupRecord_AndReferencesIt()
    {
        string key = NewKey();
        var snap = Snap(
            Tab(
                path: @"C:\a\a.txt",
                bufferKey: key,
                isActive: true,
                caretLine: 3,
                caretColumn: 5,
                codePage: 932,
                hasBom: true,
                lineEnding: 1
            )
        );
        var buffers = new Dictionary<string, string> { [key] = "dirty content" };

        var (layout, backups) = LegacySessionConverter.Convert(snap, buffers, Now);

        var t = Assert.Single(layout.Tabs);
        Assert.Equal(@"C:\a\a.txt", t.Path);
        Assert.Equal(key, t.BackupId);
        Assert.True(t.IsActive);
        Assert.Equal(3, t.CaretLine);
        Assert.Equal(5, t.CaretColumn);
        Assert.Equal(1, t.LineEnding);
        Assert.Equal(Now, layout.SavedAtUtc);
        var bk = Assert.Single(backups);
        Assert.Equal(key, bk.Id);
        Assert.Equal(@"C:\a\a.txt", bk.OriginalPath);
        Assert.Equal(0, bk.UntitledNumber);
        Assert.Equal(932, bk.CodePage);
        Assert.True(bk.HasBom);
        Assert.Equal(1, bk.LineEndingId);
        Assert.Equal("dirty content", bk.Content);
        Assert.Equal(Now, bk.TimestampUtc); // 合成 record の Timestamp は変換時刻
    }

    [Fact]
    public void Convert_CleanPathRecord_KeepsTab_WithNullBackupId_NoSyntheticRecord()
    {
        var snap = Snap(Tab(path: @"C:\a\clean.txt", bufferKey: null, caretColumn: 2));

        var (layout, backups) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        var t = Assert.Single(layout.Tabs);
        Assert.Equal(@"C:\a\clean.txt", t.Path);
        Assert.Null(t.BackupId); // 非 dirty=disk 再オープンの形
        Assert.Empty(backups);
    }

    [Fact]
    public void Convert_UntitledWithContent_SynthesizesUntitledBackup()
    {
        string key = NewKey();
        var snap = Snap(Tab(path: null, untitledNumber: 2, bufferKey: key, lineEnding: 1));
        var buffers = new Dictionary<string, string> { [key] = "unsaved new" };

        var (layout, backups) = LegacySessionConverter.Convert(snap, buffers, Now);

        var t = Assert.Single(layout.Tabs);
        Assert.Null(t.Path);
        Assert.Equal(2, t.UntitledNumber);
        Assert.Equal(key, t.BackupId);
        var bk = Assert.Single(backups);
        Assert.Null(bk.OriginalPath);
        Assert.Equal(2, bk.UntitledNumber);
        Assert.Equal("unsaved new", bk.Content);
    }

    [Fact]
    public void Convert_UntitledWithoutBufferKey_IsSkipped()
    {
        // 旧形式の「無題かつ BufferKey なし」= cap 超過で枠だけ保存された分。旧 E4 意味論
        // (空の無題タブを追加しない)を保存して skip する(設計 §10。新形式の
        // 「無題 BackupId なし=空枠復元」とは意味が異なる)。
        var snap = Snap(Tab(path: null, untitledNumber: 1, bufferKey: null));

        var (layout, backups) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        Assert.Empty(layout.Tabs);
        Assert.Empty(backups);
    }

    [Fact]
    public void Convert_PathRecord_BufferKeyMissingFromBuffers_DemotesToNullBackupId()
    {
        // buffers.json 欠落/破損相当。パスありは BackupId=null(統合復元で disk 再オープンに demote される形)。
        var snap = Snap(Tab(path: @"C:\a\a.txt", bufferKey: NewKey()));

        var (layout, backups) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        var t = Assert.Single(layout.Tabs);
        Assert.Equal(@"C:\a\a.txt", t.Path);
        Assert.Null(t.BackupId);
        Assert.Empty(backups);
    }

    [Fact]
    public void Convert_Untitled_BufferKeyMissingFromBuffers_IsSkipped()
    {
        var snap = Snap(Tab(path: null, untitledNumber: 3, bufferKey: NewKey()));

        var (layout, backups) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        Assert.Empty(layout.Tabs);
        Assert.Empty(backups);
    }

    [Theory]
    [InlineData("../evil")] // パストラバーサル形
    [InlineData("ABCDEF00112233445566778899AABBCC")] // 大文字 hex(自プロセスは必ず lowercase)
    [InlineData("0123456789abcdef0123456789abcdef0")] // 33 桁(長さ不正)
    public void Convert_InvalidBufferKey_DoesNotSynthesizeRecord(string invalidKey)
    {
        // 不正 BufferKey は BackupIdValidator で遮断=合成 BackupRecord の Id に流用しない
        // (Id はバックアップファイル名に使われる契約のため HIGH-1 対称の入口防御)。
        var snap = Snap(
            Tab(path: @"C:\a\a.txt", bufferKey: invalidKey),
            Tab(path: null, untitledNumber: 1, bufferKey: invalidKey)
        );
        var buffers = new Dictionary<string, string> { [invalidKey] = "content" };

        var (layout, backups) = LegacySessionConverter.Convert(snap, buffers, Now);

        // パスありは disk 再オープンの形で残り、無題は本文なし扱いで skip される
        var t = Assert.Single(layout.Tabs);
        Assert.Equal(@"C:\a\a.txt", t.Path);
        Assert.Null(t.BackupId);
        Assert.Empty(backups);
    }

    [Fact]
    public void Convert_WasModified_DoesNotAffectResult()
    {
        // 統合後は WasModified を持たない=バックアップ本文があれば常に Modified=true で復元される
        // (設計 §10・旧 WasModified=false の無題本文は Modified=true になる軽微な挙動変更を受容)。
        // 変換が WasModified を読まないことを true/false の出力一致で固定する。
        string key1 = NewKey();
        string key2 = NewKey();
        var buffers1 = new Dictionary<string, string> { [key1] = "text" };
        var buffers2 = new Dictionary<string, string> { [key2] = "text" };
        var snapTrue = Snap(Tab(path: null, untitledNumber: 1, bufferKey: key1, wasModified: true));
        var snapFalse = Snap(
            Tab(path: null, untitledNumber: 1, bufferKey: key2, wasModified: false)
        );

        var (layoutTrue, backupsTrue) = LegacySessionConverter.Convert(snapTrue, buffers1, Now);
        var (layoutFalse, backupsFalse) = LegacySessionConverter.Convert(snapFalse, buffers2, Now);

        // BackupId(=BufferKey 由来)以外は完全一致
        Assert.Equal(
            Assert.Single(layoutTrue.Tabs) with
            {
                BackupId = null,
            },
            Assert.Single(layoutFalse.Tabs) with
            {
                BackupId = null,
            }
        );
        Assert.Equal(
            Assert.Single(backupsTrue) with
            {
                Id = "",
            },
            Assert.Single(backupsFalse) with
            {
                Id = "",
            }
        );
    }

    [Fact]
    public void Convert_DuplicateBufferKey_OnlyFirstSynthesizes()
    {
        // 同一 BufferKey の重複参照(改竄/バグ由来)は最初の 1 個だけが合成し、2 個目以降は
        // BackupId=null に落ちる(1 バックアップ 1 タブの不変=Normalize の重複 demote と同方針)。
        string dup = NewKey();
        var snap = Snap(
            Tab(path: @"C:\a\first.txt", bufferKey: dup),
            Tab(path: @"C:\a\second.txt", bufferKey: dup)
        );
        var buffers = new Dictionary<string, string> { [dup] = "content" };

        var (layout, backups) = LegacySessionConverter.Convert(snap, buffers, Now);

        Assert.Equal(2, layout.Tabs.Count);
        Assert.Equal(dup, layout.Tabs[0].BackupId);
        Assert.Null(layout.Tabs[1].BackupId);
        Assert.Single(backups);
    }

    [Fact]
    public void Convert_NullTabElement_IsSkipped()
    {
        // 旧 settings.json の JSON `[null, {...}]` は List に null 要素として deserialize され得る。
        var snap = new LastSessionSnapshot(
            new List<SessionTabRecord> { null!, Tab(path: @"C:\a\keep.txt") }
        );

        var (layout, backups) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        var t = Assert.Single(layout.Tabs);
        Assert.Equal(@"C:\a\keep.txt", t.Path);
        Assert.Empty(backups);
    }

    [Fact]
    public void Convert_ResultPassesThroughNormalize_TabsCapped()
    {
        // 変換結果は SessionLayoutStore.Normalize を通る契約(設計 §2.3 の防御を移行入力にも適用)。
        // 代表として要素数上限(200)の切り詰めを固定する。
        var tabs = new List<SessionTabRecord>();
        for (int i = 0; i < SessionLayoutStore.MaxTabs + 1; i++)
            tabs.Add(Tab(path: $@"C:\a\file{i}.txt"));
        var snap = new LastSessionSnapshot(tabs);

        var (layout, _) = LegacySessionConverter.Convert(
            snap,
            new Dictionary<string, string>(),
            Now
        );

        Assert.Equal(SessionLayoutStore.MaxTabs, layout.Tabs.Count);
        Assert.Equal(@"C:\a\file0.txt", layout.Tabs[0].Path);
        Assert.Equal($@"C:\a\file{SessionLayoutStore.MaxTabs - 1}.txt", layout.Tabs[^1].Path);
    }
}
