using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Backup;
using kxEdit.Core.Settings;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// M-18(設計 2026-09-03): 外部変更の観測値の捕捉・検知・読み直し・保存直前の確認。
/// 実 DocumentManager+実 EditorControl+実ファイル I/O を使い、Form/OS 境界
/// (FakePrompt / FakeFileTimestampProvider)だけを偽物にする(FileControllerTests と同じ思想)。
/// ミューテーション検証は行わない(CLAUDE.md §4-A: ファイル I/O は禁止対象)。
/// </summary>
public class FileControllerExternalChangeTests
{
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FileController File { get; }
        public AppSettings Settings = new();
        public FakePrompt Prompt { get; } = new();
        public FakeFileDialogService Dialogs { get; } = new();
        public FakeReachabilityProbe Probe { get; } = new();
        public FakeFileTimestampProvider Timestamps { get; } = new();
        public List<Document> OpenedFresh { get; } = new();

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            File = new FileController(
                docs: Docs,
                owner: Form,
                settings: () => Settings,
                saveSettings: () => { },
                recentChanged: () => { },
                metaChanged: () => { },
                openedFresh: d => OpenedFresh.Add(d),
                prompt: Prompt,
                fileDialogs: Dialogs,
                reachabilityProbe: Probe,
                fileTimestamps: Timestamps
            );
        }

        public void Dispose() => Form.Dispose();
    }

    private static readonly DateTime T0 = new(2026, 09, 03, 10, 00, 00, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddMinutes(1);

    /// <summary>ファイルを作り、観測値 <paramref name="stamp"/> で開く。</summary>
    private static (Document Doc, string Path) Open(
        Host host,
        TempDir tmp,
        string name,
        string content,
        DateTime? stamp
    )
    {
        string path = tmp.File(name);
        File2.WriteAllText(path, content);
        if (stamp is DateTime s)
            host.Timestamps.Times[path] = s;
        var doc = host.File.TryOpenOrActivate(path);
        Assert.NotNull(doc);
        return (doc!, path);
    }

    // ===== Task 2: 観測値の捕捉 =====

    /// <summary>開くときは本文を読む<b>前</b>に取る(設計 §3.2)。問い合わせの瞬間に外部が書くと、
    /// 本文は書換後・観測値は書換前になる。読んだ後に取る実装では本文が "before" のまま。</summary>
    [Fact]
    public void Open_CapturesTimestampBeforeReading() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "before");
            host.Timestamps.Times[path] = T0;
            host.Timestamps.OnQuery = p => File2.WriteAllText(p, "after");

            var doc = host.File.TryOpenOrActivate(path)!;

            Assert.Equal("after", doc.Editor.Text);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc);
        });

    /// <summary>保存は書いた<b>後</b>に取る(設計 §3.2)。問い合わせの瞬間にディスクを読むと
    /// 新しい本文が見える。書く前に取る実装では "old" が見える。</summary>
    [Fact]
    public void Save_CapturesTimestampAfterWriting() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "old", T0);
            doc.Editor.ReplaceCharRange(0, 3, "new");
            host.Timestamps.Times[path] = T1; // 保存後のディスクが返す値
            string? seenAtQuery = null;
            host.Timestamps.OnQuery = p => seenAtQuery = File2.ReadAllText(p);

            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal("new", seenAtQuery);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
        });

    /// <summary>バックアップ復元は A-1 の陳腐化判定が取った値を流用する(設計 §3.2)。</summary>
    [Fact]
    public void RestoreFromBackup_CapturesDiskTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            string path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kxEdit-m18-restore.txt")
            );
            host.Timestamps.Times[path] = T1;

            var doc = host.File.RestoreFromBackup(
                new BackupRecord(
                    Id: Guid.NewGuid().ToString("N"),
                    OriginalPath: path,
                    UntitledNumber: 0,
                    CodePage: 65001,
                    HasBom: false,
                    LineEndingId: 0,
                    Content: "backup content",
                    TimestampUtc: T0
                )
            );

            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
            Assert.Single(host.Timestamps.Queries); // A-1 の 1 回だけ。追加 I/O を作らない
        });
}
