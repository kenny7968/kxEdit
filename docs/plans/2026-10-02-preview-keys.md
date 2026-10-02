# プレビューのキー操作(preview-keys) 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Markdown プレビューが Alt+C(「閉じる(&C)」)で閉じない欠陥(項目 2)を直す。モーダルの表示中に主窓のショートカットが動く経路(項目 3)を、主窓側のガードと `ShowMarkdownPreview` の再入ガードで塞ぐ。

**Architecture:** 項目 2 は、WinForms の WebView2 コントロールが `AcceleratorKeyPressed` を自分の `KeyDown` に変換することを使い、`MarkdownPreviewForm` が `_web.KeyDown` で Alt+C を拾って閉じる。項目 3 は、`MainForm.ProcessCmdKey` の先頭で「主窓が Win32 で無効(= モーダルの表示中)ならキーを食う」ガードを置き、`ShowMarkdownPreview` に再入ガードを保険として入れる。

**Tech Stack:** C# / .NET 9 / WinForms / WebView2(WinForms 1.0.4022.49) / xUnit

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3・§12(フェーズ 8)。出典は `docs/plans/2026-09-24-general-perf-improvements-design.md` §17.3 の「評価の途中で見つけたこと」。

## 0. 調査の結果と結論(計画の作成時に実施)

傘 §3.1 の「最初のタスクで調査する」を、計画の形を決めるために計画の作成時に前倒しした。

### 0.1 方法

- scratchpad のワークツリー(main `a59482a2` から detach)に計測用のログだけを入れ、release.yml と同じ形で publish した。製品コードは変えていない。
  - `Application.AddMessageFilter` で、スレッドのキューに届く WM_KEYDOWN 系を宛先の HWND とともに記録した。
  - `MainForm.ProcessCmdKey` / `WndProc`、`MarkdownPreviewForm.ProcessCmdKey` / `ProcessDialogKey` / `ProcessMnemonic` / `WndProc`、`_web.KeyDown` に記録を入れた。
  - `ShowMarkdownPreview` の入口で `Environment.StackTrace` を採った。
- 実アプリを `--new-instance` で起動し、キーは SendInput(実際のキー入力と同じ経路)で送った。NVDA 起動中。

### 0.2 項目 3(主窓のショートカットが動く)の経路

| 条件 | 結果 |
|---|---|
| プレビューの表示が終わり、WebView2 にフォーカスがある状態で Ctrl+Shift+U・Ctrl+W | **再現しない**。キーはブラウザのプロセスへ行き、ホストのスレッドには WM_KEYDOWN が 1 つも届かない。`_web.KeyDown` に届くが、どこにも転送されない |
| Ctrl+Shift+U で開いた 60 ms 後(初期化中・フォーカスはプレビューのフォーム)に Ctrl+Shift+U | **再現しない**。プレビューの `ProcessCmdKey` に届き、`false` で返る。ToolStrip のショートカット配送がルート HWND の一致を要求するため、主窓のメニューには届かない |
| プレビューの表示中に、無効化された主窓を `SetForegroundWindow` で強制的に前面化してから Ctrl+Shift+U | **再現した**。キーは主窓の HWND に直接届き、`MainForm.ProcessCmdKey` → メニューのショートカット → `ShowMarkdownPreview` の順で、プレビューが**入れ子**で開いた(スタックトレースに `ShowMarkdownPreview` が 2 段) |
| プレビューの表示中に Alt+Tab で往復・タスクバーのボタンをクリック | 前面に来るのは**プレビュー**。無効化された主窓には届かない |

- 結論: 経路は**プレビューのフォームを通らない**。主窓が無効(モーダルのオーナー)なのに前面に出された状態で、主窓自身がキーを処理する。
- 元の設計書 §17.3 の観測は、計測スクリプトが主窓を `SetForegroundWindow` で前面化してから送ったことによると見られる(`l5-automation-harness` の罠 3 と同じ形)。実ユーザーの通常の操作(Alt+Tab・タスクバー・クリック)では到達しなかった。外部のプログラムが主窓を前面化した場合には到達する。
- 副次的な観測: 入れ子のプレビューを閉じると、前面が無効な主窓に移り、外側のプレビューは開いたまま背面に残った。入れ子が起きると、利用者から見て操作できない状態になる。
- 傘 §4.3 の「`ProcessCmdKey` の参照はない」は正しかった。WebView2 は `ProcessCmdKey` を経由しない。

### 0.3 項目 2(Alt+C)の届き先

- フォーカスが WebView2 にあると、Alt+C はプレビューのフォームの `ProcessCmdKey` にも `ProcessDialogKey` にも `ProcessMnemonic` にも**届かない**(ホストのスレッドに WM_SYSKEYDOWN が来ない)。
- WinForms の WebView2(1.0.4022.49)の `_coreWebView2Controller_AcceleratorKeyPressed` は、IL で次のことをしている: `KeyEventKind` が KeyDown / SystemKeyDown なら `new KeyEventArgs(VirtualKey | Control.ModifierKeys)` を作って `OnKeyDown` を呼び、`e.Handled` を WebView2 の `Handled` に書き戻す(KeyUp も同様)。
- 実測で、`_web.KeyDown` に `C, Alt` が届いた。Ctrl+W(`W, Control`)・Ctrl+S・F3・Esc(`Escape`)も届いた。
- 結論: 傘 §12.2 の「`ProcessCmdKey` か `ProcessDialogKey` の届く方を override する」は当たらない。**`_web.KeyDown` で Alt+C を拾って `Handled = true` にし、Close する**形に精密化する。

### 0.4 傘 §12 からの精密化

- **項目 2**: override ではなく `_web.KeyDown` の購読にする(0.3)。`OnNavCompleted` の `_web.Focus()` は残す(本文を先に読ませるため)。判定は `e.KeyData == (Keys.Alt | Keys.C)` の完全一致にする。Ctrl+C(本文のコピー)と Alt+Shift+C は素通しする。
- **項目 3**: 経路がプレビューを通らないので、傘 §12.2 の第 2 案(主窓側のガード)を採る。ガードの条件は「主窓が Win32 で無効」(`NativeMethods.IsWindowEnabled(Handle) == false`)にする。
  - `ShowDialog` と `MessageBox.Show` は、表示中にオーナーを `EnableWindow(false)` で無効にする(WinForms の `Control.Enabled` は変わらないので、Win32 の状態を見る)。
  - 無効な窓にキーが届くのは、0.2 のとおり強制的に前面化されたときだけなので、通常の操作の挙動は変わらない。
  - プレビュー専用のフラグではなくこの条件にしたので、ガードは**すべてのモーダル**(設定・検索と置換・grep・文書の情報・MessageBox など)の表示中に効く。`l5-automation-harness` の罠 3(設定ダイアログが二重に開いた)も同じ経路で塞がる。
  - 位置は `ProcessCmdKey` の**先頭**。CSV の素キーの横取り・自前の switch・`base`(メニューのショートカット)のすべてより前に置く。
- **再入ガード**: `ShowMarkdownPreview` の**先頭**(`_docs.Active` を読む前)に置く。フラグは `try/finally` で戻す(`ShowDialog` が例外を投げても残らないように)。
- **テストの口**
  - 主窓のガード: テストの中で実際に小さなフォームを `ShowDialog(form)` で開き、その `Shown` の中から `ProcessCmdKey` を呼ぶ。P/Invoke で無効化を真似るより、実物の「モーダルの表示中」を作れる。
  - 再入ガード: `internal void SetPreviewShowingForTest(bool value)` を足す(`SetSuppressRestoreDialogsForTest` と同じ形)。CSV モードの文書で呼ぶ。ガードが無ければ CSV の判定で `BlockedInCsvMode` を発声して戻るので、**テストは固まらずに赤くなる**(ガードが無い退行で `ShowDialog` に入らない形を選んだ)。
  - Alt+C: `MarkdownPreviewForm` を作って Handle だけ作り(`Show` はしない = WebView2 は初期化されない)、`_web` の `OnKeyDown` をリフレクションで呼ぶ。`WebView2_HandleCreated` はコントローラが無ければ何もしないことを IL で確かめた。

### 0.5 意図的な挙動差(PR に記載する)

- 傘 §3.5 のフェーズ 8 の 2 行(プレビューが Alt+C で閉じる。プレビューを表示している間は主窓のショートカットが動かない)。
- 0.4 のとおり、後者は**プレビューに限らず、主窓をオーナーとするすべてのモーダルの表示中**に広がる。通常の操作では主窓にキーが届かないので、観測できる差は「外部から主窓を前面化された場合」だけである。

## Global Constraints

- **前倒しの脆弱性レビュー**を行う(傘 §3.3。WebView・プレビュー)。対象は Task 2(`MarkdownPreviewForm`)。Task 1 は別エージェントの仕様レビューだけでよい(外部入力・WebView に触れない)。
- 注入スクリプトで Alt+C を拾う案は採らない(傘 §12.2。WebMessage の面を広げるため)。今回は C# 側で処理できる。
- `OnNavCompleted` の `_web.Focus()` は消さない(傘 §12.2)。
- 変異検証は行わない(傘 §3.3)。陰性対照は TDD の赤で兼ねる(各 Task の Step 2)。陰性対照を別に取る場合は **commit した後に**、行を消さず条件を無効化する形で行い、ビルドの成功を確かめる(失敗すると古い DLL で緑に見える)。後始末の `git checkout -- <file>` の前に `git diff` で変更が対照だけであることを確かめる。
- テスト本体で `Thread.Sleep` を使わない(Sonar S2925)。
- L5 は必須(傘 §3.3: 閉じた後のフォーカス復帰、本文が先に読まれること)。Task 3 で行う。
- 0 warning(`-warnaserror`)。pre-commit フックを飛ばさない。
- コミットメッセージは `fix|test|docs(scope): 日本語の要約`。末尾に `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`。
- git commit が無言で止まったら、SSH 署名のハング(`-c gpg.ssh.program=` で Windows 版の ssh-keygen を指す)を疑う。

## Review Focus

1. **Ctrl+C でプレビューが閉じる**: 判定を `KeyCode == C` だけにすると、本文のコピーでプレビューが閉じる。`KeyData` の完全一致であること(Task 2 Step 1 の `Other_keys_are_left_to_WebView2`。Ctrl+C・素の C・Alt+Shift+C を入れる)。
2. **Alt+C がブラウザにも渡る**: `Handled` を立てないと、閉じる途中のブラウザに Alt+C が届く。`e.Handled == true` を確かめる(Task 2 Step 1)。
3. **モーダルを閉じた後もキーが効かない**: ガードの条件を「モーダルを開いたことがある」のようなフラグにすると、閉じた後も食い続ける。モーダルを閉じた後に同じキーが効くことを陽性対照で確かめる(Task 1 Step 1 の `Shortcuts_work_again_after_modal_closes`)。
4. **再入ガードのフラグが残る**: `ShowDialog` の後でフラグを戻し忘れると、2 回目以降プレビューが二度と開かない。`finally` で戻すこと、テストの後始末で戻した後に通常の経路(CSV の発声)が動くことを確かめる(Task 1 Step 1 の `Reentrant_preview_is_ignored`)。
5. **キーの食い過ぎ**: ガードは主窓が無効なときだけ効く。通常の状態の `ProcessCmdKey`(Ctrl+Tab 等)が変わらないことは、既存の `MainFormModeMenuTests` と Task 1 の陽性対照で確かめる。

---

### Task 1: モーダルの表示中は主窓のキーを食い、プレビューの再入を止める(項目 3)

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs`(`ProcessCmdKey` の先頭・`ShowMarkdownPreview`・テストの口)
- Create: `tests/kxEdit.App.Tests/MainFormModalGuardTests.cs`

**Interfaces:**
- Consumes: `NativeMethods.IsWindowEnabled(nint)`(既存。`src/kxEdit.App/NativeMethods.cs`)、`MainForm.FileForTest` / `DocsForTest` / `LastAnnouncementForTest`(既存)、`CsvAnnounceFormatter.BlockedInCsvMode`(既存)
- Produces: `internal void MainForm.SetPreviewShowingForTest(bool value)`

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/MainFormModalGuardTests.cs`:

```csharp
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
    // Ctrl+Tab が効けば a に移るので、「効かなかった」と「元から a だった」を区別できる。
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
                    handled = InvokeProcessCmdKey(form, Keys.Control | Keys.Tab);
                    activeDuringModal = form.DocsForTest.Active;
                }
            );

            Assert.True(handled); // 食ったこと(false だと base へ流れて他の処理に渡りうる)
            Assert.Same(docB, activeDuringModal); // タブは切り替わっていない
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

            Assert.True(InvokeProcessCmdKey(form, Keys.Control | Keys.Tab));
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
```

名前空間: `Document` は `kxEdit.App`、`CsvAnnounceFormatter` は `kxEdit.Core.Csv`(確認済み)。

- [ ] **Step 2: テストを走らせて失敗を確かめる**

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MainFormModalGuardTests"`
Expected: `SetPreviewShowingForTest` が無いのでビルドが失敗する。Step 3 で口だけ先に足して(本体のガードなしで)もう一度走らせ、次の 2 件が FAIL することを確かめる(ビルドが成功していることも確かめる):
- `Shortcuts_are_swallowed_while_modal_is_shown`: アクティブが a に変わる
- `Reentrant_preview_is_ignored`: `BlockedInCsvMode` が発声される

`Shortcuts_work_again_after_modal_closes` はガードの前から PASS する(陽性対照)。

- [ ] **Step 3: 実装する**

`src/kxEdit.App/MainForm.cs` のテストの口の並び(`SetSuppressRestoreDialogsForTest` の近く)に足す:

```csharp
    // フェーズ 8 項目 3: ShowMarkdownPreview の再入ガードを、ShowDialog を開かずに
    // 立てた状態にする(SetSuppressRestoreDialogsForTest と同じ方式)。
    internal void SetPreviewShowingForTest(bool value) => _previewShowing = value;
```

フィールド(他の private フィールドの並び):

```csharp
    // ShowMarkdownPreview の再入ガード(フェーズ 8 項目 3 の保険)。モーダルの表示中は
    // ProcessCmdKey のガードで主窓のキーが止まるので、通常はここに来ない。
    private bool _previewShowing;
```

`ProcessCmdKey` の先頭(`var activeDoc = _docs.Active;` の前):

```csharp
        // フェーズ 8 項目 3: モーダル(ShowDialog / MessageBox)の表示中、オーナーの主窓は
        // Win32 で無効化されている(Control.Enabled は変わらない)。無効な主窓にキーが届くのは、
        // 外部から SetForegroundWindow で前面化されたときだけで、そのままメニューの
        // ショートカットまで流すと、プレビューや設定がモーダルの上に入れ子で開く
        // (2026-10-02-preview-keys.md §0.2)。CSV の横取り・switch・base より前で食う。
        if (IsHandleCreated && !NativeMethods.IsWindowEnabled(Handle))
            return true;
```

`ShowMarkdownPreview` の先頭と末尾:

```csharp
    private void ShowMarkdownPreview()
    {
        // フェーズ 8 項目 3 の保険: プレビューの上にプレビューを開かない。
        if (_previewShowing)
            return;

        var doc = _docs.Active;
        // …(既存のまま)…

        _previewShowing = true;
        try
        {
            using var f = new MarkdownPreviewForm(
                html,
                dir,
                doc.State.DisplayName,
                new FileReachabilityProbe()
            );
            f.ShowDialog(this);
        }
        finally
        {
            _previewShowing = false;
        }
        _docs.Active?.FocusTarget.Focus(); // 戻り後は編集領域へフォーカス
    }
```

`ShowMarkdownPreview` の remarks に 1 行足す: 「フェーズ 8: 先頭で再入を止める(`_previewShowing`)。主窓のキーはモーダルの表示中 `ProcessCmdKey` で止まるので、これは保険。」

- [ ] **Step 4: テストを走らせて成功を確かめる**

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MainFormModalGuardTests|FullyQualifiedName~MainFormModeMenuTests|FullyQualifiedName~MainFormPreviewStructureTests"`
Expected: すべて PASS(既存の `MainFormPreviewStructureTests` の IL の順序の網も通ること)。

- [ ] **Step 5: commit する**

```bash
git add src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/MainFormModalGuardTests.cs
git commit -m "fix(app): モーダルの表示中は主窓のキーを処理せず、プレビューの再入を止める"
```

本文に、経路(無効な主窓が外部から前面化されたときだけ届く)と、ガードがすべてのモーダルに効くことを書く。

---

### Task 2: プレビューを Alt+C で閉じる(項目 2)

**前倒しの脆弱性レビュー**の対象(傘 §3.3)。

**Files:**
- Modify: `src/kxEdit.App/MarkdownPreviewForm.cs`(ctor で `_web.KeyDown` を購読・ハンドラを足す・クラスの summary)
- Create: `tests/kxEdit.App.Tests/MarkdownPreviewFormKeyTests.cs`

**Interfaces:**
- Consumes: `kxEdit.App.Tests.Fakes.FakeReachabilityProbe`(既存)
- Produces: なし(private のハンドラ `OnWebKeyDown`)

- [ ] **Step 1: 失敗するテストを書く**

`tests/kxEdit.App.Tests/MarkdownPreviewFormKeyTests.cs`:

```csharp
using System.Reflection;
using kxEdit.App.Tests.Fakes;
using Microsoft.Web.WebView2.WinForms;

namespace kxEdit.App.Tests;

/// <summary>
/// フェーズ 8 項目 2: WebView2 にフォーカスがあると、Alt+C(「閉じる(&amp;C)」)は
/// フォームの ProcessCmdKey / ProcessDialogKey / ニーモニックのどれにも届かない。
/// WinForms の WebView2 は AcceleratorKeyPressed を自分の KeyDown に変換し、e.Handled を
/// WebView2 へ書き戻す(2026-10-02-preview-keys.md §0.3)。ここではその KeyDown を直接起こす。
/// <para>
/// フォームは Handle だけ作り、Show はしない。InitAsync は Shown で走るので WebView2 は
/// 初期化されない(WebView2 ランタイムに依存しない)。
/// </para>
/// </summary>
public class MarkdownPreviewFormKeyTests
{
    private static MarkdownPreviewForm NewForm()
    {
        var f = new MarkdownPreviewForm("<p>x</p>", null, "x.md", new FakeReachabilityProbe())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        _ = f.Handle; // Close が WM_CLOSE を送って FormClosing を起こせるようにする
        return f;
    }

    /// <summary>WebView2 の AcceleratorKeyPressed が行うのと同じく、_web の OnKeyDown を呼ぶ。</summary>
    private static KeyEventArgs RaiseWebKeyDown(MarkdownPreviewForm form, Keys keyData)
    {
        var field = typeof(MarkdownPreviewForm).GetField(
            "_web",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(field);
        var web = (WebView2)field!.GetValue(form)!;
        var onKeyDown = typeof(Control).GetMethod(
            "OnKeyDown",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(onKeyDown);
        var e = new KeyEventArgs(keyData);
        onKeyDown!.Invoke(web, new object[] { e });
        return e;
    }

    [Fact]
    public void Alt_C_on_WebView2_closes_the_preview() =>
        Sta.Run(() =>
        {
            using var form = NewForm();
            int closing = 0;
            form.FormClosing += (_, _) => closing++;

            var e = RaiseWebKeyDown(form, Keys.Alt | Keys.C);

            Assert.True(e.Handled); // Review Focus 2: ブラウザへ渡さない
            Assert.Equal(1, closing);
        });

    // Review Focus 1: 本文のコピー(Ctrl+C)や、似たキーでは閉じない。
    [Theory]
    [InlineData(Keys.Control | Keys.C)]
    [InlineData(Keys.C)]
    [InlineData(Keys.Alt | Keys.Shift | Keys.C)]
    [InlineData(Keys.Alt | Keys.X)]
    public void Other_keys_are_left_to_WebView2(Keys keyData) =>
        Sta.Run(() =>
        {
            using var form = NewForm();
            int closing = 0;
            form.FormClosing += (_, _) => closing++;

            var e = RaiseWebKeyDown(form, keyData);

            Assert.False(e.Handled);
            Assert.Equal(0, closing);
        });
}
```

- [ ] **Step 2: テストを走らせて失敗を確かめる**

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MarkdownPreviewFormKeyTests"`
Expected: `Alt_C_on_WebView2_closes_the_preview` が FAIL(`Handled` が false)。`Other_keys_are_left_to_WebView2` の 4 件は PASS(陽性対照ではなく、修正後に壊れないことの網)。ビルドが成功していることを確かめる。

注意: `Handle` の作成で例外が出る(WebView2 が初期化を要求する)場合は、計画の前提(§0.4)が崩れている。実装に進まず、WebView2 の `WebView2_HandleCreated` の挙動を確かめて報告する。

- [ ] **Step 3: 実装する**

`src/kxEdit.App/MarkdownPreviewForm.cs` の ctor、`Shown += …` の前に足す:

```csharp
        // フェーズ 8 項目 2: WebView2 にフォーカスがあると(OnNavCompleted の _web.Focus() の後)、
        // Alt+C はフォームの ProcessCmdKey / ProcessDialogKey にもボタンのニーモニックにも届かない。
        // WinForms の WebView2 は AcceleratorKeyPressed を自分の KeyDown に変換し、e.Handled を
        // WebView2 へ書き戻すので、ここで拾う(2026-10-02-preview-keys.md §0.3)。
        // 注入スクリプトで拾う案は WebMessage の面を広げるので採らない。
        _web.KeyDown += OnWebKeyDown;
```

ハンドラ(`OnNavigationStarting` の前あたり):

```csharp
    /// <summary>
    /// フェーズ 8 項目 2: WebView2 にフォーカスがあるときの Alt+C(「閉じる(&amp;C)」)で閉じる。
    /// 完全一致だけを扱い、Ctrl+C(本文のコピー)などは WebView2 に任せる。
    /// <para>
    /// この KeyDown は、ブラウザのプロセスが受けた実際のキー入力から WebView2 が起こす
    /// (AcceleratorKeyPressed)。ページの中の JavaScript が作る合成のキーイベントは
    /// AcceleratorKeyPressed にならないので、文書の内容からプレビューを閉じさせることはできない
    /// (そもそも文書のスクリプトは CSP で動かない)。
    /// </para>
    /// </summary>
    private void OnWebKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyData != (Keys.Alt | Keys.C))
            return;
        e.Handled = true;
        Close();
    }
```

クラスの summary の「「閉じる」ボタンと Esc の両方でエディタへ戻る。」を「「閉じる」ボタン・Alt+C・Esc でエディタへ戻る。」にする。

- [ ] **Step 4: テストを走らせて成功を確かめる**

Run: `dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MarkdownPreviewForm"`
Expected: すべて PASS(既存の `MarkdownPreviewFormStructureTests` も)。

- [ ] **Step 5: 前倒しの脆弱性レビューを受ける**

別エージェントに、Task 2 の差分を WebView・プレビューの観点でレビューさせる。観点:
- 文書(Markdown 由来の HTML)の内容から、`KeyDown` を起こしてプレビューを閉じさせる、または他の処理を起こせるか
- `Handled = true` によって、ブラウザ側の既存の防御(`AreBrowserAcceleratorKeysEnabled = false` 等)が変わらないか
- `Close()` を WebView2 のイベントの中から呼ぶことの安全性(既存の Esc の `WebMessageReceived` からの `Close()` と同じ形か)
- 指摘は CLAUDE.md §4 の 3 択で扱う。

- [ ] **Step 6: commit する**

```bash
git add src/kxEdit.App/MarkdownPreviewForm.cs tests/kxEdit.App.Tests/MarkdownPreviewFormKeyTests.cs
git commit -m "fix(preview): WebView2 にフォーカスがあるときも Alt+C でプレビューを閉じる"
```

---

### Task 3: 実機の確認(L5)と実施記録

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§12 の末尾に「12.4 実施記録」を追記)

- [ ] **Step 1: 品質ゲート**

Run: `pwsh -File tools/pre-merge-check.ps1`
Expected: EXIT 0。

- [ ] **Step 2: 実機で確かめる(release.yml と同じ形で publish したもの。NVDA 起動中)**

1. 文書を開き Ctrl+Shift+U でプレビューを開く。NVDA が**本文を先に読む**こと(スピーチビューアーで採取)。
2. Alt+C でプレビューが閉じ、**主窓のエディタにフォーカスが戻る**こと(NVDA がエディタの内容を読む。前面の窓が主窓であること)。閉じたことは、窓が消えたことで確かめる(`l5-automation-harness` の罠 5)。
3. Esc と「閉じる」ボタンでも、従来どおり閉じること。
4. プレビューの表示中に Ctrl+Shift+U・Ctrl+W・Ctrl+S を実キーで送っても、何も起きないこと(主窓のタブ数・題名が変わらない)。
5. 計測スクリプトの形(無効な主窓を `SetForegroundWindow` で前面化してから Ctrl+Shift+U)でも、プレビューが入れ子で開かないこと(修正前は開いた。§0.2)。
6. `tools/sr-regression.ps1` が EXIT 0。

- [ ] **Step 3: 最終ブランチレビュー(2 パス)**

CLAUDE.md §3 の 5。コード品質パスと脆弱性パスを別々のエージェントで行う。指摘は fixup commit で反映する。

- [ ] **Step 4: 実施記録を書く**

傘 §12 の末尾に「### 12.4 実施記録(2026-10-02)」を追記する。項目は、前のフェーズ(§11.3)と同じ並び: 調査の結論(本計画 §0 を要約して参照)・成果物・完了条件(テスト・陰性対照・品質ゲート・L5・レビュー)・本節からの精密化(§0.4)・意図的な挙動差(§0.5)・申し送り。

申し送りの候補(実施中に見つかったものを足す):
- Esc も `_web.KeyDown` に届く(§0.3)。注入スクリプトと `WebMessageReceived` による Esc の処理を C# の `KeyDown` に寄せれば、WebMessage の面(注入スクリプト)をなくせる。挙動の変更を伴うので本フェーズの範囲外。

```bash
git add docs/plans/2026-09-27-perf-followups-design.md
git commit -m "docs(perf): フェーズ 8(プレビューのキー操作)の実施記録"
```
