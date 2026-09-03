using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// M-18(設計 2026-09-03 §3.7): MainForm 側の配線 —— 読み直した後の発声と CSV モードの復帰。
/// 判定と確認は <see cref="FileControllerExternalChangeTests"/> が固定する。
/// <c>OnActivated</c> / <c>ActiveDocumentChanged</c> からの起動は実際のウィンドウ活性化が要り
/// L3 では再現できないため、L5 チェックリスト項目 1 / 4 が担う。ここは seam 経由で本体だけを叩く。
/// 更新時刻は本物の <c>FileTimestampProvider</c> なので、外部変更は実ファイルの mtime を明示的に進めて表す。
/// </summary>
public class MainFormExternalChangeTests
{
    private static MainForm ShowMainForm(AppSettings settings, TempDir tmp, FakePrompt prompt)
    {
        var form = new MainForm(
            settings,
            System.IO.Path.Combine(tmp.Root, "settings.json"),
            backupDirectory: System.IO.Path.Combine(tmp.Root, "backups"),
            sessionLayoutPath: System.IO.Path.Combine(tmp.Root, "session-state.json"),
            prompt: prompt
        );
        form.SetLastSessionBuffersPathForTest(
            System.IO.Path.Combine(tmp.Root, "last-session-buffers.json")
        );
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32000, -32000);
        form.ShowInTaskbar = false;
        form.Show();
        return form;
    }

    private static AppSettings NewSettings(bool csvAutoModeOnOpen = false) =>
        new() { BackupEnabled = false, CsvAutoModeOnOpen = csvAutoModeOnOpen };

    /// <summary>実ファイルを外部から書き換える。mtime は同一ティック内で同じ値になりうるので明示的に進める。</summary>
    private static void ExternalWrite(string path, string content)
    {
        File2.WriteAllText(path, content);
        File2.SetLastWriteTimeUtc(path, File2.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    [Fact]
    public void Reloaded_AnnouncesAndRefreshesText() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "v1");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            ExternalWrite(path, "v2");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.Equal("v2", doc.Editor.Text);
            Assert.Equal("読み直しました", form.LastAnnouncementForTest);
        });

    [Fact]
    public void Kept_DoesNotAnnounce() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = false };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "v1");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            ExternalWrite(path, "v2");
            string before = form.LastAnnouncementForTest;

            Assert.Equal(ExternalChangeOutcome.Kept, form.CheckExternalChangeOnActiveForTest());

            Assert.Equal("v1", doc.Editor.Text);
            Assert.Equal(before, form.LastAnnouncementForTest);
        });

    /// <summary>手動で入った CSV モード(自動モード OFF)は読み直し後も保たれる。
    /// LoadInto が CsvMode を false に落とすので、MainForm が TryEnterMode で戻す(設計 §3.7)。
    /// CSV モード中はキャレットがセルに追従しないので、キャレット由来の TryEnterMode は先頭セルへ入る
    /// = (row, col) を TryGoToCell で戻さないと (0, 0) に落ちる(仕様レビュー Important-1)。
    /// 非既定位置 (1, 1) から始める(CLAUDE.md §4-B)。最後の発声はセル(TryGoToCell の ApplyCell)。</summary>
    [Fact]
    public void Reloaded_ManualCsvMode_ReentersCsvMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\ne,f\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(form.CsvForTest.TryEnterMode(doc));
            form.CsvForTest.Move(Direction.Down);
            form.CsvForTest.Move(Direction.Right);
            Assert.Equal((1, 1), (doc.State.CsvRow, doc.State.CsvCol));
            ExternalWrite(path, "a,b\r\nc,X\r\ne,f\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.True(doc.State.CsvMode);
            Assert.Equal("a,b\r\nc,X\r\ne,f\r\n", doc.Editor.Text);
            Assert.Equal((1, 1), (doc.State.CsvRow, doc.State.CsvCol));
            Assert.Equal(CsvAnnounceFormatter.Cell("X", 2, 2), form.LastAnnouncementForTest);
        });

    /// <summary>読み直しでセルが無くなっていれば先頭セルのまま(TryGoToCell は黙って false)。
    /// 最後の発声は TryEnterMode のもの(モード オン + 先頭セル)で、「移動できません」等は出ない。</summary>
    [Fact]
    public void Reloaded_ManualCsvMode_CellGone_FallsBackToFirstCell() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\ne,f\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(form.CsvForTest.TryEnterMode(doc));
            form.CsvForTest.Move(Direction.Down);
            form.CsvForTest.Move(Direction.Right);
            Assert.Equal((1, 1), (doc.State.CsvRow, doc.State.CsvCol));
            ExternalWrite(path, "a,b\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.True(doc.State.CsvMode);
            Assert.Equal((0, 0), (doc.State.CsvRow, doc.State.CsvCol));
            Assert.Equal(
                CsvAnnounceFormatter.ModeOn + " " + CsvAnnounceFormatter.Cell("a", 1, 1),
                form.LastAnnouncementForTest
            );
        });

    /// <summary>自動モード ON でも手動モードと同じ経路で戻す(最終コード品質レビュー Q-1): 読み直しの中では
    /// _openedFresh(AutoEnterCsvMode)を <c>_reloadingCsv</c> で飛ばし、MainForm が発声の後に TryEnterMode →
    /// TryGoToCell で戻す。発声順は 読み直しました → CSVモード オン … → セル で手動と同じ、パースも 1 回。
    /// この網が <c>_reloadingCsv</c> を固定する: 飛ばさないと自動モードが先に入り、TryEnterMode は
    /// 既にモード中なので false → TryGoToCell が走らず (0, 0) に落ち、最後の発声も「読み直しました」になる。</summary>
    [Fact]
    public void Reloaded_AutoCsvMode_RestoresCell() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\ne,f\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(doc.State.CsvMode); // 自動モードで入っている(手動 TryEnterMode は呼ばない)
            form.CsvForTest.Move(Direction.Down);
            form.CsvForTest.Move(Direction.Right);
            Assert.Equal((1, 1), (doc.State.CsvRow, doc.State.CsvCol));
            ExternalWrite(path, "a,b\r\nc,X\r\ne,f\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.True(doc.State.CsvMode);
            Assert.Equal((1, 1), (doc.State.CsvRow, doc.State.CsvCol));
            Assert.Equal(CsvAnnounceFormatter.Cell("X", 2, 2), form.LastAnnouncementForTest);
        });

    /// <summary>自動モード ON で読み直し後に CSV として壊れていれば、手動モードと同じく通常モードのまま・
    /// 最後の発声は解析不能の通知(「読み直しました」に埋もれない)。
    /// 限界: 通知ラベルは最後の 1 件しか持たないので、AutoEnterCsvMode を飛ばした(= 解析不能の通知が
    /// 1 回だけ)ことはここでは観測できない(飛ばさなくても最後は同じ通知になる)。飛ばすことの網は
    /// <see cref="Reloaded_AutoCsvMode_RestoresCell"/>(飛ばさないとセル位置と最後の発声が変わる)。</summary>
    [Fact]
    public void Reloaded_AutoCsvMode_ParseFails_StaysInNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(csvAutoModeOnOpen: true), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(doc.State.CsvMode); // 自動モードで入っている
            ExternalWrite(path, "a,\"unterminated\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.False(doc.State.CsvMode);
            Assert.Equal(CsvAnnounceFormatter.ParseError, form.LastAnnouncementForTest);
        });

    /// <summary>読み直し後に CSV モードへ戻れない(外部変更で CSV として壊れた)場合は通常モードのまま。
    /// TryEnterMode が自分で発声するので、最後の発声は「読み直しました」ではなく解析不能の通知になる
    /// (1 行の発声チャネルは最後の 1 件が残る)。入力は引用符が閉じないまま EOF に達する形
    /// (<see cref="CsvParser"/> が Ok=false を返す唯一の構文条件)。</summary>
    [Fact]
    public void Reloaded_ManualCsvMode_ParseFails_StaysInNormalMode() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt { YesNoResult = true };
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            string path = tmp.File("t.csv");
            File2.WriteAllText(path, "a,b\r\nc,d\r\n");
            var doc = form.FileForTest.TryOpenOrActivate(path)!;
            Assert.True(form.CsvForTest.TryEnterMode(doc));
            ExternalWrite(path, "a,\"unterminated\r\n");

            Assert.Equal(ExternalChangeOutcome.Reloaded, form.CheckExternalChangeOnActiveForTest());

            Assert.False(doc.State.CsvMode);
            Assert.Equal(CsvAnnounceFormatter.ParseError, form.LastAnnouncementForTest);
        });

    [Fact]
    public void UntitledActiveDocument_Skipped() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            var prompt = new FakePrompt();
            using var form = ShowMainForm(NewSettings(), tmp, prompt);
            // 起動直後の無題タブは Path=null → FileController 側が Skipped を返す(何も聞かない)
            Assert.Equal(ExternalChangeOutcome.Skipped, form.CheckExternalChangeOnActiveForTest());
            Assert.Empty(prompt.Log);
        });
}
