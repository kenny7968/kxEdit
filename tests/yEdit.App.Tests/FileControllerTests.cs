using System.Text;
using yEdit.App.Tests.Fakes;
using yEdit.Core.Backup;
using yEdit.Core.Session;
using yEdit.Core.Settings;
using yEdit.Core.Text;
using Directory = System.IO.Directory;
using File2 = System.IO.File;
using IOException = System.IO.IOException;

namespace yEdit.App.Tests;

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
                reachabilityProbe: Probe
            );
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>テスト毎に使い捨ての一時フォルダ(実ファイル I/O 用)。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("yEditAppTests_").FullName;

        public string File(string name) => System.IO.Path.Combine(Root, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { /* 掃除失敗はテスト失敗にしない(読み取り専用属性等は UnauthorizedAccessException) */
            }
        }
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

    [Fact]
    public void SaveAs_WhitespacePath_WarnsAndAborts() =>
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
            host.Probe.Result = false; // プローブがタイムアウト/到達不可を返す

            Assert.False(host.File.Save());

            Assert.Equal(1, host.Probe.CallCount); // Save 経路も UNC は必ずプローブを通す(HIGH-6 と対称)
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.LastTimeout); // 5s → 5min mutation の kill
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

            Assert.Equal(0, host.Probe.CallCount); // ローカルパスはプローブを回さない(挙動不変)
            Assert.Equal("abc", File2.ReadAllText(path)); // 実際にディスクへ書き出し完了
        });

    [Fact]
    public void SaveAs_ShowsErrorPrompt_WhenPickedPathIsRemoteAndUnreachable() =>
        Sta.Run(() =>
        {
            // SaveAs で新たに UNC を選んだが到達不可なシナリオ: SaveAs は WriteToPath を経由する
            // ため CSV-M-2 ガードで短絡し、SaveAsDocument の Encoding/HasBom/LineEnding ロールバックが
            // WriteToPath=false の帰り経路で発火する(既存 SaveAs_WriteFailure_RollsBack... と対称)。
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
            host.Probe.Result = false;

            Assert.False(host.File.SaveAs());

            Assert.Equal(1, host.Probe.CallCount); // SaveAs でも WriteToPath 経由で 1 回だけプローブが走る
            Assert.Equal(TimeSpan.FromSeconds(5), host.Probe.LastTimeout); // 5s pin
            // State ロールバック(WriteToPath が false を返した経路が SaveAsDocument のロールバックを発火)
            Assert.Null(doc.State.Path); // Path は旧のまま(後続 Ctrl+S の別エンコード上書き事故防止)
            Assert.Equal(65001, doc.State.Encoding.CodePage); // ロールバック(932→65001)
            Assert.False(doc.State.HasBom); // ロールバック
            Assert.Equal(LineEnding.Crlf, doc.State.LineEnding); // ロールバック
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
            // PC-Talker 廃止後も温存の UIA 配線: LoadInto が RaiseUiaSelectionEvents を確実に戻すことを固定
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
