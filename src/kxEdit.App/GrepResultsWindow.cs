using System.IO;
using kxEdit.Core.Search;

namespace kxEdit.App;

/// <summary>
/// grep 結果のモードレス一覧（ListBox・1 行 1 ヒット）。標準 Win32 ListBox なので
/// NVDA が各項目をネイティブに読む（我々の UIA 層は不要）。Enter/ダブルクリックで
/// 選択ヒットを生成時に受け取ったコールバック(<see cref="GrepResultsCallbacks.OnActivate"/>)で
/// 通知し、上位（MainForm）がジャンプする。
/// </summary>
public sealed class GrepResultsWindow : Form, IGrepResultsView
{
    private readonly GrepResultsCallbacks _cb;
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        HorizontalScrollbar = true,
    };
    private string _baseFolder = "";

    public GrepResultsWindow(GrepResultsCallbacks callbacks)
    {
        _cb = callbacks;
        Text = "検索結果";
        Width = 760;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        KeyPreview = true;
        _list.AccessibleName = "検索結果";
        _list.DoubleClick += (_, _) => ActivateSelected();
        Controls.Add(_list);
    }

    /// <summary>結果を流し込み表示する。pattern/folder はタイトル整形と相対パス表示に使う。</summary>
    public void Populate(string pattern, string folder, GrepOutcome outcome)
    {
        _baseFolder = folder;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var hit in outcome.Hits)
            _list.Items.Add(new Row(hit, Format(hit)));
        _list.EndUpdate();
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;

        string suffix = outcome.Cancelled ? "（中断）" : "";
        if (outcome.Errors.Count > 0)
            suffix += $"（読み取り不可 {outcome.Errors.Count} 件）";
        Text =
            outcome.Hits.Count == 0
                ? $"grep: \"{pattern}\" — 見つかりません{suffix}"
                : $"grep: \"{pattern}\" — {outcome.Hits.Count} 行 / {outcome.FilesMatched} ファイル{suffix}";
    }

    /// <summary>窓を前面に出して結果リストへフォーカスする（SR が先頭ヒットを読む）。</summary>
    public void ShowResults(IWin32Window owner)
    {
        if (!Visible)
            Show(owner);
        Activate();
        _list.Focus();
    }

    private string Format(GrepHit hit)
    {
        string rel = RelativePath(hit.FilePath);
        string line = hit.LineText.Trim();
        if (line.Length > 200)
        {
            int cut = 200;
            if (char.IsHighSurrogate(line[cut - 1]))
                cut--; // サロゲートペアを割らない
            line = string.Concat(line.AsSpan(0, cut), "…");
        }
        return $"{rel} (行 {hit.LineNumber}): {line}";
    }

    private string RelativePath(string full)
    {
        try
        {
            return Path.GetRelativePath(_baseFolder, full);
        }
        catch
        {
            return full;
        }
    }

    private void ActivateSelected()
    {
        if (_list.SelectedItem is Row row)
            _cb.OnActivate(row.Hit);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter when _list.Focused:
                ActivateSelected();
                return true;
            case Keys.Escape:
                Hide();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // モードレスで再利用するため、ユーザーのクローズは破棄せず隠す。
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>ListBox 1 行ぶん。Display を ListBox/SR が読み、Hit がジャンプ先。</summary>
    private sealed record Row(GrepHit Hit, string Display)
    {
        public override string ToString() => Display;
    }
}
