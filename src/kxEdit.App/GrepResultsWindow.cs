using System.IO;
using kxEdit.Core.Search;
using kxEdit.Core.Text;

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
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var hit in outcome.Hits)
            _list.Items.Add(new Row(hit, Format(hit, folder)));
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

    // 一覧に出す行の本文の最大文字数(超えたら "…" を付ける)。
    private const int MaxLineDisplay = 200;

    // 無害化に渡す前に、行の本文を切り出す窓の文字数。巨大な行(最大 64MB)の全体を
    // Trim / OneLine に渡してコピー・全走査させないため(perf-followups フェーズ 3・項目 14 の関連)。
    private const int RawLineWindow = 1024;

    /// <summary>
    /// 一覧の 1 行を作る。行の本文とファイル名は外部ファイル由来なので
    /// <see cref="SanitizeForDisplay.OneLine"/> で無害化する(perf-followups フェーズ 3・項目 12 / G-1:
    /// 8000 バイトより後ろの NUL、U+202E による拡張子の偽装)。
    /// <see cref="GrepHit.LineText"/> 自体はジャンプの照合キー(A-18)なので変えない。
    /// </summary>
    internal static string Format(GrepHit hit, string baseFolder)
    {
        string rel = SanitizeForDisplay.OneLine(RelativePath(baseFolder, hit.FilePath));

        // 先頭の空白は span のまま飛ばし(コピーしない)、そのうえで窓を切る。
        ReadOnlySpan<char> raw = hit.LineText.AsSpan().TrimStart();
        bool cutByWindow = raw.Length > RawLineWindow;
        if (cutByWindow)
        {
            int cut = RawLineWindow;
            if (char.IsHighSurrogate(raw[cut - 1]))
                cut--; // サロゲートペアを割らない
            raw = raw[..cut];
        }

        // OneLine は maxLength を超えると「maxLength - 1 字 + "…"」にする。従来の
        // 「200 字 + "…"」と同じ形にするため 201 を渡す。
        string line = SanitizeForDisplay.OneLine(raw.ToString(), MaxLineDisplay + 1).TrimStart();
        if (cutByWindow && !line.EndsWith('…'))
            line += "…";
        return $"{rel} (行 {hit.LineNumber}): {line}";
    }

    private static string RelativePath(string baseFolder, string full)
    {
        try
        {
            return Path.GetRelativePath(baseFolder, full);
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
