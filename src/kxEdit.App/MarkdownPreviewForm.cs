using kxEdit.Core.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace kxEdit.App;

/// <summary>
/// マークダウンを整形表示するモーダルプレビュー窓。WebView2 に HTML を流し込み、
/// 相対リソース（画像・ローカルリンク）は元ファイルのフォルダ基準（仮想ホスト）で解決する。
/// baseDir が null（未保存タブ等）の場合は仮想ホストを設定せず、相対リソースは解決できない。
/// 「閉じる」ボタンと Esc の両方でエディタへ戻る。
/// <para>
/// MD-M-4: WebView2 の <c>userDataFolder</c> は per-form 一意 (<see cref="PreviewUserDataFolder"/>)
/// = プロファイルロック競合回避 + 破棄で一時ディレクトリごと除去。
/// </para>
/// </summary>
public sealed class MarkdownPreviewForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, AccessibleName = "プレビュー" };

    // MD-M-4: per-form の一時 UserDataFolder。%LOCALAPPDATA%\kxEdit\WebView2\preview-{guid} に作られ、
    // Dispose(bool) で削除される (Form 側 dispose が先 = WebView2 のファイルロックが外れてから
    // Delete する順序)。
    private readonly PreviewUserDataFolder _userData = new();
    private readonly Button _close = new()
    {
        Text = "閉じる(&C)",
        AccessibleName = "閉じる",
        Left = 6,
        Top = 6,
        Width = 100,
        Height = 26,
    };
    private readonly string _html;
    private readonly string? _baseDir;

    // WebView2 の NavigateToString(html) は内部で data:text/html;charset=utf-16;base64,... へ
    // エンコードして NavigationStarting を発火させる。このオブジェクトの生存期間で 1 回だけ
    // その data URI を通すためのフラグ。通過後は false に落として MD-M-3 (二層防御) を復元する。
    // WebView2 のイベントは全て UI スレッド発火なので通常のフィールドで OK。
    private bool _bootstrappingDataUri = true;

    public MarkdownPreviewForm(string html, string? baseDir, string fileName)
    {
        _html = html;
        _baseDir = baseDir;

        Text = $"プレビュー: {fileName} - kxEdit";
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        CancelButton = _close; // ボタン/フォーム側フォーカス時の Esc を担保

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 38 };
        _close.Click += (_, _) => Close();
        bottom.Controls.Add(_close);

        // Dock 順: Fill を先に Add し、Bottom を後から載せる。
        Controls.Add(_web);
        Controls.Add(bottom);

        Shown += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // MD-M-4: per-form UserDataFolder (プロファイルロック競合回避 + 破棄で除去)。
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userData.Path);
            if (IsDisposed || Disposing)
                return;
            await _web.EnsureCoreWebView2Async(env);
            if (IsDisposed || Disposing)
                return;

            var core = _web.CoreWebView2;

            // MD-M-2: WebResourceRequested に CSP header injector を装着。
            // filter/handler を装着してから NavigateToString しないと初回サブリソース要求
            // (styles.css) で virtual CSS response を返す機会を失うため、順序が重要
            // (SetVirtualHostNameToFolderMapping と NavigateToString の間に挟む)。
            // Attach 後は event delegate が Injector を root で保持するため、
            // ローカル変数のみで OK (WebView2 Dispose で購読が切れると GC 対象になる)。
            new PreviewCspHeaderInjector(env).Attach(core);

            // 相対リソース（画像・ローカルリンク）を .md のフォルダから解決する。
            if (!string.IsNullOrEmpty(_baseDir) && System.IO.Directory.Exists(_baseDir))
            {
                core.SetVirtualHostNameToFolderMapping(
                    MarkdownRenderer.PreviewVirtualHost,
                    _baseDir,
                    CoreWebView2HostResourceAccessKind.Allow
                );
            }

            // ローカル閲覧用途のため不要機能を抑止。
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // MD-M-6: WebView2 の browser accelerator keys (Ctrl+S=名前を付けて保存 /
            // Ctrl+P=印刷 / Ctrl+O=ファイル選択 等) を無効化。プレビューは閲覧専用のため
            // ブラウザ的な操作を露出させない。
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // MD-M-1 + MD-M-5: preview 内以外のナビゲートを block/外部起動へ振り分け。
            // NavigationStarting は同一 WebView 内での遷移 (a href クリック等) を捕捉、
            // NewWindowRequested は target=_blank / window.open 等の新窓要求を捕捉する。
            // 分類ロジックは PreviewNavigationPolicy.Classify (テスト網羅済み)。
            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;

            // WebView2 にフォーカスがある時の Esc を JS 経由で拾って閉じる。
            // このスクリプトは CDP 経由で注入されページ CSP の影響を受けずに実行される。
            core.WebMessageReceived += (_, e) =>
            {
                if (e.TryGetWebMessageAsString() == "close")
                    Close();
            };
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "document.addEventListener('keydown', e => {"
                    + " if (e.key === 'Escape') window.chrome.webview.postMessage('close'); });"
            );
            if (IsDisposed || Disposing)
                return;

            // DOM 準備完了・keydown リスナー装着済みの状態で WebView にフォーカス（本文を先に読ませる）。
            // 一発着火とし、以降のリダイレクト等では発火しない。
            void OnNavCompleted(object? _s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnNavCompleted;
                if (!IsDisposed && !Disposing && e.IsSuccess)
                    _web.Focus();
            }
            core.NavigationCompleted += OnNavCompleted;

            core.NavigateToString(_html);
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing)
                return; // フォーム破棄後（初期化中に閉じられた等）は何もしない
            string head =
                ex is WebView2RuntimeNotFoundException
                    ? "マークダウンプレビューには Microsoft Edge WebView2 ランタイムが必要です。\n"
                        + "インストール後に再度お試しください。"
                    : "マークダウンプレビューを表示できませんでした。";
            MessageBox.Show(
                this,
                $"{head}\n\n詳細: {ex.Message}",
                "プレビューを表示できません",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
            if (!IsDisposed)
                Close();
        }
    }

    /// <summary>
    /// MD-M-1 + MD-M-5: WebView2 内の遷移要求を分類し、preview 内以外は cancel。
    /// LaunchExternal は既定ブラウザ/メールクライアントで開き直す。
    /// Block はキャンセルのみで何も起動しない (file:// UNC 経路の NTLM 漏出防止)。
    /// <para>
    /// 初回の NavigateToString(html) は WebView2 内部で data:text/html;... URI として
    /// 発火するため、one-shot flag で 1 回だけ素通しする。通過後は通常の Classify に戻り、
    /// 悪意リンク経由の <c>data:</c> ナビゲート (MD-M-3 XSS 二層防御) は引き続き Block。
    /// </para>
    /// </summary>
    private void OnNavigationStarting(object? _s, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_bootstrappingDataUri && PreviewNavigationPolicy.IsNavigateToStringBootstrapUri(e.Uri))
        {
            _bootstrappingDataUri = false;
            return;
        }

        var cls = PreviewNavigationPolicy.Classify(e.Uri);
        if (cls == PreviewNavigationPolicy.Classification.AllowIntra)
        {
            return; // 素通し (about:blank のみ。MD-H-1 で https://kxedit.preview/* は Block)
        }
        e.Cancel = true;
        if (cls == PreviewNavigationPolicy.Classification.LaunchExternal)
        {
            TryLaunchExternal(e.Uri);
        }
    }

    /// <summary>
    /// MD-M-1: WebView2 の新窓要求は常に Handled=true にして WebView2 に窓を作らせない。
    /// LaunchExternal のみ既定ブラウザで開き直し、Block/AllowIntra はキャンセルのみ。
    /// AllowIntra の新窓要求は今の preview モデル (単一 WebView にモデル文書を流す) と
    /// 合致しないので開かない (=モーダル閉じ忘れ防止)。
    /// </summary>
    private static void OnNewWindowRequested(object? _s, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // WebView2 に新窓を作らせない
        var cls = PreviewNavigationPolicy.Classify(e.Uri);
        if (cls == PreviewNavigationPolicy.Classification.LaunchExternal)
        {
            TryLaunchExternal(e.Uri);
        }
    }

    /// <summary>
    /// 既定ブラウザ/メールクライアントで URL を開く。起動失敗 (ブラウザ未設定等) は
    /// preview 側の操作性を優先して silent (Trace 出力のみ)。
    /// </summary>
    private static void TryLaunchExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
            );
        }
        catch (Exception ex)
            when (ex
                    is System.ComponentModel.Win32Exception
                        or InvalidOperationException
                        or System.IO.FileNotFoundException
            )
        {
            System.Diagnostics.Trace.TraceWarning($"外部プロセス起動失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// MD-M-4: WebView2 の内部ファイルハンドルが解放されてから UserDataFolder を消したいので、
    /// <c>base.Dispose(disposing)</c> (= WebView2 コントロール破棄) を先に呼び、その後で
    /// <see cref="PreviewUserDataFolder"/> を Dispose する順。逆順にすると WebView2 側の
    /// ロックにかかって Delete が Trace 警告に落ちる (silent fallback ではあるが残骸が残る)。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _userData.Dispose();
        }
    }
}
