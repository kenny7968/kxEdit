using System.Reflection;
using System.Text;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Backup;
using kxEdit.Core.Session;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 3: FileController の配線・状態遷移・ロールバックのテスト(設計書 §3)。
/// 実 DocumentManager+実 EditorControl+実ファイル I/O(TextFileService=温存対象)を使い、
/// Form/OS 境界(FakePrompt/FakeFileDialogService)だけを偽物にする。
/// Core が検証済みの照合・I/O 正しさ(TextFileService/RecentFilesList/EncodingCatalog)は再検証しない。
/// </summary>
public class FileControllerTests
{
    /// <summary>
    /// FileController を Fake 境界で配線したテストホスト。共通の HostForm.CreateWithDocs で
    /// 「可視・画面外・非アクティブ」の土台を作る(実運用 MainForm は常に可視のため)。
    /// </summary>
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
        public int SaveSettingsCount;
        public int RecentChangedCount;
        public int MetaChangedCount;
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
                saveSettings: () => SaveSettingsCount++,
                recentChanged: () => RecentChangedCount++,
                metaChanged: () => MetaChangedCount++,
                openedFresh: d => OpenedFresh.Add(d),
                prompt: Prompt,
                fileDialogs: Dialogs,
                reachabilityProbe: Probe,
                fileTimestamps: Timestamps
            );
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== SaveAs ロールバック(データ破損防止の要=最優先) =====

    [Fact]
    public void SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // 既定 State=UTF-8/BOM なし/CRLF
            // 存在しないフォルダ配下を保存先にして TextFileService.Save を確実に失敗させる
            // (DirectoryNotFoundException は IOException 派生=想定内エラー経路)。
            // CodePage は 932 を選ぶ: 既定(65001)と同値だと Encoding ロールバックの assert が
            // 空振りする(レビュー I-1)。"abc" は ASCII なので 932 でも劣化警告は出ない。
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File(@"no-such-dir\a.txt"),
                932,
                HasBom: true,
                LineEnding.Lf
            );

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path); // Path は旧のまま(後続 Ctrl+S の別エンコード上書き事故防止)
            Assert.Equal(65001, doc.State.Encoding.CodePage); // ロールバック(932→65001)
            Assert.False(doc.State.HasBom); // ロールバック
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // ロールバック
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("保存できませんでした", System.StringComparison.Ordinal)
            );
        });

    [Fact]
    public void SaveAs_Success_UpdatesMeta_SetsSavePoint_AndRegistersRecent() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty にして SetSavePoint の効果を観測する
            string path = tmp.File("a.txt");
            host.Dialogs.SaveAs = new SaveAsResult(path, 65001, HasBom: true, LineEnding.Lf);

            Assert.True(host.File.SaveAs());

            Assert.Equal(path, doc.State.Path);
            Assert.True(doc.State.HasBom);
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
            Assert.False(doc.Editor.Modified); // SetSavePoint 済み
            var bytes = File2.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray()); // HasBom が Save まで配線される
            Assert.Equal(path, host.Settings.RecentFiles[0]); // RegisterRecent の配線
            Assert.True(host.SaveSettingsCount >= 1);
            Assert.True(host.RecentChangedCount >= 1);
            // ダイアログへ現在値が初期値として渡る
            Assert.Equal(
                new SaveAsRequest(null, 65001, false, LineEnding.Crlf),
                Assert.Single(host.Dialogs.SaveAsRequests)
            );
        });

    [Fact]
    public void SaveAs_Cancelled_ReturnsFalse_AndChangesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = null; // キャンセル

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path);
            Assert.Empty(host.Prompt.Log);
            Assert.Empty(host.Settings.RecentFiles);
        });

    /// <summary>
    /// 空白パスは警告し、State を書き換えない。文言の全文と no-change を固定するのはこのテストだけ。
    /// Task 4(2026-08-23)以降、この <c>false</c> は「警告して中止した」結果ではない:
    /// 警告後は <c>continue</c> してダイアログを再表示し、2 回目に Fake のキューが枯渇して
    /// キャンセル扱いになった結果である。**再表示そのものの pin は
    /// <see cref="SaveAs_BlankPath_WarnsAndReopensDialog"/>** が持つ。
    /// (旧名 `SaveAs_WhitespacePath_WarnsAndAborts` は「中止」を主張していたが、
    /// `continue` を `return false` へ戻す改悪が入っても本テストは緑のままなので、名前が
    /// 改悪を追認する状態だった。CLAUDE.md §4「assertion の前提と guard の発火条件を一致させる」)
    /// </summary>
    [Fact]
    public void SaveAs_WhitespacePath_WarnsWithExactMessage_AndLeavesStateUnchanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult("   ", 65001, HasBom: false, LineEnding.Crlf);

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Warn" && e.Text == "ファイル名を指定してください。"
            );
        });

    // ===== ダイアログ再表示ループ =====

    /// <summary>
    /// 空白パスの警告後に SaveAs 全体を中止せず、入力し直せるようにダイアログを再表示する。
    /// 「Warn が出たこと」だけを見ると continue → return false の変異が生き残るので、
    /// PickSaveAsCount で再表示そのものを固定する。
    /// </summary>
    [Fact]
    public void SaveAs_BlankPath_WarnsAndReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            string path = tmp.File("a.txt");
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult("   ", 65001, false, LineEnding.Crlf)
            );
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(path, 65001, false, LineEnding.Crlf));

            Assert.True(host.File.SaveAs()); // 2 回目の入力で保存が成立する

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Warn" && e.Text.Contains("ファイル名")
            );
            Assert.True(File2.Exists(path));
        });

    /// <summary>キャンセルはループの唯一の途中出口。再表示しない。</summary>
    [Fact]
    public void SaveAs_Cancelled_WritesNothingAndDoesNotReopen() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = null; // キャンセル

            Assert.False(host.File.SaveAs());

            Assert.Equal(1, host.Dialogs.PickSaveAsCount);
            Assert.Null(doc.State.Path);
        });

    /// <summary>再表示のとき、直前に入力した値が初期値として戻る(打ち直しを強いない)。</summary>
    [Fact]
    public void SaveAs_Reopened_SeedsDialogWithPreviousInput() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // 既定 State = UTF-8 / BOM なし / CRLF
            // 非既定のエンコード・改行で入力する(既定と同値だと seed の伝播を検証できない)。
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult("   ", 932, HasBom: true, LineEnding.Lf)
            );

            host.File.SaveAs(); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            var second = host.Dialogs.SaveAsRequests[1];
            Assert.Equal("   ", second.Path);
            Assert.Equal(932, second.CodePage);
            Assert.True(second.HasBom);
            Assert.Equal(LineEnding.Lf, second.LineEnding);
        });

    // ===== A-4: ネットワーク共有への新規保存(保存先意味論のプローブ) =====

    /// <summary>
    /// A-4 の回帰。読み取り側の ProbeFileExistsWithTimeout(File.Exists 意味論)を使い続けていると、
    /// 存在しない新規パスは到達可能でも常に false=「ネットワークパスに到達できません」で止まる。
    /// Fake の Result(読み取り側)を false・SaveTargetResult(保存側)を到達可能にすることで、
    /// **どちらのメソッドを使っているか**を判別する(同値だと判別できない)。
    /// 共有が実在しないので書込自体は失敗する。検証するのは「止まった理由」。
    ///
    /// ホストを 127.0.0.1 にするのは意図的: この 2 本は App スイートで唯一
    /// **プローブを素通りさせて実 I/O まで到達する** UNC テストなので、所要時間がホストの
    /// 名前解決に依存する。架空のホスト名はワイルドカード DNS ゾーンで実 IP に解決されると
    /// 445 への TCP SYN 再送で 1 呼出あたり約 21 秒かかりうる。ループバックなら名前解決が
    /// 消え、共有名不在が即座に返る。<c>UncPathDetector.IsUnc</c> は先頭 <c>\\</c> の
    /// 純粋な文字列判定なので IsRemote=true は変わらない。
    /// </summary>
    [Fact]
    public void SaveAs_NewFileOnUncPath_PassesReachabilityGate() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.Result = false; // 読み取り側を使っていたら到達性エラーで止まる
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: true,
                FileExists: false
            );
            host.Dialogs.SaveAs = new SaveAsResult(
                @"\\127.0.0.1\no-such-share\a.txt",
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            Assert.False(host.File.SaveAs()); // 共有が実在しないので書込は失敗する

            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
        });

    /// <summary>
    /// 5 秒契約の pin(読み取り側 LastTimeout の観測点と対称)。
    /// ホストが 127.0.0.1 な理由は上のテストの doc を参照(名前解決依存の排除)。
    /// **回数も pin する**(A-7 (a) 追加時): 旧 <c>&gt;= 1</c> では、Fake の <c>FileExists</c> 既定が
    /// 変わるなどして上書き確認が発火し、「いいえ → 再表示 → キュー枯渇」という**別の経路を
    /// 通りながら緑のまま**になる。1 往復(ダイアログ 1 回・確認なし)であることを固定する。
    /// プローブが **2 回**なのは設計どおり(設計書 §5): SaveAsDocument 段の事前判定と、
    /// Ctrl+S も直接入る <see cref="FileController"/> 内 <c>WriteToPath</c> 冒頭の自己完結ガードで
    /// 1 回ずつ。1 に減らす修正は WriteToPath の自己完結を壊すので、ここを 1 に書き換えないこと。
    /// </summary>
    [Fact]
    public void SaveAs_UncPath_ProbesSaveTargetWithFiveSecondTimeout() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                @"\\127.0.0.1\no-such-share\a.txt",
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            host.File.SaveAs();

            Assert.Equal(2, host.Probe.SaveTargetCallCount); // 事前判定 + WriteToPath 冒頭
            Assert.Equal(1, host.Dialogs.PickSaveAsCount); // 再表示していない = 1 往復
            // 回数だけでは「確認が出て OK された」場合と区別できない(既定 OkCancelResult=true では
            // 続行するので回数が変わらない)。確認が出ていないことも直接見る。
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.SaveTargetLastTimeout);
        });

    /// <summary>
    /// 設計書 §3.3: ローカルパスはリモートゲートで素通りする(挙動不変)。
    /// ゲートを外すと「存在しないフォルダー配下への保存」がプローブで弾かれ、
    /// SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath が WriteToPath に届かなくなる。
    /// </summary>
    [Fact]
    public void SaveAs_LocalNewFile_DoesNotProbe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File("a.txt"),
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            Assert.True(host.File.SaveAs());

            Assert.Equal(0, host.Probe.SaveTargetCallCount);
        });

    // ===== A-19: 保存先パスの正規化 =====

    /// <summary>
    /// A-19。相対パスを未正規化のまま State.Path に残すと保存先が CWD 依存になり、
    /// hot exit 復元で無言の無題化を招く。Environment.CurrentDirectory を触るが、
    /// App.Tests は GlobalUsings.cs で並列実行を無効化済み(CollectionBehavior)なので安全。
    /// </summary>
    [Fact]
    public void SaveAs_RelativePath_StoresAbsolutePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            string saved = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = tmp.Root;
                host.Dialogs.SaveAs = new SaveAsResult("memo.txt", 65001, false, LineEnding.Crlf);

                Assert.True(host.File.SaveAs());
            }
            finally
            {
                Environment.CurrentDirectory = saved;
            }

            // CreateTempSubdirectory は 8.3 名や symlink 経由のパスを返しうるので、
            // 期待値も GetFullPath を通してから比較する(区切り・大小の揺れは吸収しない)。
            string expected = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(tmp.Root, "memo.txt")
            );
            Assert.Equal(expected, doc.State.Path);
            Assert.Equal(expected, host.Settings.RecentFiles[0]); // RegisterRecent も正規化済みを使う
            Assert.True(File2.Exists(expected));
        });

    /// <summary>
    /// A-19 の**再表示側**。<c>SaveAsDocument</c> の <c>seed = seed with { Path = full };</c> は
    /// 「再表示時も絶対パスを見せる(どこへ保存されるかが読み上げで分かる)」と主張しているが、
    /// この行は**丸ごと削除しても全緑だった**(最終品質パス m-2)。
    /// 上の <see cref="SaveAs_RelativePath_StoresAbsolutePath"/> は 1 周で成功するので
    /// 再表示の seed を観測できない。重複タブで <c>continue</c> を 1 回だけ起こして観測する。
    /// SR ユーザーの表示面の性質であってデータ喪失ではないが、行のコメントが
    /// 「位置も load-bearing」とまで書いている以上、無網のまま残さない。
    /// </summary>
    [Fact]
    public void SaveAs_RelativePath_RedisplaysDialogWithAbsolutePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string expected = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(tmp.Root, "memo.txt")
            );

            // 重複タブを在席させて continue を 1 回だけ起こす(2 回目の PickSaveAs を発生させる)。
            var occupant = host.Docs.CreateNew();
            occupant.State.Path = expected;

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            string saved = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = tmp.Root;
                host.Dialogs.SaveAsQueue.Enqueue(
                    new SaveAsResult("memo.txt", 65001, false, LineEnding.Crlf)
                );

                Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル
            }
            finally
            {
                Environment.CurrentDirectory = saved;
            }

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Null(host.Dialogs.SaveAsRequests[0].Path); // 1 回目は State 由来(無題)
            // 2 回目の初期値が生入力 "memo.txt" ではなく正規化済みの絶対パスであること。
            Assert.Equal(expected, host.Dialogs.SaveAsRequests[1].Path);
        });

    /// <summary>正規化不能な入力は握って「入力し直し」に落とす(未捕捉例外ダイアログにしない)。</summary>
    [Fact]
    public void SaveAs_UnnormalizablePath_WarnsAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult("bad\0name.txt", 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Warn"
                    && e.Text.StartsWith("パスが正しくありません", StringComparison.Ordinal)
            );
            Assert.Null(doc.State.Path);
        });

    /// <summary>
    /// 正規化できない入力のうち、<b>例外型が <see cref="ArgumentException"/> ではない</b>枝の網。
    /// 上の <c>bad\0name.txt</c> は <c>ArgumentException</c> 枝しか通らないため、
    /// 総長超過側が無網だった(#47 最終品質パス I-3)。
    /// <para>
    /// <b>Task 3(Issue #48)以降、本テストが担保するのはフィルタの記述ではなく配線である</b>:
    /// 正規化と例外フィルタは <see cref="FileReachabilityProbe.NormalizePathWithTimeout"/> へ
    /// 移設され、<c>FakeReachabilityProbe</c> は既定でその実実装へ委譲する。よってここが固定するのは
    /// 「seam が <see cref="PathNormalizeStatus.Invalid"/> を返したら、SaveAs は未捕捉例外
    /// ダイアログにせず『パスが正しくありません』でダイアログへ戻す」という
    /// <b>FileController 側の分岐</b>。フィルタそのものの網は
    /// <c>FileReachabilityProbeTests.NormalizePath_OverLongPath_ReturnsInvalid</c> にある。
    /// </para>
    /// <para>
    /// <b>以前ここに書いていた 2 つの主張は Task 2 の実測で反証された</b>:
    /// (1)「素の <see cref="System.IO.IOException"/> の窓は CWD 長に依存する fixture になるため
    /// 自動テストにしない」→ <b>誤り</b>。窓の上端は<b>入力長だけで決まり CWD 非依存</b>
    /// (総長 = CWD + 1 + 入力長 なので、入力長 32766 ならどんな CWD でも 32767 を超える)。
    /// <c>NormalizePath_OverLongPath_ReturnsInvalid</c> の <c>[Theory]</c> に 32766 を入れて
    /// 自動化済み(本ファイルには <c>[Theory]</c> は無い。網は別ファイルにある)。
    /// (2)「<see cref="System.IO.PathTooLongException"/> は <c>IOException</c> の派生なので、
    /// この 1 本でフィルタの <c>or IOException</c> という<b>記述そのもの</b>を pin できる」→
    /// <b>誤り</b>。<c>or IOException</c> → <c>or PathTooLongException</c> の変異は
    /// <b>全緑で生存する</b>(40,000 文字が投げるのは <c>PathTooLongException</c> なので、
    /// 狭めた列挙でも捕まってしまう)。入力長と例外型の実測マップは
    /// <see cref="FileReachabilityProbe.NormalizePathWithTimeout"/> の remarks にある。
    /// </para>
    /// </summary>
    [Fact]
    public void SaveAs_OverLongPath_WarnsAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(new string('a', 40000), 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Warn"
                    && e.Text.StartsWith("パスが正しくありません", StringComparison.Ordinal)
            );
            Assert.Null(doc.State.Path);
        });

    // ===== V-1 / V-3(脆弱性レビュー): 親フォルダーが取れない保存先 =====

    /// <summary>
    /// V-1(High)。ドライブルート(C:\)は正規化できるが親フォルダーが無い=書き込み先が確定しない。
    /// 前段ガードが無いと AtomicFile.Write の <c>Path.Combine(GetDirectoryName(...)!, ...)</c> に
    /// null が渡って ArgumentNullException になり、しかも ConvertEols が保存点を壊した後なので
    /// **保存していないのに Modified=false** が残る(ConfirmDiscardIfDirty が素通りし、
    /// 以後タブを閉じても終了しても確認なしで本文が失われる)。
    /// ローカルパスをハードコードしないため root は TempDir から導出する。
    /// </summary>
    [Fact]
    public void SaveAs_DriveRoot_WarnsAndReopens_AndKeepsModified() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string root = System.IO.Path.GetPathRoot(tmp.Root)!;
            Assert.False(string.IsNullOrEmpty(root));

            var doc = host.Docs.CreateNew();
            // 本文に CRLF を入れ、要求 EOL を LF にする = ガードを外したとき ConvertEols が
            // **非 fast-path**でバッファを差し替え、保存点が実際に壊れる条件を作る。
            // これを作らないと Modified の assert が空振りする(fast-path は no-op)。
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty= 非既定状態から検証を始める
            Assert.True(doc.Editor.Modified);
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(root, 65001, false, LineEnding.Lf));

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount); // 中止せず再表示する
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Warn"
                    && e.Text.StartsWith("パスが正しくありません", StringComparison.Ordinal)
            );
            Assert.Null(doc.State.Path);
            Assert.True(doc.Editor.Modified); // V-1 の本体: 未保存の本文が dirty のまま残る
        });

    /// <summary>
    /// V-1 / V-3。予約デバイス名は GetFullPath が <c>\\.\CON</c> へ正規化するため、先頭 <c>\\</c> で
    /// リモート扱いになる。ガードが無いと保存先プローブまで進み「ネットワークパスに到達できません」
    /// という的外れな文言になる(V-3)。親フォルダー無しとして先に弾けば正しい文言になる。
    /// </summary>
    [Fact]
    public void SaveAs_ReservedDeviceName_WarnsWithPathMessage_NotNetworkMessage() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x");
            Assert.True(doc.Editor.Modified);
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult("CON", 65001, false, LineEnding.Lf));

            Assert.False(host.File.SaveAs());

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Warn"
                    && e.Text.StartsWith("パスが正しくありません", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
            Assert.Equal(0, host.Probe.SaveTargetCallCount); // プローブまで届かせない
            Assert.Null(doc.State.Path);
            Assert.True(doc.Editor.Modified);
        });

    /// <summary>
    /// V-1 修正 (b)。SaveAsDocument の前段ガードを**通らない**経路の網: Ctrl+S は
    /// State.Path をそのまま WriteToPath へ渡す。攻撃 backup JSON の
    /// <c>OriginalPath: "C:\\"</c> は OriginalPathValidator の BlockedRoots(C:\Windows 等)に
    /// 該当しないため Ok で通り、State.Path=ドライブルートの復元タブが成立しうる。
    /// WriteToPath の catch フィルタが ArgumentException を持たないと未捕捉例外になり、
    /// ConvertEols のロールバックが発火せず Modified=false のまま残る。
    /// </summary>
    [Fact]
    public void Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string root = System.IO.Path.GetPathRoot(tmp.Root)!;

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x");
            doc.State.LineEnding = LineEnding.Lf; // ConvertEols を非 fast-path にする
            doc.State.Path = root; // 復元タブ相当(SaveAs ダイアログを経由しない)
            Assert.True(doc.Editor.Modified);

            Assert.False(host.File.Save()); // Ctrl+S 経路

            Assert.Equal(0, host.Dialogs.PickSaveAsCount); // Path 確定済み=SaveAs へ落ちない
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
            Assert.True(doc.Editor.Modified); // ロールバック発火=未保存の本文が失われない
        });

    // ===== 境界付き正規化(Issue #48 / S-15)=====

    [Fact]
    public void SaveAs_NormalizeTimesOut_ShowsReachabilityMessage_AndDoesNotSave() =>
        Sta.Run(() =>
        {
            // S-15: 不達共有上の `~` を含むパスは GetFullPath が約 21 秒 UI を止める。
            // seam のタイムアウトで中止し、**打ち間違いとは別の文言**を出すことを固定する
            // (同じ文言だと、原因がネットワークなのに利用者が入力を疑い続ける)。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            // 脆弱-m-3: V-1 の実体は「保存していないのに Modified が false」だった。
            // ガード枝の SaveAs_DriveRoot_WarnsAndReopens_AndKeepsModified と同じ fixture を作る:
            // 本文に CRLF・要求 EOL を LF にして ConvertEols を非 fast-path にしておかないと、
            // 保存点が実際に壊れる条件が成立せず Modified の assert が空振りする。
            doc.Editor.Text = "a\r\nb\r\nc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty = 非既定状態から検証を始める
            Assert.True(doc.Editor.Modified);
            // NormalizeResult は PathNormalizeResult? なので `default` と書くと null =
            // 実装への委譲になり、この網が vacuous になる。必ず明示的に構築する。
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );
            host.Dialogs.SaveAs = new SaveAsResult(@"C:\Temp\a.txt", 65001, false, LineEnding.Lf);

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path); // 保存されていない
            // 文言の弁別: 到達不能側にだけ現れる語を見る
            Assert.Contains(host.Prompt.Log, e => e.Text.Contains("到達できません"));
            // 脆弱-m-1(b): 文言の秒数は NormalizeTimeout から補間される。literal を書き戻す
            // 変異(「30 秒」など)をここで kill する。
            Assert.Contains(host.Prompt.Log, e => e.Text.Contains("5 秒"));
            // 脆弱-m-3: 現状 continue は ConvertEols より手前なので保存点に触れない。
            // これは**将来の並べ替えに対する網**で、今この 1 行だけを kill する単一変異は
            // 作れない(WriteToPath 側の catch にもロールバックがあるため)。それでも置くのは、
            // 「未保存なのに Modified が落ちる」が V-1 の実害そのものだから。
            Assert.True(doc.Editor.Modified);
        });

    [Fact]
    public void SaveAs_NormalizeInvalid_ShowsInvalidPathMessage() =>
        Sta.Run(() =>
        {
            // 対照群。Invalid と TimedOut を同じ文言にする変異をここで kill する。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Invalid,
                string.Empty
            );
            host.Dialogs.SaveAs = new SaveAsResult(@"C:\Temp\a.txt", 65001, false, LineEnding.Crlf);

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path);
            Assert.Contains(host.Prompt.Log, e => e.Text.Contains("正しくありません"));
            Assert.DoesNotContain(host.Prompt.Log, e => e.Text.Contains("到達できません"));
        });

    /// <summary>
    /// 脆弱-I-1 の網。<c>norm.Status != PathNormalizeStatus.Ok</c> が<b>単独で</b>保存を止めることを
    /// pin する(この第 1 項は本テストを足すまで<b>完全に無網</b>だった)。
    /// <para>
    /// <b>機構</b>: 上の 2 本は fixture の <c>Full</c> が空なので <c>GetDirectoryName("")</c> が
    /// null を返し、<c>||</c> の第 2 項(V-1 のディレクトリーガード)側でも同じ枝に入る。
    /// 文言 <c>switch</c> は独立に <c>norm.Status</c> を見るのでメッセージの assert も通る。
    /// 結果、第 1 項を <c>== PathNormalizeStatus.Invalid</c> へ変異させても(= TimedOut が素通り)
    /// 全緑で生存した。ここでは <c>Full</c> に<b>親フォルダーが実在する非空パス</b>を載せ、
    /// 第 2 項が真にならない状況を作る。第 1 項が壊れると SaveAs が実際に成功し
    /// <c>Assert.False</c> が落ちる。
    /// </para>
    /// <para>
    /// <b>なぜ現状の実装で実害が出ないのに網を張るか</b>: <c>RunNormalizeProbe</c> は失敗時に必ず
    /// <c>Full = string.Empty</c> を返すので、今は二重防御の内側が効いている
    /// (内側の契約は <c>RunNormalizeProbe_WorkExceedsTimeout_FailsSafeToTimedOut</c> が pin 済み)。
    /// しかし <see cref="IReachabilityProbe"/> は interface で任意の実装を差せるうえ、
    /// 「文言に打った値を出したいので失敗時も生パスを載せる」というごく自然な変更で内側は消える。
    /// そのとき第 1 項が唯一の門番になり、壊れていれば<b>正規化が一度も成功していないパスへ
    /// 実際に書き込む</b>。二重防御は外側にも網を張る。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(PathNormalizeStatus.TimedOut)]
    [InlineData(PathNormalizeStatus.Invalid)]
    public void SaveAs_NormalizeNotOk_WithNonEmptyFull_DoesNotSave(PathNormalizeStatus status) =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string decoy = tmp.File("decoy.txt"); // 親フォルダーが実在 = V-1 ガードは発火しない
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.NormalizeResult = new PathNormalizeResult(status, decoy);
            host.Dialogs.SaveAs = new SaveAsResult(@"C:\Temp\a.txt", 65001, false, LineEnding.Crlf);

            Assert.False(host.File.SaveAs());

            Assert.Null(doc.State.Path);
            Assert.False(File2.Exists(decoy)); // 正規化が成功していないパスへ書き込まない
        });

    [Fact]
    public void SaveAs_PassesFiveSecondTimeoutToNormalizeProbe() =>
        Sta.Run(() =>
        {
            // 5 秒契約の pin(既存 2 本の LastTimeout と同じ思想)。
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File("a.txt"),
                65001,
                false,
                LineEnding.Crlf
            );

            Assert.True(host.File.SaveAs());

            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.NormalizeLastTimeout);
        });

    /// <summary>
    /// 「seam を経由していること」ではなく「<b>FileController が直接
    /// <c>Path.GetFullPath</c> を呼んでいないこと</b>」の網。
    /// <para>
    /// 回数だけを見ると弱い(seam を呼んでから答を捨てて <c>GetFullPath</c> を呼ぶ変異が生き残る)
    /// ので、<b>seam の答を実入力と食い違わせる</b>: ダイアログで打たれたのは
    /// <c>typed.txt</c> だが seam は <c>redirected.txt</c> を返す。
    /// 下流(V-1 ガード・重複判定・保存先プローブ・実書込・<c>State.Path</c>・最近のファイル)が
    /// 本当に seam の出力を使っているなら、ファイルは <c>redirected.txt</c> にできる。
    /// <c>GetFullPath</c> を直接呼ぶ実装なら <c>typed.txt</c> にできて落ちる。
    /// </para>
    /// <para>
    /// あわせて <c>NormalizeCallCount == 1</c> を pin する。「1 操作あたり正規化<b>多くとも</b>
    /// 1 本」は設計書 §3 の不変条件で、境界付きにした意味(5 秒 × N にしない)がここに掛かっている。
    /// 上限であって下限ではない: Ctrl+S は 0 本
    /// (<see cref="SaveDocument_ExistingPath_DoesNotNormalizeAtAll"/>)。ここが 1 本なのは
    /// SaveAs が生入力を受け取る入口だから。
    /// </para>
    /// </summary>
    [Fact]
    public void SaveAs_UsesNormalizedPathFromProbe_NotDirectGetFullPath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string typed = tmp.File("typed.txt");
            string redirected = tmp.File("redirected.txt");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Ok,
                redirected
            );
            host.Dialogs.SaveAs = new SaveAsResult(typed, 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs());

            // 生入力を受け取る入口なので 1 本(5 秒 × N にしない)。不変条件は「多くとも 1 本」で、
            // Ctrl+S のように 0 本の操作もある。
            Assert.Equal(1, host.Probe.NormalizeCallCount);
            Assert.Equal(typed, host.Probe.NormalizeLastPath); // 生入力がそのまま seam へ届く
            Assert.Equal(redirected, doc.State.Path);
            Assert.True(File2.Exists(redirected));
            Assert.False(File2.Exists(typed));
        });

    // ===== Issue #48 Task 8: 「境界がある」ではなく「回数が減った」の網 =====
    //
    // ここまでのタスクの網は「無境界の正規化が境界付き seam を通ること」を固定してきた。
    // 以下の 3 本は**打つ回数そのもの**を固定する。S-15 の実害は 1 回 21 秒なので、
    // 回数は待ち時間に直接掛かる(復元経路の姉妹は
    // RestoreSession_NormalizesOncePerReopenedRecord)。
    //
    // 3 本は役割が分かれていて、どれも他の 2 本では代替できない:
    //   (a) DoesNotNormalizeAtAll        — Ctrl+S が seam を 0 回しか打たない(絶対値)
    //   (b) DoesNotScaleNormalizeCalls   — その 0 回がタブ数に依存しない(1+N の N 側)
    //   (c) PathEntryPoints_DoNotNormalizeOutsideTheSeam
    //                                    — seam を通さない直呼びが増えていない(構造)
    // (a)(b) は seam の呼び出し回数しか見ないので、`Path.GetFullPath` の直呼びが
    // 足された場合は**全緑のまま生存する**(結果の綴りは変わらないので挙動でも観測できない)。
    // そこだけを (c) が IL で塞ぐ。逆に (c) は回数を数えないので、seam を 2 回打つ変異は
    // (a)(b) にしか当たらない。
    //
    // 同じ「網の役割分担」は DocumentManager 側にもある: FindByPath の PathKey.ForNormalized を
    // GetFullPath 経由へ戻す変異(旧 PathKey.For 相当)は、その経路が seam を通らないので
    // (a)(b) では赤にならない。それを殺すのは DocumentManagerTests の
    // FindByPath_DoesNotTouchFileSystem(実測で確認済み)。

    /// <summary>
    /// Issue #48 の成果そのもの。Ctrl+S は不変条件(<c>State.Path</c> は正規化済み)により
    /// 境界付き正規化を<b>1 回も打たない</b>。この網が無いと、将来「念のため正規化しておく」
    /// という一見無害な追加で S-15 が戻る。
    /// <para>
    /// 差分(<c>before</c> == 後)だけでなく絶対値も pin する: 「開く 1 回 + Ctrl+S 0 回」で
    /// 合計 1 回。差分だけだと、開く側が 2 回打つ退行を起こしても差分は 0 のまま緑になる。
    /// </para>
    /// </summary>
    [Fact]
    public void SaveDocument_ExistingPath_DoesNotNormalizeAtAll() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "old");
            var doc = host.File.TryOpenOrActivate(path)!;
            doc.Editor.Text = "new";

            int before = host.Probe.NormalizeCallCount; // 開く時の 1 回を除く
            Assert.Equal(1, before); // 開くは 1 回きり(差分 0 が空振りにならないための足場)

            Assert.True(host.File.SaveDocument(doc));

            Assert.Equal(before, host.Probe.NormalizeCallCount);
            Assert.Equal("new", File2.ReadAllText(path)); // 保存自体は起きている
        });

    /// <summary>
    /// 1+N の N 側が消えたことの網。設計書 §5 のミューテーション項目 2 が指摘した穴
    /// —「seam の呼び出し回数」だけを見ていると <c>FindByPath</c> を <c>GetFullPath</c> 経由へ
    /// 戻す変異(旧 <c>PathKey.For</c> 相当)を kill できない(その経路は seam を通らない)—
    /// への対処として、<b>タブ数を変えても回数が変わらない</b>ことを固定する。
    /// <para>
    /// タブ数を実際に振る(1 と 5)のが load-bearing。固定の 5 タブで「差分 0」を見るだけだと
    /// 上の姉妹と同じことしか言っておらず、「スケールしない」という主張の証人にならない。
    /// </para>
    /// <para>
    /// <b>この網では kill できない変異</b>: <c>FindByPath</c> の <c>ForNormalized</c> を
    /// <c>GetFullPath</c> 経由へ戻す変異(旧 <c>PathKey.For</c> 相当)はここでは赤にならない
    /// (<c>Path.GetFullPath</c> を直接呼ぶので seam のカウンタが動かない)。承知のうえで置いている。
    /// 実際の kill 役は
    /// <c>DocumentManagerTests</c> の 3 本(実測 2026-08-24 — この変異で赤になるのはこの 3 本だけ):
    /// <c>FindByPath_DoesNotTouchFileSystem</c>(構造)・
    /// <c>FindByPath_DoesNotNormalizeSeparators_CallerMustNormalize</c> と
    /// <c>FindByPath_DoesNotNormalizeOpenTabPaths_CallerMustNormalize</c>(挙動)。
    /// Core の <c>PathKeyTests.ForNormalized_does_not_normalize_separators</c> は
    /// <c>PathKey</c> 自身の網なので、<c>FindByPath</c> の呼び分けを変えても<b>緑のまま</b>。
    /// </para>
    /// </summary>
    [Fact]
    public void SaveDocument_WithManyOpenTabs_DoesNotScaleNormalizeCalls() =>
        Sta.Run(() =>
        {
            Assert.Equal(0, SaveNormalizeDeltaWithOpenTabs(1));
            Assert.Equal(0, SaveNormalizeDeltaWithOpenTabs(5));
        });

    /// <summary>
    /// <paramref name="tabCount"/> 枚のタブを開いた状態で先頭タブを Ctrl+S し、
    /// 境界付き正規化の呼び出し回数が<b>いくつ増えたか</b>を返す。
    /// </summary>
    private static int SaveNormalizeDeltaWithOpenTabs(int tabCount)
    {
        using var host = new Host();
        using var tmp = new TempDir();
        for (int i = 0; i < tabCount; i++)
        {
            string p = tmp.File($"t{i}.txt");
            File2.WriteAllText(p, "x");
            Assert.NotNull(host.File.TryOpenOrActivate(p));
        }
        // 開いた枚数だけ打っている=タブ数に比例する呼出が「開く」側には確かに在る。
        // このあと Ctrl+S が同じ比例を持ち込まないことを見る。
        Assert.Equal(tabCount, host.Probe.NormalizeCallCount);

        var doc = host.Docs.FindByPath(tmp.File("t0.txt"))!;
        doc.Editor.Text = "new";

        int before = host.Probe.NormalizeCallCount;
        Assert.True(host.File.SaveDocument(doc));
        return host.Probe.NormalizeCallCount - before;
    }

    /// <summary>
    /// FileController のパス経路が <c>Path.GetFullPath</c> を<b>直接</b>呼んでいないことを
    /// IL で固定する。上の 2 本(回数の網)の死角を塞ぐ専用の網。
    /// <para>
    /// <b>なぜ挙動テストで代替できないか</b>: 走査対象が扱うパスはどれも境界付き seam を
    /// 通った後の正規化済みパスなので、そこへ <c>GetFullPath</c> を掛け直しても
    /// <b>結果の綴りは 1 文字も変わらない</b>(<c>Path.GetFullPath</c> は絶対パスに対して冪等)。
    /// 保存先も内容も同じ、seam のカウンタも動かない。観測できる差は「不達共有で 21 秒
    /// 止まる」ことだけで、それはテストでは再現しない(実 FS / ネットワークが要る)。
    /// = S-15 の再導入は<b>構造でしか検出できない</b>。
    /// </para>
    /// <para>
    /// <b>走査対象が 5 つある理由</b>。それぞれ別の穴を塞いでいて、どれも他で代替できない:
    /// <list type="number">
    /// <item><c>SaveDocument</c> — Ctrl+S の入口。</item>
    /// <item><c>WriteToPath</c> — Ctrl+S が素通しで委譲する先。<c>SaveDocument</c> だけ見ると、
    /// こちらに足された 1 行を見逃す。</item>
    /// <item><c>TryOpenOrActivateCore</c> — <b>生パスを実際に受け取る入口その 1</b>
    /// (最終レビュー Q-I-1)。</item>
    /// <item><c>SaveAsDocument</c> — 同じく生パスを受け取る入口その 2。</item>
    /// <item><c>NormalizeSavePath</c> — (4) が 1 段越しに呼ぶ薄いヘルパ。この走査は
    /// <b>直接の</b>呼出しか見ないので、ここを対象に入れないと (4) の網を 1 ホップで迂回できる。</item>
    /// </list>
    /// (3)(4) を足したのは最終ブランチレビュー Q-I-1 の指摘による。それまで網は下流((1)(2)と
    /// <c>FindByPath</c> / <c>RecentFilesList.Add</c>)しか見ておらず、<c>string full = norm.Full;</c>
    /// を <c>Path.GetFullPath(norm.Full)</c> へ書き換える変異—<b>S-15 の再導入とバイト単位で同じ形</b>—が
    /// Core / App 全緑のまま生存していた(実測)。既に絶対なパスでも <c>GetLongPathName</c> は走る
    /// (<c>GetFullPath(@"C:\PROGRA~1\a.txt")</c> → <c>C:\Program Files\a.txt</c>)ので、
    /// この 1 行が不達共有上の <c>~</c> パスに対して 21 秒の凍結を戻す。
    /// </para>
    /// <para>
    /// <c>Path</c> のメンバーを丸ごと禁じるのではなく <c>GetFullPath</c> だけを見るのが要点:
    /// (3)(4) は <c>Path.GetDirectoryName</c> を<b>正当に</b>呼ぶ(V-1 の門番)。
    /// <c>PathKey.ForNormalized</c> も許す(ファイルシステム非接触=S-15 の対象外)。
    /// なお <c>PathKey.For</c> を見る assert は不要になった: 最終レビュー Q-I-2 でメソッドごと
    /// 削除したので、呼び直しは実行時の走査ではなく<b>コンパイルエラー</b>で止まる。
    /// </para>
    /// </summary>
    [Fact]
    public void PathEntryPoints_DoNotNormalizeOutsideTheSeam()
    {
        var save = FileControllerMethod(
            nameof(FileController.SaveDocument),
            BindingFlags.Public | BindingFlags.Instance
        );
        var write = FileControllerMethod("WriteToPath");
        var open = FileControllerMethod("TryOpenOrActivateCore");
        var saveAs = FileControllerMethod("SaveAsDocument");
        var normalizeSavePath = FileControllerMethod("NormalizeSavePath");

        var saveCallees = IlCallees.Of(save);
        var writeCallees = IlCallees.Of(write);
        var openCallees = IlCallees.Of(open);
        var saveAsCallees = IlCallees.Of(saveAs);
        var normalizeCallees = IlCallees.Of(normalizeSavePath);

        // 陽性対照: 走査が実際に呼出を拾えている(拾えないなら以下の assert は無意味)。
        static bool IsNormalizeSeam(MethodBase m) =>
            m.DeclaringType == typeof(IReachabilityProbe)
            && m.Name == nameof(IReachabilityProbe.NormalizePathWithTimeout);

        Assert.Contains(
            saveCallees,
            m =>
                m.DeclaringType == typeof(DocumentManager)
                && m.Name == nameof(DocumentManager.FindByPath)
        );
        Assert.Contains(
            writeCallees,
            m =>
                m.DeclaringType == typeof(TextFileService) && m.Name == nameof(TextFileService.Save)
        );
        Assert.Contains(openCallees, IsNormalizeSeam);
        Assert.Contains(normalizeCallees, IsNormalizeSeam);
        // SaveAs は seam を直接ではなくヘルパ経由で呼ぶ。そのヘルパ自身が (5) で走査される。
        Assert.Contains(
            saveAsCallees,
            m => m.DeclaringType == typeof(FileController) && m.Name == "NormalizeSavePath"
        );

        foreach (
            var callees in new[]
            {
                saveCallees,
                writeCallees,
                openCallees,
                saveAsCallees,
                normalizeCallees,
            }
        )
        {
            Assert.DoesNotContain(
                callees,
                m =>
                    m.DeclaringType == typeof(System.IO.Path)
                    && m.Name == nameof(System.IO.Path.GetFullPath)
            );
        }
    }

    /// <summary>
    /// <see cref="FileController"/> のメソッドを名前で引く。既定は private インスタンスメソッド。
    /// 改名で <c>GetMethod</c> が null を返し、走査ゼロ件が「呼んでいない」と読める形になるのを防ぐ。
    /// </summary>
    private static MethodInfo FileControllerMethod(
        string name,
        BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance
    )
    {
        var method = typeof(FileController).GetMethod(name, flags);
        Assert.NotNull(method);
        return method;
    }

    // ===== A-7 (b): 他タブ重複の検知 =====

    /// <summary>
    /// **順序の契約も pin する**(設計書 §4.2): 重複タブ判定は保存先プローブ+上書き確認より
    /// **前**。拒否する相手の到達性を調べる意味はなく、遠隔共有で無駄な 5 秒を待たせない。
    /// fixture の <c>occupied</c> は実在するので、プローブ+確認を重複判定の上へ持ち上げる変異では
    /// 先に「上書きの確認」が出る = <c>DoesNotContain(OkCancel)</c> が落ちる
    /// (ローカルパスなので <c>SaveTargetCallCount</c> は動かず、Ctrl+S 側の双子
    /// <see cref="Save_PathAlsoOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe"/> のような
    /// プローブ回数では観測できない)。
    /// </summary>
    [Fact]
    public void SaveAs_PathOpenInAnotherTab_ShowsErrorAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string occupied = tmp.File("occupied.txt");
            File2.WriteAllText(occupied, "original");
            Assert.NotNull(host.File.TryOpenOrActivate(occupied)); // タブ A

            var doc = host.Docs.CreateNew(); // タブ B(無題)
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(occupied, 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブで開いています")
            );
            // 順序の pin(上の doc を参照): 重複タブは確認より前に弾く
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
            Assert.Equal("original", File2.ReadAllText(occupied)); // 上書きされていない
            Assert.Null(doc.State.Path);
        });

    /// <summary>
    /// 自分自身のパスへの上書き保存は正当な操作。
    /// **非既定状態から始めるのが要点**: 無題タブ(State.Path == null)から始めると
    /// FindByPath は常に null を返し、「null が返った」と「自分が返った」を区別できない
    /// =自タブ除外(!ReferenceEquals)を落とす変異が生存する。
    /// パス確定済みの doc + 別パスの他タブ、という配置にする。
    /// </summary>
    [Fact]
    public void SaveAs_OwnPath_IsNotTreatedAsDuplicate() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string other = tmp.File("other.txt");
            File2.WriteAllText(other, "other");
            Assert.NotNull(host.File.TryOpenOrActivate(other)); // 別パスの他タブを在席させる

            string mine = tmp.File("mine.txt");
            File2.WriteAllText(mine, "old");
            var doc = host.File.TryOpenOrActivate(mine); // パス確定済みの自タブ
            Assert.NotNull(doc);
            doc!.Editor.Text = "new";
            host.Dialogs.SaveAs = new SaveAsResult(mine, 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs());

            Assert.DoesNotContain(host.Prompt.Log, e => e.Text.Contains("別のタブで開いています"));
            // A-7 (a) 追加後はこのテストが新しい上書き確認を通る(mine は実在するため)。
            // FakePrompt.OkCancelResult 既定 true で素通りするので、通ること自体を明示して
            // 「気付かないうちに別の経路を測っている」状態にしない。
            // 自分自身のパスへの SaveAs で確認が出るのは意図どおり: 従来も「参照」経由なら
            // SaveFileDialog の OverwritePrompt が同じ確認を出していた(自分が開いている
            // ファイルを除外する仕組みは無い)= 非対称の解消であって新しい負担ではない。
            Assert.Contains(host.Prompt.Log, e => e.Caption == "上書きの確認");
            Assert.Contains("new", File2.ReadAllText(mine));
        });

    /// <summary>
    /// 照合は <c>PathKey</c>(GetFullPath + ToLowerInvariant)であって文字列等値ではない。
    /// <c>LoadInto</c> は渡された生パスをそのまま <c>State.Path</c> に入れるので、別経路で開いた
    /// タブと SaveAs のダイアログ入力が同じファイルの**違う綴り**を持ちうる
    /// (=文字列等値で照合すると重複を見逃して A-7 (b) が再発する)。
    /// 綴り差は Windows で実在する大文字小文字違いを使い、
    /// (a) 2 つの綴りが Ordinal で別物・(b) それでも同じファイルを指す(ボリュームが大小無視)
    /// の 2 点を fixture 側で実測して固定する。Windows 10 以降のディレクトリ単位
    /// case sensitivity が有効な環境では (b) が落ちる = 「たまたま緑」ではなく前提の破れとして見える。
    /// </summary>
    [Fact]
    public void SaveAs_PathOpenInAnotherTab_DifferentCaseSpelling_IsStillDetected() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string lower = tmp.File("occupied.txt");
            string upper = tmp.File("OCCUPIED.TXT");
            File2.WriteAllText(lower, "original");

            Assert.NotEqual(lower, upper, StringComparer.Ordinal); // (a) 綴りは別物
            Assert.True(File2.Exists(upper)); // (b) それでも同じファイル

            var tabA = host.File.TryOpenOrActivate(lower); // タブ A は小文字綴り
            Assert.NotNull(tabA);
            Assert.Equal(lower, tabA!.State.Path); // 生パスがそのまま入る

            var doc = host.Docs.CreateNew(); // タブ B(無題)
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(upper, 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            // 再表示の初期値 = 正規化済み full。大文字綴りのまま = タブ A の State.Path とは
            // 別文字列であることを実測で固定する(文字列等値では照合できない証拠)。
            string full = host.Dialogs.SaveAsRequests[1].Path!;
            Assert.Equal(upper, full);
            Assert.NotEqual(tabA.State.Path, full, StringComparer.Ordinal);

            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブで開いています")
            );
            Assert.Equal("original", File2.ReadAllText(lower)); // 上書きされていない
            Assert.Null(doc.State.Path);
        });

    /// <summary>
    /// ガードの**位置**(保存先プローブより前)を SaveAs 側でも固定する。Ctrl+S 側の双子
    /// <see cref="Save_PathAlsoOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe"/> は
    /// <c>SaveTargetCallCount == 0</c> を持っていたが、SaveAs 側は
    /// <see cref="SaveAs_PathOpenInAnotherTab_ShowsErrorAndReopens"/> がローカルパス fixture で
    /// <c>DoesNotContain(OkCancel)</c> しか見ていないため、**ガードをプローブの下・確認の上へ
    /// 移す変異が全緑で生存していた**(最終品質パス m-3)。害は無駄な 5 秒プローブであって
    /// データ喪失ではないが、src コメントが「遠隔共有で無駄な 5 秒を待たせない」と主張している。
    /// UNC でないと観測できない理由はローカル版 doc と同じ(<c>IsRemote</c> が偽だとプローブ自体
    /// 呼ばれない)。プローブ結果は「到達可能・未存在」にしておく: 呼ばれてしまった場合に
    /// 「到達不能で短絡した」と紛れないようにするため。
    /// **assert の順序が load-bearing**: ガードを下げる変異では粗い assert
    /// (戻り値・再表示回数・エラー文言)は全部通るので、位置の契約を最初に落とす。
    /// </summary>
    [Fact]
    public void SaveAs_PathOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            const string unc = @"\\nonexistent-host-42\share\x.txt";

            // UNC は実ファイルを用意できないので在席タブは State.Path 直代入で作る
            // (Task 6 のガードが SaveAs 経由での重複生成を塞いでいるため。先例 =
            // Save_PathAlsoOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe)。
            var occupant = host.Docs.CreateNew();
            occupant.Editor.Text = "occupant-content";
            occupant.State.Path = unc;

            var doc = host.Docs.CreateNew(); // 無題タブ(こちらがアクティブ)
            doc.Editor.Text = "abc";
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: true,
                FileExists: false
            );
            host.Dialogs.SaveAsQueue.Enqueue(new SaveAsResult(unc, 65001, false, LineEnding.Crlf));

            bool saved = host.File.SaveAs(); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(0, host.Probe.SaveTargetCallCount); // 位置の契約(最初に落とす)
            Assert.False(saved);
            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブで開いています")
            );
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
            Assert.Null(doc.State.Path);
        });

    // ===== A-7 (b) 残余(Task 6b): 既にある重複状態での Ctrl+S =====

    /// <summary>
    /// Task 6(SaveAs 側)のガードは重複タブが**生まれる**経路しか塞がない。**既にある**状態
    /// (復元 extras の dedup がバックアップ Id のみで照合するため現行フリートでも発生する)では
    /// <c>SaveDocument</c> が <c>FindByPath</c> を参照せず、Ctrl+S が無警告でもう一方のタブの
    /// 内容をディスクから消していた。
    /// fixture は SaveAs を通さず <c>State.Path</c> を直接代入して作る
    /// (Task 6 のガードが SaveAs 経由での重複生成を塞いでいるため)。
    /// 同手法の先例 = <see cref="Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified"/>。
    /// </summary>
    [Fact]
    public void Save_PathAlsoOpenInAnotherTab_IsBlocked_AndFileKeepsOtherTabContent() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string shared = tmp.File("shared.txt");
            File2.WriteAllText(shared, "tabA-content");
            var tabA = host.File.TryOpenOrActivate(shared); // 先に生まれたタブ
            Assert.NotNull(tabA);

            var tabB = host.Docs.CreateNew(); // 後から生まれた重複タブ(復元 dedup 漏れ相当)
            tabB.Editor.Text = "tabB-content";
            tabB.State.Path = shared; // SaveAs を経由せず衝突状態を作る
            tabB.Editor.ReplaceCharRange(0, 0, "x"); // dirty=保存点が打たれていないことを観測可能にする
            Assert.True(tabB.Editor.Modified);
            // 生成順で先勝ち = FindByPath は tabA を返す(tabB が「新しい方」であることを固定する)。
            Assert.Same(tabA, host.Docs.FindByPath(shared));

            Assert.False(host.File.Save()); // tabB がアクティブ = Ctrl+S 経路

            // 本体: 相手タブの内容がディスク上に生き残る(戻り値だけでなく実ファイルで見る)。
            Assert.Equal("tabA-content", File2.ReadAllText(shared));
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブが同じファイルを開いています")
            );
            // 文言が Task 6(SaveAs)のものと別であることを固定する。Ctrl+S では呼び出し元の
            // タブ自身もそのパスを持っているので「そのタブで保存してください」は成立しない
            // (コピペで Task 6 の文言を持ち込むと SR ユーザーが実行不能な指示を聞かされる)。
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.Contains("そのタブで保存してください")
            );
            Assert.Equal(0, host.Dialogs.PickSaveAsCount); // Path 確定済み=SaveAs へは落ちない
            Assert.True(tabB.Editor.Modified); // 保存点を打っていない=未保存であることが SR に伝わる
        });

    /// <summary>
    /// 対照群(過剰検知の防止): 同じ衝突状態でも**先に生まれたタブ**の Ctrl+S は通る。
    /// これが無いと「常に false を返す」変異と「<c>!ReferenceEquals</c> を落とす」変異が生き残る
    /// (衝突相手が在席したままなので <c>FindByPath</c> は必ず非 null を返す = 自タブ除外だけが効いている)。
    /// </summary>
    [Fact]
    public void Save_OlderTabOfCollidingPair_StillWrites() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string shared = tmp.File("shared.txt");
            File2.WriteAllText(shared, "original");
            var tabA = host.File.TryOpenOrActivate(shared);
            Assert.NotNull(tabA);

            var tabB = host.Docs.CreateNew(); // 衝突相手を在席させる(検知対象が在ることが前提)
            tabB.Editor.Text = "tabB-content";
            tabB.State.Path = shared;
            Assert.Same(tabA, host.Docs.FindByPath(shared)); // 検知対象は tabA 自身

            host.Docs.Activate(tabA!);
            tabA!.Editor.ReplaceCharRange(
                0,
                tabA.Editor.CurrentBuffer.Current.CharLength,
                "tabA-saved"
            );
            Assert.True(tabA.Editor.Modified);

            Assert.True(host.File.Save());

            Assert.Equal("tabA-saved", File2.ReadAllText(shared));
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.Contains("別のタブが同じファイルを開いています")
            );
            Assert.False(tabA.Editor.Modified); // SetSavePoint 済み=書き込み経路を通っている
        });

    /// <summary>
    /// ガードの**位置**(WriteToPath 冒頭の到達性プローブより前)を固定する。重複タブは保存させない
    /// ので到達性を調べる意味がなく、遠隔共有で無駄な 5 秒を待たせてはいけない。
    /// <c>TryInspectSaveTarget</c> は <c>RemotePathDetector.IsRemote</c> が真のときしかプローブを
    /// 呼ばないため、**ローカルパスの fixture ではこの契約を観測できない**(ガードを
    /// プローブ直後へ移しても他の網は全緑のまま=src コメントが存在しない安全網を主張する状態に
    /// なる。Task 2 の F-1 と同型)。UNC 版が必要な理由はここ。
    /// fixture の先例 = <see cref="Save_ShowsErrorPrompt_WhenRemoteUncUnreachable"/>。
    /// </summary>
    [Fact]
    public void Save_PathAlsoOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            const string unc = @"\\nonexistent-host-42\share\x.txt";

            // UNC は実ファイルを用意できないので 2 タブとも State.Path 直代入で作る
            // (Task 6 のガードが SaveAs 経由での重複生成を塞いでいる事情はローカル版と同じ)。
            var tabA = host.Docs.CreateNew(); // 先に生まれたタブ
            tabA.Editor.Text = "tabA-content";
            tabA.State.Path = unc;

            var tabB = host.Docs.CreateNew(); // 後から生まれた重複タブ
            tabB.Editor.Text = "tabB-content";
            tabB.State.Path = unc;
            tabB.Editor.ReplaceCharRange(0, 0, "x"); // dirty=保存点が打たれていないことを観測可能にする
            Assert.True(tabB.Editor.Modified);
            Assert.Same(tabA, host.Docs.FindByPath(unc)); // 生成順で先勝ち=tabB が止められる側

            // 到達可能・未存在(既定)のまま残す: プローブが呼ばれてしまった場合に
            // 「到達不能で短絡した」と紛れないようにする(呼ばれた事実だけを見る)。
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: true,
                FileExists: false
            );

            bool saved = host.File.Save(); // tabB がアクティブ = Ctrl+S 経路

            // **assert の順序が load-bearing**: ガードを外す/プローブ後へ移す変異では
            // Assert.False(saved) も落ちるので、先に書くとプローブの網が隠れる。
            // 位置の契約(プローブより前)を最初に落とす。
            Assert.Equal(0, host.Probe.SaveTargetCallCount);
            Assert.False(saved);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("別のタブが同じファイルを開いています")
            );
            // 短絡であって到達性エラーでも書込失敗でもない(プローブ非実行の裏取り)。
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
            Assert.True(tabB.Editor.Modified); // 保存点を打っていない
        });

    // ===== A-7 (a): 上書き確認 =====

    /// <summary>
    /// SR ユーザーの主経路(ダイアログのテキストボックス直入力)でも上書き確認が出る。
    /// fixture は**実ファイルをディスクに置く**だけで作る(Fake の申告ではなくローカル枝の
    /// <c>File.Exists</c> が読む実体を入力にする)。<c>TryOpenOrActivate</c> は呼ばない:
    /// 重複タブ検知が上書き確認より**前**にあるので、別タブで開いたパスを使うと
    /// Task 6 のエラーに当たって本テストが別のものを pin する。
    /// </summary>
    [Fact]
    public void SaveAs_ExistingFile_AsksOverwriteConfirmation() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "original");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "new";
            host.Dialogs.SaveAs = new SaveAsResult(path, 65001, false, LineEnding.Crlf);

            Assert.True(host.File.SaveAs()); // FakePrompt.OkCancelResult 既定 true = 上書き承諾

            var confirm = Assert.Single(
                host.Prompt.Log,
                e => e.Kind == "OkCancel" && e.Caption == "上書きの確認"
            );
            // 読み上げ順の pin: 最大 200 文字のパスより**問いが先**に来る
            // (SR は本文を頭から読む。パスを先に置くと何を聞かれているかが最後まで分からない)。
            Assert.StartsWith(
                "同じ名前のファイルが既に存在します。上書きしますか?",
                confirm.Text,
                StringComparison.Ordinal
            );
            Assert.Contains(path, confirm.Text, StringComparison.Ordinal); // どこへ書くかも読める
            // S-12: 破壊的な確認は既定フォーカスをキャンセル側に置く。SaveAsDialog は
            // AcceptButton = OK なので「ファイル名を打つ → Enter」が主経路であり、
            // 既定が OK 側だと Enter 連打でこの確認ごと確定してしまう。
            Assert.Equal(("上書きの確認", true), Assert.Single(host.Prompt.OkCancelCalls));
            Assert.Contains("new", File2.ReadAllText(path));
        });

    /// <summary>
    /// A-7 (a) の**リモート枝**。上のテストはローカル枝(素の <c>File.Exists</c>)しか通らないため、
    /// <c>TryInspectSaveTarget</c> のリモート枝 <c>exists = probe.FileExists;</c> を
    /// <c>exists = false;</c> にする変異が**全緑で生存していた**(最終品質パス I-1)。
    /// = UNC / マップドネットワークドライブ上の既存ファイルを無確認で上書きする退行が
    /// 検出されない状態だった。本ブランチの目玉の半分が無網だったということ。
    /// 原因は fixture 側にある: スイート内で唯一 <c>FileExists: true</c> を置いていた
    /// <see cref="SaveAs_UnreachableUncPath_ShowsErrorAndReopens"/> は
    /// <c>Reachable: false</c> と対にした**意図的な毒値**なので、
    /// 「リモート + 到達可能 + 既存」の組み合わせがどこにも無かった。
    /// 辞退させるのは実書込へ進ませないため(共有は実在しないので書込は必ず失敗し、
    /// 何を pin しているのか分からなくなる)。
    /// </summary>
    [Fact]
    public void SaveAs_ExistingFileOnUncPath_AsksOverwriteConfirmation() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: true,
                FileExists: true
            );
            host.Prompt.OkCancelResult = false; // 辞退 → 再表示 → キュー枯渇で終了
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(@"\\127.0.0.1\no-such-share\a.txt", 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs());

            Assert.Equal(("上書きの確認", true), Assert.Single(host.Prompt.OkCancelCalls));
            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
        });

    /// <summary>
    /// 「いいえ」でファイルもドキュメント状態も一切変えずにダイアログへ戻る。
    /// **非既定のエンコード/BOM/改行を選ぶのが要点**(CLAUDE.md §4 の no-change 規範。
    /// 本ファイルでは 3 度目の同型指摘): 既定(65001 / BOM なし / CRLF)を選ぶと
    /// 「State を触っていない」が既定値と区別できず、**確認ブロックを
    /// <c>doc.State.Encoding = newEncoding</c> の下へ移す変異が全緑で生存する**。
    /// その位置に落ちると「State だけ新エンコードで Path は旧のまま」が残り、
    /// 後続の Ctrl+S が元ファイルを別エンコードでサイレント上書きする
    /// (FileController の同箇所のコメントが警告しているデータ破損そのもの)。
    /// </summary>
    [Fact]
    public void SaveAs_OverwriteDeclined_KeepsFileAndReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "original");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "new"; // ASCII=932 でも劣化警告は出ない
            host.Prompt.OkCancelResult = false; // 「いいえ」
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(path, 932, HasBom: true, LineEnding.Lf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Equal("original", File2.ReadAllText(path)); // 上書きされていない
            Assert.Null(doc.State.Path);
            // 選んだ 932 / BOM / LF はどれも State に触れていない(既定のまま)
            Assert.Equal(65001, doc.State.Encoding.CodePage);
            Assert.False(doc.State.HasBom);
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
        });

    /// <summary>
    /// 新規ファイルでは確認しない。FakePrompt.OkCancelResult の既定は true なので
    /// 「保存が成功した」だけでは確認の有無を区別できない(vacuous になる)。
    /// Log に OkCancel が**出ないこと**で固定する。
    /// </summary>
    [Fact]
    public void SaveAs_NewFile_DoesNotAskOverwrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File("fresh.txt"),
                65001,
                false,
                LineEnding.Crlf
            );

            Assert.True(host.File.SaveAs());

            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
        });

    /// <summary>
    /// 到達不能なリモート保存先はエラーにして再表示する(書込を試みない)。
    /// <c>FileExists</c> に **true** を置くのは意図的な毒値: 到達不能のとき FileExists は
    /// 無意味という <see cref="SaveTargetProbeResult"/> の契約(本物のプローブは
    /// <c>Reachable = fileExists || dirExists</c> なのでこの組は production では作れない)を、
    /// 「読んだら上書き確認が出てしまう」という観測可能な形に変える。
    /// false のままだと「戻り値を先に見る」短絡を外す変異が Log 上は無変化になり、
    /// DoesNotContain(OkCancel) が vacuous になる。
    /// </summary>
    [Fact]
    public void SaveAs_UnreachableUncPath_ShowsErrorAndReopens() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: false,
                FileExists: true
            );
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(@"\\127.0.0.1\no-such-share\a.txt", 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs());

            // **assert の順序が load-bearing**(先例 =
            // Save_PathAlsoOpenInAnotherTab_RemoteUnc_IsBlockedBeforeProbe): 短絡を外す変異では
            // 再表示回数も落ちるので、粗い方を先に書くと契約違反そのものの網が隠れる。
            // 到達不能のとき FileExists は無意味(SaveTargetProbeResult の契約)= 読んではいけない。
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith(
                        "ネットワークパスに到達できません",
                        StringComparison.Ordinal
                    )
            );
            // 書込は試みていない = WriteToPath の失敗エラーは出ない
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("保存できませんでした", StringComparison.Ordinal)
            );
        });

    // ===== Save 公開入口(active 経由 Ctrl+S) / ReadOnly 復元(WriteToPath finally) =====

    [Fact]
    public void Save_NoActive_ReturnsFalse() =>
        Sta.Run(() =>
        {
            // タブ 0 枚(Host 生成直後は docs.CreateNew を呼ばないため Active=null)。
            // Save() の `docs.Active is not null` ガードを `true` に変える NRE 変異を kill する
            // (ガードが外れれば SaveDocument(null) で NullReferenceException が伝播する)。
            using var host = new Host();
            Assert.Equal(0, host.Docs.Count);
            Assert.Null(host.Docs.Active);

            Assert.False(host.File.Save());
            Assert.Empty(host.Prompt.Log); // ダイアログにも一切進まない
        });

    [Fact]
    public void Save_ExistingPath_WritesAndClearsModified() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "orig"); // ASCII=UTF-8 として妥当(初回オープンで警告なし)
            var doc = host.File.TryOpenOrActivate(path)!;
            // 開いた直後は Modified=false=SetSavePoint 済み。編集して Save で再度 SetSavePoint されるかを観測する。
            doc.Editor.ReplaceCharRange(0, doc.Editor.CurrentBuffer.Current.CharLength, "changed");
            Assert.True(doc.Editor.Modified);

            // Ctrl+S 導線: FileController.Save() は docs.Active を SaveDocument に流す公開入口。
            // 既存 SaveDocument 直呼び系(ConfirmDiscardIfDirty_Yes_...)と異なり、Active 経由のエントリを固定する。
            Assert.True(host.File.Save());

            Assert.Equal("changed", File2.ReadAllText(path)); // ディスクへ書き出し=バッファと一致
            Assert.False(doc.Editor.Modified); // SetSavePoint 済み
        });

    [Fact]
    public void Save_ReadOnlyDocument_RestoresReadOnlyAfterSave() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            // 既存 Save 系テストの流儀(CreateNew + Text + State.Path)。既定 State=UTF-8/BOM なし/CRLF。
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.State.Path = path;
            doc.Editor.ReadOnly = true; // CSV モード相当(閲覧専用に落として保存する経路)

            Assert.True(host.File.Save());

            Assert.True(doc.Editor.ReadOnly); // WriteToPath の try/finally で復元される契約
            Assert.Equal("abc", File2.ReadAllText(path)); // ディスクは更新されている(=Save 経路が抜けている)
        });

    [Fact]
    public void Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "orig"); // ReadOnly 属性を付けるため一旦実在させる
            File2.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
            try
            {
                var doc = host.Docs.CreateNew();
                doc.Editor.Text = "changed";
                doc.State.Path = path;
                doc.Editor.ReadOnly = true; // CSV モード相当

                // 保存先ファイルの ReadOnly 属性で AtomicFile.Write の File.Replace が UnauthorizedAccessException
                // (WriteToPath の catch フィルタで false 返却+prompt.Error 通知)。
                // (inner finally は TextFileService.Save が例外を投げる前に完走・ReadOnly=true 復元済み)
                Assert.False(host.File.Save());
                Assert.True(doc.Editor.ReadOnly); // 失敗経路でも finally で復元される(=CSV 復帰不能を防止)
                Assert.Equal("orig", File2.ReadAllText(path)); // 原本は不変(AtomicFile の契約)
                Assert.Contains(
                    host.Prompt.Log,
                    e =>
                        e.Kind == "Error"
                        && e.Text.StartsWith(
                            "保存できませんでした",
                            System.StringComparison.Ordinal
                        )
                );
            }
            finally
            {
                // TempDir の再帰削除が ReadOnly 属性で失敗するのを避け、テスト成否に関わらず属性を戻す。
                File2.SetAttributes(path, System.IO.FileAttributes.Normal);
            }
        });

    // ===== 符号化劣化警告(CanEncodeBuffer 経由) =====

    /// <summary>
    /// 劣化警告に「キャンセル」と答えると、ファイルにも State にも一切触れない。
    /// Task 8(2026-08-23)以降、この <c>false</c> は「警告して中止した」結果ではない:
    /// 警告後は <c>continue</c> してダイアログを再表示し、2 回目に 1-shot の
    /// <see cref="Fakes.FakeFileDialogService.SaveAs"/> が払い出しを終えてキャンセル扱いに
    /// なった結果である。**再表示そのものの pin は
    /// <see cref="SaveAs_EncodingWarningDeclined_ReopensDialog"/>** が持つ。
    /// (先例 = <see cref="SaveAs_WhitespacePath_WarnsWithExactMessage_AndLeavesStateUnchanged"/>。
    /// テスト名の Cancel は**警告への答**であって SaveAs の中止ではない。名前が
    /// 「中止」を主張すると、<c>continue</c> を <c>return false</c> へ戻す改悪を名前が追認する)
    /// </summary>
    [Fact]
    public void SaveAs_LossyEncoding_CancelKeepsStateAndWritesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは😀"; // 😀 は Shift_JIS(932) で表せない
            string path = tmp.File("a.txt");
            host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
            host.Prompt.OkCancelResult = false; // 中止

            Assert.False(host.File.SaveAs());

            Assert.False(File2.Exists(path));
            Assert.Equal(65001, doc.State.Encoding.CodePage); // 警告は State 反映前=変化なし
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "OkCancel" && e.Caption == "文字コードの警告"
            );
            // S-12 と**対称**: 破壊的な確認は両方とも既定フォーカスをキャンセル側に置く。
            // Task 8 で警告のキャンセルが「入力を保ったまま再表示」になり、誤爆した Enter の
            // コストが消えたので、劣化警告だけ既定 OK にしておく根拠がなくなった
            // (コンボの初期値は seed 由来なので、ユーザーが文字コードに触れていなくても
            // 警告条件は成立しうる = 「直前に自分で選んだ」前提が成り立たない)。
            // defaultCancel を false へ戻す変異をここで殺す。
            Assert.Equal(("文字コードの警告", true), Assert.Single(host.Prompt.OkCancelCalls));
        });

    /// <summary>
    /// 文字コード劣化警告のキャンセルもダイアログへ戻す(選び直せる場所がそのダイアログだから)。
    /// 保存先は**新規ファイル**にして、上書き確認と <c>OkCancelResult</c> を取り合わないようにする
    /// (FakePrompt の応答は 1 つしかないので、既存ファイルにすると上書き確認も同時に「いいえ」になる)。
    /// 停止保証は <c>SaveAsQueue</c> の**枯渇=キャンセル**だけ: 警告が continue になった今、
    /// 「同じ入力 → 警告 → キャンセル → 再表示」は自力では終わらない
    /// (だから SaveAsQueue に「最後の値を繰り返す」モードを足してはいけない)。
    /// 選ぶ 932 は既定(65001)と別値なので末尾の no-change assert は空振りしない
    /// (CLAUDE.md §4。<c>doc.State.Encoding = newEncoding</c> を警告ブロックの上へ移す変異は
    /// この assert で落ちる)。
    /// </summary>
    [Fact]
    public void SaveAs_EncodingWarningDeclined_ReopensDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "絵文字 \U0001F600"; // SJIS(932)で表せない
            host.Prompt.OkCancelResult = false; // 警告に「キャンセル」
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(tmp.File("a.txt"), 932, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            Assert.Equal(2, host.Dialogs.PickSaveAsCount);
            Assert.Contains(host.Prompt.Log, e => e.Caption == "文字コードの警告");
            Assert.False(File2.Exists(tmp.File("a.txt")));
            Assert.Equal(65001, doc.State.Encoding.CodePage); // State は書き換わっていない
        });

    [Fact]
    public void SaveAs_LossyEncoding_OkProceedsAndWrites() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "こんにちは😀";
            string path = tmp.File("a.txt");
            host.Dialogs.SaveAs = new SaveAsResult(path, 932, HasBom: false, LineEnding.Crlf);
            host.Prompt.OkCancelResult = true; // 続行

            Assert.True(host.File.SaveAs());

            Assert.True(File2.Exists(path));
            Assert.Equal(932, doc.State.Encoding.CodePage);
        });

    [Fact]
    public void SaveAs_Utf8_SkipsLossyWarning() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "😀"; // astral でも UTF-8 は全表現可
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File("a.txt"),
                65001,
                HasBom: false,
                LineEnding.Crlf
            );

            Assert.True(host.File.SaveAs());

            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "OkCancel");
        });

    // ===== 開く系(TryOpenOrActivate は path を開く唯一の経路) =====

    [Fact]
    public void TryOpenOrActivate_NewFile_LoadsMetaContent_AndFiresOpenedFresh() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            // 本文は LF 改行: 既定(Crlf)と同値だと改行検出配線のアサートが空振りする(レビュー I-2)
            File2.WriteAllBytes(
                path,
                new byte[] { 0xEF, 0xBB, 0xBF }
                    .Concat(Encoding.UTF8.GetBytes("あい\nう"))
                    .ToArray()
            );

            var doc = host.File.TryOpenOrActivate(path);

            Assert.NotNull(doc);
            Assert.Equal(path, doc!.State.Path);
            Assert.Equal(65001, doc.State.Encoding.CodePage);
            Assert.True(doc.State.HasBom); // BOM 検出の配線(既定 false に対し非デフォルト)
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding); // 改行検出の配線(既定 Crlf に対し非デフォルト)
            Assert.Equal("あい\nう", doc.Editor.Text);
            Assert.False(doc.Editor.Modified); // SetSavePoint 済み
            Assert.Same(doc, Assert.Single(host.OpenedFresh)); // .csv 自動モード判定への通知
            Assert.Equal(path, host.Settings.RecentFiles[0]);
        });

    [Fact]
    public void TryOpenOrActivate_AlreadyOpen_ActivatesExistingTab_WithoutReload() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc");
            var first = host.File.TryOpenOrActivate(path);
            _ = host.Docs.CreateNew(); // 別タブをアクティブにしてから再オープン

            var second = host.File.TryOpenOrActivate(path);

            Assert.Same(first, second); // 既存タブ再利用(二重編集の上書き事故防止)
            Assert.Same(first, host.Docs.Active); // アクティブ化
            Assert.Equal(2, host.Docs.Count); // タブは増えない
            Assert.Single(host.OpenedFresh); // 再ロードなし=openedFresh は初回のみ
        });

    [Fact]
    public void TryOpenOrActivate_LoadFailure_DiscardsScratchTab_AndRestoresPrevious() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            // タブ 3 枚構成にして prev を先頭以外に置く: TabControl は選択中タブの除去後に
            // 先頭(index 0)を自動選択するため、prev が先頭だと自動選択と明示復帰(Activate)を
            // 判別できない(レビュー I-1・ミューテーションで実証)。prev=2 枚目なら自動選択(先頭)と区別できる
            _ = host.Docs.CreateNew(); // 1 枚目(自動選択の着地先)
            var prev = host.Docs.CreateNew(); // 2 枚目(作成時点でアクティブ=直前のアクティブ)

            // Task 4 と同じ方式: 実在し得る絶対パス直書きを避け、一時フォルダ配下の
            // 存在しないサブフォルダを使う(レビュー申し送り)。
            var doc = host.File.TryOpenOrActivate(tmp.File(@"no-such-dir\no-such-file.txt"));

            Assert.Null(doc);
            Assert.Equal(2, host.Docs.Count); // 作りかけタブは破棄
            // 作りかけ(末尾)除去後の TabControl 自動選択は先頭=明示復帰がないと落ちる
            Assert.Same(prev, host.Docs.Active); // 直前のアクティブへ復帰
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("開けませんでした", System.StringComparison.Ordinal)
            );
        });

    [Fact]
    public void TryOpenOrActivate_SuppressAutoCsv_DoesNotFireOpenedFresh() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.csv");
            File2.WriteAllText(path, "a,b");

            var doc = host.File.TryOpenOrActivate(path, suppressAutoCsv: true); // grep ジャンプ経路

            Assert.NotNull(doc);
            Assert.Empty(host.OpenedFresh); // 選択+エディタフォーカスを機能させるため自動 CSV を抑止
        });

    // ===== 境界付き正規化(Issue #48 / S-15): 開く入口 =====

    [Fact]
    public void TryOpenOrActivate_NormalizeTimesOut_ReturnsNull_AndLeavesNoTab() =>
        Sta.Run(() =>
        {
            // 作りかけタブを残さないこと(残すと次の RestoreSession が initialEmpty を
            // 閉じられない等の二次汚染につながる = 既存 Task 5 review I-1 の論点)。
            using var host = new Host();
            int before = host.Docs.Count;
            // NormalizeResult は PathNormalizeResult? なので `default` と書くと null =
            // 実装への委譲になり、この網が vacuous になる。必ず明示的に構築する。
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );

            Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"));

            // 脆弱-I-3 と同じ将来ガード: 現行では単独の変異を殺さない(弾いた後にタブを
            // 残す形へ壊れたときのための網)。kill を担うのは下の文言 assert。
            Assert.Equal(before, host.Docs.Count);
            // 文言の弁別: 到達不能側にだけ現れる語を見る(保存側と同じ思想)。
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("到達できません")
            );
            // 秒数は NormalizeTimeout から補間される。**違う数値の** literal を書き戻す変異
            // (「30 秒」など)をここで kill する(保存側の双子と同じ例示。同じ数値の literal
            // へ戻す変異は殺せない=補間であること自体は測れない)。
            Assert.Contains(host.Prompt.Log, e => e.Text.Contains("5 秒"));
            Assert.DoesNotContain(host.Prompt.Log, e => e.Text.Contains("正しくありません"));
        });

    [Fact]
    public void TryOpenOrActivate_NormalizeInvalid_ShowsInvalidPathMessage() =>
        Sta.Run(() =>
        {
            // 対照群。Invalid と TimedOut を同じ文言にする変異をここで kill する。
            using var host = new Host();
            int before = host.Docs.Count;
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Invalid,
                string.Empty
            );

            Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"));

            Assert.Equal(before, host.Docs.Count);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("正しくありません")
            );
            Assert.DoesNotContain(host.Prompt.Log, e => e.Text.Contains("到達できません"));
        });

    [Fact]
    public void TryOpenOrActivate_StoresNormalizedAbsolutePath() =>
        Sta.Run(() =>
        {
            // 不変条件(設計書 §3.1)の本体。区切りが混ざった入力でも State.Path は
            // 正規化済み絶対パスになる。Fake の既定は実実装へ委譲するので、
            // ここは本番の GetFullPath の答えを見ている。
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            System.IO.File.WriteAllText(path, "x");

            var doc = host.File.TryOpenOrActivate(path.Replace('\\', '/'))!;

            Assert.Equal(path, doc.State.Path);
        });

    /// <summary>
    /// <c>norm.Status != PathNormalizeStatus.Ok</c> が<b>単独で</b>開くのを止めることを pin する。
    /// <para>
    /// <b>機構</b>: 上の 2 本は fixture の <c>Full</c> が空なので、第 1 項を
    /// <c>== PathNormalizeStatus.Invalid</c> へ変異させても(= TimedOut が素通り)
    /// 第 2 項(親フォルダーのガード)が <c>GetDirectoryName("")</c> = null で同じ枝へ倒れ、
    /// 文言 <c>switch</c> は独立に <c>norm.Status</c> を見るのでメッセージの assert まで通る
    /// = <b>全緑で生存する</b>(Task 3 で実際に生存した形)。ここでは <c>Full</c> に
    /// <b>実在するファイル</b>を載せて第 2 項が真にならない状況を作る。第 1 項が壊れると
    /// その decoy が実際に開けてしまい <c>Assert.Null</c> が落ちる。
    /// </para>
    /// <para>
    /// <b>なぜ現状の実装で実害が出ないのに網を張るか</b>: <c>RunNormalizeProbe</c> は失敗時に必ず
    /// <c>Full = string.Empty</c> を返すので、今は内側の二重防御が効いている。しかし
    /// <see cref="IReachabilityProbe"/> は interface で任意の実装を差せるうえ、「文言に打った値を
    /// 出したいので失敗時も生パスを載せる」というごく自然な変更で内側は消える。そのとき
    /// 第 1 項が唯一の門番になり、壊れていれば<b>正規化が一度も成功していないパスを開く</b>。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(PathNormalizeStatus.TimedOut)]
    [InlineData(PathNormalizeStatus.Invalid)]
    public void TryOpenOrActivate_NormalizeNotOk_WithNonEmptyFull_DoesNotOpen(
        PathNormalizeStatus status
    ) =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string decoy = tmp.File("decoy.txt"); // 実在=親フォルダーのガードは発火しない
            File2.WriteAllText(decoy, "decoy");
            int before = host.Docs.Count;
            host.Probe.NormalizeResult = new PathNormalizeResult(status, decoy);

            // 入力側は「実在しない一時パス」にする(第 1 項を壊す変異で decoy が開くことを
            // 見たいので、入力そのものが開けてしまう環境依存を排除する)。
            Assert.Null(host.File.TryOpenOrActivate(tmp.File("typed.txt")));

            Assert.Equal(before, host.Docs.Count);
            Assert.DoesNotContain(host.Docs.Documents, d => d.State.Path == decoy);
            Assert.Empty(host.Settings.RecentFiles); // 開いていない=履歴も汚さない
        });

    /// <summary>
    /// 「seam を経由していること」ではなく「<b>FileController が直接 <c>Path.GetFullPath</c> を
    /// 呼んでいないこと</b>」の網(保存側 <see cref="SaveAs_UsesNormalizedPathFromProbe_NotDirectGetFullPath"/>
    /// と同じ発想)。<b>seam の答を実入力と食い違わせる</b>: 打たれたのは実在しない
    /// <c>typed.txt</c> だが seam は実在する <c>redirected.txt</c> を返す。下流
    /// (<c>LoadInto</c> / <c>FindByPath</c> / <c>RegisterRecent</c>)が本当に seam の出力を
    /// 使っているなら、開けて・本文が読めて・履歴に載って・2 回目は同じタブが再利用される。
    /// あわせて <c>NormalizeCallCount == 1</c> を pin する(「1 操作あたり正規化<b>多くとも</b>
    /// 1 本」= 設計書 §3 の不変条件。境界付きにした意味がここに掛かっている。上限であって
    /// 下限ではなく、Ctrl+S は 0 本 =
    /// <see cref="SaveDocument_ExistingPath_DoesNotNormalizeAtAll"/>)。
    /// <para>
    /// <b>打つ側を<c>裸の相対名</c>にしてあるのが load-bearing</b>(Task 4 レビュー 仕様-m-2):
    /// これが絶対パスだと、ガードのオペランドを <c>GetDirectoryName(norm.Full)</c> から
    /// <c>GetDirectoryName(path)</c>(生入力)へ取り違える変異が全緑で生存した。相対名は
    /// 生の親が空・正規化後の親は非空なので、オペランドが入れ替わった瞬間に「パスが
    /// 正しくありません」で弾かれて赤くなる。実運用でも旧 <c>RecentFiles</c> / 旧 session JSON に
    /// 相対パスが残りうる(A-19)ので、守っているのは実在の経路。seam は Fake が固定値を
    /// 返すので、この相対名がカレントディレクトリーに依存することはない。
    /// </para>
    /// </summary>
    [Fact]
    public void TryOpenOrActivate_UsesNormalizedPathFromProbe_NotDirectGetFullPath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            const string typed = "typed.txt"; // 裸の相対名(上の doc 参照)。実在させない
            string redirected = tmp.File("redirected.txt");
            File2.WriteAllText(redirected, "redirected");
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Ok,
                redirected
            );

            var doc = host.File.TryOpenOrActivate(typed);

            Assert.NotNull(doc);
            // 生入力を受け取る入口なので 1 本(5 秒 × N にしない)。不変条件は「多くとも 1 本」で、
            // Ctrl+S のように 0 本の操作もある。
            Assert.Equal(1, host.Probe.NormalizeCallCount);
            Assert.Equal(typed, host.Probe.NormalizeLastPath); // 生入力がそのまま seam へ届く
            Assert.Equal(redirected, doc!.State.Path); // LoadInto が seam の出力を使う
            Assert.Equal("redirected", doc.Editor.Text);
            Assert.Equal(redirected, host.Settings.RecentFiles[0]); // RegisterRecent も同じ

            var again = host.File.TryOpenOrActivate(typed);

            Assert.Same(doc, again); // FindByPath も seam の出力で照合している
            Assert.Equal(1, host.Docs.Count);
            // fast-path 側の RegisterRecent も seam の出力を使う(生入力を積むと、開いてすら
            // いないパスが履歴の 2 件目として並ぶ)。1 件目の assert とは別の呼出点なので
            // 独立に pin する。
            Assert.Equal(redirected, Assert.Single(host.Settings.RecentFiles));
        });

    [Fact]
    public void TryOpenOrActivate_PassesFiveSecondTimeoutToNormalizeProbe() =>
        Sta.Run(() =>
        {
            // 5 秒契約の pin(保存側の双子 SaveAs_PassesFiveSecondTimeoutToNormalizeProbe と同じ思想)。
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc");

            Assert.NotNull(host.File.TryOpenOrActivate(path));

            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.NormalizeLastTimeout);
        });

    /// <summary>
    /// 保存側 V-1 / V-3 の開く側対称。予約デバイス名は <c>GetFullPath</c> が <c>\\.\NUL</c> へ
    /// 正規化する = seam は <c>Ok</c> を返す(「文字列として正規化できた」以上の意味を持たない)。
    /// ガードが無いと先頭 <c>\\</c> で <see cref="RemotePathDetector"/> がリモート判定し、
    /// 無意味な到達性プローブを 1 本通ったうえで「ネットワークパスに到達できません」という
    /// 的外れな文言になる(= V-3 で潰したはずの症状)。
    /// <para>
    /// <b>入力が <c>&lt;tmp&gt;\NUL</c> なのは load-bearing</b>(Task 4 レビュー 脆弱-I-2):
    /// 裸の <c>NUL</c> だと<b>生入力の親も</b>空なので、ガードを正規化の<b>前</b>へ移す変異が
    /// 全緑で生存した。ディレクトリー付きなら生の親は <c>&lt;tmp&gt;</c>(非空)で、正規化後に
    /// 初めて <c>\\.\NUL</c>(親 null)になるため、順序が壊れた瞬間に赤くなる。実測(2026-08-23):
    /// <c>&lt;tmp&gt;\NUL</c> → <c>\\.\NUL</c>。<b><c>CON</c> はディレクトリー付きだと変換されない</b>
    /// (<c>&lt;tmp&gt;\CON</c> はそのまま)ので、この形が作れるのは <c>NUL</c> のほう。
    /// </para>
    /// <para>
    /// <b>訂正(脆弱-I-4)</b>: 当初ここには「<c>CON</c> だと <c>File.OpenRead</c> が無期限に
    /// ブロックするので fixture に <c>NUL</c> を選んだ」と書いていた。<b>これは誤り</b>。
    /// <c>LoadAsBufferAuto</c> は本文を読む前に <c>probe.Length</c> を打ち、デバイスパスは
    /// そこで <c>NotSupportedException</c> になるので読みに到達しない(実測: <c>\\.\CON</c> /
    /// <c>\\.\NUL</c> とも <c>LoadAsBufferAuto</c> は 1〜3 ms で <c>NotSupportedException</c>)。
    /// <c>CON</c> でもハングしないので、fixture 選択にその制約は無い。
    /// </para>
    /// <para>
    /// <b>「到達性プローブまで届かせない」が観測できるのは Fake の既定が <c>Result=true</c> だから</b>
    /// (仕様-n-1)。本番の <see cref="FileReachabilityProbe"/> は <c>File.Exists(@"\\.\NUL")</c> が
    /// false なので、ガードが無くてもプローブ側が止める。ここで見ているのは
    /// 「ガードが**プローブより前に**弾くか」であって、実運用で必ず開けてしまうかではない。
    /// </para>
    /// </summary>
    [Fact]
    public void TryOpenOrActivate_ReservedDeviceName_IsRejectedBeforeProbe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string deviceViaDir = tmp.File("NUL"); // 生の親は非空・正規化すると \\.\NUL
            int before = host.Docs.Count;

            Assert.Null(host.File.TryOpenOrActivate(deviceViaDir));

            // 脆弱-I-3: この 1 行は**単独の変異を殺さない**(ガードを外しても
            // LoadAsBufferAuto が NotSupportedException になり、作りかけタブは
            // TryOpenOrActivate の失敗経路が閉じるので件数は戻る)。将来
            // 「弾いた後にタブを残す」形へ壊れたときのための網として残す
            // = Task 3 の Modified assert と同じ扱い。kill を担うのは下の 2 本。
            Assert.Equal(before, host.Docs.Count);
            Assert.Equal(0, host.Probe.CallCount); // 到達性プローブまで届かせない
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("正しくありません")
            );
            // V-3 と同じ: リモート扱いによる的外れな文言に落とさない。
            Assert.DoesNotContain(
                host.Prompt.Log,
                e => e.Text.StartsWith("ネットワークパスに到達できません", StringComparison.Ordinal)
            );
        });

    [Fact]
    public void TryOpenOrActivate_DriveRoot_IsRejectedWithPathMessage() =>
        Sta.Run(() =>
        {
            // ルートは正規化できるが親フォルダーが無い=ファイルとして確定しない。
            // ガードが無いと LoadInto の catch(UnauthorizedAccessException)へ落ちて
            // 「開けませんでした」になる=文言 assert が変異を kill する。
            // 脆弱-I-2: 入力を裸の root ではなく `<root>\anything\..` にするのが load-bearing。
            // 裸の root は生入力の親も null なので、ガードを正規化の**前**へ移す変異が生存する。
            // この形なら生の親は `<root>anything`(非空)で、正規化して初めて root になる
            // (実測 2026-08-23: C:\anything\.. → C:\)。`anything` は実在しなくてよい
            // (`..` の畳み込みは純粋な文字列処理)。
            // ローカルパスをハードコードしないため root は TempDir から導出する。
            using var host = new Host();
            using var tmp = new TempDir();
            string root = System.IO.Path.GetPathRoot(tmp.Root)!;
            Assert.False(string.IsNullOrEmpty(root));
            string rootViaDotDot = System.IO.Path.Combine(root, "anything", "..");
            int before = host.Docs.Count;

            Assert.Null(host.File.TryOpenOrActivate(rootViaDotDot));

            Assert.Equal(before, host.Docs.Count); // 脆弱-I-3 と同じ将来ガード(単独では kill しない)
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Error" && e.Text.Contains("正しくありません")
            );
        });

    /// <summary>
    /// 復元経路(<see cref="FileController.RestoreSession"/>)は
    /// <see cref="FileController.WithLoadErrorPromptSuppressed"/> の中でここへ来る。
    /// 新しい失敗経路が抑止スコープを無視すると、<b>起動時に per-file ダイアログが増える</b>
    /// (既存の <c>LoadInto</c> catch / <c>ReportUnreachable</c> と同じ扱いに揃える)。
    /// 戻り値 null は抑止に関係なく伝播し、呼出元が failedPaths へ集約する。
    /// </summary>
    [Fact]
    public void TryOpenOrActivate_NormalizeFailure_RespectsSuppressedPromptScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );

            // 通常経路: エラーダイアログが 1 個出る
            Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"));
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");

            host.Prompt.Log.Clear();

            // 抑止 ON: ダイアログは出ないが失敗自体は伝播する
            host.File.WithLoadErrorPromptSuppressed(() =>
                Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"))
            );
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Error");

            // 抑止解除後: 再びダイアログが出る(finally での復元確認)
            Assert.Null(host.File.TryOpenOrActivate(@"C:\Temp\a.txt"));
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
        });

    // ===== HIGH-6: UNC ロードの短タイムアウトプローブ(LoadInto 冒頭) =====

    [Fact]
    public void LoadInto_ShowsErrorPrompt_WhenRemoteUncUnreachable() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Probe.Result = false; // プローブがタイムアウト/到達不可を返す

            // 存在しない UNC パス。プローブが false を返すため TextFileService には到達しない。
            var doc = host.File.TryOpenOrActivate(@"\\nonexistent-host-42\share\x.txt");

            Assert.Null(doc);
            Assert.Equal(1, host.Probe.CallCount); // UNC は必ずプローブを通す
            // FileController が渡すタイムアウトを pin(5s → 5min のような mutation を kill)。
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.LastTimeout);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith(
                        "ネットワークパスに到達できません",
                        System.StringComparison.Ordinal
                    )
            );
        });

    [Fact]
    public void LoadInto_SkipsProbe_ForLocalPath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("local.txt");
            File2.WriteAllText(path, "abc");

            var doc = host.File.TryOpenOrActivate(path);

            Assert.NotNull(doc); // ローカルは通常経路で開ける
            Assert.Equal(0, host.Probe.CallCount); // ローカルパスはプローブを回さない(挙動不変)
        });

    // ===== CSV-M-2: Save 経路のリーチャビリティプローブ(WriteToPath 冒頭・HIGH-6 の Save 側対称) =====

    [Fact]
    public void Save_ShowsErrorPrompt_WhenRemoteUncUnreachable() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 既存 UNC ファイルを開いた後にサーバがダウンしたシナリオ:
            // Path だけ UNC の Document を用意し、以後の Save でリーチャビリティチェックを走らせる。
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // Text setter は fresh buffer=Modified=false
            // Save 前に必ず dirty 状態を作る(SetSavePoint が呼ばれていないこと=WriteToPath が
            // プローブで短絡したことの観測点)。fast-path 回避のために別の 1 文字挿入→即削除で
            // content は "abc" のまま Modified=true にする。
            doc.Editor.ReplaceCharRange(0, 0, "z"); // "zabc", Modified=true
            doc.Editor.ReplaceCharRange(0, 1, ""); // "abc", Modified=true(_savedRoot からズレたまま)
            Assert.True(doc.Editor.Modified); // 前提: Save 前は dirty
            doc.State.Path = @"\\nonexistent-host-42\share\x.txt";
            // A-4: 書込側は保存先意味論のプローブを使う。読み取り側の Result は既定 true のまま
            // 残す=「書込側が読み取り側を呼んでいたら素通りして書込に進み、
            // 到達性エラーではなく "保存できませんでした" になる」ので、どちらを使っているか判別できる。
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: false,
                FileExists: false
            );

            Assert.False(host.File.Save());

            // Save 経路も UNC は必ずプローブを通す(HIGH-6 と対称)
            Assert.Equal(1, host.Probe.SaveTargetCallCount);
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.SaveTargetLastTimeout); // 5s → 5min mutation の kill
            // "ネットワークパスに到達できません" が Save 経路でも 1 件だけ発火する(Load と Save の
            // 二重発火を避ける=WriteToPath 冒頭ガードのみで完結する契約)。
            var reachErrors = host.Prompt.Log.Where(e =>
                e.Kind == "Error"
                && e.Text.StartsWith(
                    "ネットワークパスに到達できません",
                    System.StringComparison.Ordinal
                )
            );
            Assert.Single(reachErrors);
            // 副作用非発生の pin:
            // - Modified=true 維持 → SetSavePoint が呼ばれていない(=WriteToPath の成功パスに入っていない)
            // - Assert.DoesNotContain("保存できませんでした") → 短絡 return であって catch 経由の失敗ではない
            // (content が "abc"=改行なしのため ConvertEols は元々 no-op=このテスト単体では ConvertEols
            //  経由か短絡かの直接判別はできないが、上記 2 点で「WriteToPath の副作用ブロックに入って
            //  いない」ことは pin できる)
            Assert.True(doc.Editor.Modified);
            Assert.Equal("abc", doc.Editor.SnapshotText);
            Assert.DoesNotContain(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("保存できませんでした", System.StringComparison.Ordinal)
            );
        });

    [Fact]
    public void Save_SkipsProbe_ForLocalPath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.State.Path = path;

            Assert.True(host.File.Save()); // 通常経路で成功

            // ローカルパスはプローブを回さない(挙動不変)。A-4 で書込側が保存先意味論へ移ったので
            // 観測点も SaveTargetCallCount にする(CallCount は書込側から呼ばれなくなり
            // ローカル/リモートを問わず 0=リモートゲートの変異を殺せない vacuous な網になる)。
            Assert.Equal(0, host.Probe.SaveTargetCallCount);
            Assert.Equal("abc", File2.ReadAllText(path)); // 実際にディスクへ書き出し完了
        });

    [Fact]
    public void SaveAs_ShowsErrorPrompt_WhenPickedPathIsRemoteAndUnreachable() =>
        Sta.Run(() =>
        {
            // SaveAs で新たに UNC を選んだが到達不可なシナリオ。
            // **短絡位置は A-7 (a) で前へ移った**: 以前は WriteToPath 冒頭の CSV-M-2 ガードで止まり
            // Encoding/HasBom/LineEnding は「一度書き換えてからロールバック」されていたが、
            // 現在は SaveAsDocument の事前判定(上書き確認の直前)で止まるため State には
            // **触れないまま**同じ結果になる。以下の State assert はロールバックではなく
            // 「一切変更していない」を pin する(ロールバック自体の網は
            // SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath が持つ)。
            // 再表示の網は SaveAs_UnreachableUncPath_ShowsErrorAndReopens。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // 既定 State=UTF-8/BOM なし/CRLF
            // CodePage は 932 を選ぶ: 既定(65001)と同値だと Encoding ロールバックの assert が
            // 空振りする(既存 SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath と同旨)。
            host.Dialogs.SaveAs = new SaveAsResult(
                @"\\nonexistent-host-42\share\x.txt",
                932,
                HasBom: true,
                LineEnding.Lf
            );
            // A-4: 書込側は保存先意味論のプローブ。読み取り側の Result は既定 true のままにして
            // 「どちらを呼んでいるか」を判別可能にする(Save 経路の同型テストと対称)。
            host.Probe.SaveTargetResult = new SaveTargetProbeResult(
                Reachable: false,
                FileExists: false
            );

            Assert.False(host.File.SaveAs());

            // 事前判定で短絡するのでプローブは 1 回だけ(WriteToPath まで進めば 2 回になる)
            Assert.Equal(1, host.Probe.SaveTargetCallCount);
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.SaveTargetLastTimeout); // 5s pin
            // State は一切変わらない(旧: WriteToPath 失敗からのロールバック)
            Assert.Null(doc.State.Path); // Path は旧のまま(後続 Ctrl+S の別エンコード上書き事故防止)
            Assert.Equal(65001, doc.State.Encoding.CodePage); // 932 を選んだが反映前に止まる
            Assert.False(doc.State.HasBom);
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith(
                        "ネットワークパスに到達できません",
                        System.StringComparison.Ordinal
                    )
            );
        });

    [Fact]
    public void OpenFileWithDialog_UsesPickedPath_AndCancelDoesNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            host.Dialogs.OpenPath = null; // キャンセル
            host.File.OpenFileWithDialog();
            Assert.Equal(0, host.Docs.Count);

            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc");
            host.Dialogs.OpenPath = path;
            host.File.OpenFileWithDialog();
            Assert.Equal(path, host.Docs.Active!.State.Path); // 選択パスが唯一の開く経路へ流れる
        });

    // ===== 文字コード指定の開き直し =====

    [Fact]
    public void ReopenWithEncoding_WithoutPath_InformsAndSkipsDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.Docs.CreateNew(); // Path=null の無題

            host.File.ReopenWithEncoding();

            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "Info" && e.Text == "ファイルを開いてから実行してください。"
            );
            Assert.Equal(0, host.Dialogs.PickEncodingCount); // ダイアログまで進まない
        });

    [Fact]
    public void ReopenWithEncoding_ForcedCodePage_Reloads_AndReenablesUiaSelectionEvents() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc"); // ASCII=どのコードページでも同一内容(判定を決定的にする)
            var doc = host.File.TryOpenOrActivate(path)!;
            // 個別 SR 対応廃止後も温存の UIA 配線: LoadInto が RaiseUiaSelectionEvents を確実に戻すことを固定
            doc.Editor.RaiseUiaSelectionEvents = false;
            host.Dialogs.EncodingCodePage = 932;

            host.File.ReopenWithEncoding();

            Assert.Equal(932, doc.State.Encoding.CodePage);
            Assert.True(doc.Editor.RaiseUiaSelectionEvents);
            Assert.Equal(2, host.OpenedFresh.Count); // 開き直しも .csv 自動モードの対象
        });

    [Fact]
    public void ReopenWithEncoding_DirtyCancelled_AbortsBeforeDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc");
            var doc = host.File.TryOpenOrActivate(path)!;
            doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty
            host.Prompt.YesNoCancelResult = DialogResult.Cancel;

            host.File.ReopenWithEncoding();

            Assert.Equal(0, host.Dialogs.PickEncodingCount); // 未保存確認で中止=ダイアログまで進まない
            Assert.True(doc.Editor.Modified);
            Assert.Equal(65001, doc.State.Encoding.CodePage);
        });

    [Fact]
    public void Reopen_WithReplacementChar_WarnsToReopen() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "abc"); // ASCII=UTF-8 として妥当=初回オープンで置換文字は発生しない
            Assert.NotNull(host.File.TryOpenOrActivate(path)); // 初回オープン成功
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn"); // 初回オープンは警告なしを固定

            // 本体を UTF-8 で不正なバイト(0xFF)に差し替える。TextBufferBuilder の Utf8Sanitizer が
            // U+FFFD へ置換し HadReplacementChar=true を返す=文字コード取り違えの示唆経路を発火させる。
            // (forcedCodePage=65001 で UTF-8 として強制デコード=0xFF は不正バイト→U+FFFD 置換)
            File2.WriteAllBytes(path, new byte[] { 0xFF });
            host.Dialogs.EncodingCodePage = 65001;

            host.File.ReopenWithEncoding();

            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Warn"
                    && e.Text.Contains("置換文字")
                    && e.Caption == "文字コードの警告"
            );
        });

    // ===== 未保存確認(Yes=保存成否/No=true/Cancel=false) =====

    [Fact]
    public void ConfirmDiscardIfDirty_CleanDocument_TrueWithoutPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc"; // Text セッター=新規バッファで Modified=false

            Assert.True(host.File.ConfirmDiscardIfDirty(doc));
            Assert.Empty(host.Prompt.Log); // クリーンなら問わない
        });

    [Fact]
    public void ConfirmDiscardIfDirty_No_ReturnsTrueWithoutSaving() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.Editor.ReplaceCharRange(0, 0, "x");
            doc.State.Path = tmp.File("a.txt"); // まだ存在しないファイル
            host.Prompt.YesNoCancelResult = DialogResult.No;

            Assert.True(host.File.ConfirmDiscardIfDirty(doc)); // 破棄=続行してよい

            Assert.False(File2.Exists(doc.State.Path)); // 保存はしない
            Assert.True(doc.Editor.Modified);
        });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_SavesDocument_AndReturnsSaveResult() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.Editor.ReplaceCharRange(0, 0, "x");
            doc.State.Path = tmp.File("a.txt");
            host.Prompt.YesNoCancelResult = DialogResult.Yes;

            Assert.True(host.File.ConfirmDiscardIfDirty(doc));

            Assert.True(File2.Exists(doc.State.Path)); // Yes=保存してから続行
            Assert.False(doc.Editor.Modified);
            Assert.Contains(
                host.Prompt.Log,
                e => e.Kind == "YesNoCancel" && e.Text.Contains("の変更を保存しますか")
            );
        });

    [Fact]
    public void ConfirmDiscardIfDirty_Yes_WithoutPath_FallsBackToSaveAs_CancelMeansFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            doc.Editor.ReplaceCharRange(0, 0, "x"); // dirty な無題(Path=null)
            host.Prompt.YesNoCancelResult = DialogResult.Yes;
            host.Dialogs.SaveAs = null; // SaveAs ダイアログでキャンセル

            Assert.False(host.File.ConfirmDiscardIfDirty(doc)); // Yes→SaveAs 失敗=続行しない(閉じない)
        });

    // ===== NewFile 既定+無題連番 / バックアップ復元 =====

    [Fact]
    public void NewFile_AppliesSettingsDefaults_AndNumbersUntitledTabs() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Settings.DefaultCodePage = 932;
            host.Settings.DefaultLineEnding = 1; // LineEnding.Lf

            host.File.NewFile();
            var doc1 = host.Docs.Active!;
            host.File.NewFile();
            var doc2 = host.Docs.Active!;

            Assert.Equal(932, doc1.State.Encoding.CodePage); // 設定の既定コードページ
            Assert.Equal(LineEnding.Lf, doc1.State.LineEnding); // 設定の既定改行
            Assert.False(doc1.State.HasBom); // 既定と同値=契約の文書化(NewFile は BOM なし固定)
            Assert.Equal(1, doc1.State.UntitledNumber);
            Assert.Equal(2, doc2.State.UntitledNumber); // セッション内で再利用しない連番
            Assert.Equal("無題 1", doc1.Page.Text);
            Assert.False(doc2.Editor.Modified);
            Assert.True(host.MetaChangedCount >= 2); // タイトル・ステータス更新の配線
        });

    [Fact]
    public void RestoreFromBackup_UntitledRecord_KeepsNumber_StaysDirty_AndAdvancesSeq() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var rec = new BackupRecord(
                "id-1",
                OriginalPath: null,
                UntitledNumber: 5,
                CodePage: 932,
                HasBom: false,
                LineEndingId: 1,
                Content: "abc",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(5, doc.State.UntitledNumber); // ダイアログ表示と復元後タブの番号一致
            Assert.Equal(932, doc.State.Encoding.CodePage);
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
            Assert.Equal("abc", doc.Editor.Text);
            Assert.True(doc.Editor.Modified); // 保存点を打たない=ユーザーが保存できる(復元 dirty 化バグの修正で本来意図へ)
            Assert.Equal("* 無題 5", doc.Page.Text);

            host.File.NewFile(); // 連番カウンタは既存最大値の先へ進む
            Assert.Equal(6, host.Docs.Active!.State.UntitledNumber);
        });

    [Fact]
    public void RestoreFromBackup_PathRecord_SetsMetaFromRecord_AndToleratesNullContent() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // UntitledNumber: 7 は「path レコードでは旧無題番号を無視して 0 化する」契約を実効検証するため
            // (0 のままだとコピー実装でも 0 化実装でも通ってしまう=レビュー I-1 と同型の空振り)
            var rec = new BackupRecord(
                "id-2",
                OriginalPath: @"C:\backup-origin\b.txt",
                UntitledNumber: 7,
                CodePage: 65001,
                HasBom: true,
                LineEndingId: 0,
                Content: null!,
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec); // 復元はディスクを読まない=実在しないパスでよい

            Assert.Equal(@"C:\backup-origin\b.txt", doc.State.Path);
            Assert.Equal(0, doc.State.UntitledNumber); // path レコードは旧無題番号(7)を無視して 0 化
            Assert.True(doc.State.HasBom);
            Assert.Equal("", doc.Editor.Text); // JSON 破損(null)でも空タブ復元で継続(レビュー M-5 の防御)
            Assert.True(doc.Editor.Modified); // 復元 dirty 化バグの修正で本来意図へ
            Assert.Equal("* b.txt", doc.Page.Text);
        });

    // ===== HIGH-2: OriginalPath 白リスト検証(RestoreFromBackup フォールバック) =====

    [Fact]
    public void RestoreFromBackup_KeepsOriginalPath_WhenPathIsSafe() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // ユーザ配下(TempPath 直下)=OriginalPathValidator.Check → Ok。既存の path レコード契約が
            // 白リスト導入後も維持されることを固定する(挙動不変性の担保)。
            var safePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "safe-restore.txt");
            var rec = new BackupRecord(
                "id-safe",
                OriginalPath: safePath,
                UntitledNumber: 0,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "safe content",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(System.IO.Path.GetFullPath(safePath), doc.State.Path);
            Assert.Equal(0, doc.State.UntitledNumber); // path レコードは 0 化(既存契約)
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn"); // 警告は出さない
        });

    [Fact]
    public void RestoreFromBackup_FallsBackToUntitled_WhenPathIsRejected() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // System32 配下=攻撃者が JSON を植えた復元先の代表例(Ctrl+S で hosts 上書き導線を作らせない)。
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"
            );
            var rec = new BackupRecord(
                "id-attack",
                OriginalPath: attackPath,
                UntitledNumber: 0,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "poison",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Null(doc.State.Path); // 無題フォールバック=Path は null
            Assert.True(doc.State.UntitledNumber > 0); // 無題連番が付く
            var warn = Assert.Single(host.Prompt.Log, e => e.Kind == "Warn");
            Assert.Contains("バックアップの元パスが無効なため", warn.Text);
            Assert.Contains(attackPath, warn.Text); // 拒絶した元パスを本文に含める(ユーザ判断のため)
        });

    [Fact]
    public void RestoreFromBackup_MaliciousPath_ContentStillLoadedForSaveAs() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 攻撃 Path でフォールバックしても本文は失わない=ユーザが「名前を付けて保存」で救出できる。
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"
            );
            var rec = new BackupRecord(
                "id-attack-content",
                OriginalPath: attackPath,
                UntitledNumber: 0,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "important user data",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Null(doc.State.Path); // サイレントに target を上書きしない
            Assert.Equal("important user data", doc.Editor.Text); // 本文は保持=SaveAs で救出可能
            Assert.True(doc.Editor.Modified); // dirty=ユーザーが保存点を打てる
        });

    // ===== BK-L-1 / BK-L-2: LineEndingId / CodePage フォールバック(2026-07-19) =====
    //
    // 攻撃者 JSON が範囲外の LineEndingId(例 999 / -1)や未サポートの CodePage(99999 / -1)を
    // 持つ場合、以前は
    //   - `(LineEnding)rec.LineEndingId` は enum 範囲外の値を無検査で返し、
    //     ToEolString()/ToDisplayString() の `_ => "\r\n"` 分岐で silent CRLF 上書きになる
    //   - `EncodingCatalog.Get(rec.CodePage)` は ArgumentException / NotSupportedException を投げ、
    //     RestoreFromBackup が try/catch を持たないため MainForm へ伝播=他タブの復元まで巻き添え喪失
    // という 2 つの脆弱性(BK-L-1 / BK-L-2)があった。修正後は
    //   - LineEndingId が Enum.IsDefined 不成立なら Crlf にフォールバック
    //   - CodePage が Argument/NotSupported を投げたら UTF-8(65001)にフォールバック
    // を行い、いずれも silent recovery(_prompt.Warn は追加しない=ユーザは復元後 Save で確定できる)。
    // 復元の他メタ(Path/HasBom/Content/Modified)は正常経路と同じで、フォールバックが本文や
    // 保存導線を壊さないことを固定する。

    [Fact]
    public void RestoreFromBackup_OutOfRangeLineEndingId_FallsBackToCrlf() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var rec = new BackupRecord(
                "id-eol-oor",
                OriginalPath: null,
                UntitledNumber: 3,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 999, // 定義外(Enum.IsDefined=false)=CRLF にフォールバック
                Content: "hello",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
            // 本文・他メタは正常復元(フォールバックが復元全体を壊さない)
            Assert.Equal("hello", doc.Editor.Text);
            Assert.Equal(3, doc.State.UntitledNumber);
            Assert.Equal(65001, doc.State.Encoding.CodePage);
            Assert.True(doc.Editor.Modified);
            // silent recovery=_prompt.Warn は増やさない
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn");
        });

    [Fact]
    public void RestoreFromBackup_NegativeLineEndingId_FallsBackToCrlf() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // -1 は Enum.IsDefined でも false になる corner(999 と別経路の代表値)。
            var rec = new BackupRecord(
                "id-eol-neg",
                OriginalPath: null,
                UntitledNumber: 4,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: -1,
                Content: "neg",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
            Assert.Equal("neg", doc.Editor.Text);
            Assert.Equal(4, doc.State.UntitledNumber);
            Assert.True(doc.Editor.Modified);
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn");
        });

    [Fact]
    public void RestoreFromBackup_UnsupportedCodePage_FallsBackToUtf8() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 存在しない CodePage 99999 は Encoding.GetEncoding が NotSupportedException を投げる。
            // RestoreFromBackup は UTF-8(65001)にフォールバック=例外は上位へ伝播させない。
            var rec = new BackupRecord(
                "id-cp-oor",
                OriginalPath: null,
                UntitledNumber: 8,
                CodePage: 99999,
                HasBom: false,
                LineEndingId: 0, // Crlf
                Content: "cp-fallback",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(65001, doc.State.Encoding.CodePage);
            // 本文・他メタは正常復元(フォールバックが復元全体を壊さない)
            Assert.Equal("cp-fallback", doc.Editor.Text);
            Assert.Equal(8, doc.State.UntitledNumber);
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
            Assert.True(doc.Editor.Modified);
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn");
        });

    [Fact]
    public void RestoreFromBackup_NegativeCodePage_FallsBackToUtf8() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // -1 は Encoding.GetEncoding が ArgumentOutOfRangeException(=ArgumentException 派生)を投げる。
            // フォールバックの catch フィルタ(ArgumentException or NotSupportedException)がカバーする経路。
            var rec = new BackupRecord(
                "id-cp-neg",
                OriginalPath: null,
                UntitledNumber: 9,
                CodePage: -1,
                HasBom: false,
                LineEndingId: 0, // Crlf
                Content: "neg-cp",
                TimestampUtc: DateTime.UtcNow
            );

            var doc = host.File.RestoreFromBackup(rec);

            Assert.Equal(65001, doc.State.Encoding.CodePage);
            Assert.Equal("neg-cp", doc.Editor.Text);
            Assert.Equal(9, doc.State.UntitledNumber);
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding);
            Assert.True(doc.Editor.Modified);
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Warn");
        });

    // ===== A-1 第 2 層: 復元時の陳腐化検出(設計 2026-08-22 §4) =====

    private static readonly DateTime StaleBase = new(2026, 08, 22, 12, 00, 00, DateTimeKind.Utc);

    /// <summary>ユーザ配下(TempPath 直下)= OriginalPathValidator.Check → Ok になる復元先。
    /// 正規化後のパスで Fake を引くため、キーも GetFullPath を通して合わせる。</summary>
    private static string SafeRestorePath(string name) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name));

    private static BackupRecord StaleRec(string? path, DateTime timestampUtc) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: 0,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "backup content",
            TimestampUtc: timestampUtc
        );

    /// <summary>ディスク側がバックアップ取得後に更新されていれば、警告対象として記録する。
    /// A-1 の害は「Ctrl+S で<b>無警告</b>に新内容が消える」ことなので、記録 = 警告が出れば害は消える。</summary>
    [Fact]
    public void RestoreFromBackup_DiskNewerThanBackup_RecordsStalePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var path = SafeRestorePath("kxEdit-stale-newer.txt");
            host.Timestamps.Times[path] = StaleBase.AddMinutes(5); // ディスクの方が新しい

            _ = host.File.RestoreFromBackup(StaleRec(path, StaleBase));

            Assert.Equal(new[] { path }, host.File.TakeStaleRestoredPaths());
        });

    /// <summary>ディスクが古い(通常のクラッシュ復元)なら記録しない = 警告を出さない。</summary>
    [Fact]
    public void RestoreFromBackup_DiskOlderThanBackup_RecordsNothing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var path = SafeRestorePath("kxEdit-stale-older.txt");
            host.Timestamps.Times[path] = StaleBase.AddMinutes(-5);

            _ = host.File.RestoreFromBackup(StaleRec(path, StaleBase));

            Assert.Empty(host.File.TakeStaleRestoredPaths());
            Assert.Single(host.Timestamps.Queries); // 判定自体は走っている(空の理由が「未呼出」でない)
        });

    /// <summary>パス検証 NG(攻撃者 JSON 由来)では<b>そもそも I/O しない</b>。
    /// 検証していないパスへ触らないのは HIGH-2 の思想。Queries が空であることで固定する。</summary>
    [Fact]
    public void RestoreFromBackup_RejectedPath_DoesNotQueryTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"
            );

            _ = host.File.RestoreFromBackup(StaleRec(attackPath, StaleBase));

            Assert.Empty(host.Timestamps.Queries);
            Assert.Empty(host.File.TakeStaleRestoredPaths());
        });

    /// <summary>無題レコード(OriginalPath=null)は判定対象外(比較すべきディスク実体が無い)。</summary>
    [Fact]
    public void RestoreFromBackup_UntitledRecord_DoesNotQueryTimestamp() =>
        Sta.Run(() =>
        {
            using var host = new Host();

            _ = host.File.RestoreFromBackup(StaleRec(path: null, StaleBase));

            Assert.Empty(host.Timestamps.Queries);
        });

    /// <summary>Take は読み取りと同時にクリアする = 同じ警告を二度出さない。</summary>
    [Fact]
    public void TakeStaleRestoredPaths_ClearsAfterRead() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var path = SafeRestorePath("kxEdit-stale-take.txt");
            host.Timestamps.Times[path] = StaleBase.AddMinutes(5);

            _ = host.File.RestoreFromBackup(StaleRec(path, StaleBase));

            Assert.Single(host.File.TakeStaleRestoredPaths());
            Assert.Empty(host.File.TakeStaleRestoredPaths()); // 2 回目は空
        });

    /// <summary>ON(hot exit silent)経路も同じ判定を通ること。A-1 の主経路はこちら
    /// (RestoreSession → RestoreDirtyFromBackup)なので、OFF 経路のテストだけでは網にならない。</summary>
    [Fact]
    public void RestoreSession_DiskNewerThanBackup_RecordsStalePath() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var path = SafeRestorePath("kxEdit-stale-session.txt");
            host.Timestamps.Times[path] = StaleBase.AddMinutes(5);
            var bk = StaleRec(path, StaleBase);
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
                StaleBase
            );

            _ = host.File.RestoreSession(
                layout,
                new[] { bk },
                initialEmpty: null,
                adoptRestored: null
            );

            Assert.Equal(new[] { path }, host.File.TakeStaleRestoredPaths());
        });

    // ===== EOL 非ロールバックの修正確認(Batch A Task 1・2026-07-15) =====
    //
    // 経緯: WriteToPath (:268-) は保存直前に doc.Editor.ConvertEols(EolMode) で本文中の
    // 改行を State.LineEnding に一括変換してから TextFileService.Save を呼ぶ。以前は書込失敗時に
    // SaveAsDocument (:231-236) が State(Encoding/LineEnding/HasBom)を元値へロールバックするだけで、
    // 本文の EOL(バグ 1)と、ConvertEols 非 fast-path の ReplaceSource で fresh 化された
    // TextBuffer の保存点(バグ 2=Save 前 dirty が Save 後 Modified=false に落ちる)が
    // ロールバックされない静音喪失導線があった。既存 SaveAs 系ロールバックテスト(本ファイル上部)は
    // fixture の本文が "abc"(改行なし)で ConvertEols が no-op のため、この 2 バグを検出できていなかった。
    //
    // 修正(2026-07-15・fix(app) コミット): WriteToPath は ConvertEols 前に旧 TextBuffer 参照を握り、
    // 失敗時にその参照へ戻す。TextBuffer 内部の _savedRoot/_current は ConvertEols/ReplaceSource で
    // 書き換わらないため、参照を戻すだけで本文も Modified も一括で復元される。以下の 2 テストは
    // かつて「バグ を pin する ★修正時に赤化」だったものを反転させ、修正後の担保として固定するもの。
    //
    // 【★★履歴】旧テスト名は SaveAs_WriteFailure_LeavesEolConverted_KnownBehavior /
    // Save_WriteFailure_LeavesEolNormalized_KnownBehavior。assertion は "a\nb"/"x\r\ny"/Modified=false を
    // pin していた(バグ固定)。修正後は "a\r\nb"/"x\ny"/Modified=true(ロールバック担保)に反転。

    [Fact]
    public void SaveAs_WriteFailure_RollsBackContentEol() =>
        Sta.Run(() =>
        {
            // 修正確認(旧: SaveAs_WriteFailure_LeavesEolConverted_KnownBehavior):
            // WriteToPath 失敗時、State(Encoding/LineEnding/HasBom)だけでなく、Editor.ConvertEols で
            // 正規化済みの本文もロールバックされる(バグ 1 の修正担保)。
            using var host = new Host();
            using var tmp = new TempDir();
            var doc = host.Docs.CreateNew();
            // 既定 State=UTF-8/BOM なし/CRLF。本文は CRLF 改行(意図的に SaveAs で LF を選ぶ=非デフォルト)。
            doc.Editor.Text = "a\r\nb";
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // 前提: 既定は CRLF
            // 存在しないサブディレクトリ配下=TextFileService.Save が DirectoryNotFoundException(IOException 派生)
            // で失敗する(既存 SaveAs_WriteFailure_RollsBackEncodingBomEol_AndKeepsPath と同型の失敗導線)。
            // ダイアログ側で LineEnding.Lf を選ばせる=ConvertEols(Lf) で本文 "a\r\nb" が "a\nb" に変換される。
            host.Dialogs.SaveAs = new SaveAsResult(
                tmp.File(@"no-such-dir\a.txt"),
                65001,
                HasBom: false,
                LineEnding.Lf
            );

            Assert.False(host.File.SaveAs());

            // ---- State ロールバック側(既存テストと同じ担保・回帰防止のため再確認) ----
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // CRLF へロールバック(SaveAsDocument :234)
            Assert.Null(doc.State.Path); // Path は旧のまま維持(:238 は失敗時通らない)
            Assert.Contains(
                host.Prompt.Log,
                e =>
                    e.Kind == "Error"
                    && e.Text.StartsWith("保存できませんでした", System.StringComparison.Ordinal)
            );

            // ---- 本文ロールバック(バグ 1 修正で緑化=修正後の担保) ----
            // ConvertEols(Lf) で "a\r\nb" → "a\nb" に一旦変換されたが、Save 失敗の catch で WriteToPath が
            // ConvertEols 前の TextBuffer 参照へ戻すため CRLF に復元される(以前は LF のまま残っていた=バグ 1)。
            Assert.Equal("a\r\nb", doc.Editor.SnapshotText); // ★バグ 1 修正で緑化=ConvertEols 済み本文の復元 ★
        });

    [Fact]
    public void Save_WriteFailure_RollsBackContentEol_And_KeepsModifiedFlag() =>
        Sta.Run(() =>
        {
            // 修正確認(旧: Save_WriteFailure_LeavesEolNormalized_KnownBehavior):
            // WriteToPath 失敗時、本文の EOL(バグ 1)と Modified フラグ(バグ 2)の両方がロールバックされる。
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "orig"); // ReadOnly 属性を付けるため一旦実在させる
            File2.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
            try
            {
                var doc = host.Docs.CreateNew();
                // 既定 State=CRLF。本文は LF のみ(意図的な非デフォルト=ConvertEols(Crlf) で "x\ny" → "x\r\ny")。
                doc.Editor.Text = "x\ny";
                doc.State.Path = path;
                Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // 前提: 既定は CRLF(Save 経路は State を変えない)

                // Save 前に必ず dirty 状態を作る(=バグ 2 検出のための必須前提):
                // Text setter は TextBuffer.FromString で fresh buffer(_savedRoot=root=Modified=false)を差し込む
                // ため、そのままだと Save 前も後も Modified=false で「差替で dirty が消える」を検出できない。
                // 1 文字挿入→即削除で content は "x\ny" のまま _current.Root だけ進める=
                // 保存点(_savedRoot)からズレて Modified=true になる。この状態で Save 失敗させ、
                // ConvertEols(Crlf) の非 fast-path が ReplaceSource で新規 TextBuffer に差し替えても、
                // 修正後は WriteToPath catch で旧 TextBuffer 参照へ戻すため Modified=true が復元される。
                doc.Editor.ReplaceCharRange(0, 0, "z"); // "zx\ny", root=B, Modified=true
                doc.Editor.ReplaceCharRange(0, 1, ""); // "x\ny", root=C, Modified=true(_savedRoot=A のまま)
                Assert.Equal("x\ny", doc.Editor.SnapshotText); // 前提: content は元に戻っている
                Assert.True(doc.Editor.Modified); // 前提: Save 前は dirty(_current.Root != _savedRoot)

                // 保存先ファイルの ReadOnly 属性で AtomicFile.Write が UnauthorizedAccessException を投げ、
                // WriteToPath の catch フィルタで false 返却+prompt.Error 通知される。
                Assert.False(host.File.Save());

                // ---- State は元々変わらない(Save 経路は SaveAsDocument と違い State を触らない) ----
                Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // 契約: Save は State 不変
                Assert.Equal("orig", File2.ReadAllText(path)); // 原本は不変(AtomicFile の契約)
                Assert.Contains(
                    host.Prompt.Log,
                    e =>
                        e.Kind == "Error"
                        && e.Text.StartsWith(
                            "保存できませんでした",
                            System.StringComparison.Ordinal
                        )
                );

                // ---- 本文ロールバック(バグ 1 修正で緑化=修正後の担保) ----
                // ConvertEols(Crlf) で "x\ny" → "x\r\ny" に一旦変換されたが、Save 失敗の catch で
                // WriteToPath が ConvertEols 前の TextBuffer 参照へ戻すため LF に復元される
                // (以前は CRLF のまま残り、以後の Ctrl+S 成功で意図しない CRLF が確定していた=バグ 1)。
                Assert.Equal("x\ny", doc.Editor.SnapshotText); // ★バグ 1 修正で緑化=ConvertEols 済み本文の復元 ★

                // ---- Modified 保持(バグ 2 修正で緑化=修正後の担保) ----
                // 以前は ConvertEols の非 fast-path が ReplaceSource で新規 TextBuffer(Modified=false)に
                // 差し替えるため Save 失敗後に Modified=false へ落ちていた(セーブポイント破壊=バグ 2)。
                // 修正後は WriteToPath catch で旧 TextBuffer 参照へ戻すため、_savedRoot は保持され
                // Save 前 dirty のままの状態が復元される(タブ「*」・終了時の保存確認が正しく動く)。
                Assert.True(doc.Editor.Modified); // ★バグ 2 修正で緑化=保存点の復元 ★
            }
            finally
            {
                // TempDir の再帰削除が ReadOnly 属性で失敗するのを避け、テスト成否に関わらず属性を戻す
                // (必須の後始末=既存 Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly と同旨)。
                File2.SetAttributes(path, System.IO.FileAttributes.Normal);
            }
        });

    // Batch A Task 1 Minor-3(2026-07-15): 上の 2 テスト(SaveAs=CRLF→LF・Save=LF→CRLF)は
    // どちらも ConvertEols が「本文の EOL ≠ target EOL」の非 fast-path 経路しか踏まない
    // (=ReplaceSource で新規 TextBuffer に差替=CurrentBuffer 参照が変わる)。WriteToPath (:303)
    // の <c>!ReferenceEquals(doc.Editor.CurrentBuffer, snapshotBefore)</c> guard は fast-path
    // (本文 EOL=target EOL=IsEolAlreadyUniform が true → EolMode 更新のみ・buffer 差替なし)で
    // <see cref="EditorControl.SetOrReplaceSource"/> をスキップし、キャレット/選択/スクロールが
    // <see cref="EditorControl.ReplaceSource"/> によって 0 リセットされるのを防いでいる。
    //
    // この guard が将来のリファクタで削除されて「常に SetOrReplaceSource(snapshotBefore) を呼ぶ」
    // 形に変わっても、上の 2 テストは非 fast-path しか踏まないため緑のまま通る=サイレント退行が
    // 可能。本テストは fast-path で I/O 失敗を起こし、caret/anchor/topLine/scrollX が Save 前と
    // 同じであることを固定して、その退行を kill する。
    [Fact]
    public void Save_WriteFailure_FastPath_PreservesCaretAndScroll() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "orig"); // ReadOnly 属性を付けるため一旦実在させる
            File2.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
            try
            {
                var doc = host.Docs.CreateNew();
                // 既定 State=CRLF。本文も CRLF のみで統一=ConvertEols(Crlf) が
                // IsEolAlreadyUniform=true で fast-path(EolMode 更新のみ・ReplaceSource なし)を踏む。
                // 4 論理行あるので TopLine=1 を有効に設定できる(maxLine=3)=非既定位置から検証開始
                // (レビュー標準 §3-2)。
                doc.Editor.Text = "abcdef\r\nghijkl\r\nmnopqr\r\nstuvwx";
                doc.State.Path = path;
                Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // 前提: 既定 CRLF=本文と一致=fast-path 経路

                // Save 前に非 0 位置の caret + 選択範囲 + TopLine を設定。
                // caret=5, anchor=2 → 選択 [2, 5) が "cde"(1 行目内・shift+右 3 文字相当)。
                doc.Editor.SetSelectionAnchored(anchor: 2, caret: 5);
                doc.Editor.TopLine = 1;
                int caretBefore = doc.Editor.CaretCharOffset;
                int anchorBefore = doc.Editor.SelectionAnchor;
                int topLineBefore = doc.Editor.TopLine;
                int scrollXBefore = doc.Editor.ScrollX;
                // 前提: TopLine セッターは maxLine=3 なので value=1 を通す(実効的に非 0 に置ける)。
                // これが 0 のまま=fixture 前提崩れ=以降の assert が空振りする。
                Assert.Equal(5, caretBefore);
                Assert.Equal(2, anchorBefore);
                Assert.Equal(1, topLineBefore);
                // 注: ScrollX は非表示 HScrollBar 下では setter が no-op=非 0 に置けないため 0 のまま。
                // このため ScrollX の retention 単体では guard 削除ミューテーションを kill できない
                // (before=0=after=0 も 0 リセット後の 0 と区別できない)。caret/anchor/topLine の
                // 3 値で guard 削除は十分 kill できるため実用上問題なし。

                // 保存先ファイルの ReadOnly 属性で AtomicFile.Write が UnauthorizedAccessException を投げ、
                // WriteToPath catch フィルタで false 返却+prompt.Error 通知される。
                Assert.False(host.File.Save());

                // ---- fast-path guard の kill 対象(★ここが本テストの核) ----
                // 現行実装: CurrentBuffer 参照が snapshotBefore と同一(fast-path=ReplaceSource 未発火)
                // のため WriteToPath catch は SetOrReplaceSource をスキップ=caret/anchor/topLine は保持。
                // guard 削除後: SetOrReplaceSource(snapshotBefore) → ReplaceSource が発火し、
                // caret=0/anchor=0/topLine=0/scrollX=0 に全リセット=下 3 行が赤化して mutation を kill。
                Assert.Equal(caretBefore, doc.Editor.CaretCharOffset); // ★ guard 削除で 0 に落ちる ★
                Assert.Equal(anchorBefore, doc.Editor.SelectionAnchor); // ★ 同上 ★
                Assert.Equal(topLineBefore, doc.Editor.TopLine); // ★ 同上 ★
                Assert.Equal(scrollXBefore, doc.Editor.ScrollX); // 観測制約=常に 0=documentation 目的
                Assert.Contains(
                    host.Prompt.Log,
                    e =>
                        e.Kind == "Error"
                        && e.Text.StartsWith(
                            "保存できませんでした",
                            System.StringComparison.Ordinal
                        )
                );
            }
            finally
            {
                // TempDir の再帰削除が ReadOnly 属性で失敗するのを避け、テスト成否に関わらず属性を戻す
                // (必須の後始末=既存 Save_ReadOnlyDocument_WriteFailure_StillRestoresReadOnly と同旨)。
                File2.SetAttributes(path, System.IO.FileAttributes.Normal);
            }
        });

    // ===== CSV-L-5: _prompt.Error/Warn に生 path を載せる導線を SanitizeForDisplay で無害化 =====
    //
    // 攻撃 path (U+202E RLO / 改行 / 500 文字超) が _prompt へそのまま流れると、
    //   - RLO 反転で拡張子スプーフィング (evil-{RLO}gpj.exe が evil-exe.jpg 風に表示)
    //   - CR/LF で警告本文が複数行に化けて偽の追加情報を差し込める
    //   - 巨大 path で MessageBox の視認性が破壊される
    // という 3 系のスプーフィング/UX 破壊が可能。SanitizeForDisplay.OneLine(path, 200) で
    // BiDi/format 系を drop・改行を空白へ畳み・末尾を "…" で切詰め、prompt に載る前段で
    // 無害化する。U+202E は UnicodeCategory.Format のため culture-sensitive な Contains で
    // 常に "見つかる" 側に倒れるので、以下は StringComparison.Ordinal を明示する
    // (RestoreDialogTests のクラス header と同旨)。

    [Fact]
    public void RestoreFromBackup_SanitizesRloOverride_InOriginalPathWarn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // System32 配下=OriginalPathValidator が Rejected を返して _prompt.Warn 経路に入る。
            // path に U+202E RLO を混入し、警告本文に生の RLO が載らないことを固定する。
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "evil-‮txt.hosts"
            );
            var rec = new BackupRecord(
                "id-attack-rlo",
                OriginalPath: attackPath,
                UntitledNumber: 0,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "poison",
                TimestampUtc: DateTime.UtcNow
            );

            _ = host.File.RestoreFromBackup(rec);

            var warn = Assert.Single(host.Prompt.Log, e => e.Kind == "Warn");
            Assert.DoesNotContain("‮", warn.Text, StringComparison.Ordinal);
            // 警告本文の骨格 (案内文 + "元パス:" ラベル + 改行区切り) は保持=OneLine は path 部分のみ。
            Assert.Contains("バックアップの元パスが無効なため", warn.Text);
            Assert.Contains("元パス:", warn.Text);
            Assert.Contains("\n\n元パス:", warn.Text); // path 部分だけを OneLine=文全体の改行は残す
        });

    [Fact]
    public void LoadInto_SanitizesRloOverride_InUnreachableRemoteErrorPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Probe.Result = false;
            // UNC 先頭 `\\` は UncPathDetector.IsUnc→IsRemote=true→プローブ経路へ乗る。
            // path に U+202E RLO を混入し、"ネットワークパスに到達できません: ..." に
            // 生の RLO が載らないことを固定する (拡張子スプーフィング防御)。
            var attackPath = @"\\server\share\evil-" + "‮" + "txt.exe";

            var doc = host.File.TryOpenOrActivate(attackPath);

            Assert.Null(doc);
            var err = Assert.Single(host.Prompt.Log, e => e.Kind == "Error");
            Assert.StartsWith(
                "ネットワークパスに到達できません",
                err.Text,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("‮", err.Text, StringComparison.Ordinal);
        });

    [Fact]
    public void LoadInto_SanitizesCrlf_InUnreachableRemoteErrorPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Probe.Result = false;
            // path に CR/LF を混入し、prompt 本文が複数行に化けないことを固定する
            // (OneLine が CR/LF を単一空白へ畳み込む=1 行整合の維持)。
            var attackPath = "\\\\server\\share\\evil\r\ninjected.txt";

            var doc = host.File.TryOpenOrActivate(attackPath);

            Assert.Null(doc);
            var err = Assert.Single(host.Prompt.Log, e => e.Kind == "Error");
            Assert.DoesNotContain("\r", err.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", err.Text, StringComparison.Ordinal);
            // 1 行として存在(改行崩壊しない=Split('\n') の要素は 1 個)
            Assert.Single(err.Text.Split('\n'));
        });

    [Fact]
    public void LoadInto_TruncatesLongPath_InUnreachableRemoteErrorPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Probe.Result = false;
            // 500 文字級の UNC path。OneLine(path, 200) が 200 code unit を超える path を
            // "…"(U+2026)で切詰めることを固定する (MessageBox 視認性破壊の防御)。
            var longSegment = new string('a', 500);
            var attackPath = @"\\server\share\" + longSegment + ".txt";

            var doc = host.File.TryOpenOrActivate(attackPath);

            Assert.Null(doc);
            var err = Assert.Single(host.Prompt.Log, e => e.Kind == "Error");
            // 切詰めマーカ "…" が末尾に出現=200 code unit を超えた path が省略された。
            Assert.Contains("…", err.Text, StringComparison.Ordinal);
            // 元 path 全体は載らない (500 文字 'a' 連続が丸ごとは入らない)。
            Assert.DoesNotContain(new string('a', 500), err.Text, StringComparison.Ordinal);
        });

    /// <summary>
    /// 保存側(A-7 / A-19)が足した文言も同じ無害化を通る。本ブランチが追加した prompt は 4 本
    /// (重複タブ 2 種・上書き確認・「パスが正しくありません」)で、いずれも
    /// <c>SanitizeForDisplay.OneLine(path, 200)</c> を通しているが**どれにも網が無く**、
    /// 生 path へ戻す変異が全緑で通った(最終品質パス m-5)。代表 1 本で idiom を pin する。
    /// 選んだのは SaveAs の重複タブエラー: 実ファイルもプローブも要らず、
    /// 在席タブの <c>State.Path</c>(= 復元 BackupRecord 由来 = 攻撃者 JSON 起源になりうる面。
    /// 申し送り S-6)がそのまま文言に載る導線だから。
    /// U+202E は <c>UnicodeCategory.Format</c> のため culture-sensitive な Contains では
    /// 常に「見つかる」側へ倒れる。上の 3 本と同じく <c>StringComparison.Ordinal</c> を明示する。
    /// <para>
    /// メソッド名は <c>SaveAs_</c> で始まるが、射程は保存側だけではない: Task 3 で保存側の
    /// TimedOut 文言、Task 4 で<b>開く側</b>の TimedOut 文言を足してある。名前を変えないのは
    /// 「OneLine の idiom は代表 1 本で pin する」(#47 最終品質パス m-5 の受容)を続けるため。
    /// </para>
    /// </summary>
    [Fact]
    public void SaveAs_SanitizesRloOverride_InDuplicateTabErrorPrompt() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 拡張子スプーフィング: evil-{RLO}txt.exe が "evil-exe.txt" 風に表示される。
            const string attackPath = "\\\\server\\share\\evil-‮txt.exe";

            var occupant = host.Docs.CreateNew(); // 復元で生まれた在席タブ相当
            occupant.State.Path = attackPath;

            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "abc";
            host.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(attackPath, 65001, false, LineEnding.Crlf)
            );

            Assert.False(host.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            var err = Assert.Single(host.Prompt.Log, e => e.Kind == "Error");
            Assert.StartsWith(
                "このファイルは別のタブで開いています",
                err.Text,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain("‮", err.Text, StringComparison.Ordinal);

            // 脆弱-m-2(Task 3): 代表テストの射程に、境界付き正規化が足した TimedOut 文言を
            // 1 つ加える。新しい代表を作らないのは #47 最終品質パス m-5 の受容
            // (OneLine の idiom は代表 1 本で pin する)を継続するため。TimedOut を選ぶのは、
            // 「不達の共有」= 復元 JSON 由来の UNC が seed に載る導線そのものだから。
            using var host2 = new Host();
            var doc2 = host2.Docs.CreateNew();
            doc2.Editor.Text = "abc";
            host2.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );
            host2.Dialogs.SaveAsQueue.Enqueue(
                new SaveAsResult(attackPath, 65001, false, LineEnding.Crlf)
            );

            Assert.False(host2.File.SaveAs()); // 2 回目はキュー枯渇=キャンセル

            var warn = Assert.Single(host2.Prompt.Log, e => e.Kind == "Warn");
            Assert.Contains("到達できません", warn.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("‮", warn.Text, StringComparison.Ordinal);

            // 脆弱-I-1(Task 4): **開く側**の TimedOut 文言も同じ射程に入れる。生 path へ戻す
            // 変異が全緑で生存していた。開く側は保存側より危険で、抑止スコープの外に
            // 攻撃者がファイル名を決められる経路が実在する: grep ジャンプ(MainForm の
            // OpenAndSelect)と「最近のファイル」(settings.json 由来)。どちらも
            // WithLoadErrorPromptSuppressed の外なので、不達共有上の evil-{RLO}txt.exe が
            // そのままダイアログに載る。代表 1 本の原則は維持し、新しい代表は作らない。
            using var host3 = new Host();
            host3.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.TimedOut,
                string.Empty
            );

            Assert.Null(host3.File.TryOpenOrActivate(attackPath));

            var openErr = Assert.Single(host3.Prompt.Log, e => e.Kind == "Error");
            Assert.Contains("到達できません", openErr.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("‮", openErr.Text, StringComparison.Ordinal);
        });

    // ===== Task 4: LoadInto エラーダイアログ抑止 seam(復元経路 Task 5 用) =====

    [Fact]
    public void LoadInto_SuppressErrorPrompt_SwallowsErrorDialog_ButStillReturnsFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string missing = tmp.File("no-such-file.txt");

            // 通常経路: エラーダイアログが 1 個出る
            host.File.TryOpenOrActivate(missing);
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");

            host.Prompt.Log.Clear();

            // 抑止 ON: ダイアログは出ないが失敗自体は伝播する
            host.File.WithLoadErrorPromptSuppressed(() =>
            {
                var result = host.File.TryOpenOrActivate(missing);
                Assert.Null(result);
            });
            Assert.DoesNotContain(host.Prompt.Log, e => e.Kind == "Error");

            // 抑止解除後: 再びダイアログが出る(finally での復元確認)
            host.File.TryOpenOrActivate(missing);
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
        });

    /// <summary>
    /// 脆弱-m-3(Task 4)。<c>try/finally</c> を外して逐次実行にする変異が全緑で生存していた。
    /// 既存メソッドだが、本タスクが<b>新しい失敗経路をここに通す</b>ので網を足す。
    /// <para>
    /// <c>action()</c> が投げたときにフラグが立ちっぱなしになると、以後<b>プロセスの寿命の間</b>
    /// 「開けませんでした」「ネットワークパスに到達できません」「置換文字の警告」、そして本タスクが
    /// 足した「到達できません / パスが正しくありません」まで<b>無言で消える</b>
    /// (利用者は開けない理由を一切知らされない)。<c>_suppressRegisterRecent</c> 側が
    /// 立ちっぱなしなら「最近のファイル」も更新されなくなるので、2 つとも戻ることを見る。
    /// 例外型は catch フィルタに一致しない型を選ぶ(<see cref="InvalidOperationException"/>)=
    /// 途中で握られず <c>finally</c> だけが仕事をする状況にするため。
    /// </para>
    /// </summary>
    [Fact]
    public void WithLoadErrorPromptSuppressed_RestoresBothFlags_WhenActionThrows() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string missing = tmp.File("no-such-file.txt");
            string existing = tmp.File("a.txt");
            File2.WriteAllText(existing, "abc");

            Assert.Throws<InvalidOperationException>(() =>
                host.File.WithLoadErrorPromptSuppressed(() =>
                    throw new InvalidOperationException("boom")
                )
            );

            // (a) 失敗ダイアログが戻る
            Assert.Null(host.File.TryOpenOrActivate(missing));
            Assert.Contains(host.Prompt.Log, e => e.Kind == "Error");
            // (b) RecentFiles の登録も戻る
            Assert.NotNull(host.File.TryOpenOrActivate(existing));
            Assert.Equal(existing, host.Settings.RecentFiles[0]);
        });

    // ===== 統合復元 Task 5: RestoreSession(hot exit 統合・設計 2026-07-23 §3.3/§4) =====

    /// <summary>正規形(GUID N・lowercase 32 桁 hex)のバックアップ Id を生成する。</summary>
    private static string NewBackupId() => Guid.NewGuid().ToString("N");

    private static SessionLayoutRecord LayoutRec(
        string? path = null,
        int untitledNumber = 0,
        string? backupId = null,
        bool isActive = false,
        int caretLine = 0,
        int caretColumn = 0,
        int lineEnding = 0
    ) => new(path, untitledNumber, backupId, isActive, caretLine, caretColumn, lineEnding);

    private static SessionLayout Layout(params SessionLayoutRecord[] tabs) =>
        new(new List<SessionLayoutRecord>(tabs), DateTime.UtcNow);

    private static BackupRecord Backup(
        string id,
        string? originalPath = null,
        int untitledNumber = 0,
        int codePage = 65001,
        bool hasBom = false,
        int lineEndingId = 0,
        string? content = "",
        DateTime? timestampUtc = null
    ) =>
        new(
            id,
            originalPath,
            untitledNumber,
            codePage,
            hasBom,
            lineEndingId,
            content,
            timestampUtc ?? DateTime.UtcNow
        );

    [Fact]
    public void RestoreSession_DirtyPathRecord_RestoresContentEncodingCaret_ModifiedTrue() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("a.txt");
            File2.WriteAllText(p, "on disk");
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            // E12 対称: bk.OriginalPath は rec.Path と食い違わせ、採用されるのが rec.Path 側で
            // あること(bk 側パスを信用しない)を実効検証する。
            var backups = new[]
            {
                Backup(
                    id,
                    originalPath: @"C:\untrusted\other.txt",
                    codePage: 65001,
                    hasBom: true,
                    lineEndingId: (int)LineEnding.Lf,
                    content: "in memory dirty"
                ),
            };
            var layout = Layout(
                LayoutRec(path: p, backupId: id, isActive: true, caretLine: 0, caretColumn: 3)
            );
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            var failed = host.File.RestoreSession(
                layout,
                backups,
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Empty(failed);
            Assert.Equal(1, host.Docs.Count); // initialEmpty は閉じる
            var doc = host.Docs.Active!;
            Assert.Equal(p, doc.State.Path); // rec.Path 側を採用(bk.OriginalPath ではない)
            Assert.Equal("in memory dirty", doc.Editor.SnapshotText); // disk ではなくバックアップ本文
            Assert.True(doc.Editor.Modified);
            Assert.Equal(65001, doc.State.Encoding.CodePage);
            Assert.True(doc.State.HasBom);
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
            Assert.Equal(3, doc.Editor.GetColumn(doc.Editor.CurrentPosition));
            var a = Assert.Single(adopted);
            Assert.Same(doc, a.Doc);
            Assert.Same(backups[0], a.Rec); // adopt はバックアップ由来の復元にのみ発火
            Assert.Empty(host.Prompt.Log); // silent 経路=ダイアログなし
        });

    [Fact]
    public void RestoreSession_CleanPathRecord_OpensFromDisk_SetsCaret_NoRecentPollution() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("clean.txt");
            File2.WriteAllText(p, "disk content");
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(LayoutRec(path: p, isActive: true, caretColumn: 2));
            var adopted = new List<(Document, BackupRecord)>();

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Empty(failed);
            Assert.Equal(1, host.Docs.Count);
            var doc = host.Docs.Active!;
            Assert.Equal(p, doc.State.Path);
            Assert.Equal("disk content", doc.Editor.SnapshotText);
            Assert.False(doc.Editor.Modified);
            Assert.Equal(2, doc.Editor.GetColumn(doc.Editor.CurrentPosition));
            Assert.Empty(adopted); // disk 再オープンは adopt 対象外
            Assert.Empty(host.Settings.RecentFiles); // 復元経路は RecentFiles を汚さない
        });

    [Fact]
    public void RestoreSession_UntitledDirty_RestoresModifiedTrue_AndAdvancesSeq() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            var backups = new[]
            {
                Backup(id, untitledNumber: 5, lineEndingId: (int)LineEnding.Lf, content: "unsaved"),
            };
            var layout = Layout(
                LayoutRec(
                    untitledNumber: 5,
                    backupId: id,
                    isActive: true,
                    caretColumn: 4,
                    lineEnding: (int)LineEnding.Lf
                )
            );

            host.File.RestoreSession(layout, backups, initialEmpty, adoptRestored: null);

            Assert.Equal(1, host.Docs.Count);
            var doc = host.Docs.Active!;
            Assert.Null(doc.State.Path);
            Assert.Equal(5, doc.State.UntitledNumber);
            Assert.Equal("unsaved", doc.Editor.SnapshotText);
            Assert.True(doc.Editor.Modified); // 統合後は WasModified 廃止=常に dirty 復元
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
            Assert.Equal(4, doc.Editor.GetColumn(doc.Editor.CurrentPosition));

            host.File.NewFile(); // 連番カウンタは既存最大値の先へ進む(衝突しない)
            Assert.Equal(6, host.Docs.Active!.State.UntitledNumber);
        });

    [Fact]
    public void RestoreSession_UntitledEmptyFrame_RestoresLineEnding_ModifiedFalse() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.Settings.DefaultLineEnding = (int)LineEnding.Crlf; // 既定=CRLF(非既定値の反映を観測)
            var initialEmpty = host.Docs.CreateNew();
            // BackupId=null の無題=「終了時に空だったタブ」の枠を復元する(旧 E4 の skip とは異なる新意味論)
            var layout = Layout(
                LayoutRec(untitledNumber: 3, isActive: true, lineEnding: (int)LineEnding.Lf)
            );

            host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Equal(1, host.Docs.Count); // 空枠でも openedCount に数え initialEmpty を閉じる
            var doc = host.Docs.Active!;
            Assert.Null(doc.State.Path);
            Assert.Equal(3, doc.State.UntitledNumber);
            Assert.Equal("", doc.Editor.SnapshotText);
            Assert.False(doc.Editor.Modified); // fresh バッファ=クリーン
            Assert.Equal(LineEnding.Lf, doc.State.LineEnding);
        });

    [Fact]
    public void RestoreSession_DirtyPathRecord_BackupMissing_DemotesToDiskReopen() =>
        Sta.Run(() =>
        {
            // E9': BackupId 参照はあるが record 欠落=編集は失われている → disk 再オープンへ demote
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("a.txt");
            File2.WriteAllText(p, "on disk");
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(LayoutRec(path: p, backupId: NewBackupId(), isActive: true));

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Empty(failed);
            var doc = host.Docs.Active!;
            Assert.Equal(p, doc.State.Path);
            Assert.Equal("on disk", doc.Editor.SnapshotText);
            Assert.False(doc.Editor.Modified);
        });

    [Fact]
    public void RestoreSession_UntitledRecord_BackupMissing_SkipsRecord() =>
        Sta.Run(() =>
        {
            // E4': 無題の編集内容が欠落 → 誤解を招く空枠を作らず skip
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(
                LayoutRec(untitledNumber: 1, backupId: NewBackupId(), isActive: true)
            );

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Empty(failed);
            Assert.Equal(1, host.Docs.Count); // 何も復元されない=initialEmpty を保持
            Assert.Same(initialEmpty, host.Docs.Documents[0]);
        });

    [Fact]
    public void RestoreSession_PathOnlyBackup_DemotesToDiskReopen_AndNotRevivedInExtras() =>
        Sta.Run(() =>
        {
            // E11: Content=null(>32M path-only)を silent で「空 dirty+実パス」に載せない
            // (Ctrl+S 切り詰め事故の遮断)。consumed 記帳により extras での復活も防ぐ。
            using var host = new Host();
            using var tmp = new TempDir();
            string p1 = tmp.File("layout-side.txt");
            string p2 = tmp.File("backup-side.txt");
            File2.WriteAllText(p1, "P1");
            File2.WriteAllText(p2, "P2");
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            // bk.OriginalPath(p2)は rec.Path(p1)と食い違わせる: consumed 記帳が漏れると
            // extras が p2 を開いて Docs.Count==2 になる=記帳の実効検証(同一パスだと fast-path
            // activate で差が出ない)。
            var backups = new[] { Backup(id, originalPath: p2, content: null) };
            var layout = Layout(LayoutRec(path: p1, backupId: id, isActive: true));
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            var failed = host.File.RestoreSession(
                layout,
                backups,
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Empty(failed);
            Assert.Equal(1, host.Docs.Count); // p1 のみ(p2 は consumed 済で extras に落ちない)
            var doc = host.Docs.Active!;
            Assert.Equal(p1, doc.State.Path);
            Assert.Equal("P1", doc.Editor.SnapshotText); // disk 再読込(空 dirty ではない)
            Assert.False(doc.Editor.Modified);
            Assert.Empty(host.Prompt.Log);
            // 最終品質パス I-1: 消費した path-only record は adopt される(→ clean 検出で次 tick
            // Delete=旧 session dir にレコードを残置させずゾンビ復活を根治)。
            var a = Assert.Single(adopted);
            Assert.Same(doc, a.Doc);
            Assert.Same(backups[0], a.Rec);
        });

    [Fact]
    public void RestoreSession_PathOnlyBackup_UnnormalizedRecordPath_ReusesTab_AndDoesNotAdopt() =>
        Sta.Run(() =>
        {
            // Issue #48 Task 5 の門番: 「fast-path activate(既存タブ)には adopt しない」
            // (= Id 上書きで既存 adopt を壊し別のゾンビを作らない)。
            // rec.Path はレイアウト JSON 由来で、正規化されている保証が**無い**
            // (LegacySessionConverter が旧 LastSessionSnapshot の Path を素通しで載せる)。
            // Task 4 で TryOpenOrActivate が入口で正規化するようになり、Task 5 で FindByPath が
            // ForNormalized 照合(区切り差を吸収しない)になったため、素朴に
            // _docs.FindByPath(rec.Path) を打つと同じファイルなのに「既存タブ無し」へ倒れる。
            // 上の姉妹テスト(既存タブ無し=adopt する)と対で、門番の両側を固定する。
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("shared.txt");
            File2.WriteAllText(p, "on disk");
            string unnormalized = p.Replace('\\', '/'); // 同じファイルの非正規化綴り
            Assert.NotEqual(p, unnormalized); // fixture の前提(綴りが実際に食い違う)
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            var backups = new[] { Backup(id, originalPath: p, content: null) }; // path-only=E11 demote
            // 1 本目(正規化済み・BackupId なし)で p のタブを作り、2 本目(非正規化・path-only)が
            // その既存タブを fast-path activate で再利用する形にする。
            var layout = Layout(
                LayoutRec(path: p),
                LayoutRec(path: unnormalized, backupId: id, isActive: true)
            );
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            var failed = host.File.RestoreSession(
                layout,
                backups,
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Empty(failed);
            Assert.Equal(1, host.Docs.Count); // 2 レコードが同じ 1 タブへ畳まれた=fast-path を通った
            Assert.Equal(p, host.Docs.Documents[0].State.Path); // State.Path は正規化済み(§3.1)
            // ★本テストの核。判定が「既存タブ無し」へ倒れるとここに 1 件入る。
            Assert.Empty(adopted);
        });

    [Fact]
    public void RestoreSession_DirtyPathRecord_InvalidPath_FallsBackToUntitled_NoDialog() =>
        Sta.Run(() =>
        {
            // E12: rec.Path 側を OriginalPathValidator で検証し、NG は無題フォールバック
            // (HIGH-2 踏襲)。silent 経路のため Warn ダイアログは出さない(trace のみ)。
            using var host = new Host();
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"
            );
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            var backups = new[] { Backup(id, originalPath: attackPath, content: "poison") };
            var layout = Layout(LayoutRec(path: attackPath, backupId: id, isActive: true));

            var failed = host.File.RestoreSession(
                layout,
                backups,
                initialEmpty,
                adoptRestored: null
            );

            Assert.Empty(failed);
            var doc = host.Docs.Active!;
            Assert.Null(doc.State.Path); // サイレントに target を上書きさせない
            Assert.True(doc.State.UntitledNumber > 0);
            Assert.Equal("poison", doc.Editor.SnapshotText); // 本文は保持=SaveAs で救出可能
            Assert.True(doc.Editor.Modified);
            Assert.Empty(host.Prompt.Log); // silent 経路=RestoreFromBackup 系の Warn も出ない
        });

    [Fact]
    public void RestoreSession_ExtrasRestored_NewestFirst_AdoptReceivesDocAndRecord() =>
        Sta.Run(() =>
        {
            // extras=レイアウト外バックアップ(クラッシュ直前タブ・他インスタンス遺物・「あとで」孤児)。
            // TimestampUtc 降順で「拾って開く」+adopt callback に (doc, rec) が渡る。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            var older = Backup(
                NewBackupId(),
                untitledNumber: 1,
                content: "A",
                timestampUtc: new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc)
            );
            var newer = Backup(
                NewBackupId(),
                untitledNumber: 2,
                content: "B",
                timestampUtc: new DateTime(2026, 7, 23, 2, 0, 0, DateTimeKind.Utc)
            );
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            host.File.RestoreSession(
                Layout(), // 空レイアウト=全件 extras
                new[] { older, newer },
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Equal(2, host.Docs.Count); // initialEmpty は閉じる
            Assert.Equal("B", host.Docs.Documents[0].Editor.SnapshotText); // 新しい方が先
            Assert.Equal("A", host.Docs.Documents[1].Editor.SnapshotText);
            Assert.Equal(2, adopted.Count);
            Assert.Same(host.Docs.Documents[0], adopted[0].Doc);
            Assert.Same(newer, adopted[0].Rec);
            Assert.Same(host.Docs.Documents[1], adopted[1].Doc);
            Assert.Same(older, adopted[1].Rec);
            Assert.Empty(host.Prompt.Log);
        });

    [Fact]
    public void RestoreSession_ExtrasPathOnly_ValidPath_OpensFromDisk_AdoptsForCleanup() =>
        Sta.Run(() =>
        {
            // extras の path-only(Content=null)は E11 と同方針: パス正当時のみ disk 再オープン。
            // 最終品質パス I-1: 消費 record は adopt する(doc は clean なので次 Reconcile が
            // Delete=レイアウト外 Id の毎起動ゾンビ復活を根治)。
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("big.txt");
            File2.WriteAllText(p, "big file on disk");
            var initialEmpty = host.Docs.CreateNew();
            var rec = Backup(NewBackupId(), originalPath: p, content: null);
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            host.File.RestoreSession(
                Layout(),
                new[] { rec },
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Equal(1, host.Docs.Count);
            var doc = host.Docs.Documents[0];
            // TryOpenOrActivate は OriginalPathValidator.Check の normalized を受ける
            Assert.Equal(System.IO.Path.GetFullPath(p), doc.State.Path);
            Assert.Equal("big file on disk", doc.Editor.SnapshotText);
            Assert.False(doc.Editor.Modified);
            var a = Assert.Single(adopted);
            Assert.Same(doc, a.Doc);
            Assert.Same(rec, a.Rec);
        });

    [Fact]
    public void RestoreSession_ExtrasPathOnly_AlreadyOpenTab_ReusesTab_AndDoesNotAdopt() =>
        Sta.Run(() =>
        {
            // extras 側にも同じ門番がある(fast-path activate には adopt しない)。
            // ここは上のレイアウト側と対で、Task 5 で「既存タブを再利用したか」の判定機構を
            // TryOpenOrActivate 自身へ移した後も両側が生きていることを固定する。
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("shared.txt");
            File2.WriteAllText(p, "on disk");
            var initialEmpty = host.Docs.CreateNew();
            // レイアウトが先に p を開き、レイアウト外(extras)の path-only が同じ p を指す。
            var extra = Backup(NewBackupId(), originalPath: p, content: null);
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            host.File.RestoreSession(
                Layout(LayoutRec(path: p, isActive: true)),
                new[] { extra },
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Equal(1, host.Docs.Count); // extras は既存タブを再利用(新タブを作らない)
            Assert.Equal(p, host.Docs.Documents[0].State.Path);
            Assert.Empty(adopted); // ★既存タブの adopt を上書きしない
        });

    /// <summary>
    /// Task 5 レビュー m-1 の網。extras 側の門番を「呼出前の <c>FindByPath</c>」から
    /// 「<c>TryOpenOrActivate</c> 自身に答えさせる」機構へ揃えた<b>選択</b>を固定する。
    /// 上の姉妹は<b>挙動</b>を固定するが<b>機構</b>は固定しない: extras が渡す値は
    /// <c>OriginalPathValidator.Check</c> の出力で、現在の実装では seam の正規化結果と
    /// 綴りまで一致してしまうため、旧形へ戻す変異が全緑のまま生存する(実測で確認)。
    /// <para>
    /// ここでは seam(<c>IReachabilityProbe</c>)の応答を固定して「2 つの正規化器の出力が
    /// 綴りまで同じとは限らない」状況を作る。Fake は本番実装の性質の証人にはならないが、
    /// <b>門番がその一致に依存していないこと</b>は Fake でしか作れない(本番の 2 本は
    /// どちらも <c>GetFullPath</c> 由来なので綴りが割れる入力を作れない)。綴り差に区切り
    /// (<c>/</c>)を使うのは、<c>FindByPath</c> が <c>ToLowerInvariant</c> のみになった今
    /// 大小差では吸収されてしまい変異が生存するため。
    /// </para>
    /// </summary>
    [Fact]
    public void RestoreSession_ExtrasPathOnly_ReusesTab_EvenWhenNormalizerSpellingsDiffer() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("shared.txt");
            File2.WriteAllText(p, "on disk");
            string canonical = System.IO.Path.GetFullPath(p); // OriginalPathValidator.Check の綴り
            host.Probe.NormalizeResult = new PathNormalizeResult(
                PathNormalizeStatus.Ok,
                canonical.Replace('\\', '/') // seam だけが別綴りを返す(Windows は / も受ける)
            );
            var initialEmpty = host.Docs.CreateNew();
            var extra = Backup(NewBackupId(), originalPath: p, content: null);
            var adopted = new List<(Document Doc, BackupRecord Rec)>();

            host.File.RestoreSession(
                Layout(LayoutRec(path: p, isActive: true)),
                new[] { extra },
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Equal(1, host.Docs.Count); // extras は既存タブを再利用する
            // ★旧形(呼出前の FindByPath(canonical))だと「既存タブ無し」へ倒れて adopt が走る
            Assert.Empty(adopted);
        });

    /// <summary>
    /// Issue #48 Task 8: 復元経路の<b>回数</b>の網(Ctrl+S 側の姉妹は
    /// <see cref="SaveDocument_ExistingPath_DoesNotNormalizeAtAll"/>)。
    /// レイアウト由来・extras 由来のどちらも、再オープン 1 レコードあたり境界付き正規化は
    /// <b>1 回</b>で、2 回にはならない。
    /// <para>
    /// <b>なぜ価値があるか</b>: 上の 2 本(<c>ReusesTab</c> 姉妹)は「門番が正しく判定するか」
    /// という<b>正しさ</b>の網で、コストは見ていない。門番を「呼出前に自前で正規化してから
    /// <c>FindByPath</c> と <c>TryOpenOrActivate</c> の両方へ渡す」形(設計書の案 1)に
    /// 書き換えても、判定結果は同じなので <c>ReusesTab</c> は全緑のまま通る。しかし
    /// 1 レコードあたりの正規化が 2 回になり、S-15 の実害
    /// (不達共有で 1 回あたり最大 <c>NormalizeTimeout</c> 待ち)が起動時に<b>倍</b>掛かる。
    /// 復元は起動時に無人で走り、レコード数は攻撃者制御の JSON で増やせるため、
    /// 1 レコードあたりの係数がそのまま「起動できない時間」になる。
    /// </para>
    /// <para>
    /// <b>この網が言っていないこと</b>: extras の path-only 枝は
    /// <c>OriginalPathValidator.Check</c> の中で無境界の <c>Path.GetFullPath</c> を
    /// なお 1 回打つ(監査 A-16・設計書 §6 で明示的に対象外・申し送り S-1)。
    /// ここで固定するのは<b>seam の呼び出し回数</b>であって「無境界がゼロ」ではない。
    /// </para>
    /// </summary>
    [Fact]
    public void RestoreSession_NormalizesOncePerReopenedRecord() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string fromLayoutA = tmp.File("layout-a.txt");
            string fromLayoutB = tmp.File("layout-b.txt");
            string fromExtras = tmp.File("extra.txt");
            foreach (string p in new[] { fromLayoutA, fromLayoutB, fromExtras })
                File2.WriteAllText(p, "on disk");
            var initialEmpty = host.Docs.CreateNew();

            host.File.RestoreSession(
                Layout(LayoutRec(path: fromLayoutA, isActive: true), LayoutRec(path: fromLayoutB)),
                // extras = レイアウトが参照しないバックアップ。Content=null(path-only)は
                // ディスク再オープンへ demote される=レイアウト側と同じ再オープン経路を通る。
                new[] { Backup(NewBackupId(), originalPath: fromExtras, content: null) },
                initialEmpty,
                adoptRestored: null
            );

            // 3 レコードとも実際に開けている(開けていなければ回数の assert が空振りする)。
            Assert.Equal(3, host.Docs.Count);
            Assert.Equal(3, host.Probe.NormalizeCallCount); // ★1 レコード 1 回。案 1 なら 6
        });

    [Fact]
    public void RestoreSession_ExtrasPathOnly_Untitled_IsSkipped() =>
        Sta.Run(() =>
        {
            // 無題の path-only は開く根拠(パスも本文も)が無い → skip+トレースのみ
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();

            host.File.RestoreSession(
                Layout(),
                new[] { Backup(NewBackupId(), originalPath: null, content: null) },
                initialEmpty,
                adoptRestored: null
            );

            Assert.Equal(1, host.Docs.Count);
            Assert.Same(initialEmpty, host.Docs.Documents[0]); // 何も開かれない=initialEmpty 保持
            Assert.Empty(host.Prompt.Log);
        });

    [Fact]
    public void RestoreSession_ExtrasInvalidOriginalPath_FallsBackToUntitled_NoDialog() =>
        Sta.Run(() =>
        {
            // extras の content あり+不正 OriginalPath は既存 RestoreFromBackup の HIGH-2 検証で
            // 無題フォールバックする。silent 経路では invalid-path Warn を抑止する(本 Task の
            // _suppressLoadErrorPrompt ガード追加)。ダイアログ経路(OfferRestoreOnStartup 直呼び)の
            // Warn は既存テスト RestoreFromBackup_FallsBackToUntitled_WhenPathIsRejected が固定する。
            using var host = new Host();
            var attackPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"
            );
            var initialEmpty = host.Docs.CreateNew();

            host.File.RestoreSession(
                Layout(),
                new[] { Backup(NewBackupId(), originalPath: attackPath, content: "rescued") },
                initialEmpty,
                adoptRestored: null
            );

            Assert.Equal(1, host.Docs.Count);
            var doc = host.Docs.Documents[0];
            Assert.Null(doc.State.Path);
            Assert.Equal("rescued", doc.Editor.SnapshotText);
            Assert.True(doc.Editor.Modified);
            Assert.Empty(host.Prompt.Log); // silent 経路=Warn を出さない
        });

    [Fact]
    public void RestoreSession_FailedPathsAggregated_NoIndividualDialog() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string ok1 = tmp.File("ok1.txt");
            string ok2 = tmp.File("ok2.txt");
            File2.WriteAllText(ok1, "OK1");
            File2.WriteAllText(ok2, "OK2");
            string missing = tmp.File("missing.txt");
            var initialEmpty = host.Docs.CreateNew();
            // fixture 順は [ok, missing, ok] — 中央に失敗を置き prefix/suffix 除外を確認(Stage 8 教訓)
            var layout = Layout(
                LayoutRec(path: ok1),
                LayoutRec(path: missing),
                LayoutRec(path: ok2, isActive: true)
            );

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Single(failed);
            Assert.Equal(missing, failed[0]);
            Assert.Empty(host.Prompt.Log); // per-file ダイアログなし(集約は呼び出し側の責務)
            Assert.Equal(2, host.Docs.Count); // ok1 + ok2, initialEmpty は閉じる
        });

    [Fact]
    public void RestoreSession_IsActiveRecord_ActivatesRestoredDoc() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            string p1 = tmp.File("a.txt");
            string p2 = tmp.File("b.txt");
            File2.WriteAllText(p1, "AAA");
            File2.WriteAllText(p2, "BBB");
            var initialEmpty = host.Docs.CreateNew();
            // IsActive を非先頭に置き「index 0 が既定でアクティブになる」実装と区別する
            var layout = Layout(LayoutRec(path: p1), LayoutRec(path: p2, isActive: true));

            host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Equal(p2, host.Docs.Active!.State.Path);
        });

    [Fact]
    public void RestoreSession_NothingRestored_KeepsInitialEmpty() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            using var tmp = new TempDir();
            var initialEmpty = host.Docs.CreateNew();
            // 全レコードが復元不成立: 欠落パス+バックアップ欠落の無題(E4' skip)
            var layout = Layout(
                LayoutRec(path: tmp.File("missing.txt")),
                LayoutRec(untitledNumber: 1, backupId: NewBackupId())
            );

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Single(failed);
            Assert.Equal(1, host.Docs.Count);
            Assert.Same(initialEmpty, host.Docs.Documents[0]); // openedCount=0 → initialEmpty 保持
        });

    [Fact]
    public void RestoreSession_LayoutNull_ExtrasStillRestored() =>
        Sta.Run(() =>
        {
            // E5': session-state.json 破損/欠落(layout=null)でも extras 復元は実施
            // =「レイアウトだけ失いタブ順が崩れる」に留めて内容は守る。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            var adopted = new List<(Document, BackupRecord)>();

            host.File.RestoreSession(
                layout: null,
                new[] { Backup(NewBackupId(), untitledNumber: 1, content: "survivor") },
                initialEmpty,
                (d, r) => adopted.Add((d, r))
            );

            Assert.Equal(1, host.Docs.Count); // initialEmpty は閉じる
            Assert.Equal("survivor", host.Docs.Documents[0].Editor.SnapshotText);
            Assert.True(host.Docs.Documents[0].Editor.Modified);
            Assert.Single(adopted);
        });

    [Fact]
    public void RestoreSession_DuplicateBackupIds_NewestWins() =>
        Sta.Run(() =>
        {
            // 別 session dir の stale record と Id 競合した場合は TimestampUtc が新しい方を採用。
            // 挿入順の両方向(古→新・新→古)で固定し「常に上書き/常に先勝ち」の変異を殺す。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            string id1 = NewBackupId();
            string id2 = NewBackupId();
            var t1 = new DateTime(2026, 7, 23, 1, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 7, 23, 2, 0, 0, DateTimeKind.Utc);
            var backups = new[]
            {
                Backup(id1, untitledNumber: 1, content: "old1", timestampUtc: t1), // 古→新の順
                Backup(id1, untitledNumber: 1, content: "new1", timestampUtc: t2),
                Backup(id2, untitledNumber: 2, content: "new2", timestampUtc: t2), // 新→古の順
                Backup(id2, untitledNumber: 2, content: "old2", timestampUtc: t1),
            };
            var layout = Layout(
                LayoutRec(untitledNumber: 1, backupId: id1, isActive: true),
                LayoutRec(untitledNumber: 2, backupId: id2)
            );

            host.File.RestoreSession(layout, backups, initialEmpty, adoptRestored: null);

            Assert.Equal(2, host.Docs.Count); // 敗者 record は extras に復活しない(byId で消滅)
            Assert.Equal("new1", host.Docs.Documents[0].Editor.SnapshotText);
            Assert.Equal("new2", host.Docs.Documents[1].Editor.SnapshotText);
        });

    [Fact]
    public void RestoreSession_NullAdoptCallback_DoesNotCrash() =>
        Sta.Run(() =>
        {
            // レガシー移行経路は adoptRestored=null で呼ぶ(合成 record は通常 RegisterNew 管理)。
            // layout 由来+extras 由来の両方の adopt 発火点が null-safe であることを固定する。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            var backups = new[]
            {
                Backup(id, untitledNumber: 1, content: "from layout"),
                Backup(NewBackupId(), untitledNumber: 2, content: "extra"),
            };
            var layout = Layout(LayoutRec(untitledNumber: 1, backupId: id, isActive: true));

            host.File.RestoreSession(layout, backups, initialEmpty, adoptRestored: null);

            Assert.Equal(2, host.Docs.Count);
            Assert.Equal("from layout", host.Docs.Active!.Editor.SnapshotText);
        });

    [Fact]
    public void RestoreSession_CleanPathRecord_WithReplacementChar_NoWarnDialog_TracesInstead() =>
        Sta.Run(() =>
        {
            // fixup: 復元経路では置換文字警告ダイアログも抑止する(設計 §3.3 silent 原則)。
            // UTF-8 BOM+不正バイト(0xFF)=auto-detect で UTF-8 確定+U+FFFD 置換が発生する fixture。
            // 通常経路の Warn 発火は既存 Reopen_WithReplacementChar_WarnsToReopen が pin する
            // (=ガード条件の反転変異はそちらで殺す)。抑止時は trace で診断可能性を維持する契約。
            using var host = new Host();
            using var tmp = new TempDir();
            string p = tmp.File("mojibake.txt");
            File2.WriteAllBytes(p, new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a', 0xFF });
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(LayoutRec(path: p, isActive: true));

            // App.Tests は並列実行無効(GlobalUsings)のため、プロセス共有の Trace.Listeners への
            // 一時 listener 追加は他テストと競合しない。
            var sw = new System.IO.StringWriter();
            var listener = new System.Diagnostics.TextWriterTraceListener(sw);
            System.Diagnostics.Trace.Listeners.Add(listener);
            try
            {
                var failed = host.File.RestoreSession(
                    layout,
                    Array.Empty<BackupRecord>(),
                    initialEmpty,
                    adoptRestored: null
                );

                Assert.Empty(failed); // 開くこと自体は成功(警告のみ抑止)
                var doc = host.Docs.Active!;
                Assert.Equal(p, doc.State.Path);
                Assert.Equal("a�", doc.Editor.SnapshotText); // U+FFFD は本文に見える形で残る=silent data loss ではない
                Assert.Empty(host.Prompt.Log); // Warn 含め silent 経路ではダイアログを一切出さない
                System.Diagnostics.Trace.Flush();
                Assert.Contains("restore-replacement-char-detected", sw.ToString());
            }
            finally
            {
                System.Diagnostics.Trace.Listeners.Remove(listener);
                listener.Dispose();
            }
        });

    [Fact]
    public void RestoreSession_ExtrasOverMaxTabs_CappedAndTraced() =>
        Sta.Run(() =>
        {
            // Low fixup: extras の大量植え込み(攻撃 JSON)でも起動時のタブ生成は
            // SessionLayoutStore.MaxTabs 件で打ち切る(session-state.json のレイアウト側
            // 切り詰めと対称の防御・同じ定数を参照=二重定義なし)。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            var backups = new List<BackupRecord>();
            for (int i = 0; i < SessionLayoutStore.MaxTabs + 1; i++)
                backups.Add(Backup(NewBackupId(), untitledNumber: i + 1, content: "x"));

            var sw = new System.IO.StringWriter();
            var listener = new System.Diagnostics.TextWriterTraceListener(sw);
            System.Diagnostics.Trace.Listeners.Add(listener);
            try
            {
                host.File.RestoreSession(Layout(), backups, initialEmpty, adoptRestored: null);

                // MaxTabs 件で打ち切り(復元は成立するため initialEmpty は閉じる)
                Assert.Equal(SessionLayoutStore.MaxTabs, host.Docs.Count);
                System.Diagnostics.Trace.Flush();
                Assert.Contains("restore-extras-capped", sw.ToString());
            }
            finally
            {
                System.Diagnostics.Trace.Listeners.Remove(listener);
                listener.Dispose();
            }
        });

    // ===== Task 7: 旧経路(PR #22)テストからの移植(新経路で未カバーだった意味論) =====

    [Fact]
    public void RestoreSession_CaretPosition_ClampsOutOfRange() =>
        Sta.Run(() =>
        {
            // 旧経路の CaretPosition_ClampsOutOfRange テストの移植: stale/攻撃 JSON の
            // 範囲外 caret はバッファ実範囲へクランプされる(SetCaretByLineColumn の契約を
            // 復元経路で pin。SessionLayoutStore.Normalize は負値 clamp のみで正の範囲外は通す)。
            using var host = new Host();
            var initialEmpty = host.Docs.CreateNew();
            string id = NewBackupId();
            var backups = new[] { Backup(id, untitledNumber: 1, content: "abc") };
            var layout = Layout(
                LayoutRec(
                    untitledNumber: 1,
                    backupId: id,
                    isActive: true,
                    caretLine: 999,
                    caretColumn: 999
                )
            );

            host.File.RestoreSession(layout, backups, initialEmpty, adoptRestored: null);

            var doc = host.Docs.Active!;
            Assert.Equal(0, doc.Editor.CurrentLine);
            Assert.Equal(3, doc.Editor.GetColumn(doc.Editor.CurrentPosition));
        });

    [Fact]
    public void RestoreSession_PathWithEmbeddedNull_OneBadRecord_DoesNotBreakOthers() =>
        Sta.Run(() =>
        {
            // 旧経路の PathWithEmbeddedNull テストの移植: null 文字入り path
            // (File.OpenRead が ArgumentException を投げる形=悪意/破損 JSON 由来)が 1 レコード
            // あっても failedPaths に載るだけで、後続レコードの復元は続行される
            // (per-record 隔離+LoadInto catch フィルタの ArgumentException 吸収)。
            using var host = new Host();
            using var tmp = new TempDir();
            string ok = tmp.File("ok.txt");
            File2.WriteAllText(ok, "OK");
            string bad = "\0/bad-path.txt";
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(LayoutRec(path: bad), LayoutRec(path: ok, isActive: true));

            var failed = host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Contains(bad, failed);
            Assert.Equal(1, host.Docs.Count); // ok のみ・initialEmpty 閉じる
            Assert.Equal(ok, host.Docs.Active!.State.Path);
        });

    [Fact]
    public void RestoreSession_DuplicatePathInLayout_DoesNotPolluteRecentFiles() =>
        Sta.Run(() =>
        {
            // 旧経路の DoesNotPolluteRecentFiles + DuplicatePathInSnap 両テストの移植:
            // 通常ロード経路(1 個目)と FindByPath fast-path(2 個目=重複パス)の両方で
            // _suppressRegisterRecent が尊重される(Task 10 review I-2)。非既定状態
            // (既存 RecentFiles 1 件)から開始し「変化しない」ことを検証する(Stage 6 教訓)。
            using var host = new Host();
            using var tmp = new TempDir();
            string p1 = tmp.File("dup.txt");
            File2.WriteAllText(p1, "DUP");
            host.Settings.RecentFiles = new List<string> { @"C:\pre-existing.txt" };
            var initialEmpty = host.Docs.CreateNew();
            var layout = Layout(LayoutRec(path: p1), LayoutRec(path: p1, isActive: true));

            host.File.RestoreSession(
                layout,
                Array.Empty<BackupRecord>(),
                initialEmpty,
                adoptRestored: null
            );

            Assert.Single(host.Settings.RecentFiles);
            Assert.Equal(@"C:\pre-existing.txt", host.Settings.RecentFiles[0]);
        });
}
