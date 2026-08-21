namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 1: DocumentManager の配線・状態遷移テスト(設計書 §3)。
/// リファクタ不要で実物 EditorControl+TabControl を STA 上で使い、
/// タブ生成/ラベル更新/イベント転送のアクティブ限定/巡回選択/KeyBasedSwitch を検証する。
/// Core が検証済みの照合・I/O 正しさは再検証しない(責務=App 層の配線)。
/// </summary>
public class DocumentManagerTests
{
    /// <summary>実 DocumentManager を可視フォームに載せたテストホスト(共通 HostForm.CreateWithDocs を使う。
    /// 可視が必要な理由と共通化の経緯は TestHost.cs 参照)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
        }

        /// <summary>クリーンな本文を持つ文書を作る(Text セッター=新規バッファで Modified=false)。</summary>
        public Document NewDocWithText(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== CreateNew の配線 =====

    [Fact]
    public void CreateNew_FirstDocument_BecomesActiveWithUntitledLabel() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            Assert.Same(doc, host.Docs.Active);
            Assert.Equal(1, host.Docs.Count);
            Assert.Equal("無題", doc.Page.Text); // 変更なし=「*」なし
        });

    [Fact]
    public void CreateNew_SecondDocument_ActivatesIt_AndRaisesActiveDocumentChanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.Docs.CreateNew();
            int changed = 0;
            host.Docs.ActiveDocumentChanged += (_, _) => changed++;
            var doc2 = host.Docs.CreateNew();
            Assert.Same(doc2, host.Docs.Active);
            Assert.Equal(1, changed); // タブ切替(doc1→doc2)が 1 回だけ転送される
            Assert.Equal(new[] { doc1, doc2 }, host.Docs.Documents);
        });

    [Fact]
    public void DirtyEdit_OnActiveDocument_MarksLabel_AndRaisesActiveDirtyChanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDocWithText("abc");
            int dirtyChanged = 0;
            host.Docs.ActiveDirtyChanged += (_, _) => dirtyChanged++;

            doc.Editor.ReplaceCharRange(0, 0, "x"); // SavePointLeft
            Assert.Equal("* 無題", doc.Page.Text);
            Assert.Equal(1, dirtyChanged);

            doc.Editor.SetSavePoint(); // SavePointReached
            Assert.Equal("無題", doc.Page.Text);
            Assert.Equal(2, dirtyChanged);
        });

    [Fact]
    public void DirtyEdit_OnInactiveDocument_MarksItsLabel_ButDoesNotRaiseActiveDirtyChanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.NewDocWithText("abc");
            var doc2 = host.NewDocWithText("def"); // doc2 がアクティブ
            int dirtyChanged = 0;
            host.Docs.ActiveDirtyChanged += (_, _) => dirtyChanged++;

            doc1.Editor.ReplaceCharRange(0, 0, "x"); // 非アクティブの編集
            Assert.Equal("* 無題", doc1.Page.Text); // ラベルはどのタブでも更新される
            Assert.Equal(0, dirtyChanged); // 転送はアクティブ限定
            Assert.Same(doc2, host.Docs.Active);
        });

    [Fact]
    public void CaretChange_ForwardedOnlyForActiveDocument() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.NewDocWithText("abc");
            var doc2 = host.NewDocWithText("def"); // doc2 がアクティブ
            int caretChanged = 0;
            host.Docs.ActiveCaretChanged += (_, _) => caretChanged++;

            doc2.Editor.ReplaceCharRange(0, 0, "x"); // AfterEdit は常に UpdateUI を発火
            Assert.True(caretChanged >= 1);

            caretChanged = 0;
            doc1.Editor.ReplaceCharRange(0, 0, "y"); // 非アクティブ分は転送しない
            Assert.Equal(0, caretChanged);
        });

    // ===== FindByPath(PathKey 同一視) =====

    [Fact]
    public void FindByPath_MatchesCaseAndSeparatorInsensitively() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\A.TXT";
            Assert.Same(doc, host.Docs.FindByPath("c:/temp/a.txt")); // 大小文字・区切り差を同一視
        });

    [Fact]
    public void FindByPath_IgnoresUntitled_AndReturnsNullWhenNoMatch() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.Docs.CreateNew(); // 未保存(Path=null)は対象外
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt";
            Assert.Null(host.Docs.FindByPath(@"C:\Temp\other.txt"));
        });

    // ===== TryClose =====

    [Fact]
    public void TryClose_ConfirmRejected_KeepsDocumentAlive() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            Assert.False(host.Docs.TryClose(doc, _ => false));
            Assert.Equal(1, host.Docs.Count);
            Assert.Contains(doc, host.Docs.Documents);
            Assert.False(doc.Editor.IsDisposed);
            Assert.False(doc.Page.IsDisposed);
        });

    [Fact]
    public void TryClose_ConfirmAccepted_RemovesAndDisposes() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            Document? asked = null;
            Assert.True(
                host.Docs.TryClose(
                    doc,
                    d =>
                    {
                        asked = d;
                        return true;
                    }
                )
            );
            Assert.Same(doc, asked); // confirm には対象文書が渡る
            Assert.Equal(0, host.Docs.Count);
            Assert.DoesNotContain(doc, host.Docs.Documents);
            Assert.True(doc.Editor.IsDisposed); // ネイティブ資源の解放
            Assert.True(doc.Page.IsDisposed);
        });

    // DocumentClosed は「閉じた文書に紐づく保持(SearchController の材質化キャッシュ=
    // 文書 1 本ぶんのバイト列)を解放させる」ための唯一の通知源。ActiveDocumentChanged は
    // 選択タブ削除で発火が保証されず、非アクティブタブのクローズでは切替自体が起きない。

    [Fact]
    public void TryClose_ConfirmAccepted_RaisesDocumentClosed_WithClosedDocument() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.Docs.CreateNew(); // 残すタブ(最後の 1 枚を閉じる場合と区別する)
            var doc = host.Docs.CreateNew();
            var closed = new List<Document>();
            host.Docs.DocumentClosed += (_, d) => closed.Add(d);

            Assert.True(host.Docs.TryClose(doc, _ => true));

            Assert.Equal(new[] { doc }, closed); // 閉じた当人が 1 回だけ渡る
        });

    [Fact]
    public void TryClose_ConfirmRejected_DoesNotRaiseDocumentClosed() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            int closed = 0;
            host.Docs.DocumentClosed += (_, _) => closed++;

            Assert.False(host.Docs.TryClose(doc, _ => false)); // 保存確認でキャンセル

            Assert.Equal(0, closed); // 生きている文書のキャッシュを落とさない
            Assert.Contains(doc, host.Docs.Documents);
        });

    // ===== SelectNext 巡回 / SelectAt 範囲外 no-op =====

    [Fact]
    public void SelectNext_WrapsFromLastToFirst() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[]
            {
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
            }; // アクティブ=末尾
            host.Docs.SelectNext(+1);
            Assert.Same(docs[0], host.Docs.Active); // 端は巡回
        });

    [Fact]
    public void SelectNext_WrapsFromFirstToLast() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[]
            {
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
            };
            host.Docs.SelectAt(0);
            host.Docs.SelectNext(-1);
            Assert.Same(docs[2], host.Docs.Active); // 先頭から逆方向も巡回
        });

    [Fact]
    public void SelectAt_OutOfRange_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[] { host.Docs.CreateNew(), host.Docs.CreateNew() }; // アクティブ=docs[1]
            int switched = 0;
            host.Docs.KeyBasedSwitch += (_, _) => switched++;
            host.Docs.SelectAt(-1);
            host.Docs.SelectAt(2);
            Assert.Same(docs[1], host.Docs.Active);
            Assert.Equal(0, switched);
        });

    // ===== KeyBasedSwitch は実切替時のみ発火 =====

    [Fact]
    public void KeyBasedSwitch_FiresWithNewDocument_OnlyWhenIndexActuallyChanges() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[]
            {
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
            }; // アクティブ=docs[2]
            var switchedTo = new List<Document>();
            host.Docs.KeyBasedSwitch += (_, d) => switchedTo.Add(d);

            host.Docs.SelectAt(0); // 実切替 → 発火(新アクティブが渡る)
            Assert.Equal(new[] { docs[0] }, switchedTo);

            host.Docs.SelectAt(0); // 同一 index → no-op で発火しない
            Assert.Equal(new[] { docs[0] }, switchedTo);
        });

    [Fact]
    public void KeyBasedSwitch_SingleTab_SelectNext_DoesNotFire() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            int switched = 0;
            host.Docs.KeyBasedSwitch += (_, _) => switched++;
            host.Docs.SelectNext(+1); // 1 タブでは index が変わらない=冗長発声を出さない
            Assert.Equal(0, switched);
            Assert.Same(doc, host.Docs.Active);
        });

    // ===== BeforeActiveChange(切替直前フック) =====
    // MainForm はこのフックで _csv.AbortEdit()(F2 オーバーレイの後始末)を配線している。
    // 発火が消えると「編集中状態が他タブへ漏れる」退行が検出不能になるため、5 発火点を機械固定する。
    //
    // counter 設計: 可視ウィンドウではプログラム切替でも Deselecting が同期発火し(TestHost.cs の
    // xmldoc=Stage 1 プローブ実測)、その配線(ctor)経由の二重発火が「>= 1」を常に満たしてしまう=
    // 各メソッド内の明示発火点を削除しても検出できない(本 Stage の変異 A で実測確認)。
    // そこで Deselecting 回数をテスト側でも計測し「fired >= deselecting + 1」を assert する。
    // BeforeActiveChange 総数 = 明示発火 + Deselecting 由来(1:1 配線)なので、この差分 assert は
    // 「明示発火 >= 1」と等価であり、Deselecting が発火する/しない環境の双方で変異を kill できる。

    /// <summary>TabHost(実体 TabControl)の Deselecting 発火数を計測する購読を張る
    /// (Deselecting 由来の BeforeActiveChange 二重発火分を差し引くための基準値)。</summary>
    private static int[] CountDeselecting(Host host)
    {
        var count = new int[1];
        ((TabControl)host.Docs.TabHost).Deselecting += (_, _) => count[0]++;
        return count;
    }

    [Fact]
    public void CreateNew_SecondTab_FiresBeforeActiveChange() =>
        Sta.Run(() =>
        {
            // kill 対象: CreateNew 内(タブ切替直前)の BeforeActiveChange?.Invoke() 削除
            using var host = new Host();
            _ = host.Docs.CreateNew();
            int fired = 0;
            host.Docs.BeforeActiveChange = () => fired++;
            var desel = CountDeselecting(host);
            _ = host.Docs.CreateNew(); // 既存タブから切り替わる前に後始末フックが走る
            Assert.True(fired >= desel[0] + 1); // Deselecting 由来分を除いても明示発火が 1 回以上
        });

    [Fact]
    public void Activate_DifferentTab_FiresBeforeActiveChange() =>
        Sta.Run(() =>
        {
            // kill 対象: Activate 内(別タブ guard 内)の BeforeActiveChange?.Invoke() 削除
            using var host = new Host();
            var doc1 = host.Docs.CreateNew();
            _ = host.Docs.CreateNew(); // アクティブ=2 枚目
            int fired = 0;
            host.Docs.BeforeActiveChange = () => fired++;
            var desel = CountDeselecting(host);
            host.Docs.Activate(doc1);
            Assert.True(fired >= desel[0] + 1); // Deselecting 由来分を除いても明示発火が 1 回以上
        });

    [Fact]
    public void Activate_SameTab_DoesNotFire() =>
        Sta.Run(() =>
        {
            // kill 対象: Activate の guard(SelectedTab != doc.Page)の無条件化
            using var host = new Host();
            var doc = host.Docs.CreateNew(); // アクティブ=doc
            int fired = 0;
            host.Docs.BeforeActiveChange = () => fired++;
            host.Docs.Activate(doc); // SelectedTab 不変 → guard で発火しない(Deselecting も出ない)
            Assert.Equal(0, fired);
        });

    [Fact]
    public void SelectNext_FiresBeforeActiveChange() =>
        Sta.Run(() =>
        {
            // kill 対象: SelectNext 内(キーボード経路)の BeforeActiveChange?.Invoke() 削除
            using var host = new Host();
            _ = host.Docs.CreateNew();
            _ = host.Docs.CreateNew();
            int fired = 0;
            host.Docs.BeforeActiveChange = () => fired++;
            var desel = CountDeselecting(host);
            host.Docs.SelectNext(+1);
            Assert.True(fired >= desel[0] + 1); // Deselecting 由来分を除いても明示発火が 1 回以上
        });

    [Fact]
    public void SelectAt_FiresBeforeActiveChange() =>
        Sta.Run(() =>
        {
            // kill 対象: SelectAt 内(キーボード経路)の BeforeActiveChange?.Invoke() 削除
            using var host = new Host();
            _ = host.Docs.CreateNew();
            _ = host.Docs.CreateNew(); // アクティブ=index 1
            int fired = 0;
            host.Docs.BeforeActiveChange = () => fired++;
            var desel = CountDeselecting(host);
            host.Docs.SelectAt(0);
            Assert.True(fired >= desel[0] + 1); // Deselecting 由来分を除いても明示発火が 1 回以上
        });

    [Fact]
    public void BeforeActiveChange_FiresBeforeActiveSwitches() =>
        Sta.Run(() =>
        {
            // kill 対象: 発火を切替「後」へ移す変異(直前フック契約の本体=旧アクティブを後始末できること)
            using var host = new Host();
            _ = host.Docs.CreateNew();
            var doc2 = host.Docs.CreateNew(); // アクティブ=doc2
            var seen = new List<Document?>();
            host.Docs.BeforeActiveChange = () => seen.Add(host.Docs.Active);
            host.Docs.SelectAt(0); // doc2 → doc1
            Assert.NotEmpty(seen);
            Assert.All(seen, d => Assert.Same(doc2, d)); // 全発火が切替前=旧アクティブを観測する
        });

    [Fact]
    public void BeforeActiveChange_Type_IsIntentionallyAction()
    {
        // Task 1e (案 A 採択): sender/args とも意味を持たない = EventHandler 化しない意図的例外。
        // 他 5 個の event(ActiveDocumentChanged/ActiveDirtyChanged/ActiveCaretChanged/
        // EditorGotFocus/KeyBasedSwitch 等) が EventHandler 系に統一されている中の
        // 単独 Action プロパティの型を機械固定する。将来 EventHandler 化する場合は
        // 本テストを **必ず更新** すること(案 B 採用の signal になる)。
        var property = typeof(DocumentManager).GetProperty("BeforeActiveChange");
        Assert.NotNull(property);
        Assert.Equal(typeof(Action), property!.PropertyType);
    }
}
