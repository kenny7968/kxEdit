using System.IO;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Search;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 7: GrepController の配線・Open ライフサイクル・入力検証・成功系・
/// 追い越し guard・BeginClose 抑止・Cancel のテスト。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeGrepView/FakeGrepResultsView)と
/// 検索(FakeGrepSearchFn)だけを偽物にする。GrepService の照合正しさ(Core 検証済み)は
/// 再検証しない(責務=Controller の状態機械・SR 通知・エラー件数ありの必須発声・追い越し guard)。
/// </summary>
public class GrepControllerTests
{
    /// <summary>
    /// Progress&lt;T&gt; を同期発火にするための SC(Stage 8 Task D-2)。
    /// Progress&lt;T&gt; は ctor 時点の SynchronizationContext.Current を捕捉し、Report 時に Post する。
    /// Sta.Run の裸 STA は SC=null で ThreadPool 経由=非決定的になるため、テストが `_cts=null` 化と
    /// 競合して guard 効果を観測できない。Post を同期実行に置換して、
    /// Report が返るまでに guard の評価結果(SetStatus 呼ぶ/呼ばない)を確定させる。
    ///
    /// 復元不要: <see cref="Sta.Run"/> は各テストで新規 STA スレッドを立てて join するため、
    /// SC は同スレッドの寿命で消える(=テスト間で leak しない)。将来 Sta.Run を
    /// 「常駐スレッド+Post 待機」等に refactor する場合はこの前提が崩れるため、
    /// D-2 テスト側で try/finally による SetSynchronizationContext(previous) 復元が必要になる。
    /// </summary>
    private sealed class SynchronousSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
    }

    /// <summary>GrepController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeGrepView View { get; } = new();
        public FakeGrepResultsView Results { get; private set; } = null!;
        public FakeGrepSearchFn SearchFn { get; } = new();
        public GrepController Grep { get; }
        public List<GrepHit> Jumps { get; } = new();
        public int ViewFactoryCalls;
        public int ResultsFactoryCalls;

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Grep = new GrepController(
                docs: Docs,
                owner: Form,
                viewFactory: _ =>
                {
                    ViewFactoryCalls++;
                    return View;
                },
                resultsFactory: () =>
                {
                    ResultsFactoryCalls++;
                    Results = new FakeGrepResultsView(
                        new GrepResultsCallbacks(hit => Jumps.Add(hit))
                    );
                    return Results;
                },
                searchFn: SearchFn.Invoke
            );
        }

        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    /// <summary>テストで folder として渡す実在ディレクトリ(Directory.Exists ガードのみ突破すればよい)。</summary>
    private static readonly string ExistingFolder = Environment.GetFolderPath(
        Environment.SpecialFolder.MyDocuments
    );

    /// <summary>DefaultFolder の "activePath 由来" と "MyDocuments フォールバック" を区別するための実在ディレクトリ(必ず MyDocuments と異なる)。</summary>
    private static readonly string TempFolder = Path.TrimEndingDirectorySeparator(
        Path.GetTempPath()
    );

    // ===== ctor(対応固定=生成時点で view/results/searchFn 呼び出しなし) =====

    [Fact]
    public void Ctor_DoesNotInvokeViewOrResultsOrSearchFn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            Assert.Equal(0, host.ViewFactoryCalls);
            Assert.Equal(0, host.ResultsFactoryCalls);
            Assert.Empty(host.SearchFn.Invocations);
            Assert.Empty(host.View.Notifications);
        });

    // ===== Open(ビューのライフサイクル・DefaultFolder 分岐) =====

    [Fact]
    public void Open_First_ShowsView_UsesDefaultFolderFromActiveDoc() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("body");
            doc.State.Path = Path.Combine(TempFolder, "sample.txt"); // ディレクトリ=TempFolder(MyDocuments フォールバックと区別)

            host.Grep.Open();

            Assert.Equal(1, host.ViewFactoryCalls);
            Assert.Equal(1, host.View.ShowAndFocusCount);
            Assert.True(host.View.Visible);
            Assert.Equal(new[] { TempFolder }, host.View.FolderLog); // activePath から派生した DefaultFolder が設定される
        });

    [Fact]
    public void Open_UsesFolderAsIs_IfAlreadySet() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.View.Folder = "C:/preset"; // 既にセット済み=上書きしない(現行 IsNullOrEmpty ガード)

            host.Grep.Open();

            Assert.Empty(host.View.FolderLog); // SetFolder が呼ばれない
            Assert.Equal("C:/preset", host.View.Folder);
        });

    [Fact]
    public void Open_NoActivePath_FallsBackToMyDocuments() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body"); // State.Path=null(無題)

            host.Grep.Open();

            Assert.Single(host.View.FolderLog);
            Assert.Equal(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                host.View.FolderLog[0]
            );
        });

    [Fact]
    public void Open_ReusesView_WhileAlive() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");

            host.Grep.Open();
            host.Grep.Open(); // 再表示は同一ビュー

            Assert.Equal(1, host.ViewFactoryCalls);
            Assert.Equal(2, host.View.ShowAndFocusCount); // 再表示のたびフォーカス手順
        });

    [Fact]
    public void Open_RecreatesView_AfterDispose() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();

            host.View.IsDisposed = true; // owner クローズ等でダイアログが破棄された状況
            host.Grep.Open();

            Assert.Equal(2, host.ViewFactoryCalls); // 作り直す(Disposed ビューを使い回さない)
        });

    // ===== 入力検証(3 分岐+早期 return=検索デリゲートは呼ばれない) =====

    [Fact]
    public void RunAsync_EmptyPattern_Notifies_AndDoesNotInvokeSearchFn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "";
            host.View.Folder = ExistingFolder;

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "検索文字列を入力してください" }, host.View.Notifications);
            Assert.Empty(host.SearchFn.Invocations);
            Assert.Empty(host.View.RunningLog); // SetRunning にも到達しない
        });

    [Fact]
    public void RunAsync_MissingFolder_Notifies_AndDoesNotInvokeSearchFn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = "Z:/no/such/folder/definitely/absent";

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "フォルダが見つかりません" }, host.View.Notifications);
            Assert.Empty(host.SearchFn.Invocations);
            Assert.Empty(host.View.RunningLog); // SetRunning にも到達しない
        });

    [Fact]
    public void RunAsync_InvalidRegex_Notifies_AndDoesNotInvokeSearchFn() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "(";
            host.View.Folder = ExistingFolder;
            host.View.UseRegex = true;

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { "正規表現が正しくありません" }, host.View.Notifications);
            Assert.Empty(host.SearchFn.Invocations);
            Assert.Empty(host.View.RunningLog); // SetRunning にも到達しない
        });

    [Fact]
    public void RunAsync_NoView_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // Open を呼ばず _view=null のまま
            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(0, host.ViewFactoryCalls);
            Assert.Empty(host.SearchFn.Invocations);
            Assert.Empty(host.View.Notifications); // view は生成されていないので Fake も呼ばれない
        });

    // ===== RunAsync 成功系(検索デリゲート即完了=Task.FromResult パス) =====

    [Fact]
    public void RunAsync_ValidInputs_TogglesRunning_AndAnnouncesStart() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.EmptyOutcome();

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(new[] { true, false }, host.View.RunningLog); // 開始で true・完了で false
            Assert.Contains("検索を開始しました", host.View.Notifications);
        });

    [Fact]
    public void RunAsync_WithHits_PopulatesResults_AndShowsResults_WithSilentSummary() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 3);

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Single(host.Results.PopulateLog);
            Assert.Equal("abc", host.Results.PopulateLog[0].Pattern);
            Assert.Equal(ExistingFolder, host.Results.PopulateLog[0].Folder);
            Assert.Equal(1, host.Results.ShowResultsCount);
            // ヒットありエラー無しは Summary を発声せず視覚のみ(結果窓フォーカスの二重読み回避)
            Assert.DoesNotContain(host.View.Notifications, s => s.Contains("行 /"));
            Assert.Contains("3 行 / 1 ファイル", host.View.Status ?? "");
        });

    [Fact]
    public void RunAsync_WithHits_AndErrors_AnnouncesSummary_ForcedSpeech() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 3, errors: 2);

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(1, host.Results.ShowResultsCount); // ヒット>0 なので結果窓は開く
            // エラーがある時は summary を必ず発声(誤った「見つかりません」防止)
            Assert.Contains(
                host.View.Notifications,
                s => s.Contains("3 行 / 1 ファイル") && s.Contains("読み取り不可 2 件")
            );
        });

    [Fact]
    public void RunAsync_NoHits_AnnouncesNotFound_AndDoesNotShowResults() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.EmptyOutcome();

            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(0, host.Results.ShowResultsCount); // ヒット 0 なので窓は出さない
            Assert.Single(host.Results.PopulateLog); // Populate は呼ぶ(見つかりません表示のため)
            Assert.Contains("見つかりません", host.View.Notifications);
        });

    [Fact]
    public void RunAsync_Cancelled_AnnouncesInterrupted() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 0, cancelled: true);

            host.Grep.RunAsync().GetAwaiter().GetResult();

            // Summary: Hits=0 かつ Cancelled → "中断しました(0 件)"(現行 GrepController.Summary の文言=全角括弧)
            Assert.Contains(
                host.View.Notifications,
                s => s.StartsWith("中断しました", System.StringComparison.Ordinal)
            );
        });

    [Fact]
    public void RunAsync_SearchFnThrows_AnnouncesError_AndResetsRunning() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            tcs.SetException(new InvalidOperationException("boom"));
            task.GetAwaiter().GetResult();

            Assert.Contains(
                host.View.Notifications,
                s =>
                    s.StartsWith("検索エラー: ", System.StringComparison.Ordinal)
                    && s.Contains("boom", System.StringComparison.Ordinal)
            );
            Assert.Equal(new[] { true, false }, host.View.RunningLog); // catch でも finally は必ず走る
        });

    // ===== 追い越し guard・BeginClose 抑止・Cancel =====

    [Fact]
    public void SecondRunAsync_OvertakesFirst_FirstResultsSkipped() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs1 = new TaskCompletionSource<GrepOutcome>();
            var tcs2 = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs1);
            host.SearchFn.Pending.Enqueue(tcs2);

            var task1 = host.Grep.RunAsync(); // 保留中: _cts=cts1
            var task2 = host.Grep.RunAsync(); // 追い越し: _cts=cts2(cts1 は Cancel 済み)

            tcs1.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 9)); // 先行の結果が到着
            task1.GetAwaiter().GetResult();
            tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3)); // 後発の結果
            task2.GetAwaiter().GetResult();

            // 先行の結果は UI に反映されない=Populate/ShowResults は 1 回(後発分だけ)
            Assert.Single(host.Results.PopulateLog);
            Assert.Equal(3, host.Results.PopulateLog[0].Outcome.Hits.Count);
            Assert.Equal(1, host.Results.ShowResultsCount);
            // 先行の cts はキャンセルされていること(協調キャンセルの入り口が生きていることの担保)
            Assert.True(host.SearchFn.Invocations[0].Token.IsCancellationRequested);
        });

    [Fact]
    public void BeginClose_DuringRun_SuppressesResults() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            host.Grep.BeginClose(); // 終了開始
            tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5)); // 遅れて結果到着
            task.GetAwaiter().GetResult();

            // 結果反映は抑止(ShowResults 早期 return により resultsFactory が呼ばれない=結果ビュー自体を生成しない)
            Assert.Equal(0, host.ResultsFactoryCalls);
            Assert.Null(host.Results);
            // Notifications は "検索を開始しました" までは記録される(BeginClose 前に発声済み)
            Assert.Contains("検索を開始しました", host.View.Notifications);
        });

    [Fact]
    public void CancelClose_AfterBeginClose_RestoresResultDisplay() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 5);

            host.Grep.BeginClose(); // アプリ終了開始(_closing=true)
            host.Grep.CancelClose(); // 終了キャンセル=MainForm.OnFormClosing が呼ぶ唯一の復帰経路

            host.Grep.RunAsync().GetAwaiter().GetResult();

            // BeginClose_DuringRun_SuppressesResults(ResultsFactoryCalls=0・Results=null)の対称形:
            // CancelClose 後の実行では結果窓が生成され Populate される(恒久無言化しない)。
            // kill 対象: CancelClose の `_closing = false` が消える/`true` になる変異で赤。
            Assert.Equal(1, host.ResultsFactoryCalls);
            Assert.Single(host.Results.PopulateLog);
        });

    [Fact]
    public void Open_AfterBeginClose_DoesNotResetClosingFlag() =>
        Sta.Run(() =>
        {
            // Phase 2 最終トリアージ Task A1: Open() の内側に将来「善意の cleanup」として
            // `_closing=false;` が紛れ込む回帰を機械検出。終了確認カスケード中に grep が
            // 意図せず復活する経路を予防的に pin する(復帰は CancelClose() 経由のみ)。
            // mutation kill: Open() 先頭に `_closing = false;` を挿入 → 本テスト赤化を目視確認済み。
            using var host = new Host();

            host.Grep.Open(); // 初回オープン(_closing=false のはず)
            host.Grep.BeginClose(); // 終了フラグ = true

            // Act: 終了フラグ立ち中に Open() を再呼び(終了確認ダイアログ中に間違って開かれた等)
            host.Grep.Open();

            // Assert: _closing はまだ true のまま(Open() は解除しない)
            //   → private field を reflection で観察(GrepController._closing)
            var closingField = typeof(GrepController).GetField(
                "_closing",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );
            Assert.NotNull(closingField);
            Assert.True(
                (bool)closingField!.GetValue(host.Grep)!,
                "Open() は _closing を勝手にリセットしてはならない。復帰は CancelClose() 経由のみ。"
            );
        });

    [Fact]
    public void Cancel_CancelsCurrentRun_TokenObserved() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            host.Grep.Cancel(); // 実行中の中止(Stop ボタン相当)
            tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 0, cancelled: true)); // GrepService は cancelled=true で戻る
            task.GetAwaiter().GetResult();

            Assert.True(host.SearchFn.Invocations[0].Token.IsCancellationRequested);
            Assert.Equal(new[] { true, false }, host.View.RunningLog); // 中止でも finally で SetRunning(false)
        });

    // ===== 結果窓のアクティベート→jumpTo 対応固定 =====

    [Fact]
    public void ResultsActivate_InvokesJumpTo_WithHit() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 2);
            host.Grep.RunAsync().GetAwaiter().GetResult();

            var hit = host.Results.PopulateLog[0].Outcome.Hits[1];
            host.Results.FireActivate(hit); // ダブルクリック/Enter 相当

            Assert.Single(host.Jumps);
            Assert.Same(hit, host.Jumps[0]);
        });

    // ===== 設計不変(GrepController は GrepHit ジャンプ経路を知らない) =====

    [Fact]
    public void Controller_HasNoJumpToField_NorActionOfGrepHitField()
    {
        // 目的: Stage 8 Task C の設計改善(GrepResultsCallbacks 組立を factory 側に移す)が
        // 後退リファクタで戻らないよう機械的に固定。
        // GrepController は「grep 結果を結果窓へ反映」責務のみで、ジャンプ経路(Action<GrepHit>)を知らない。
        var fields = typeof(GrepController).GetFields(
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public
        );

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(Action<GrepHit>));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(GrepResultsCallbacks));
    }

    [Fact]
    public void GrepController_Ctor_DoesNotAccept_LegacyJumpToDelegate()
    {
        // Batch D Task 10: field のみを見る Controller_HasNoJumpToField_... と対で ctor 引数側も機械固定。
        // ctor 経由の閉じ込め回帰(引数に Action<GrepHit> が復活し private フィールドで受ける)を検出できるよう
        // param 型スキャンで補強する(field 反射だけでは param→field 経路を跨ぐ mutation を漏らす)。
        // 併せて resultsFactory の型が Func<IGrepResultsView> のままであることも固定
        // (Func<GrepResultsCallbacks, IGrepResultsView> への戻し=Stage 8 Task C 差戻しを機械検出)。
        var ctors = typeof(GrepController).GetConstructors();
        Assert.Single(ctors); // 前提: ctor 1 個(前提が崩れれば Assert.Single が早期検出)
        var ctor = ctors[0];
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.DoesNotContain(paramTypes, t => t == typeof(Action<GrepHit>));
        Assert.Contains(paramTypes, t => t == typeof(Func<IGrepResultsView>));
    }

    [Fact]
    public void GrepResultsWindow_Ctor_DoesNotAccept_LegacyActionDelegate()
    {
        // Phase 2 最終トリアージ Task D1: cleanup Task 10 と同型で GrepResultsWindow 側も機械固定。
        // legacy な Action<GrepHit> 直渡しの復活(record 経由の GrepResultsCallbacks を経由せず
        // callback を直接注入する差戻し)を検出する。
        var ctors = typeof(GrepResultsWindow).GetConstructors();
        Assert.Single(ctors); // 前提: sealed + ctor 1 個(前提が崩れれば Assert.Single が早期検出)
        var ctor = ctors[0];
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(paramTypes, t => t == typeof(Action<GrepHit>));
        Assert.Contains(paramTypes, t => t == typeof(GrepResultsCallbacks));
    }

    // ===== Cancel/Dispose の副作用網羅(Stage 8 Task D-1) =====

    // Note: 計画原案の `Cancel_AfterOutcomeReturned_DoesNotAnnounceSummary_NorPopulate` は
    // 「Cancel 後に到着した outcome の Populate/Summary が抑止される」ことを主張していたが、
    // 実装の Cancel は `_cts?.Cancel()` のみで _cts を差し替えない=await 後の追い越し guard
    // (`d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing`)は Cancel 単独では true にならない
    // ため、Cancel 単独では Populate/Summary は現に抑止されない。よって計画名を維持したままの
    // アサートは真ではない。ここでは追い越し guard の d.IsDisposed 分岐(既存テストが未被覆)を
    // 埋める Dispose 経路のテストに置換し、mutation 5(guard 全消し)の kill を IsDisposed 分岐で成立させる。

    [Fact]
    public void Dispose_DuringRun_SuppressesShowResults_AndSummary() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            host.View.IsDisposed = true; // owner クローズ等でダイアログ破棄
            tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5)); // 破棄後に outcome 到着
            task.GetAwaiter().GetResult();

            // 追い越し guard の d.IsDisposed 分岐で早期 return=結果窓生成・Populate・Summary はすべて抑止
            Assert.Equal(0, host.ResultsFactoryCalls);
            Assert.DoesNotContain(
                host.View.Notifications,
                n => n.Contains("行 /") || n.Contains("見つかりません")
            );
        });

    // 元 `Cancel_DoesNotChangeViewVisibility` はレビュー由来で削除(Task D レビュー Minor #1):
    // IGrepView に Hide がなく GrepController も Visible に書き込まないため、Cancel 有無に
    // 関係なく trivially true=coverage 0。将来 Hide 経路が追加された時点で defensive テストを
    // 再検討(YAGNI)。

    // ===== Progress 追い越し guard 3 条件(Stage 8 Task D-2) =====

    [Fact]
    public void Progress_AfterDispose_DoesNotUpdateStatus() =>
        Sta.Run(() =>
        {
            // Progress<T> は ctor 時点の SC を捕捉するため、RunAsync 前に SyncSC を仕込む(同期発火化)
            SynchronizationContext.SetSynchronizationContext(new SynchronousSyncContext());
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            var progress = host.SearchFn.Invocations[0].Progress!;
            host.View.IsDisposed = true; // owner クローズ等でダイアログ破棄相当
            int statusCountBefore = host.View.StatusLog.Count;

            // SyncSC により Report は同期発火 → Return 前に guard 評価が確定
            progress.Report(new GrepProgress(FilesScanned: 10, HitCount: 3, CurrentFile: "x"));

            // Progress ラムダの d.IsDisposed guard(1 条件目)により SetStatus は呼ばれない
            Assert.Equal(statusCountBefore, host.View.StatusLog.Count);

            // task の後始末(finally=SetRunning(false) は Disposed 時スキップ・_cts=null)
            tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3));
            task.GetAwaiter().GetResult();
        });

    [Fact]
    public void Progress_AfterCtsSwappedByNewRun_DoesNotUpdateStatus() =>
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronousSyncContext());
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs1 = new TaskCompletionSource<GrepOutcome>();
            var tcs2 = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs1);
            host.SearchFn.Pending.Enqueue(tcs2);

            var task1 = host.Grep.RunAsync();
            var progress1 = host.SearchFn.Invocations[0].Progress!; // 旧 Progress(旧 cts に対応)
            var task2 = host.Grep.RunAsync(); // _cts を差し替え(cts2 に)

            // 旧 Progress による報告(distinctive な数字を選び後段の Assert で識別)
            // SyncSC により Report は同期発火 → guard 評価は「旧 Progress のクロージャが持つ cts1 と現 _cts=cts2」の不一致を見て早期 return
            progress1.Report(new GrepProgress(FilesScanned: 999, HitCount: 42, CurrentFile: null));

            // Progress ラムダの !ReferenceEquals(_cts, cts) guard(2 条件目)により、
            // 旧 Progress の内容(999 ファイル・42 件)は StatusLog に現れない
            Assert.DoesNotContain(
                host.View.StatusLog,
                s => s.Contains("999 ファイル") || s.Contains("42 件")
            );

            // 後始末
            tcs1.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 3));
            tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 5));
            task1.GetAwaiter().GetResult();
            task2.GetAwaiter().GetResult();
        });

    [Fact]
    public void Progress_DuringBeginClose_DoesNotUpdateStatus() =>
        Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronousSyncContext());
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            var progress = host.SearchFn.Invocations[0].Progress!;
            host.Grep.BeginClose(); // 終了開始(_closing=true・_cts.Cancel)
            int statusCountBefore = host.View.StatusLog.Count;

            // SyncSC により Report は同期発火 → guard 評価は _closing=true を見て早期 return
            progress.Report(new GrepProgress(FilesScanned: 777, HitCount: 11, CurrentFile: "y"));

            // Progress ラムダの _closing guard(3 条件目)により SetStatus は呼ばれない
            Assert.Equal(statusCountBefore, host.View.StatusLog.Count);

            // 後始末
            tcs.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 1));
            task.GetAwaiter().GetResult();
        });

    // ===== catch 内 guard の分岐被覆(Stage 8 Task D-3・準等価変異 kill) =====

    [Fact]
    public void Catch_AfterDispose_DoesNotAnnounceError() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs);

            var task = host.Grep.RunAsync();
            host.View.IsDisposed = true; // 検索完了前に Dispose
            tcs.SetException(new InvalidOperationException("boom"));
            task.GetAwaiter().GetResult();

            // catch 内 guard の !d.IsDisposed により、Disposed 済み view には RaiseNotification しない
            Assert.DoesNotContain(
                host.View.Notifications,
                n => n.StartsWith("検索エラー:", System.StringComparison.Ordinal)
            );
        });

    [Fact]
    public void Catch_AfterCtsSwapped_DoesNotAnnounceError() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            var tcs1 = new TaskCompletionSource<GrepOutcome>();
            var tcs2 = new TaskCompletionSource<GrepOutcome>();
            host.SearchFn.Pending.Enqueue(tcs1);
            host.SearchFn.Pending.Enqueue(tcs2);

            var task1 = host.Grep.RunAsync(); // 旧 run(cts1)
            var task2 = host.Grep.RunAsync(); // 追い越し=_cts=cts2(cts1 は Cancel 済み)

            tcs1.SetException(new InvalidOperationException("boom (from old run)"));
            tcs2.SetResult(FakeGrepSearchFn.OutcomeWith(hits: 0));
            task1.GetAwaiter().GetResult();
            task2.GetAwaiter().GetResult();

            // catch 内 guard の ReferenceEquals(_cts, cts) により、旧 run の例外はエラー通知しない
            Assert.DoesNotContain(host.View.Notifications, n => n.Contains("boom (from old run)"));
        });

    // ===== GrepDialog の IAnnouncer 注入(Batch D Task 12) =====

    [Fact]
    public void GrepDialog_RaiseNotification_ForwardsToInjectedAnnouncer() =>
        Sta.Run(() =>
        {
            // Batch D Task 12: GrepDialog は `new UiaAnnouncer(_status)` の直生成を廃止し ctor で IAnnouncer 受け取り。
            // 従来は「直生成 UiaAnnouncer が _status を掴む」ため FakeAnnouncer 経由で発声を観測できなかった。
            // 本テストは注入経路の配線を機械固定する: RaiseNotification が注入 IAnnouncer.Say を呼ぶこと・
            // 複数回・順序が維持されること。
            // kill 対象:
            //  - RaiseNotification 内の `_announcer.Say(message)` 削除 → Said が空・Assert.Equal で赤
            //  - 引数取り違え(`_announcer.Say("")` などハードコード)→ Said 内容不一致で赤
            //  - ctor で受け取った announcer を別インスタンス(new UiaAnnouncer 復活等)に差し替え → fake に届かず赤
            // 視覚側(_status.Text) の pin=晴眼/弱視 first-class 契約(SR 発声だけでなく dialog 内視覚更新も保存)。
            // 従来 UiaAnnouncer.Say が持っていた `_status.Text = message` 副作用を明示追加した line を kill 対象化:
            //  - RaiseNotification の `_status.Text = message` 削除 → 最終メッセージが _status に反映されず赤
            var fake = new FakeAnnouncer();
            var cb = new GrepCallbacks(() => Task.CompletedTask, () => { });
            using var dialog = new GrepDialog(cb, fake);

            dialog.RaiseNotification("検索を開始しました");
            dialog.RaiseNotification("見つかりません");

            Assert.Equal(new[] { "検索を開始しました", "見つかりません" }, fake.Said);

            // 視覚側 pin: 最後に発声したメッセージが _status label に反映されている
            var statusField = typeof(GrepDialog).GetField(
                "_status",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            );
            Assert.NotNull(statusField);
            var statusLabel = (Label)statusField!.GetValue(dialog)!;
            Assert.Equal("見つかりません", statusLabel.Text);
        });

    // ===== ShowResults の _resultsView.IsDisposed 分岐被覆(Batch B Task 6) =====

    [Fact]
    public void ShowResults_RecreatesView_When_PreviousDisposed() =>
        Sta.Run(() =>
        {
            // ShowResults(GrepController.cs:148)の分岐 `_resultsView is null || _resultsView.IsDisposed`
            // の右辺(IsDisposed 側)を機械固定。左辺(is null 側)は他テストで既に踏んでいる。
            // 想定: owner クローズ等で結果窓が破棄された後の 2 回目 Grep で factory が再度呼ばれ、
            // 新しい resultsView が生成される。
            // kill 対象: `|| _resultsView.IsDisposed` の削除で 2 回目の factory 呼び出しが起きず
            // ResultsFactoryCalls が 1 のままになる=Assert.Equal(2, ...) が赤化する。
            using var host = new Host();
            host.NewDoc("body");
            host.Grep.Open();
            host.View.Pattern = "abc";
            host.View.Folder = ExistingFolder;
            host.SearchFn.DefaultOutcome = FakeGrepSearchFn.OutcomeWith(hits: 1);

            // 1 回目: resultsView 生成
            host.Grep.RunAsync().GetAwaiter().GetResult();
            Assert.Equal(1, host.ResultsFactoryCalls);
            var firstResults = host.Results;

            // Disposed 相当(owner クローズ等の破棄)。実 GrepResultsWindow は Form.Dispose で
            // IsDisposed=true になるため Fake 側の setter で同等状態を再現。
            firstResults.IsDisposed = true;

            // 2 回目: `_resultsView.IsDisposed` 分岐で早期に factory 再呼び出し → 新規 view
            host.Grep.RunAsync().GetAwaiter().GetResult();

            Assert.Equal(2, host.ResultsFactoryCalls);
            Assert.NotSame(firstResults, host.Results); // Host.Results は最新の factory 戻り値で差し替わる
            Assert.Single(host.Results.PopulateLog); // 新規 view にだけ Populate(旧 view には触らない)
            Assert.Single(firstResults.PopulateLog); // 旧 view は 1 回目の Populate のまま(2 回目は届かない)
        });
}
