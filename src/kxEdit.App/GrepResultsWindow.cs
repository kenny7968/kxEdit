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
    // 一覧に出す行の本文の最大文字数(超えたら "…" を付ける)。
    private const int MaxLineDisplay = 200;

    // 無害化に渡す前に、行の本文を切り出す窓の文字数(perf-followups フェーズ 3・項目 12)。
    private const int RawLineWindow = 1024;

    // 一覧に出すパスの最大文字数(超えたら先頭を "…" にして末尾を残す)と、無害化に渡す前の末尾の窓。
    private const int MaxPathDisplay = 260;
    private const int RawPathWindow = 1024;

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
        try
        {
            _list.Items.Clear();
            // 同じファイルのヒットは続けて並ぶ(GrepService はファイルごとに行順で積む)。
            // パスの表示文字列はファイルが変わったときだけ作り直す(最終レビュー I-1)。
            string? lastPath = null;
            string lastDisplay = "";
            foreach (var hit in outcome.Hits)
            {
                if (!ReferenceEquals(hit.FilePath, lastPath))
                {
                    lastPath = hit.FilePath;
                    lastDisplay = DisplayPath(folder, hit.FilePath);
                }
                _list.Items.Add(new Row(hit, Compose(lastDisplay, hit)));
            }
        }
        finally
        {
            _list.EndUpdate();
        }
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;

        string suffix = outcome.Cancelled ? "（中断）" : "";
        if (outcome.Truncated)
            suffix += "（上限で打ち切り）";
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

    /// <summary>
    /// 一覧の 1 行を作る。行の本文とファイル名は外部ファイル由来なので
    /// <see cref="SanitizeForDisplay.OneLine"/> で無害化する(perf-followups フェーズ 3・項目 12 / G-1:
    /// 8000 バイトより後ろの NUL、U+202E による拡張子の偽装)。
    /// <see cref="GrepHit.LineText"/> 自体はジャンプの照合キー(A-18)なので変えない。
    /// </summary>
    internal static string Format(GrepHit hit, string baseFolder) =>
        Compose(DisplayPath(baseFolder, hit.FilePath), hit);

    private static string Compose(string displayPath, GrepHit hit) =>
        $"{displayPath} (行 {hit.LineNumber}): {DisplayLine(hit.LineText)}";

    /// <summary>
    /// 行の本文の表示。巨大な行(最大 64MB)の全体を Trim / OneLine に渡してコピー・全走査させないため、
    /// 先頭の空白を span のまま飛ばし(コピーしない)、窓で切ってから無害化する。
    /// 200 字を超えるか窓で切ったら、200 字 + "…" にする(従来の Trim → 200 字 + "…" と同じ境界)。
    /// </summary>
    private static string DisplayLine(string lineText)
    {
        ReadOnlySpan<char> raw = lineText.AsSpan().TrimStart();
        bool cutByWindow = raw.Length > RawLineWindow;
        if (cutByWindow)
            raw = raw[..CutKeepingPair(raw, RawLineWindow)];

        // 末尾の Trim は、従来の Trim() と同じく全角空白(U+3000)・NBSP も落とすため。
        string line = SanitizeForDisplay.OneLine(raw.ToString()).Trim();
        if (line.Length > MaxLineDisplay)
            return string.Concat(line.AsSpan(0, CutKeepingPair(line, MaxLineDisplay)), "…");
        return cutByWindow ? line + "…" : line;
    }

    /// <summary>
    /// パスの表示。基準フォルダーからの相対パスを無害化し、<see cref="MaxPathDisplay"/> 字を超えたら
    /// 先頭を "…" にして末尾(ファイル名と拡張子)を残す。深い階層で数万字になるパスを、
    /// ヒットごとの表示文字列に丸ごと載せないため(最終レビュー I-1)。
    /// 無害化の前に末尾の窓で切るので、無害化のコストもパスの長さに依らない。
    /// </summary>
    private static string DisplayPath(string baseFolder, string full)
    {
        string rel = RelativePath(baseFolder, full);
        bool cutByWindow = rel.Length > RawPathWindow;
        if (cutByWindow)
            rel = rel[TailStart(rel, RawPathWindow)..];

        string shown = SanitizeForDisplay.OneLine(rel);
        if (shown.Length > MaxPathDisplay)
            return string.Concat("…", shown.AsSpan(TailStart(shown, MaxPathDisplay - 1)));
        return cutByWindow ? "…" + shown : shown;
    }

    // s の先頭 n 字で切る位置。末尾が高サロゲートなら 1 字戻してペアを割らない。
    private static int CutKeepingPair(ReadOnlySpan<char> s, int n) =>
        char.IsHighSurrogate(s[n - 1]) ? n - 1 : n;

    // s の末尾 n 字の開始位置。先頭が低サロゲートなら 1 字進めてペアを割らない。
    private static int TailStart(string s, int n)
    {
        int start = s.Length - n;
        return char.IsLowSurrogate(s[start]) ? start + 1 : start;
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
