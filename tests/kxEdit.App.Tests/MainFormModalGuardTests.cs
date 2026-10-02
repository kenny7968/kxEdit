using System.Reflection;
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;
using File2 = System.IO.File;

namespace kxEdit.App.Tests;

/// <summary>
/// フェーズ 8 項目 3: モーダルの表示中に主窓のショートカットが動く経路を塞ぐ
/// (`docs/plans/2026-10-02-preview-keys.md` §0.2)。
/// <para>
/// 実ユーザーの操作では、モーダルの表示中に主窓へキーは届かない。届くのは、無効化された主窓を
/// 外部から <c>SetForegroundWindow</c> で前面化したときだけで、そのときキーは主窓の
/// <c>ProcessCmdKey</c> に直接来る。ここではその入口を直接呼び、主窓が Win32 で無効な間は
/// 何もしないことを確かめる。
/// </para>
/// </summary>
public class MainFormModalGuardTests
{
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    private static MainForm ShowMainForm(TempDir tmp, bool csvAutoModeOnOpen = false)
    {
        var form = new MainForm(
            new AppSettings { BackupEnabled = false, CsvAutoModeOnOpen = csvAutoModeOnOpen },
            System.IO.Path.Combine(tmp.Root, "settings.json"),
            backupDirectory: System.IO.Path.Combine(tmp.Root, "backups"),
            sessionLayoutPath: System.IO.Path.Combine(tmp.Root, "session-state.json")
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

    private static bool InvokeProcessCmdKey(MainForm form, Keys keyData)
    {
        var m = typeof(MainForm).GetMethod("ProcessCmdKey", Priv);
        Assert.NotNull(m);
        object?[] args = { default(Message), keyData };
        return (bool)m!.Invoke(form, args)!;
    }

    /// <summary>
    /// <paramref name="owner"/> をオーナーにした小さなモーダルを実際に開き、表示中に
    /// <paramref name="whileModal"/> を実行してから閉じる。<c>ShowDialog</c> はオーナーを
    /// Win32 で無効化する(<c>Control.Enabled</c> は変わらない)ので、本物の「モーダルの表示中」になる。
    /// </summary>
    private static void WhileModal(MainForm owner, Action whileModal)
    {
        using var dlg = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        Exception? captured = null;
        dlg.Shown += (_, _) =>
        {
            try
            {
                whileModal();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                dlg.Close();
            }
        };
        dlg.ShowDialog(owner);
        if (captured is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(captured).Throw();
    }

    // 非既定の状態から始める(CLAUDE.md §4-B): タブを 2 つ開き、アクティブは 2 つ目(b)。
    // Ctrl+Shift+Tab(前のタブ)が効けば a に移るので、「効かなかった」と「元から a だった」を
    // 区別できる。Ctrl+Tab を使わないのは、起動時の空の無題タブが先頭に残り、b の次は
    // 折り返してその無題タブになるため(a に移ることを確かめられない)。
    [Fact]
    public void Shortcuts_are_swallowed_while_modal_is_shown() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string a = tmp.File("a.txt");
            string b = tmp.File("b.txt");
            File2.WriteAllText(a, "a");
            File2.WriteAllText(b, "b");
            using var form = ShowMainForm(tmp);
            var docA = form.FileForTest.TryOpenOrActivate(a);
            var docB = form.FileForTest.TryOpenOrActivate(b);
            Assert.NotNull(docA);
            Assert.Same(docB, form.DocsForTest.Active); // 前提: アクティブは b

            bool handled = false;
            Document? activeDuringModal = null;
            WhileModal(
                form,
                () =>
                {
                    // 前提: ガードの発火条件どおり、主窓は Win32 で無効化されている(CLAUDE.md §4-B)。
                    Assert.False(NativeMethods.IsWindowEnabled(form.Handle));
                    handled = InvokeProcessCmdKey(form, Keys.Control | Keys.Shift | Keys.Tab);
                    activeDuringModal = form.DocsForTest.Active;
                }
            );

            // キーが消費されたことの確認のみ。ガードが無くても switch が Ctrl+Shift+Tab を処理して
            // true を返すので、これではガードを判別できない。判別するのは次の assert。
            Assert.True(handled);
            Assert.Same(docB, activeDuringModal); // タブは切り替わっていない(ガードの判別)
        });

    // 陽性対照(Review Focus 3): モーダルを閉じた後は、同じキーが効く。
    [Fact]
    public void Shortcuts_work_again_after_modal_closes() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string a = tmp.File("a.txt");
            string b = tmp.File("b.txt");
            File2.WriteAllText(a, "a");
            File2.WriteAllText(b, "b");
            using var form = ShowMainForm(tmp);
            var docA = form.FileForTest.TryOpenOrActivate(a);
            var docB = form.FileForTest.TryOpenOrActivate(b);
            Assert.Same(docB, form.DocsForTest.Active);

            WhileModal(form, () => { });

            Assert.True(InvokeProcessCmdKey(form, Keys.Control | Keys.Shift | Keys.Tab));
            Assert.Same(docA, form.DocsForTest.Active);
        });

    // 再入ガード(保険)。CSV モードの文書で呼ぶので、ガードが無い退行では CSV の判定で
    // BlockedInCsvMode を発声して戻る = ShowDialog に入らず、テストは固まらずに赤くなる。
    [Fact]
    public void Reentrant_preview_is_ignored() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            string path = tmp.File("data.csv");
            File2.WriteAllText(path, "a,b\n1,2");
            using var form = ShowMainForm(tmp, csvAutoModeOnOpen: true);
            var doc = form.FileForTest.TryOpenOrActivate(path);
            Assert.True(doc!.State.CsvMode); // 前提
            var show = typeof(MainForm).GetMethod("ShowMarkdownPreview", Priv);
            Assert.NotNull(show);
            string before = form.LastAnnouncementForTest;
            Assert.NotEqual(CsvAnnounceFormatter.BlockedInCsvMode, before); // 前提: 区別できる

            form.SetPreviewShowingForTest(true);
            show!.Invoke(form, null);
            Assert.Equal(before, form.LastAnnouncementForTest); // 何もしていない

            // Review Focus 4: フラグを戻せば通常の経路に戻る(陽性対照)。
            form.SetPreviewShowingForTest(false);
            show.Invoke(form, null);
            Assert.Equal(CsvAnnounceFormatter.BlockedInCsvMode, form.LastAnnouncementForTest);
        });
}
