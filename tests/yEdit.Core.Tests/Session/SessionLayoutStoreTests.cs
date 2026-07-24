using System.IO;
using Xunit;
using yEdit.Core.Session;

namespace yEdit.Core.Tests.Session;

public class SessionLayoutStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    /// <summary>正規形(GUID N・lowercase 32 桁 hex)の BackupId を生成する。</summary>
    private static string NewBackupId() => Guid.NewGuid().ToString("N");

    /// <summary>既定値のレコードを 1 個作る(必要なフィールドだけ上書きして使う)。</summary>
    private static SessionLayoutRecord Rec(
        string? path = null,
        int untitledNumber = 0,
        string? backupId = null,
        bool isActive = false,
        int caretLine = 0,
        int caretColumn = 0,
        int lineEnding = 0
    ) => new(path, untitledNumber, backupId, isActive, caretLine, caretColumn, lineEnding);

    // ---- Save → Load 往復 ----

    [Fact]
    public void Save_Then_Load_Roundtrips_EmptyTabs()
    {
        string path = TempPath();
        try
        {
            var savedAt = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc);
            SessionLayoutStore.Save(
                path,
                new SessionLayout(new List<SessionLayoutRecord>(), savedAt)
            );
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.Tabs);
            Assert.Equal(savedAt, loaded.SavedAtUtc);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_SingleTab_JapanesePath()
    {
        string path = TempPath();
        try
        {
            string backupId = NewBackupId();
            var tab = Rec(
                path: @"C:\メモ\日本語 パス.txt",
                backupId: backupId,
                isActive: true,
                caretLine: 3,
                caretColumn: 5,
                lineEnding: 1
            );
            var savedAt = new DateTime(2026, 7, 23, 12, 34, 56, 789, DateTimeKind.Utc);
            SessionLayoutStore.Save(
                path,
                new SessionLayout(new List<SessionLayoutRecord> { tab }, savedAt)
            );
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            var t = Assert.Single(loaded.Tabs);
            Assert.Equal(@"C:\メモ\日本語 パス.txt", t.Path);
            Assert.Equal(backupId, t.BackupId);
            Assert.True(t.IsActive);
            Assert.Equal(3, t.CaretLine);
            Assert.Equal(5, t.CaretColumn);
            Assert.Equal(1, t.LineEnding);
            Assert.Equal(savedAt, loaded.SavedAtUtc);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_Then_Load_Roundtrips_ManyTabs_PreservesOrder()
    {
        string path = TempPath();
        try
        {
            var tabs = new List<SessionLayoutRecord>
            {
                Rec(path: @"C:\a\一.txt"),
                Rec(untitledNumber: 2), // 無題タブ(Path=null)
                Rec(path: @"C:\a\三.md", isActive: true),
                Rec(path: @"C:\b\四.csv"),
                Rec(untitledNumber: 5),
            };
            SessionLayoutStore.Save(path, new SessionLayout(tabs, DateTime.UtcNow));
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(5, loaded.Tabs.Count);
            Assert.Equal(@"C:\a\一.txt", loaded.Tabs[0].Path);
            Assert.Equal(2, loaded.Tabs[1].UntitledNumber);
            Assert.True(loaded.Tabs[2].IsActive);
            Assert.Equal(@"C:\b\四.csv", loaded.Tabs[3].Path);
            Assert.Equal(5, loaded.Tabs[4].UntitledNumber);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ---- Load の防御(設計 §2.3) ----

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(SessionLayoutStore.Load(TempPath()));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            Assert.Null(SessionLayoutStore.Load(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsNull()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, string.Empty);
            Assert.Null(SessionLayoutStore.Load(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_JsonNullLiteral_ReturnsNull()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "null");
            Assert.Null(SessionLayoutStore.Load(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_OversizeFile_ReturnsNull_EvenIfValidJson()
    {
        string path = TempPath();
        try
        {
            // Task 1 レビュー: パース失敗で緑になる入力(ゼロ埋め等)では cap 除去の変異が
            // 生き残る。JSON 末尾空白は合法=cap がなければパース成功する入力で cap 自体を固定する。
            SessionLayoutStore.Save(
                path,
                new SessionLayout(
                    new List<SessionLayoutRecord> { Rec(path: @"C:\a\ok.txt") },
                    DateTime.UtcNow
                )
            );
            File.AppendAllText(path, new string(' ', (int)SessionLayoutStore.MaxLoadFileSizeBytes));
            Assert.Null(SessionLayoutStore.Load(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_TrailingWhitespace_WithinCap_StillLoads()
    {
        string path = TempPath();
        try
        {
            // 上の oversize テストの前提確認: 末尾空白パディング自体は Deserialize を壊さない
            // (=oversize テストを落とすのは size cap だけ、を保証する)。
            SessionLayoutStore.Save(
                path,
                new SessionLayout(
                    new List<SessionLayoutRecord> { Rec(path: @"C:\a\ok.txt") },
                    DateTime.UtcNow
                )
            );
            File.AppendAllText(path, new string(' ', 1024));
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Single(loaded.Tabs);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_AppliesNormalize_ToNonNormalizedFile()
    {
        string path = TempPath();
        try
        {
            // Task 1 レビュー: Load→Normalize の結線を公開 API 経由で固定する(結線を外す変異が
            // Normalize 直接テストだけでは生き残るため)。重複 BackupId+複数 IsActive の 2 防御で検証。
            string dup = NewBackupId();
            var tabs = new List<SessionLayoutRecord>
            {
                Rec(backupId: dup, isActive: true),
                Rec(backupId: dup, isActive: true),
            };
            SessionLayoutStore.Save(path, new SessionLayout(tabs, DateTime.UtcNow));
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Tabs.Count);
            Assert.Equal(dup, loaded.Tabs[0].BackupId);
            Assert.Null(loaded.Tabs[1].BackupId); // 重複 demote が Load 経由でも効く
            Assert.True(loaded.Tabs[0].IsActive);
            Assert.False(loaded.Tabs[1].IsActive); // active 単一化が Load 経由でも効く
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // ---- Save ----

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "session-state.json");
        try
        {
            SessionLayoutStore.Save(
                path,
                new SessionLayout(new List<SessionLayoutRecord>(), DateTime.UtcNow)
            );
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Save_Overwrites_Existing_File_And_LeavesNoTempResidue()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string path = Path.Combine(dir, "session-state.json");
        try
        {
            // 新規 Move 経路と Replace 経路の両方を通す(AtomicFile 契約=temp 残骸なし)。
            SessionLayoutStore.Save(
                path,
                new SessionLayout(
                    new List<SessionLayoutRecord> { Rec(path: @"C:\a\first.txt") },
                    DateTime.UtcNow
                )
            );
            SessionLayoutStore.Save(
                path,
                new SessionLayout(
                    new List<SessionLayoutRecord> { Rec(path: @"C:\a\second.txt") },
                    DateTime.UtcNow
                )
            );
            var loaded = SessionLayoutStore.Load(path);
            Assert.NotNull(loaded);
            var t = Assert.Single(loaded.Tabs);
            Assert.Equal(@"C:\a\second.txt", t.Path);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    // ---- Delete ----

    [Fact]
    public void Delete_MissingFile_IsNoOp()
    {
        string path = TempPath();
        // 例外にならず、存在しないファイルが依然として存在しないこと(no-op)を確認する。
        SessionLayoutStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_ExistingFile_Removes()
    {
        string path = TempPath();
        File.WriteAllText(path, "{}");
        SessionLayoutStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    // ---- Normalize 防御(設計 §2.3。InternalsVisibleTo 経由で直接検証) ----

    [Fact]
    public void Normalize_NullTabs_ReturnsEmptyTabs()
    {
        var savedAt = new DateTime(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc);
        var result = SessionLayoutStore.Normalize(new SessionLayout(null!, savedAt));
        Assert.Empty(result.Tabs);
        Assert.Equal(savedAt, result.SavedAtUtc);
    }

    [Fact]
    public void Normalize_NullElements_AreSkipped()
    {
        // JSON の `[null, {...}]` は List<T> に null 要素として deserialize され得る。
        var tabs = new List<SessionLayoutRecord> { null!, Rec(path: @"C:\a\keep.txt"), null! };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        var t = Assert.Single(result.Tabs);
        Assert.Equal(@"C:\a\keep.txt", t.Path);
    }

    [Fact]
    public void Normalize_TabsOverMax_TruncatedToMaxTabs()
    {
        var tabs = new List<SessionLayoutRecord>();
        for (int i = 0; i < SessionLayoutStore.MaxTabs + 1; i++)
            tabs.Add(Rec(untitledNumber: i));
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        Assert.Equal(SessionLayoutStore.MaxTabs, result.Tabs.Count);
        // 先頭 MaxTabs 件が順序どおり残ること(末尾切り詰め)。
        Assert.Equal(0, result.Tabs[0].UntitledNumber);
        Assert.Equal(SessionLayoutStore.MaxTabs - 1, result.Tabs[^1].UntitledNumber);
    }

    [Fact]
    public void Normalize_WhitespaceOnlyPath_IsSkipped_ButNullPathUntitledKept()
    {
        var tabs = new List<SessionLayoutRecord>
        {
            Rec(path: "   "), // 空白のみ → skip
            Rec(path: "\t"), // タブのみ → skip
            Rec(path: null, untitledNumber: 3), // 無題(Path=null)は保持
        };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        var t = Assert.Single(result.Tabs);
        Assert.Null(t.Path);
        Assert.Equal(3, t.UntitledNumber);
    }

    [Theory]
    [InlineData("../evil")] // パストラバーサル形
    [InlineData("ABCDEF00112233445566778899AABBCC")] // 大文字 hex(自プロセスは必ず lowercase)
    [InlineData("0123456789abcdef0123456789abcdef0")] // 33 桁(長さ不正)
    [InlineData(" 0123456789abcdef0123456789abcdef")] // 先頭半角空白(TryParseExact の Trim 対策)
    [InlineData("0123456789abcdef0123456789abcdef\n")] // 末尾 LF
    [InlineData("　0123456789abcdef0123456789abcdef")] // 先頭全角空白 (U+3000)
    public void Normalize_InvalidBackupId_IsNulled(string invalidId)
    {
        var tabs = new List<SessionLayoutRecord> { Rec(backupId: invalidId) };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        var t = Assert.Single(result.Tabs);
        Assert.Null(t.BackupId);
    }

    [Fact]
    public void Normalize_DuplicateBackupId_SecondAndLaterNulled()
    {
        string dup = NewBackupId();
        var tabs = new List<SessionLayoutRecord>
        {
            Rec(backupId: dup),
            Rec(backupId: dup),
            Rec(backupId: dup),
        };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        Assert.Equal(3, result.Tabs.Count);
        Assert.Equal(dup, result.Tabs[0].BackupId);
        Assert.Null(result.Tabs[1].BackupId);
        Assert.Null(result.Tabs[2].BackupId);
    }

    [Fact]
    public void Normalize_MultipleActive_OnlyFirstKeptTrue()
    {
        // 最初の active を非先頭(index 1)に置き、「index 0 が既定で true になる」実装と区別する。
        var tabs = new List<SessionLayoutRecord>
        {
            Rec(isActive: false),
            Rec(isActive: true),
            Rec(isActive: true),
        };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        Assert.False(result.Tabs[0].IsActive);
        Assert.True(result.Tabs[1].IsActive);
        Assert.False(result.Tabs[2].IsActive);
    }

    [Fact]
    public void Normalize_NegativeNumbers_ClampedToZero()
    {
        var tabs = new List<SessionLayoutRecord>
        {
            Rec(untitledNumber: -1, caretLine: -2, caretColumn: -3, lineEnding: -4),
        };
        var result = SessionLayoutStore.Normalize(new SessionLayout(tabs, DateTime.UtcNow));
        var t = Assert.Single(result.Tabs);
        Assert.Equal(0, t.UntitledNumber);
        Assert.Equal(0, t.CaretLine);
        Assert.Equal(0, t.CaretColumn);
        Assert.Equal(0, t.LineEnding);
    }

    [Fact]
    public void Normalize_ValidRecord_IsPreservedUnchanged()
    {
        // 正常入力が Normalize で変質しないこと(防御が過剰に効かない)。
        string backupId = NewBackupId();
        var tab = Rec(
            path: @"C:\a\ok.txt",
            untitledNumber: 1,
            backupId: backupId,
            isActive: true,
            caretLine: 10,
            caretColumn: 20,
            lineEnding: 2
        );
        var result = SessionLayoutStore.Normalize(
            new SessionLayout(new List<SessionLayoutRecord> { tab }, DateTime.UtcNow)
        );
        Assert.Equal(tab, Assert.Single(result.Tabs));
    }
}
