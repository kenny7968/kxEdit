using System.IO;
using kxEdit.Core.Search;

namespace kxEdit.App;

/// <summary>
/// grep の統括。ダイアログ入力を検索デリゲート(既定=<see cref="GrepService"/>・別スレッド)へ渡し、
/// 結果を結果窓へ反映し件数を SR 通知する。ジャンプ経路は結果窓生成側(MainForm)が
/// <see cref="GrepResultsCallbacks"/> に組み込む=Controller はジャンプ経路を知らない。
/// Core はスレッド非依存のため、スレッド制御は本クラスに閉じる(§4.1)。
/// </summary>
public sealed class GrepController
{
    private readonly DocumentManager _docs;
    private readonly IWin32Window _owner;
    private readonly Func<GrepCallbacks, IGrepView> _viewFactory;
    private readonly Func<IGrepResultsView> _resultsFactory;
    private readonly Func<
        GrepRequest,
        IProgress<GrepProgress>?,
        CancellationToken,
        Task<GrepOutcome>
    > _searchFn;
    private IGrepView? _view;
    private IGrepResultsView? _resultsView;
    private CancellationTokenSource? _cts;
    private bool _closing; // アプリ終了中は結果反映を抑止

    public GrepController(
        DocumentManager docs,
        IWin32Window owner,
        Func<GrepCallbacks, IGrepView> viewFactory,
        Func<IGrepResultsView> resultsFactory,
        Func<
            GrepRequest,
            IProgress<GrepProgress>?,
            CancellationToken,
            Task<GrepOutcome>
        >? searchFn = null
    )
    {
        _docs = docs;
        _owner = owner;
        _viewFactory = viewFactory;
        _resultsFactory = resultsFactory;
        // 既定=現行の `await Task.Run(() => GrepService.Search(...))` と 1:1(await 位置と例外セマンティクス不変)
        _searchFn =
            searchFn ?? ((req, prog, ct) => Task.Run(() => GrepService.Search(req, prog, ct)));
    }

    /// <summary>ダイアログを開く（既定フォルダ＝アクティブ文書のフォルダ）。</summary>
    public void Open()
    {
        if (_view is null || _view.IsDisposed)
            _view = _viewFactory(new GrepCallbacks(RunAsync, Cancel));
        if (string.IsNullOrEmpty(_view.Folder))
            _view.SetFolder(DefaultFolder());
        _view.ShowAndFocus(_owner);
    }

    private string DefaultFolder()
    {
        string? path = _docs.Active?.State.Path;
        if (path is not null)
        {
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null && dir.Length > 0)
                    return dir;
            }
            catch
            { /* 不正パスはマイドキュメントへフォールバック */
            }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>入力を検証して別スレッドで grep を実行し、結果を反映する。</summary>
    internal async Task RunAsync()
    {
        var d = _view;
        if (d is null)
            return;
        if (string.IsNullOrEmpty(d.Pattern))
        {
            d.RaiseNotification("検索文字列を入力してください");
            return;
        }
        if (!Directory.Exists(d.Folder))
        {
            d.RaiseNotification("フォルダが見つかりません");
            return;
        }

        var opts = new SearchOptions(d.Pattern, d.MatchCase, d.WholeWord, d.UseRegex);
        if (!new TextSearcher(opts).IsValid)
        {
            d.RaiseNotification("正規表現が正しくありません");
            return;
        }

        var req = new GrepRequest(d.Folder, d.Filter, d.Recursive, opts);
        string pattern = d.Pattern,
            folder = d.Folder;

        // 連打対策: 直前の実行を中止し、本実行専用の CTS を作る（破棄は本実行の finally で）。
        if (_cts is not null)
            await _cts.CancelAsync();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        var progress = new Progress<GrepProgress>(p =>
        {
            // 破棄済み・後発実行に追い越された・終了中なら、古い進捗で新しい状態を上書きしない。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing)
                return;
            d.SetStatus(
                p.CurrentFile is null
                    ? $"{p.FilesScanned} ファイル走査・{p.HitCount} 件"
                    : $"{p.FilesScanned} ファイル走査中… {p.HitCount} 件"
            );
        });

        d.SetRunning(true);
        // 発声→視覚の順（Say は Label も更新するため、後から視覚専用の実行中表示で上書きして保持する）。
        d.RaiseNotification("検索を開始しました");
        d.SetStatus("検索中…");
        try
        {
            // 検索デリゲート(既定=GrepService.Search を Task.Run で包む)は協調キャンセルで
            // 部分結果+Cancelled を返す（例外で打ち切らない）。
            var outcome = await _searchFn(req, progress, ct);

            // ビュー破棄済み・後発の実行に追い越された・終了中なら UI を触らない（結果窓も出さない）。
            if (d.IsDisposed || !ReferenceEquals(_cts, cts) || _closing)
                return;

            ShowResults(pattern, folder, outcome);
            // ヒットがあれば結果窓のフォーカスが SR を駆動するので二重読みを避ける。ただし
            // 読み取りエラーがある時は走査が不完全な旨を必ず音声化する（誤った「見つかりません」防止）。
            if (outcome.Hits.Count == 0 || outcome.Errors.Count > 0)
                d.RaiseNotification(Summary(outcome));
            else
                d.SetStatus(Summary(outcome));
        }
        catch (Exception ex)
        {
            if (!d.IsDisposed && ReferenceEquals(_cts, cts))
                d.RaiseNotification("検索エラー: " + ex.Message);
        }
        finally
        {
            // 本実行が最新なら状態をリセット。後発実行に追い越されていればそちらに任せる。
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                if (!d.IsDisposed)
                    d.SetRunning(false);
            }
            cts.Dispose();
        }
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>アプリ終了開始: 実行中の grep を中止し、以後の結果反映を抑止する。</summary>
    public void BeginClose()
    {
        _closing = true;
        _cts?.Cancel();
    }

    /// <summary>終了がキャンセルされた場合に通常運用へ戻す。</summary>
    public void CancelClose() => _closing = false;

    private static string Summary(GrepOutcome o)
    {
        string errs = o.Errors.Count > 0 ? $"・読み取り不可 {o.Errors.Count} 件" : "";
        if (o.Hits.Count == 0)
            return (o.Cancelled ? "中断しました（0 件）" : "見つかりません") + errs;
        string head = o.Cancelled ? "中断: " : "";
        return $"{head}{o.Hits.Count} 行 / {o.FilesMatched} ファイル{errs}";
    }

    private void ShowResults(string pattern, string folder, GrepOutcome outcome)
    {
        if (_resultsView is null || _resultsView.IsDisposed)
            _resultsView = _resultsFactory();
        _resultsView.Populate(pattern, folder, outcome);
        if (outcome.Hits.Count > 0)
            _resultsView.ShowResults(_owner);
    }
}
