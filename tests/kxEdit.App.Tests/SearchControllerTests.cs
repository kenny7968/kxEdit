using System.Linq;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Csv;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 4: SearchController の配線・歩進状態・通知文言のテスト(設計書 §3)。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeFindReplaceView)と
/// 通知(FakeAnnouncer)だけを偽物にする。照合・件数の正しさ(SnapshotSearcher)は
/// Core 検証済みのため再検証しない(責務=歩進・スコープ・状態リセット・文言の配線)。
/// </summary>
public class SearchControllerTests
{
    /// <summary>SearchController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public SearchController Search { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public FakeFindReplaceView View { get; } = new();
        public FindReplaceCallbacks? Callbacks; // 直近のファクトリ呼び出しで渡されたコールバック束
        public int FactoryCalls;

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Search = new SearchController(
                docs,
                form,
                Announcer,
                cb =>
                {
                    FactoryCalls++;
                    Callbacks = cb;
                    return View;
                }
            );
        }

        /// <summary>クリーンな本文を持つアクティブ文書を作る(Text セッター=新規バッファで Modified=false・キャレット 0)。</summary>
        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== Open(ビューのライフサイクルと表示配線) =====

    [Fact]
    public void OpenFind_ShowsViewInFindMode_AndClearsStatus() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");

            host.Search.OpenFind();

            Assert.Equal(1, host.FactoryCalls);
            Assert.Equal(new[] { false }, host.View.ModeLog); // 検索モード
            Assert.Equal(1, host.View.ShowAndFocusCount);
            Assert.True(host.View.Visible);
            Assert.Equal("", host.View.Status); // 空パターン=ステータスはクリア
            Assert.Empty(host.Announcer.Said); // Open は発声しない
        });

    [Fact]
    public void OpenReplace_ShowsViewInReplaceMode() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");

            host.Search.OpenReplace();

            Assert.Equal(new[] { true }, host.View.ModeLog);
            Assert.Equal(1, host.View.ShowAndFocusCount);
        });

    [Fact]
    public void Open_ReusesView_WhileAlive() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");

            host.Search.OpenFind();
            host.Search.OpenReplace(); // 検索→置換の切替は同一ビューのモード変更

            Assert.Equal(1, host.FactoryCalls);
            Assert.Equal(new[] { false, true }, host.View.ModeLog);
            Assert.Equal(2, host.View.ShowAndFocusCount); // 再表示のたびフォーカス手順
        });

    [Fact]
    public void Open_RecreatesView_AfterDispose() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.Search.OpenFind();

            host.View.IsDisposed = true; // owner クローズ等でダイアログが破棄された状況
            host.Search.OpenFind();

            Assert.Equal(2, host.FactoryCalls); // 作り直す(Disposed ビューを使い回さない)
        });

    // ===== コールバック束の対応固定(Task 3 品質レビュー Important 対応) =====

    [Fact]
    public void Callbacks_AreWiredToMatchingControllerMethods() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            var cb = host.Callbacks!;
            Assert.NotNull(cb);

            // Func<bool> 2 本の判別: FindNext は前進・FindPrev は後退(取り違えると選択位置で失敗)
            Assert.True(cb.FindNext());
            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
            Assert.True(cb.FindNext());
            Assert.Equal((4, 7), doc.Editor.GetSelectionCharRange());
            Assert.True(cb.FindPrev());
            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());

            // Action 3 本の判別 1: UpdateCount は発声も本文変更もしない
            int saidBefore = host.Announcer.Said.Count;
            cb.UpdateCount();
            Assert.Equal(saidBefore, host.Announcer.Said.Count);
            Assert.Equal("abc abc", doc.Editor.Text);

            // Action<bool> は 1 本のみ=型で一意(OFF に戻して以降の置換へ影響させない)
            cb.InSelectionToggled(false);

            // Action 3 本の判別 2: ReplaceOne は選択中の 1 件のみ置換(ReplaceAll なら "X X" になる)
            cb.ReplaceOne();
            Assert.Equal("X abc", doc.Editor.Text);

            // Action 3 本の判別 3: ReplaceAll は全置換(ReplaceOne なら 1 件のみ)
            doc.Editor.Text = "abc abc";
            cb.ReplaceAll();
            Assert.Equal("X X", doc.Editor.Text);
        });

    // ===== UpdateCount(ステータスのみ・発声しない) =====

    [Fact]
    public void UpdateCount_WithHits_ShowsCount_WithoutSpeech() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";

            host.Search.OpenFind(); // Open 経由で UpdateCount が走る

            Assert.Equal("3 件", host.View.Status);
            Assert.Empty(host.Announcer.Said); // 件数はステータスのみ(発声しない)
        });

    [Fact]
    public void UpdateCount_NoHits_ShowsNotFound() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "xyz";

            host.Search.OpenFind();

            Assert.Equal("見つかりません", host.View.Status);
        });

    [Fact]
    public void UpdateCount_InvalidRegex_ShowsErrorStatus() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "(";
            host.View.UseRegex = true;

            host.Search.OpenFind();

            Assert.Equal("正規表現が正しくありません", host.View.Status);
            Assert.Empty(host.Announcer.Said); // カウントのエラーは通知しない(ステータスのみ)
        });

    // ===== Announce 契約(非表示ビューを経由しない=G-2 の支え) =====

    [Fact]
    public void Announce_ViewHidden_SpeaksWithoutStatusUpdate() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            host.View.Visible = false; // G-2: 検索モードは「次を検索」成功後にダイアログが Hide される
            int statusBefore = host.View.StatusLog.Count;

            Assert.True(host.Search.FindNext()); // F3/メニュー経路(ダイアログ非表示のまま)

            Assert.Equal("1 件中 1 件目", host.Announcer.Said[^1]); // 発声は共有 Announcer 直結で成立
            Assert.Equal(statusBefore, host.View.StatusLog.Count); // 非表示中は SetStatus しない
        });

    // ===== FindNext/FindPrev(歩進=_lastHit と選択の一致判定) =====

    [Fact]
    public void FindNext_SelectsFirstHit_AndAnnouncesOrdinal() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
            Assert.Equal("3 件中 1 件目", host.Announcer.Said[^1]);
            Assert.Equal("3 件中 1 件目", host.View.Status); // 表示中はダイアログ内ステータスにも同文言
        });

    [Fact]
    public void FindNext_Repeated_AdvancesFromLastHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();

            host.Search.FindNext();
            Assert.True(host.Search.FindNext()); // 選択が _lastHit と一致=その次から

            Assert.Equal((4, 7), doc.Editor.GetSelectionCharRange());
            Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void FindNext_ZeroWidthHit_AdvancesByOne() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("aaa");
            host.View.Pattern = "(?=a)"; // ゼロ幅ヒット(長さ 0)
            host.View.UseRegex = true;
            host.Search.OpenFind();

            host.Search.FindNext(); // (0,0)
            Assert.True(host.Search.FindNext()); // Max(1, h.Length)=1 で前進(同位置に張り付かない)

            Assert.Equal((1, 1), doc.Editor.GetSelectionCharRange());
            Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void FindNext_SelectionMovedByUser_SearchesFromSelectionEnd() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            host.Search.FindNext(); // (0,3)
            doc.Editor.SelectCharRange(5, 0); // ユーザーがキャレット移動(選択≠_lastHit)

            Assert.True(host.Search.FindNext());

            Assert.Equal((8, 11), doc.Editor.GetSelectionCharRange()); // 5 以降の次ヒット(4 始まりは跨ぎ済み)
            Assert.Equal("3 件中 3 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void FindNext_NoMoreHits_AnnouncesWithoutMoving() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            host.Search.FindNext(); // (0,3)=最後のヒット

            Assert.False(host.Search.FindNext()); // 折り返さない

            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange()); // 選択は動かない
        });

    [Fact]
    public void FindPrev_FromLastHit_SelectsPreviousHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            host.Search.FindNext();
            host.Search.FindNext(); // (4,7)=2 件目

            Assert.True(host.Search.FindPrev()); // _lastHit の Start より前を探す

            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
            Assert.Equal("3 件中 1 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void Find_InvalidRegex_AnnouncesError() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "(";
            host.View.UseRegex = true;
            host.Search.OpenFind();

            Assert.False(host.Search.FindNext());

            Assert.Equal("正規表現が正しくありません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void FindNext_BeforeOpeningDialog_ReturnsFalse_Silently() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");

            Assert.False(host.Search.FindNext()); // Ctrl+F 前の F3/メニュー: ビュー未生成=条件不足で無反応

            Assert.Empty(host.Announcer.Said);
            Assert.Equal(0, host.FactoryCalls); // 勝手にビューを作らない
        });

    // ===== 検索オプション配線(MatchCase/WholeWord) =====
    // 件数 assert は引数 swap で対称になり得るため、FindNext 後の選択位置で判別する。

    [Fact]
    public void FindNext_MatchCaseTrue_SkipsCaseMismatch() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("ABC abc");
            host.View.Pattern = "abc";
            host.View.MatchCase = true;
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            // swap 変異(WholeWord=true/MatchCase=false 扱い)だと先頭の ABC=単語一致 (0,3) を選択するため選択位置で赤になる
            Assert.Equal((4, 7), doc.Editor.GetSelectionCharRange());
            Assert.Equal("1 件中 1 件目", host.Announcer.Said[^1]); // ABC は数えない(大小区別を件数でも固定)
        });

    [Fact]
    public void FindNext_MatchCaseFalse_MatchesBothCases() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("ABC abc");
            host.View.Pattern = "abc"; // MatchCase=false(既定)のまま=OFF 方向の配線固定
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            // d.MatchCase を定数 true 化する変異だと ABC を飛ばして (4,7)="1 件中 1 件目" になるため選択位置と序数の両面で赤になる
            Assert.Equal((0, 3), doc.Editor.GetSelectionCharRange());
            Assert.Equal("2 件中 1 件目", host.Announcer.Said[^1]); // 大文字 ABC もヒットに数える
        });

    [Fact]
    public void FindNext_WholeWordTrue_SkipsPartialWord() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abcx abc");
            host.View.Pattern = "abc";
            host.View.WholeWord = true;
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            // swap 変異(MatchCase=true/WholeWord=false 扱い)だと abcx 内の部分一致 (0,3) を選択するため選択位置で赤になる
            Assert.Equal((5, 8), doc.Editor.GetSelectionCharRange());
            Assert.Equal("1 件中 1 件目", host.Announcer.Said[^1]); // abcx 内の部分一致は数えない(単語境界を件数でも固定)
        });

    // ===== 文書切替(_lastHit/_selectionScope のリセット+件数の追随) =====

    [Fact]
    public void ActiveDocumentChanged_ResetsStepState() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.NewDoc("aaa");
            host.View.Pattern = "(?=a)"; // ゼロ幅: リセット有無で歩進結果が分かれる(通常パターンでは区別不能)
            host.View.UseRegex = true;
            host.Search.OpenFind();
            host.Search.FindNext();
            host.Search.FindNext(); // (1,1)=2 件目・_lastHit=(1,0)

            _ = host.NewDoc("x"); // 文書切替(リセット発火)
            host.Docs.SelectAt(0); // doc1 へ戻す(再度リセット・選択 (1,1) は保持されている)

            int saidBefore = host.Announcer.Said.Count; // setup の 2 回目 FindNext が同一文言を発声済みのため件数でも検証
            Assert.True(host.Search.FindNext());
            // リセット済みなら選択終端(1)から再探索=同じ 2 件目。_lastHit が残っていれば 1+Max(1,0)=2 から=3 件目になる
            Assert.Equal((1, 1), doc1.Editor.GetSelectionCharRange());
            Assert.Equal("3 件中 2 件目", host.Announcer.Said[^1]);
            Assert.Equal(saidBefore + 1, host.Announcer.Said.Count); // 新規発声が 1 件増えた(既存文言との空振り一致でない)
        });

    [Fact]
    public void ActiveDocumentChanged_WhileVisible_RefreshesCount() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            Assert.Equal("1 件", host.View.Status);

            _ = host.Docs.CreateNew(); // 空の新文書がアクティブに

            Assert.Equal("見つかりません", host.View.Status); // 新アクティブ文書で件数を更新
        });

    [Fact]
    public void ActiveDocumentChanged_ClearsSelectionScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc1.Editor.SelectCharRange(0, 3);
            host.Search.OnInSelectionToggled(true); // doc1 で [0,3) を捕捉

            var doc2 = host.NewDoc("abc"); // 文書切替=捕捉済みスコープ無効化
            host.Search.ReplaceAll();

            Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc2.Editor.Text); // 新文書は置換されない
            Assert.Equal("abc abc", doc1.Editor.Text); // 旧文書のスコープへも波及しない
        });

    // ===== ReplaceOne(VSCode 準拠 G-3: 未選択なら検索して即置換) =====

    [Fact]
    public void ReplaceOne_SelectedHit_ReplacesAndSelectsNext() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3) を選択

            host.Search.ReplaceOne();

            Assert.Equal("X abc abc", doc.Editor.Text);
            Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange()); // 置換後テキスト上の次ヒットを選択
            Assert.Equal("置換しました。2 件中 1 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_NoSelection_ReplacesNextHitImmediately() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace(); // キャレット (0,0)・選択なしのまま

            host.Search.ReplaceOne(); // G-3: 検索して即置換(選択待ちの空振りにしない)

            Assert.Equal("X abc", doc.Editor.Text);
            Assert.Equal((2, 5), doc.Editor.GetSelectionCharRange());
            Assert.Equal("置換しました。1 件中 1 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_LastHit_AnnouncesReplacedAndNoMore() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext();

            host.Search.ReplaceOne();

            Assert.Equal("X", doc.Editor.Text);
            Assert.Equal("置換しました。これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_EmptyReplacement_DoesNotSkipAdjacentHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("aa");
            host.View.Pattern = "a";
            host.View.Replacement = "";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,1)

            host.Search.ReplaceOne(); // 空置換(削除)後の前進は repl.Length=0(+1 すると隣接ヒットを取りこぼす)

            Assert.Equal("a", doc.Editor.Text);
            Assert.Equal((0, 1), doc.Editor.GetSelectionCharRange()); // 詰めて隣接した次ヒットを選択
            Assert.Equal("置換しました。1 件中 1 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InCsvMode_IsBlocked() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            doc.State.CsvMode = true; // CsvController を介さず状態だけ立てる(判定は State 経由)

            host.Search.ReplaceOne();

            Assert.Equal("abc", doc.Editor.Text); // 読取専用本文への無反映置換=誤成功通知を出さない
            Assert.Equal(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InvalidRegex_AnnouncesError() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "(";
            host.View.UseRegex = true;
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            host.Search.ReplaceOne(); // Find と別コードパスの同ガード(削除すると「これ以上見つかりません」の誤通知になる)

            Assert.Equal("正規表現が正しくありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc.Editor.Text);
        });

    // ===== A-14: CRLF 文書で現ヒットを取り違えない =====

    [Fact]
    public void ReplaceOne_RegexLfInCrlfDocument_ReplacesTheSelectedHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 2 行目以降を持たせて「次の出現を置換した」と「その位置を置換した」を弁別する。
            var doc = host.NewDoc("abc\r\ndef\r\nghi");
            host.View.Pattern = @"\n";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext(); // 1 つ目の LF(index 4)にヒット=選択は [3,5) にスナップされる

            host.Search.ReplaceOne();

            // 修正前は ReplacementAt が外れて FindNext(5) に落ち、2 つ目の LF を置換して
            // "abc\r\ndef\rXghi" になっていた。
            Assert.Equal("abc\rXdef\r\nghi", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_RegexLfInCrlfDocument_MatchesReplaceAllResult() =>
        Sta.Run(() =>
        {
            // 単発を一括に揃える=同じ 1 件だけの文書で両者の結果が一致すること。
            using var one = new Host();
            using var all = new Host();
            var docOne = one.NewDoc("abc\r\ndef");
            var docAll = all.NewDoc("abc\r\ndef");
            foreach (var h in new[] { one, all })
            {
                h.View.Pattern = @"\n";
                h.View.Replacement = "X";
                h.View.UseRegex = true;
                h.Search.OpenReplace();
            }

            one.Search.FindNext();
            one.Search.ReplaceOne();
            all.Search.ReplaceAll();

            Assert.Equal(docAll.Editor.Text, docOne.Editor.Text);
            Assert.Equal("abc\rXdef", docOne.Editor.Text);
        });

    [Fact]
    public void FindNext_RegexCrInCrlfDocument_AdvancesToNextHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc\r\ndef\r\nghi");
            host.View.Pattern = @"\r";
            host.View.Replacement = "";
            host.View.UseRegex = true;
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext()); // 1 つ目の CR(index 3)。選択は [3,3) に潰れる
            Assert.True(host.Search.FindNext()); // 修正前はここが同じ位置に留まっていた

            // 2 つ目の CR(index 8)へ進んだ = 選択の始端が 8 になっている
            Assert.Equal(8, doc.Editor.GetSelectionCharRange().Start);
        });

    [Fact]
    public void ReplaceOne_RegexCrInCrlfDocument_KeepsTheLf() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc\r\ndef");
            host.View.Pattern = @"\r";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext();

            host.Search.ReplaceOne();

            Assert.Equal("abcX\ndef", doc.Editor.Text); // LF を巻き込まない
        });

    [Fact]
    public void ReplaceOne_AfterUserMovesSelection_FallsBackToSearchFromCaret() =>
        Sta.Run(() =>
        {
            // 現ヒットが「生きていない」ときは従来経路(次を検索して即置換)のままであること。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3) を選択

            doc.Editor.SetCaretCharOffset(4); // ユーザーが選択を動かした=現ヒットは無効

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // キャレット以降の最初のヒットを置換
        });

    [Fact]
    public void ReplaceOne_AfterExternalEdit_DoesNotReuseStaleHit() =>
        Sta.Run(() =>
        {
            // 先頭挿入で選択が (4,7) へ動くので、この網が実際に通るのは「選択そのものがヒット」
            // の中間分岐。見ているのは「捕捉時のずれた (0,3) を使わない」ことだけで、
            // 世代チェックそのものは検査していない(世代チェックを外しても緑)。
            // 世代チェックを固定しているのは AfterEditThatKeepsTheSelection のほう。
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3)

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 先頭へ挿入。選択も (4,7) へ動く
            host.Search.ReplaceOne();

            Assert.Equal("QQQQX abc", doc.Editor.Text); // ずれた (0,3) を使っていない
        });

    [Fact]
    public void ReplaceOne_PatternChangedAfterFind_DoesNotReuseStaleHit() =>
        Sta.Run(() =>
        {
            // 第 1 分岐の ReplacementAt ガードの網。ResolveSearcher は照合条件が変われば
            // searcher を作り直すが _lastHit はクリアしない。文書もスナップショットも選択も
            // 不変なので LiveHit は生きたままで、新しい照合条件に対して現ヒットが
            // ヒットでなくなったことは ReplacementAt でしか分からない。
            using var host = new Host();
            var doc = host.NewDoc("abc def");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            host.Search.FindNext(); // (0,3) を選択・_lastHit=(0,3)

            host.View.Pattern = "def"; // 選択も文書もそのまま、照合条件だけ変わる

            host.Search.ReplaceOne();

            // ガードを外すと選択中の "abc" を "X" に潰す(SR ユーザーには置換位置が見えない)。
            Assert.Equal("abc X", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_ZeroWidthHitInsideCrlf_AdvancesFromTheReturnedOffset() =>
        Sta.Run(() =>
        {
            // 置換後の前進起点は ReplaceCharRangeExact の戻り値であって span.Start + repl.Length
            // ではない。ゼロ幅ヒットは挿入点が論理文字の境界まで後退する(CRLF は割らない)ので、
            // 導出値だと 1 code unit ぶん後ろから探し始め、次のヒットを 1 件飛ばす。
            // 飛ばしても本文と選択は一致してしまう(選択も境界へスナップされるため)。
            // 弁別できるのは通知だけなので、そこを固定する(この fixture では序数がずれる。
            // 別の fixture では「これ以上見つかりません」になる=通知の内容は fixture 次第)。
            using var host = new Host();
            var doc = host.NewDoc("a\r\nb");
            // 前半=CR と LF の間のゼロ幅ヒット。後半=置換で入る X の直後のゼロ幅ヒット
            // (置換後に「飛ばされる側」の 1 件を作るために要る)。
            host.View.Pattern = @"(?<=\r)(?=\n)|(?<=X)";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext();

            host.Search.ReplaceOne();

            Assert.Equal("aX\r\nb", doc.Editor.Text);
            Assert.Equal((2, 2), doc.Editor.GetSelectionCharRange());
            // 本文も選択も変異と同じ。序数だけが違う(変異では「2 件中 2 件目」になる)。
            Assert.Equal("置換しました。2 件中 1 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_ManuallySelectedMatch_ReplacesTheSelection() =>
        Sta.Run(() =>
        {
            // 挙動不変の網(A-14 修正前から緑)。Find を使わず手で語を選んで「置換」を押す操作。
            // 現ヒットが無いからといって FindNext(selEnd) へ落とすと、選択の「次」の出現を
            // 置換してしまう(SR ユーザーには置換位置が見えないので気付けない)。
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            doc.Editor.SelectCharRange(0, 3); // 検索を経由せず選択だけ作る

            host.Search.ReplaceOne();

            Assert.Equal("X abc", doc.Editor.Text);
        });

    [Fact]
    public void FindPrev_FromLfHitOfCrlf_DoesNotSkipTheCrHit() =>
        Sta.Run(() =>
        {
            // 後方検索も選択の始端ではなく現ヒットの始端から遡る。CRLF の LF ヒットは
            // 選択の始端が CR まで後退するので、選択基準だと [selStart, Hit.Start) に居る
            // CR ヒットを飛ばす(F3 前進側と同じ取りこぼしの鏡像)。
            using var host = new Host();
            host.NewDoc("a\r\nb\r\nc"); // [\r\n] のヒットは index 1 / 2 / 4 / 5 の 4 件
            host.View.Pattern = @"[\r\n]";
            host.View.UseRegex = true;
            host.Search.OpenFind();

            for (int i = 0; i < 4; i++)
                Assert.True(host.Search.FindNext()); // 4 件目(index 5 の LF)まで進む

            Assert.True(host.Search.FindPrev());

            // 3 件目=index 4 の CR。選択始端基準だと 2 件目(index 2 の LF)へ飛んでしまう。
            Assert.Equal("4 件中 3 件目", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_AfterEditThatKeepsTheSelection_DoesNotReuseStaleHit() =>
        Sta.Run(() =>
        {
            // 世代チェックの網。選択の数値だけでは「同じヒットを選んだまま」を判定できない
            // (末尾を編集しても手前の選択位置はずれない)ので、スナップショット参照で弁別する。
            using var host = new Host();
            var doc = host.NewDoc("abc\r\ndef\r\nghi");
            host.View.Pattern = @"\n";
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.Search.OpenReplace();
            host.Search.FindNext(); // ヒット (4,1)・選択は [3,5) へスナップ

            doc.Editor.ReplaceCharRange(12, 1, "Z"); // 末尾を編集=世代が変わる(手前の位置は不動)
            doc.Editor.SelectCharRange(3, 2); // 捕捉時と同じ選択へ戻す

            host.Search.ReplaceOne();

            // 現ヒットは死んでいる。選択 [3,5)="\r\n" は \n のヒットではないので従来経路へ落ち、
            // 2 つ目の LF(index 9)を置換する。世代チェックを外すと 1 つ目を置換して
            // "abc\rXdef\r\nghZ" になる。
            Assert.Equal("abc\r\ndef\rXghZ", doc.Editor.Text);
        });

    // ===== ReplaceAll(全文/捕捉済み選択スコープ) =====

    [Fact]
    public void ReplaceAll_ReplacesAllMatches_AndAnnouncesCount() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            host.Search.ReplaceAll();

            Assert.Equal("X X X", doc.Editor.Text);
            Assert.Equal("3 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_NoMatch_AnnouncesNotFound_AndKeepsText() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "xyz";
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            host.Search.ReplaceAll();

            Assert.Equal("abc", doc.Editor.Text);
            Assert.Equal("見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_ReplacesOnlyCapturedScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 7); // "abc abc" を選択
            host.Search.OnInSelectionToggled(true); // スコープ捕捉

            host.Search.ReplaceAll();

            Assert.Equal("X X abc", doc.Editor.Text); // 範囲外の 3 件目は置換されない
            Assert.Equal("2 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_WithoutCapturedScope_Announces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            host.Search.OnInSelectionToggled(true); // 選択なし(ゼロ幅)で ON=スコープは捕捉されない

            host.Search.ReplaceAll();

            Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceAll_CapturedScope_SurvivesFindMoves() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 7);
            host.Search.OnInSelectionToggled(true); // [0,7) を捕捉

            Assert.True(host.Search.FindNext()); // 検索移動で実選択は (8,11) へクロバーされる
            host.Search.ReplaceAll();

            Assert.Equal("X X abc", doc.Editor.Text); // 捕捉時のスコープが生きている(実選択に追随しない)
            Assert.Equal("2 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_AfterEdit_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 後半の "abc" だけを選択
            host.Search.OnInSelectionToggled(true); // [4,7) を捕捉

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 先頭へ挿入=捕捉位置が別の中身を指す

            host.Search.ReplaceAll();

            // 修正前はここで [4,7)=前半の "abc"(ユーザーが選択していない側)が置換され
            // "QQQQX abc" + 「1 件置換しました」になっていた。
            Assert.Equal("QQQQabc abc", doc.Editor.Text); // 一文字も書き換えない
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_UnchangedSnapshot_StillReplacesScopeOnly() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 中央だけを捕捉する=prefix "abc " と suffix " abc" の両方を除外する fixture
            // (全選択との区別。CLAUDE.md §4 のテスト設計の教訓)。
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true); // [4,7) を捕捉

            host.Search.ReplaceAll(); // 編集を挟まない=スナップショットは同一

            Assert.Equal("abc X abc", doc.Editor.Text); // 前後の 2 件は残る
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_AfterBufferSwap_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 3);
            host.Search.OnInSelectionToggled(true); // [0,3) を捕捉

            // 同一タブでバッファごと差し替え(開き直し・復元・EOL 変換の相当)。
            // 文書切替イベントは起きないので ActiveDocumentChanged では捕まらない経路。
            doc.Editor.Text = "abc zzz";

            host.Search.ReplaceAll();

            Assert.Equal("abc zzz", doc.Editor.Text); // 差し替え後の [0,3) は "abc" で一致するが置換しない
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_RetoggleAfterEdit_RecapturesScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true); // 古いスコープ
            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 陳腐化させる

            doc.Editor.SelectCharRange(8, 3); // "QQQQabc abc" の後半 "abc"
            host.Search.OnInSelectionToggled(true); // 取り直す
            host.Search.ReplaceAll();

            Assert.Equal("QQQQabc X", doc.Editor.Text); // 取り直した範囲だけが置換される
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_StaleScope_IsDroppedAfterRefusal() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);
            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 陳腐化させる

            host.Search.ReplaceAll(); // 1 回目=拒否
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);

            host.Search.ReplaceAll(); // 2 回目=拒否時にスコープを捨てているので「ありません」へ落ちる

            Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
            Assert.Equal("QQQQabc abc", doc.Editor.Text); // どちらの回でも書き換えない
        });

    [Fact]
    public void ReplaceAll_InSelection_AfterUndoToSameContent_StillRefuses() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);
            doc.Editor.ReplaceCharRange(0, 0, "QQQQ");
            doc.Editor.Undo();
            Assert.Equal("abc abc", doc.Editor.Text); // 内容は捕捉時と同一に戻っている

            host.Search.ReplaceAll();

            // TextBuffer.Undo は同じ Root を新しい TextSnapshot で包み直すため参照は一致しない。
            // 内容が同一でも「陳腐化」と見なす=安全側。TextBuffer.Modified(Root 比較)との差。
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);
            Assert.Equal("abc abc", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceAll_InSelection_ConsecutiveReplacesStayInScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("aaa bbb ccc");
            host.View.Pattern = "aaa";
            // 置換で範囲が伸びる語を選ぶ。旧 rangeLen(7)で取り直す実装だと 2 回目の "bbb" が
            // 範囲外へ落ちて「見つかりません」になるため、境界が fragment.Length であることまで固定できる。
            host.View.Replacement = "LONGER";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 7); // "aaa bbb" を捕捉(suffix " ccc" を除外)
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceAll(); // 1 回目=範囲は [0,7) → [0,10) へ伸びる
            Assert.Equal("LONGER bbb ccc", doc.Editor.Text);

            host.View.Pattern = "bbb"; // 同じ範囲のまま語を変えて続ける(現実的なワークフロー)
            host.View.Replacement = "Y";
            host.Search.ReplaceAll(); // 2 回目

            Assert.Equal("LONGER Y ccc", doc.Editor.Text); // 範囲外の "ccc" は残る
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_ScopeEndInsideCrlf_DoesNotDuplicateCr() =>
        Sta.Run(() =>
        {
            // main 既存バグ(本ブランチの退行ではない)。ReplaceInRange は素の範囲
            // [start, start+len) で断片を組むのに、書き戻しが非 Exact な ReplaceCharRange だと
            // 両端をスナップして範囲を「狭める」ため、断片と書込先の長さが食い違う。
            // スコープ端が CRLF の内側にあると CR が重複して空行が増える。
            using var host = new Host();
            var doc = host.NewDoc("a\rXY\nb");
            host.View.Pattern = "XY";
            host.View.Replacement = "";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 4); // "a\rXY" を捕捉(位置 4 はまだ境界)
            host.Search.OnInSelectionToggled(true);

            // 単発置換で XY を消すと CR と LF が隣接し、スコープ終端 2 が CRLF の内側になる。
            host.Search.ReplaceOne();
            Assert.Equal("a\r\nb", doc.Editor.Text);

            host.View.Pattern = "a";
            host.View.Replacement = "ZZ";
            host.Search.ReplaceAll();

            // 修正前は "ZZ\r\r\nb"(CR が重複=空行が 1 行増える)。
            Assert.Equal("ZZ\r\nb", doc.Editor.Text);
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InSelection_ScopeStartInsideCrlf_DoesNotDeleteOutsideCr() =>
        Sta.Run(() =>
        {
            // 始端側は被害がさらに悪く、選択範囲「外」の文字が黙って消える(発声は成功のまま)。
            using var host = new Host();
            var doc = host.NewDoc("a\rXY\nb");
            host.View.Pattern = "XY";
            host.View.Replacement = "";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(2, 4); // "XY\nb" を捕捉(prefix "a\r" を除外)
            host.Search.OnInSelectionToggled(true);

            // 単発置換で XY を消すと、スコープ始端 2 が CRLF の内側になる。
            host.Search.ReplaceOne();
            Assert.Equal("a\r\nb", doc.Editor.Text);

            host.View.Pattern = "b";
            host.View.Replacement = "Q";
            host.Search.ReplaceAll();

            // 修正前は "a\nQ"=スコープ外(index 1)の CR が消える。
            Assert.Equal("a\r\nQ", doc.Editor.Text);
            Assert.Equal("1 件置換しました", host.Announcer.Said[^1]);
        });

    // ===== T-3: 「選択範囲のみ」を単発置換にも効かせる =====

    [Fact]
    public void ReplaceOne_InSelection_CaretAfterScope_DoesNotReplaceOutsideScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // prefix "abc " と suffix " abc" の両方を除外できる fixture(全選択との区別)。
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央の "abc" だけを捕捉
            host.Search.OnInSelectionToggled(true);
            doc.Editor.SetCaretCharOffset(8); // キャレットをスコープの外(3 件目の先頭)へ

            host.Search.ReplaceOne();

            // 修正前は 3 件目が置換され "abc abc X" + 成功発声になっていた。
            Assert.Equal("abc abc abc", doc.Editor.Text);
            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_SelectionOutsideScope_IsNotReplaced() =>
        Sta.Run(() =>
        {
            // 第 2 分岐(選択そのものがヒット)のガードの網。
            // スコープ捕捉後に手でスコープ外のヒットを選び直しても置換しない。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央を捕捉
            host.Search.OnInSelectionToggled(true);
            doc.Editor.SelectCharRange(8, 3); // 手で 3 件目を選び直す(選択はヒットそのもの)

            host.Search.ReplaceOne();

            Assert.Equal("abc abc abc", doc.Editor.Text);
            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_FindMovedOutsideScope_IsNotReplaced() =>
        Sta.Run(() =>
        {
            // 第 1 分岐(生きている現ヒット)のガードの網。
            // 「範囲を捕捉 → F3 で範囲外へ移動 → 置換」= 現ヒットが生きたままスコープ外にある。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央を捕捉
            host.Search.OnInSelectionToggled(true);

            Assert.True(host.Search.FindNext()); // 3 件目 (8,11) へ移動(Find は全文のまま)

            host.Search.ReplaceOne();

            Assert.Equal("abc abc abc", doc.Editor.Text);
            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_ZeroWidthHitOutsideScope_IsNotReplaced() =>
        Sta.Run(() =>
        {
            // 第 1 分岐のガードだけを弁別する網。上の Find 版は選択もヒットそのものなので
            // 第 2 分岐のガードでも止まる=第 1 分岐のガードを外しても落ちない。
            // ゼロ幅ヒットは選択が幅ゼロになり第 2 分岐が `selEnd > selStart` で短絡するため、
            // 止められるのは第 1 分岐のガードだけになる。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "(?=abc)"; // ゼロ幅=位置 0 / 4 / 8 にヒット
            host.View.Replacement = "X";
            host.View.UseRegex = true;
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 中央を捕捉
            host.Search.OnInSelectionToggled(true);

            Assert.True(host.Search.FindNext()); // 位置 8 のゼロ幅ヒットへ移動(選択は (8,8))

            host.Search.ReplaceOne();

            // 第 1 分岐のガードを外すと "abc abc Xabc" になる(スコープ外へ X を挿入)。
            Assert.Equal("abc abc abc", doc.Editor.Text);
            Assert.Equal("これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_ReplacesInsideScope() =>
        Sta.Run(() =>
        {
            // 過剰無効化の網。この操作は修正前も動いていた(第 2 分岐を通る)。
            // _LastHitInScope_AnnouncesNoMore と fixture は同一だが、重複ではなく意図的な対:
            // こちらは本文、あちらは発声文言という別の観測面を見ており、互いに部分集合ではない
            // (span.Length を +1 する変異はこちらだけを、スコープ伸縮の符号反転と置換後の
            //  包含判定除去はあちらだけを落とす)。畳むと弁別が消えるので分けたまま残す。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // 前後の 2 件は残る
        });

    [Fact]
    public void ReplaceOne_InSelection_CaretBeforeScope_SkipsForwardIntoScope() =>
        Sta.Run(() =>
        {
            // 起点をスコープ先頭まで繰り上げる=スコープより前のヒットを置換しない。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);
            doc.Editor.SetCaretCharOffset(0); // キャレットをスコープより前へ

            host.Search.ReplaceOne();

            Assert.Equal("abc X abc", doc.Editor.Text); // 1 件目ではなく 2 件目が置換される
        });

    [Fact]
    public void ReplaceOne_InSelection_TwiceInARow_SecondIsNotRefused() =>
        Sta.Run(() =>
        {
            // 置換のたびにスコープを伸縮させて捕捉し直さないと 2 回目が「陳腐化」で拒否される。
            using var host = new Host();
            var doc = host.NewDoc("zz abc abc zz");
            host.View.Pattern = "abc";
            host.View.Replacement = "XY"; // 長さが変わる=伸縮の計算を効かせる
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(3, 7); // "abc abc" を捕捉(選択はヒットそのものではない)
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();
            host.Search.ReplaceOne();

            Assert.Equal("zz XY XY zz", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_LongerReplacement_GrowsScopeToKeepFollowingHit() =>
        Sta.Run(() =>
        {
            // スコープ伸縮の「伸ばす」向きの網。TwiceInARow は縮む向き(3→2)なので、
            // 伸縮を丸ごと落とす変異(End 据え置き)では許容側に倒れて生き残る。
            // 伸ばし忘れ=スコープ内の未置換ヒットを取りこぼす。
            using var host = new Host();
            var doc = host.NewDoc("zz abc abc zz");
            host.View.Pattern = "abc";
            host.View.Replacement = "XYZW"; // 3 → 4 文字=スコープは 1 文字ぶん伸びる必要がある
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(3, 7); // "abc abc" を捕捉(前後の "zz" を除外)
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();
            host.Search.ReplaceOne();

            // 伸ばし忘れると 2 件目が [3,10) からはみ出し、1 回目で
            // 「置換しました。これ以上見つかりません」になって "zz XYZW abc zz" で止まる。
            Assert.Equal("zz XYZW XYZW zz", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_ShorterReplacement_ShrinksScopeToProtectOutside() =>
        Sta.Run(() =>
        {
            // スコープ伸縮の「縮める」向きの網。伸ばす向きと違い、こちらを落とすと
            // 選択範囲外の文字が置換される=T-3 が潰そうとしている不具合クラスそのもの。
            using var host = new Host();
            var doc = host.NewDoc("abcdef");
            host.View.Pattern = "."; // 1 文字ずつ削除していく=1 回の置換で 1 文字ぶん縮む
            host.View.UseRegex = true;
            host.View.Replacement = "";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(0, 2); // "ab" だけを捕捉(suffix "cdef" を除外)
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne(); // "a" を削除=スコープは [0,2) → [0,1) へ縮む
            host.Search.ReplaceOne(); // "b" を削除=スコープは [0,1) → [0,0) へ縮む
            host.Search.ReplaceOne(); // スコープが空=何も置換しない

            // 縮め忘れるとスコープが [0,2) のまま残り、3 回目で選択外の "c" まで消える。
            Assert.Equal("cdef", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_LastHitInScope_AnnouncesNoMore() =>
        Sta.Run(() =>
        {
            // 置換後の「次」がスコープ外なら、そこへ飛ばずに終わる。
            using var host = new Host();
            var doc = host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3);
            host.Search.OnInSelectionToggled(true);

            host.Search.ReplaceOne();

            Assert.Equal("置換しました。これ以上見つかりません", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceOne_InSelection_WithoutCapturedScope_Announces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            host.Search.OnInSelectionToggled(true); // 選択なしで ON=捕捉されない

            host.Search.ReplaceOne();

            Assert.Equal("選択範囲がありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc.Editor.Text);
        });

    [Fact]
    public void ReplaceOne_InSelection_AfterEdit_RefusesStaleScope() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.View.InSelection = true;
            host.Search.OpenReplace();
            doc.Editor.SelectCharRange(4, 3); // 後半だけを捕捉
            host.Search.OnInSelectionToggled(true);

            doc.Editor.ReplaceCharRange(0, 0, "QQQQ"); // 捕捉位置が別の中身を指すようになる

            host.Search.ReplaceOne();

            Assert.Equal("QQQQabc abc", doc.Editor.Text); // 一文字も書き換えない
            Assert.Equal("選択範囲が変わりました。選択し直してください", host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InCsvMode_IsBlocked() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.View.Replacement = "X";
            host.Search.OpenReplace();
            doc.State.CsvMode = true;

            host.Search.ReplaceAll();

            Assert.Equal("abc", doc.Editor.Text);
            Assert.Equal(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said[^1]);
        });

    [Fact]
    public void ReplaceAll_InvalidRegex_AnnouncesAndDoesNotModify() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("abc");
            host.View.Pattern = "(";
            host.View.UseRegex = true;
            host.View.Replacement = "X";
            host.Search.OpenReplace();

            host.Search.ReplaceAll(); // Find/ReplaceOne と別コードパスの同ガード(削除すると「見つかりません」の誤通知になる)

            Assert.Equal("正規表現が正しくありません", host.Announcer.Said[^1]);
            Assert.Equal("abc", doc.Editor.Text);
        });

    // ===== searcher の保持と破棄(照合条件ごとに 1 本を使い回す) =====
    // 保持/破棄は結果値からは観測できない(作り直しても同じ答えを返す)ため、
    // SearchController.SearcherForTest の参照同一性で観測する。
    // 保持が壊れると打鍵のたびに Regex 再コンパイル+材質化のやり直しになり、
    // 破棄が漏れると材質化キャッシュ(TextSnapshot → ピース木 → バイト配列の強参照)が
    // 閉じた文書をピン留めし続ける。両方向を固定する。

    [Fact]
    public void Searcher_IsReused_WhileMatchConditionUnchanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind(); // Open 内の UpdateCount で 1 本目を解決
            var first = host.Search.SearcherForTest;
            Assert.NotNull(first);

            host.Search.UpdateCount(); // 打鍵ごとの件数更新(条件は同じ)
            Assert.True(host.Search.FindNext());
            Assert.True(host.Search.FindNext());

            Assert.Same(first, host.Search.SearcherForTest); // 条件が変わらない限り作り直さない
        });

    [Fact]
    public void Searcher_IsRecreated_WhenMatchConditionChanges() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("ABC abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            var first = host.Search.SearcherForTest;
            Assert.NotNull(first);
            Assert.Equal("2 件", host.View.Status); // MatchCase=false: ABC も数える

            host.View.MatchCase = true; // チェックボックス操作 → UpdateCount
            host.Search.UpdateCount();

            var second = host.Search.SearcherForTest;
            Assert.NotNull(second);
            Assert.NotSame(first, second); // 条件が変われば作り直す(参照同一性)
            Assert.Equal("1 件", host.View.Status); // 使い回すと "2 件" のまま=結果でも固定する
        });

    [Fact]
    public void Searcher_IsDropped_WhenPatternBecomesEmpty() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            Assert.NotNull(host.Search.SearcherForTest);

            host.View.Pattern = ""; // 検索語を消す打鍵(条件が無効になる)
            host.Search.UpdateCount();

            Assert.Null(host.Search.SearcherForTest); // 素の early return だと保持が続いてしまう
        });

    [Fact]
    public void Searcher_IsDropped_OnActiveDocumentChanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            var first = host.Search.SearcherForTest;
            Assert.NotNull(first);

            _ = host.NewDoc("abc"); // 文書切替(表示中なので直後の UpdateCount で新しい 1 本が立つ)

            Assert.NotNull(host.Search.SearcherForTest);
            Assert.NotSame(first, host.Search.SearcherForTest); // 旧文書のキャッシュごと捨てる
        });

    [Fact]
    public void Searcher_IsDropped_OnDocumentClosed() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc1 = host.NewDoc("abc"); // 閉じる対象
            _ = host.NewDoc("abc"); // アクティブのまま=クローズで文書切替を起こさない
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            var first = host.Search.SearcherForTest;
            Assert.NotNull(first);
            int activeChanged = 0;
            host.Docs.ActiveDocumentChanged += (_, _) => activeChanged++;

            Assert.True(host.Docs.TryClose(doc1, _ => true)); // 非アクティブタブのクローズ

            // 切替が起きていないことまで固定する(起きていると破棄の出所が DocumentClosed か
            // ActiveDocumentChanged か区別できず、DocumentClosed 削除の変異を殺せない)。
            Assert.Equal(0, activeChanged);
            Assert.Null(host.Search.SearcherForTest); // 閉じた文書をピン留めしない
        });

    [Fact]
    public void Searcher_IsDropped_OnDismissed_AndRebuiltOnNextSearch() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            Assert.NotNull(host.Search.SearcherForTest);

            host.View.RaiseDismissed(); // ユーザーが検索を終えた(閉じる/Escape/×)
            Assert.Null(host.Search.SearcherForTest);

            host.View.RaiseDismissed(); // 冪等(Escape → 再表示 → また Escape)
            Assert.Null(host.Search.SearcherForTest);

            Assert.True(host.Search.FindNext()); // 破棄しても検索は壊れない(次の操作で作り直す)
            Assert.NotNull(host.Search.SearcherForTest);
        });

    [Fact]
    public void Searcher_IsDropped_WhenViewIsRecreated() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            var first = host.Search.SearcherForTest;
            Assert.NotNull(first);

            host.View.IsDisposed = true; // owner ごと破棄された等(この経路では Dismissed が来ない)
            host.Search.OpenFind(); // ビュー再生成=新しいダイアログセッション

            Assert.Equal(2, host.FactoryCalls);
            Assert.NotSame(first, host.Search.SearcherForTest); // 前セッションの保持を持ち越さない
        });

    [Fact]
    public void Searcher_SurvivesG2Hide_AcrossRepeatedFindNext() =>
        Sta.Run(() =>
        {
            // 本設計の核: 「非表示」は破棄トリガではない(G-2 の一時退避と終了は
            // 発生源でしか区別できないので、Dismissed だけを破棄トリガに使う)。
            using var host = new Host();
            host.NewDoc("abc abc abc");
            host.View.Pattern = "abc";
            host.Search.OpenFind();
            Assert.True(host.Search.FindNext());
            var searcher = host.Search.SearcherForTest;
            Assert.NotNull(searcher);

            host.View.Visible = false; // G-2 の自動 Hide(RaiseDismissed ではない)
            Assert.True(host.Search.FindNext()); // 非表示のまま F3 連打
            Assert.True(host.Search.FindNext());

            Assert.Same(searcher, host.Search.SearcherForTest); // キャッシュは生きたまま
        });

    // ===== A-3(2026-08-22): 検索ジャンプの追従スクロール =====

    [Fact]
    public void FindNext_ScrollsHitIntoView() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 200 行 + 末尾に唯一のヒット。既定サイズのホストフォームでも必ず可視域外になる。
            var doc = host.NewDoc(
                string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line{i}")) + "\nNEEDLE"
            );
            doc.Editor.TopLine = 0;
            host.View.Pattern = "NEEDLE";
            host.Search.OpenFind();

            Assert.True(host.Search.FindNext());

            // 追従が無いと TopLine=0 のまま=晴眼ユーザーにはヒットが見えない(A-3)。
            // 「動いた」だけでなく「ヒット行が可視域に入っている」ことまで固定する。
            int visibleRows = Math.Max(
                1,
                doc.Editor.ClientSize.Height / Math.Max(1, doc.Editor.LineHeightPx)
            );
            int hitLine = doc.Editor.CurrentLine; // SelectCharRange 後のキャレット=ヒット末尾
            Assert.Equal(200, hitLine); // ヒットは最終行(fixture の前提を固定する)
            Assert.True(doc.Editor.TopLine > 0, $"expected TopLine > 0, got {doc.Editor.TopLine}");
            Assert.InRange(hitLine, doc.Editor.TopLine, doc.Editor.TopLine + visibleRows - 1);
        });
}
