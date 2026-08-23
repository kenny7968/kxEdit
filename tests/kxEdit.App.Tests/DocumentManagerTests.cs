using System.Reflection;

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

    // ===== FindByPath(PathKey.ForNormalized 照合)=====
    // Issue #48: 以前は照会パスと**開いている全タブのパス**に PathKey.For を打っていた
    // (= 呼び出しあたり GetFullPath が 1 + タブ数回)。不達共有上の `~` タブが 1 つあるだけで
    // Ctrl+S / 開く / grep ジャンプ / 復元のすべてが約 21 秒固まった。
    // 呼出側が正規化済みパスを渡す契約に変え、ここはファイルシステムに触れない。

    [Fact]
    public void FindByPath_MatchesCaseInsensitively() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\A.TXT";
            Assert.Same(doc, host.Docs.FindByPath(@"c:\temp\a.txt")); // 大小文字は同一視
        });

    [Fact]
    public void FindByPath_DoesNotNormalizeSeparators_CallerMustNormalize() =>
        Sta.Run(() =>
        {
            // 新契約の pin(意図的な挙動変更)。ここで区切りを吸収させると
            // GetFullPath が戻り、S-15 が丸ごと再発する。
            // App レベルの呼出側は全員 TryOpenOrActivate / NormalizeSavePath を通るので
            // 実害は無い(設計書 §3.3)。
            // このテストが縛るのは**照会パス側**だけ(下の姉妹がタブ側を縛る)。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = @"C:\Temp\a.txt";
            Assert.Null(host.Docs.FindByPath("C:/Temp/a.txt"));
        });

    [Fact]
    public void FindByPath_DoesNotNormalizeOpenTabPaths_CallerMustNormalize() =>
        Sta.Run(() =>
        {
            // 上の姉妹で、縛るのは**タブ側**(ループ内)。2 本要る理由: 照会側だけ / タブ側だけを
            // PathKey.For へ戻す変異は、もう一方のテストでは緑のまま通り抜ける(片側が
            // 区切りを吸収し、もう片側が吸収しないので結局一致しない)。
            // S-15 の実害はタブ数に比例するループ側なので、ここを空けると主犯が戻る。
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.State.Path = "C:/Temp/a.txt"; // 非正規化の綴り(旧レイアウト JSON 由来を模す)
            Assert.Null(host.Docs.FindByPath(@"C:\Temp\a.txt"));
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

    /// <summary>
    /// S-15 の主犯(<c>PathKey.For</c> = <c>GetFullPath</c>)が本当に消えたことを IL で直接固定する。
    /// 上の挙動 2 本は「<b>結果に効く</b> GetFullPath」しか捕まえられず、結果を捨てる呼出
    /// (挙動不変・コストだけが残る形)を見逃す。S-15 はコストの問題なので、
    /// 「呼出が 1 つも無い」ことをここで見る。
    /// 陽性対照(<c>ForNormalized</c> を拾えること)を同時に置くのは、走査が空を返しただけで
    /// 緑になる vacuous 化を防ぐため。
    /// <para>
    /// <b>この網の射程(Task 5 レビュー m-2・実測で生存を確認)</b>: 走査するのは
    /// <see cref="DocumentManager.FindByPath"/> の<b>直接の</b>呼出だけで、推移的な呼出は見ない。
    /// 結果を捨てる <c>GetFullPath</c> を private ヘルパ 1 段越しに置く変異は、この網を含めて
    /// 全緑のまま生存する。つまり本テストは「<c>FindByPath</c> の本体に FS 接触の呼出が
    /// 直接は無い」ことしか言っておらず、「この関数から FS に到達しない」ことは保証しない。
    /// ただし抜けるのは<b>片側だけをヘルパへ切り出した形</b>に限る: <c>ForNormalized</c> の
    /// 呼出 2 本を<b>両方</b>ヘルパへ移すと陽性対照の <c>Assert.Contains</c> が落ちて赤になるので、
    /// 本体をまるごとヘルパへ移す形は検出できる。
    /// </para>
    /// </summary>
    [Fact]
    public void FindByPath_DoesNotCallFileSystemTouchingPathKeyFor()
    {
        var callees = CalleesOf(
            typeof(DocumentManager).GetMethod(nameof(DocumentManager.FindByPath))!
        );
        // 陽性対照: 走査が実際に呼出を拾えている(拾えないなら以下の 2 本は無意味)。
        Assert.Contains(
            callees,
            m =>
                m.DeclaringType == typeof(kxEdit.Core.Text.PathKey)
                && m.Name == nameof(kxEdit.Core.Text.PathKey.ForNormalized)
        );
        Assert.DoesNotContain(
            callees,
            m =>
                m.DeclaringType == typeof(kxEdit.Core.Text.PathKey)
                && m.Name == nameof(kxEdit.Core.Text.PathKey.For)
        );
        // PathKey を経由しない直接呼び(Path.GetFullPath / Path.GetLongPathName 相当)も塞ぐ。
        Assert.DoesNotContain(callees, m => m.DeclaringType == typeof(System.IO.Path));
    }

    /// <summary>
    /// method の IL から <c>call</c> / <c>callvirt</c> の対象として解決できたメソッドを集める。
    /// オペランドを誤読した偽陽性はメタデータテーブル種別(MethodDef / MemberRef / MethodSpec)と
    /// 解決可否で捨てる。残る偽陽性は「呼んでいないものが混ざる」方向にしか働かないので、
    /// 「呼んでいない」の assert が偽陽性で<b>緑になることはない</b>
    /// (逆に、将来の本体変更で偽陽性が当たれば赤で気付ける)。
    /// </summary>
    private static List<MethodBase> CalleesOf(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        var typeArgs = method.DeclaringType!.GetGenericArguments();
        var methodArgs = method.GetGenericArguments();
        var result = new List<MethodBase>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) // call / callvirt(いずれも 4 バイトのトークンを伴う)
                continue;
            int token = BitConverter.ToInt32(il, i + 1);
            byte table = (byte)((uint)token >> 24);
            if (table != 0x06 && table != 0x0A && table != 0x2B) // MethodDef/MemberRef/MethodSpec
                continue;
            try
            {
                var m = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                if (m is not null)
                    result.Add(m);
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
                // 解決できないトークン=オペランドの誤読。呼出ではないので捨てる。
            }
        }
        return result;
    }

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

    // ===== A-1 / M-31: 任意の文書の dirty 遷移を伝えるイベント(設計 2026-08-22 §3.1) =====

    /// <summary>既存 ActiveDirtyChanged はアクティブ分しか飛ばないため、非アクティブタブの
    /// 保存(別タブで作業中の Ctrl+S 相当)を BackupCoordinator が取りこぼす。
    /// DocumentDirtyChanged は文書を引数に取り、非アクティブでも飛ぶことを固定する。</summary>
    [Fact]
    public void DocumentDirtyChanged_FiresForNonActiveDocument() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var first = host.Docs.CreateNew();
            var second = host.Docs.CreateNew(); // second がアクティブになる
            Assert.Same(second, host.Docs.Active);

            var seen = new List<Document>();
            var activeOnly = 0;
            host.Docs.DocumentDirtyChanged += (_, d) => seen.Add(d);
            host.Docs.ActiveDirtyChanged += (_, _) => activeOnly++;

            first.Editor.Text = "x";
            first.Editor.ClearSavePoint(); // 非アクティブ文書を dirty 化

            Assert.Contains(first, seen);
            Assert.Equal(0, activeOnly); // 既存イベントでは観測できないことの対照
        });

    /// <summary>dirty 化(SavePointLeft)と clean 化(SavePointReached)の両方で飛ぶ。
    /// 片方だけの配線だと、購読側が「clean 化のみ処理する」フィルタを持てない。</summary>
    [Fact]
    public void DocumentDirtyChanged_FiresOnBothLeftAndReached() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.Docs.CreateNew();
            doc.Editor.Text = "x";

            var states = new List<bool>();
            host.Docs.DocumentDirtyChanged += (_, d) => states.Add(d.Editor.Modified);

            doc.Editor.ClearSavePoint(); // → dirty
            doc.Editor.SetSavePoint(); // → clean

            Assert.Contains(true, states);
            Assert.Contains(false, states);
        });
}
