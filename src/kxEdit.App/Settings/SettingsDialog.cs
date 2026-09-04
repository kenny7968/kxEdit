using kxEdit.App.Settings.Tabs;
using kxEdit.Core.Settings;

namespace kxEdit.App.Settings;

/// <summary>
/// 設定ダイアログ（タブ構成・アクセシブル）。
/// タブ実装は <see cref="ISettingsTab"/>。タブ追加は _tabs 配列に 1 行足すだけで完結する。
/// 呼び出し側（MainForm.OpenSettings）は new SettingsDialog(_settings) → dlg.Result の
/// 従来インターフェースをそのまま使う。
/// </summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _baseline;
    private readonly IReadOnlyList<ISettingsTab> _tabs;

    // AccessibleName は付けない: タブ切替のたびに TabControl 名が読まれて冗長になるため。
    // タブヘッダ（TabPage.Text）＝カテゴリ名で識別は十分。
    private readonly TabControl _tabControl = new() { Dock = DockStyle.Fill };

    private readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(8),
    };

    /// <summary>再入ガード。<see cref="OnPageBodyLayout"/> 参照。</summary>
    private bool _resizing;

    public SettingsDialog(AppSettings s)
    {
        _baseline = s.Clone();
        _tabs = new ISettingsTab[]
        {
            new BasicSettingsTab(),
            new EditSettingsTab(),
            new KinsokuSettingsTab(),
            new DisplaySettingsTab(),
            new BackupSettingsTab(),
        };

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        BuildLayout();
        foreach (var t in _tabs)
            t.LoadFrom(_baseline); // BuildPage の後に必ず呼ぶ

        // 寸法は必ず LoadFrom の後に測る。[表示]タブのフォント名ラベルのように、設定値を
        // 流し込んで初めて中身が決まる項目があるため(BuildLayout 末尾で測ると空文字列のまま
        // 測ることになり、既定の「ＭＳ ゴシック」でたまたま余白 0 で収まっていただけで、
        // より長いフォント名の設定では毎回溢れる。実測: body 幅 488 -> 518)。
        ClientSize = SettingsTabLayoutHelper.ComputeDialogClientSize(_tabControl, _buttons);
        ActiveControl = _tabControl; // 先頭タブ「基本」の位置に居る
    }

    /// <summary>
    /// 編集結果の設定。ShowDialog が OK の後に読む。ダイアログで編集しない項目は元設定の値を保持する。
    /// 取得のたびに独立したインスタンスを組み立てる（保持状態を書き換えない・副作用なし）。
    /// </summary>
    public AppSettings Result
    {
        get
        {
            var r = _baseline.Clone();
            foreach (var t in _tabs)
                t.SaveTo(r);
            return r;
        }
    }

    private void BuildLayout()
    {
        foreach (var t in _tabs)
        {
            var page = new TabPage(t.Title) { UseVisualStyleBackColor = true };
            var body = t.BuildPage();
            body.Dock = DockStyle.Fill;
            // ページ本体の Layout を購読して、開いたまま内容が伸びたときに追随する
            // (例: [表示]タブでフォントを選び直すとフォント名ラベルがその場で伸びる)。
            // Form / TabControl の Layout はこの変化では発火しない(実測: Form=0 回・
            // TabControl=0 回・body=1 回)ため、body を購読するしかない。
            // ここを「Form の Layout を見れば足りる」と整理すると静かに壊れる。
            body.Layout += OnPageBodyLayout;
            page.Controls.Add(body);
            _tabControl.TabPages.Add(page);
        }

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            AutoSize = true,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
        };
        _buttons.Controls.AddRange(ok, cancel);

        // Dock は「子インデックスが大きい方」から確定する。したがって Dock=Fill を先に Add し、
        // Dock=Bottom を後に Add する(逆順にすると Fill の TabControl がクライアント全面を
        // 取ってボタン列を覆う)。DocumentInfoDialog も同じ順序。
        Controls.Add(_tabControl);
        Controls.Add(_buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// ページ本体の内容が伸びたときにダイアログを広げる。開いた時点の寸法で固定すると、
    /// [表示]タブでフォントを選び直したときにフォント名ラベルがその場で切れる。
    /// <b>広げる方向にのみ</b>更新する(縮めるとユーザーの操作中に座標が跳ぶ・ちらつく)。
    /// <see cref="Form.ClientSize"/> の代入が再びレイアウトを起こすため再入ガードを置く
    /// (広げる方向のみなので放っておいても収束するが、意図として明示する)。
    /// </summary>
    private void OnPageBodyLayout(object? sender, LayoutEventArgs e)
    {
        if (_resizing)
            return;

        _resizing = true;
        try
        {
            var want = SettingsTabLayoutHelper.ComputeDialogClientSize(_tabControl, _buttons);
            var now = ClientSize;
            if (want.Width > now.Width || want.Height > now.Height)
            {
                ClientSize = new Size(
                    Math.Max(now.Width, want.Width),
                    Math.Max(now.Height, want.Height)
                );
            }
        }
        finally
        {
            _resizing = false;
        }
    }

    // CA1001 対応(Sub 3.4-B): ISettingsTab 実装は Control フィールドを保持するため
    // IDisposable を持つ。BuildLayout が走った後は各 Control は Form の Controls ツリーに
    // 接続され Form.Dispose 経由で解放されるが、ISettingsTab 自身の Dispose 呼び出しを
    // 保証しないと BuildPage 未実行の異常系で Control がリークする。冪等なので二重解放は安全。
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var t in _tabs)
                t.Dispose();
        }
        base.Dispose(disposing);
    }
}
