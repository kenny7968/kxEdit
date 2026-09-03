using System.IO;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Backup;
using kxEdit.Core.Session;
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

    /// <summary>外部プロセスの書換を模す: 本文と、プロバイダが返す更新時刻の両方を変える。</summary>
    private static void ExternalWrite(Host host, string path, string content, DateTime stamp)
    {
        File2.WriteAllText(path, content);
        host.Timestamps.Times[path] = stamp;
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
            // 問い合わせは M-18 の 1 回だけ: 将来 read 前に別の問い合わせが足されて M-18 の問い合わせが
            // read 後へ動いても、OnQuery が同じ書込を 2 回するため緑のまま通ってしまうのを塞ぐ。
            Assert.Single(host.Timestamps.Queries);
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
            // Open ヘルパの 1 回+保存の 1 回。問い合わせが増えると OnQuery が同じ読取を繰り返し、
            // 「書いた後」の問い合わせが書く前へ動いても最後の読取で緑になってしまうのを塞ぐ。
            Assert.Equal(2, host.Timestamps.Queries.Count);
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

    /// <summary>hot exit 復元(RestoreSession → RestoreDirtyFromBackup = A-1 の主経路)でも
    /// A-1 の値が入る(設計 §3.2)。<see cref="RestoreFromBackup_CapturesDiskTimestamp"/> は
    /// ダイアログ経路(RestoreFromBackup)だけを通るので、RestoreDirtyFromBackup 側の代入を
    /// 固定する網はこの 1 本だけ。</summary>
    [Fact]
    public void RestoreSession_DirtyRestore_CapturesDiskTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            string path = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kxEdit-m18-session.txt")
            );
            host.Timestamps.Times[path] = T1;
            var bk = new BackupRecord(
                Id: Guid.NewGuid().ToString("N"),
                OriginalPath: path,
                UntitledNumber: 0,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "backup content",
                TimestampUtc: T0
            );
            var layout = new SessionLayout(
                new List<SessionLayoutRecord>
                {
                    new(
                        Path: path,
                        UntitledNumber: 0,
                        BackupId: bk.Id,
                        IsActive: true,
                        CaretLine: 0,
                        CaretColumn: 0,
                        LineEnding: 0
                    ),
                },
                T0
            );

            _ = host.File.RestoreSession(
                layout,
                new[] { bk },
                initialEmpty: null,
                adoptRestored: null
            );

            var doc = host.Docs.Active!;
            Assert.Equal(path, doc.State.Path);
            Assert.True(doc.Editor.Modified); // dirty 復元であること(RestoreDirtyFromBackup を通った)
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
        });

    // ===== Task 3: CheckExternalChange =====

    [Fact]
    public void Check_Untitled_Skipped_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "x";

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    /// <summary>観測値が無い(開いた時に取れなかった)なら判定しない。ディスク側に値が有っても聞かない。</summary>
    [Fact]
    public void Check_NoKnownStamp_Skipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", stamp: null);
            host.Timestamps.Times[path] = T1;

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    /// <summary>ディスク側が取れない(削除・到達不能)なら判定しない(設計 §3.3 / §9)。</summary>
    [Fact]
    public void Check_DiskUnavailable_Skipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            host.Timestamps.Times.Remove(path);

            Assert.Equal(ExternalChangeOutcome.Skipped, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
        });

    [Fact]
    public void Check_SameStamp_NoChange_NoPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            File2.WriteAllText(path, "v2"); // 本文だけ変えても mtime(観測値)が同じなら変更とみなさない

            Assert.Equal(ExternalChangeOutcome.NoChange, host.File.CheckExternalChange(doc));
            Assert.Empty(host.Prompt.YesNoCalls);
            Assert.Equal("v1", doc.Editor.Text);
        });

    /// <summary>未保存なし・はい: 読み直し、観測値更新、キャレット位置は非既定位置から保たれる。
    /// 文言に「失われます」を含まず、既定は「はい」(defaultNo=false)。</summary>
    [Fact]
    public void Check_Changed_Clean_Yes_ReloadsAndKeepsCaret() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1 text", T0);
            doc.Editor.SetCaretCharOffset(3); // 非既定位置(CLAUDE.md §4-B)
            ExternalWrite(host, path, "v2 longer text", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal("v2 longer text", doc.Editor.Text);
            Assert.False(doc.Editor.Modified);
            Assert.Equal(T1, doc.State.LastKnownWriteTimeUtc);
            // 既定値の確認。リセットの網は Check_Kept_ThenChangedAgain_PromptsAgain 側。
            Assert.Null(doc.State.AcknowledgedWriteTimeUtc);
            Assert.Equal(3, doc.Editor.CaretCharOffset);
            var call = Assert.Single(host.Prompt.YesNoCalls);
            Assert.Equal(("ファイルの変更", false), call);
            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            Assert.Equal("'a.txt' は kxEdit の外で変更されました。読み直しますか?", text);
            // 開き直しと同じく .csv 自動モードの対象(設計 §3.5 手順 4)。1 回目は Open ヘルパの
            // TryOpenOrActivate、2 回目が読み直し。Contains では初回の分で常に緑になり網にならない。
            Assert.Equal(2, host.OpenedFresh.Count(d => ReferenceEquals(d, doc)));
        });

    /// <summary>新しい本文が短ければキャレットは末尾へクランプされる。</summary>
    [Fact]
    public void Check_Changed_Clean_Yes_ClampsCaret() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "0123456789", T0);
            doc.Editor.SetCaretCharOffset(8);
            ExternalWrite(host, path, "ab", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(2, doc.Editor.CaretCharOffset);
        });

    /// <summary>読み直しは現在の文字コードで固定する(自動判定に戻さない = 設計 §3.5)。ユーザーが
    /// 「開き直す」で 932 にした ASCII 本文は、外部変更後の読み直しでも 932 のまま。
    /// <c>forcedCodePage</c> を null(自動判定)に変異させると、ASCII は EncodingDetector の
    /// ②(厳格 UTF-8 デコード成功)で 65001 へ戻るので弁別できる。</summary>
    [Fact]
    public void Check_Reload_KeepsUserChosenEncoding() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "ascii v1", T0);
            Assert.Equal(65001, doc.State.Encoding.CodePage); // 自動判定の既定(ASCII → UTF-8)
            host.Dialogs.EncodingCodePage = 932;
            host.File.ReopenWithEncoding(); // アクティブタブ = 開いた直後の doc
            Assert.Equal(932, doc.State.Encoding.CodePage);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc); // LoadInto が取り直すが Times は不変
            ExternalWrite(host, path, "ascii v2", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(932, doc.State.Encoding.CodePage);
            Assert.Equal("ascii v2", doc.Editor.Text);
        });

    /// <summary>未保存あり・いいえ: 文言が損失を伝え、既定は「いいえ」。本文も Modified も不変。
    /// 観測値(本文の基準)は動かず、ディスクの値は別に憶えて <b>2 回目は聞かない</b>。</summary>
    [Fact]
    public void Check_Changed_Dirty_No_KeepsAndAcknowledges() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            Assert.True(doc.Editor.Modified);
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = false;

            Assert.Equal(ExternalChangeOutcome.Kept, host.File.CheckExternalChange(doc));

            Assert.Equal("mine v1", doc.Editor.Text);
            Assert.True(doc.Editor.Modified);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc); // 本文の基準は動かない(保存直前の確認が生きる)
            Assert.Equal(T1, doc.State.AcknowledgedWriteTimeUtc); // 「読み直さない」の値だけ憶える
            var call = Assert.Single(host.Prompt.YesNoCalls);
            Assert.Equal(("ファイルの変更", true), call);
            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            Assert.Equal(
                "'a.txt' は kxEdit の外で変更されました。読み直すと、このタブの未保存の変更は失われます。読み直しますか?",
                text
            );

            Assert.Equal(ExternalChangeOutcome.NoChange, host.File.CheckExternalChange(doc));
            Assert.Single(host.Prompt.YesNoCalls); // 2 回目は出ない
        });

    /// <summary>「読み直さない」のあとにディスクが<b>さらに</b>変われば聞き直す(憶えた値と一致しない)。
    /// 読み直せば憶えた値は null に戻る=次の判定は本文の基準だけで行う。未編集タブで「いいえ」→ 後で
    /// 読み直す、という一番ありふれた流れ。</summary>
    [Fact]
    public void Check_Kept_ThenChangedAgain_PromptsAgain() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            ExternalWrite(host, path, "v2", T1);
            host.Prompt.YesNoResult = false;
            Assert.Equal(ExternalChangeOutcome.Kept, host.File.CheckExternalChange(doc));

            DateTime t2 = T0.AddMinutes(2);
            ExternalWrite(host, path, "v3", t2);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(2, host.Prompt.YesNoCalls.Count);
            Assert.Equal("v3", doc.Editor.Text);
            Assert.Equal(t2, doc.State.LastKnownWriteTimeUtc);
            Assert.Null(doc.State.AcknowledgedWriteTimeUtc); // 読み直しで戻る
        });

    [Fact]
    public void Check_Changed_Dirty_Yes_DiscardsEdits() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            doc.Editor.ReplaceCharRange(0, 0, "mine ");
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = true;

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal("theirs", doc.Editor.Text);
            Assert.False(doc.Editor.Modified);
        });

    /// <summary>読み直しに失敗(ロック中)したら <see cref="ExternalChangeOutcome.ReloadFailed"/>。
    /// 観測値は更新しない(次の復帰でまた聞く)。本文は不変。エラーは LoadInto が出す。</summary>
    [Fact]
    public void Check_Changed_Yes_ReloadFails_KeepsStampAndText() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            ExternalWrite(host, path, "theirs", T1);
            host.Prompt.YesNoResult = true;
            using var locker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

            Assert.Equal(ExternalChangeOutcome.ReloadFailed, host.File.CheckExternalChange(doc));

            Assert.Equal("v1", doc.Editor.Text);
            Assert.Equal(T0, doc.State.LastKnownWriteTimeUtc);
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
            // 読み直しに失敗したら _openedFresh は走らない(設計 §3.5 手順 2)。1 回 = Open ヘルパの分だけ。
            Assert.Equal(1, host.OpenedFresh.Count(d => ReferenceEquals(d, doc)));
        });

    /// <summary>確認ダイアログのモーダルループの中で届いた 2 本目は何もしない(設計 §3.4 再入ガード)。</summary>
    [Fact]
    public void Check_Reentrant_InnerCallSkipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a.txt", "v1", T0);
            ExternalWrite(host, path, "v2", T1);
            host.Prompt.YesNoResult = true;
            ExternalChangeOutcome? inner = null;
            host.Prompt.OnYesNo = () => inner = host.File.CheckExternalChange(doc);

            Assert.Equal(ExternalChangeOutcome.Reloaded, host.File.CheckExternalChange(doc));

            Assert.Equal(ExternalChangeOutcome.Skipped, inner);
            Assert.Single(host.Prompt.YesNoCalls);
        });

    /// <summary>文言に載るファイル名は無害化する(BiDi 制御文字はファイル名に使える)。</summary>
    [Fact]
    public void Check_PromptSanitizesDisplayName() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var (doc, path) = Open(host, tmp, "a\u202Eb.txt", "v1", T0);
            ExternalWrite(host, path, "v2", T1);
            host.Prompt.YesNoResult = false;

            _ = host.File.CheckExternalChange(doc);

            var text = Assert.Single(host.Prompt.Log, e => e.Kind == "YesNo").Text;
            // Ordinal 必須: 既定の culture 比較(ICU)は U+202E を「無視可能」として空一致させ、
            // 無害化の有無に関わらず常に「見つかった」になる(= 網として機能しない)。
            Assert.DoesNotContain("\u202E", text, StringComparison.Ordinal);
        });
}
