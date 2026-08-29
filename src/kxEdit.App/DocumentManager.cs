using kxEdit.Core.Text;
using kxEdit.Editor;

namespace kxEdit.App;

/// <summary>
/// タブ（TabControl）と複数 Document の管理。各 Document は独立した EditorControl を持つ。
/// アクティブ由来のイベントのみ上位（MainForm）へ転送し、どのタブでも変更状態は
/// そのタブのラベルへ反映する。
/// </summary>
public sealed class DocumentManager : IDisposable
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly List<Document> _docs = new();
    private readonly Func<EditorControl> _editorFactory;

    public DocumentManager(Func<EditorControl> editorFactory)
    {
        _editorFactory = editorFactory;
        _tabs.Selected += (_, _) => OnSelectedTabChanged();
        _tabs.Deselecting += (_, _) => BeforeActiveChange?.Invoke(); // 切替直前に通知（マウス操作を含む）
        _tabs.KeyDown += OnTabKeyDown; // タブ列で Enter → エディタへ（編集開始）
    }

    /// <summary>MainForm の Controls へ載せるためのビュー（実体は TabControl）。</summary>
    public Control TabHost => _tabs;

    /// <summary>アクティブタブが切り替わる直前のフック（F2 編集中なら中断させる等）。
    /// マウス操作は Deselecting で、キーボード/プログラム経路は各選択メソッドから発火する。</summary>
    /// <remarks>
    /// <b>設計判断(Task 1e で確認済・案 A 採用)</b>:
    /// sender/args とも意味を持たない = <see cref="EventHandler"/> 化しない意図的例外。
    /// 他 5 個の event(<see cref="ActiveDocumentChanged"/>/<see cref="ActiveDirtyChanged"/>/
    /// <see cref="ActiveCaretChanged"/>/<see cref="EditorGotFocus"/>/<see cref="KeyBasedSwitch"/> 等)と
    /// 型が違うのは意図的。呼び出し側は <c>= () =&gt; ...;</c> の代入形式で購読する。
    /// 案 B(<see cref="EventHandler"/> 統一)も検討したが、購読側が sender/args を無視する
    /// 空実装になり益がないため見送り。将来 sender/args を使う必要が生じたら再検討する。
    /// テストで型を <see cref="Action"/> と機械固定(BeforeActiveChange_Type_IsIntentionallyAction)。
    /// </remarks>
    public Action? BeforeActiveChange { get; set; }

    public Document? Active => _tabs.SelectedTab?.Tag as Document;
    public IReadOnlyList<Document> Documents => _docs;
    public int Count => _docs.Count;

    public event EventHandler? ActiveDocumentChanged; // タブ切替
    public event EventHandler? ActiveDirtyChanged; // アクティブの変更状態（タイトル更新）
    public event EventHandler? ActiveCaretChanged; // アクティブの UpdateUI（行・桁更新）

    /// <summary>アクティブ Document のエディタが Win32 フォーカスを得た。CSVモード中の
    /// シンク退避判断は上位（MainForm）が行う（_csv.IsEditing を参照できるのが上位のため）。</summary>
    public event EventHandler<Document>? EditorGotFocus;

    /// <summary>キー起因(Ctrl+Tab/Ctrl+1..9)のタブ切替時に発火。MainForm が Announcer でタブ名を読ませる。</summary>
    public event EventHandler<Document>? KeyBasedSwitch;

    /// <summary>タブを閉じ切った直後に発火(閉じた Document を渡す)。購読側はその文書に
    /// 紐づく保持(検索の材質化キャッシュ等)を解放する。
    /// <b><see cref="ActiveDocumentChanged"/> では代用できない</b>: 選択タブ削除時の
    /// <c>TabControl.Selected</c> 発火は WinForms の仕様上保証されず(MainForm.CloseActiveTab の注記)、
    /// 非アクティブタブを閉じる経路ではそもそも切替が起きない。「閉じた」の唯一の通知源。</summary>
    public event EventHandler<Document>? DocumentClosed;

    /// <summary>任意の文書の dirty 状態が変化した(SavePointLeft / SavePointReached の両方)。
    /// <see cref="ActiveDirtyChanged"/> は<b>アクティブ分しか飛ばない</b>ため、非アクティブタブの
    /// 保存を購読側が取りこぼす。BackupCoordinator が「clean 化 = バックアップ不要」を
    /// 即時に知るための通知源(設計 2026-08-22 §3.1・A-1 / M-31)。</summary>
    public event EventHandler<Document>? DocumentDirtyChanged;

    /// <summary>A-13(設計 2026-08-29 §4.3): いずれかの文書でクリップボード操作が失敗した
    /// (他プロセスがクリップボードを保持中など)。MainForm が Announcer へ流す。
    /// <see cref="ActiveDirtyChanged"/> と違い<b>アクティブ限定にしない</b>:
    /// 失敗した操作は必ずユーザーの直前の操作=そのタブがアクティブのはずだが、
    /// 将来の非アクティブ経路(マクロ・自動化 API 等)でも取りこぼさないため
    /// <see cref="DocumentDirtyChanged"/> と同じ「全文書から拾う」形にする。</summary>
    public event EventHandler<ClipboardFailureKind>? ClipboardFailed;

    /// <summary>新しい空タブを生成しアクティブ化する。State の中身は呼び出し側が設定する。</summary>
    public Document CreateNew()
    {
        var editor = _editorFactory();
        editor.Dock = DockStyle.Fill;
        var page = new TabPage();
        page.Controls.Add(editor);

        var doc = new Document(editor, page);
        page.Tag = doc;

        // どのタブでも保存点変化でそのタブのラベルを更新（アクティブなら上位へ転送）。
        editor.SavePointLeft += (_, _) => OnDirtyChanged(doc);
        editor.SavePointReached += (_, _) => OnDirtyChanged(doc);
        // キャレット移動はアクティブ分のみ上位へ。
        editor.UpdateUI += (_, _) =>
        {
            if (ReferenceEquals(doc, Active))
                ActiveCaretChanged?.Invoke(this, EventArgs.Empty);
        };
        editor.GotFocus += (_, _) =>
        {
            if (ReferenceEquals(doc, Active))
                EditorGotFocus?.Invoke(this, doc);
        };
        // A-13: クリップボード失敗はどのタブからでも上位へ再送する(アクティブ限定にしない)。
        editor.ClipboardFailed += (_, kind) => ClipboardFailed?.Invoke(this, kind);

        _docs.Add(doc);
        _tabs.TabPages.Add(page);
        UpdateLabel(doc);
        BeforeActiveChange?.Invoke(); // 既存タブから切り替わる前に F2 編集等を後始末
        _tabs.SelectedTab = page; // 既存タブがあれば Selected 発火→ActiveDocumentChanged
        FocusActiveEditor(); // 新規/開く直後はエディタで即編集できるようにする
        return doc;
    }

    /// <summary>
    /// 保存済みの同一パスを開いているタブを探す（未保存タブは対象外）。
    /// <b>引数は正規化済み絶対パス</b>(Issue #48 / 設計書 §3.1 の不変条件)。
    /// ここではファイルシステムに触れない — 触ると開いているタブ数に比例して
    /// <c>GetFullPath</c> が走り、不達共有上の <c>~</c> パスが 1 つあるだけで
    /// UI が約 21 秒固まる(S-15)。正規化は呼出側が
    /// <see cref="IReachabilityProbe.NormalizePathWithTimeout"/> で、1 操作につき多くとも
    /// 1 回だけ行う(Ctrl+S のように 0 回で済む操作もある)。
    /// </summary>
    /// <remarks>
    /// <b>意図的な挙動変更(Issue #48 Task 5)</b>: 区切り差(<c>/</c> と <c>\</c>)や
    /// 相対セグメント(<c>..</c>)は<b>吸収しない</b>。以前は照会パスと開いている全タブのパスの
    /// 両方に <c>PathKey.For</c>(= <c>GetFullPath</c>。最終レビュー Q-I-2 で削除済み)を打っており、
    /// 呼び出しあたり 1 + タブ数回の実 I/O になりえた。Ctrl+S / 開く / grep ジャンプ / 復元は
    /// すべてここを通るため、不達共有上の <c>~</c> タブが 1 枚あるだけで全部が固まっていた。
    /// 吸収させたくなったらそれは呼出側が正規化を怠っているということなので、ここではなく
    /// 呼出側を直す(<see cref="PathKey.ForNormalized"/> の注記と同じ規約)。
    /// </remarks>
    public Document? FindByPath(string path)
    {
        string key = PathKey.ForNormalized(path);
        foreach (var d in _docs)
            if (d.State.Path is not null && PathKey.ForNormalized(d.State.Path) == key)
                return d;
        return null;
    }

    public void Activate(Document doc)
    {
        if (_tabs.SelectedTab != doc.Page)
        {
            BeforeActiveChange?.Invoke();
            _tabs.SelectedTab = doc.Page;
        }
        doc.FocusTarget.Focus(); // 開いた/呼び出したタブで即編集できるようにする
    }

    /// <summary>confirm が続行可を返したら閉じてネイティブ資源を解放する。閉じたら true。</summary>
    public bool TryClose(Document doc, Func<Document, bool> confirm)
    {
        if (!confirm(doc))
            return false;
        _docs.Remove(doc);
        _tabs.TabPages.Remove(doc.Page);
        doc.Editor.Dispose();
        doc.Page.Dispose();
        // 解放の後に通知する(購読側は「閉じた文書を掴んでいる参照を捨てる」だけで、
        // 破棄済みの Editor/Page には触らない)。confirm 却下の早期 return では発火しない。
        DocumentClosed?.Invoke(this, doc);
        return true;
    }

    /// <summary>タブを相対移動し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
    public void SelectNext(int dir)
    {
        int n = _tabs.TabPages.Count;
        if (n == 0)
            return;
        int prev = _tabs.SelectedIndex;
        BeforeActiveChange?.Invoke(); // 切替前に F2 編集等を後始末（キーボード経路）
        _tabs.SelectedIndex = ((prev + dir) % n + n) % n; // 端は巡回
        AnnounceThenFocus(prev); // I-5: 切替が発生した時のみタブ名を発声してからエディタへ遷移
    }

    /// <summary>指定位置のタブを選択し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
    public void SelectAt(int index)
    {
        if (index < 0 || index >= _tabs.TabPages.Count)
            return;
        int prev = _tabs.SelectedIndex;
        BeforeActiveChange?.Invoke(); // 切替前に F2 編集等を後始末（キーボード経路）
        _tabs.SelectedIndex = index;
        AnnounceThenFocus(prev); // I-5: 切替が発生した時のみタブ名を発声してからエディタへ遷移
    }

    // I-5: SelectedIndex が実際に変化した時だけタブ名を能動発声(単一タブや同一 index の no-op で
    // 冗長な発声を出さない)。発声→フォーカス遷移の順にすることで、エディタ UIA FocusChanged が
    // SR の発声キューを先取りするのを避け、タブ名が確実に先に読まれるようにする。
    private void AnnounceThenFocus(int prevIndex)
    {
        if (_tabs.SelectedIndex != prevIndex && Active is { } d)
            KeyBasedSwitch?.Invoke(this, d);
        FocusActiveEditor();
    }

    public static void UpdateLabel(Document doc) => doc.Page.Text = doc.TabLabel;

    // 選択変更そのものはフォーカスを動かさない（フォーカス先は呼び出し側が決める：
    // 新規/開く/閉じる→エディタ、Ctrl+Tab/番号での切替→エディタ(タブ名は KeyBasedSwitch で発声)）。
    private void OnSelectedTabChanged() => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);

    private void FocusActiveEditor() => Active?.FocusTarget.Focus();

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        // タブ列にフォーカスがある状態で Enter を押したらエディタへ移って編集を開始する
        // (I-5 以降は Ctrl+Tab/Ctrl+1..9 で直接エディタへ遷移するため、この救済路は
        // Alt+Tab 等で直接タブ列にフォーカスが渡った場合のフォールバック)。
        //
        // 重要: TabControl.ProcessKeyPreview は子孫（エディタ）にフォーカスがある編集中でも
        // プレビュー経路でこの KeyDown を発火させる。_tabs.Focused でタブ列自身がフォーカスを
        // 持つ時だけに限定しないと、編集中の Enter＝改行を横取りして native Scintilla へ
        // 渡らなくなる（改行が入力できなくなる）。タブ列フォーカス時のみ処理すること。
        if (e.KeyCode == Keys.Enter && _tabs.Focused)
        {
            FocusActiveEditor();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnDirtyChanged(Document doc)
    {
        UpdateLabel(doc);
        if (ReferenceEquals(doc, Active))
            ActiveDirtyChanged?.Invoke(this, EventArgs.Empty);
        DocumentDirtyChanged?.Invoke(this, doc); // 非アクティブ分も含めて購読側へ(A-1 / M-31)
    }

    // CA1001 対応(Sub 3.4-B): 通常は MainForm.Controls に _tabs(=TabHost) が接続され
    // Form.Dispose 経由で _tabs → TabPages → EditorControl まで一括解放される。
    // ただし DocumentManager が MainForm へ接続される前に破棄される異常系(コンストラクタ例外/
    // テスト)では _tabs と配下 Document(Editor+Page)がリークする。_tabs.Dispose は冪等で
    // TabPages/Controls 配下も再帰的に破棄するため、二重呼び出しでも安全。
    public void Dispose()
    {
        _tabs.Dispose();
    }
}
