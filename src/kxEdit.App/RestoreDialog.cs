using System.IO;
using kxEdit.Core.Backup;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 起動時のクラッシュ復元ダイアログ（モーダル・CheckedListBox）。前回の異常終了で残った
/// バックアップを一覧し、復元するものを選ばせる。標準 Win32 コントロールなので NVDA が
/// ネイティブに各項目を読む。
/// </summary>
public sealed class RestoreDialog : Form
{
    public enum RestoreAction
    {
        Later,
        Restore,
        DiscardAll,
    }

    private readonly CheckedListBox _list = new()
    {
        Dock = DockStyle.Fill,
        CheckOnClick = true,
        IntegralHeight = false,
        AccessibleName = "復元する未保存ファイル",
    };
    private readonly IReadOnlyList<BackupRecord> _records;

    public RestoreAction Action { get; private set; } = RestoreAction.Later;
    public IReadOnlyList<BackupRecord> Checked { get; private set; } = Array.Empty<BackupRecord>();

    public RestoreDialog(IReadOnlyList<BackupRecord> records)
    {
        _records = records;
        Text = "前回の未保存ファイルの復元";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 560;
        Height = 360;
        KeyPreview = true;

        BuildLayout();

        foreach (var r in records)
        {
            int idx = _list.Items.Add(Describe(r));
            _list.SetItemChecked(idx, true); // 既定で全チェック
        }
    }

    /// <summary>
    /// <see cref="BackupRecord"/> をダイアログの 1 行表示文字列へ変換する。
    /// 攻撃者 JSON 由来の BiDi/format 制御(U+202E RLO 等)と CR/LF を
    /// <see cref="SanitizeForDisplay.OneLine(string?, int)"/> で除去し、ファイル名スプーフィング
    /// と改行注入によるダイアログ表示崩しを塞ぐ(BK-L-4)。path traversal 側面は HIGH-2 側で
    /// 塞ぎ済みという前提で組み立てる。
    /// BK-M-3 (v0.11): <see cref="BackupRecord.Content"/>=null (path-only fallback) の record は
    /// 末尾に <see cref="PathOnlyMarker"/> を付加し、ユーザに「本文は保存されていない=元ファイルを
    /// 開き直す」よう明示する。
    /// テストの都合で <c>internal</c>(<c>App.Tests</c> から Form を作らずに戻り値を検証する
    /// ため。<c>InternalsVisibleTo kxEdit.App.Tests</c> で公開)。
    /// </summary>
    internal static string Describe(BackupRecord r)
    {
        string timestamp = $"（{r.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm}）";
        // BK-M-3: Content=null の record は path-only fallback(サイズ上限超過)。
        // 末尾に固定マーカーを添え、SR は timestamp の後に「本文なし」旨を読み上げる。
        string suffix = r.Content is null ? " " + PathOnlyMarker : string.Empty;
        if (r.OriginalPath is null)
        {
            string untitled = r.UntitledNumber > 0 ? $"無題 {r.UntitledNumber}" : "無題";
            return $"{untitled}{timestamp}{suffix}";
        }
        var fileName = SanitizeForDisplay.OneLine(Path.GetFileName(r.OriginalPath));
        // UIA-M-3 (v0.11): dir が %USERPROFILE% 配下なら "~\..." に縮退する。OneLine を
        // 先に通してから HomeRelative を適用: OneLine は home 部分(信頼済み文字列)を変えず、
        // 攻撃者制御の suffix 側の BiDi/format 制御を先に除去する順序が安全。
        var dir = HomeRelative(
            SanitizeForDisplay.OneLine(Path.GetDirectoryName(r.OriginalPath) ?? string.Empty)
        );
        // HIGH-2 視認性強化: フルパスを 1 行に併記し、復元先ディレクトリを利用者が識別できるようにする。
        // SR 互換のため OwnerDraw で 2 段化はせず、" — " 区切りの 1 行に留める(header 冒頭コメント準拠)。
        return $"{fileName} — {ElideMiddle(dir, maxLen: 60)}{timestamp}{suffix}";
    }

    /// <summary>
    /// UIA-M-3 (v0.11): ディレクトリパスが %USERPROFILE% 配下なら先頭を "~" に置換して縮退表示する。
    /// 位置づけは「SR 発話が短くなり、スピーカー越しの周囲漏れも減る」純粋 UX/プライバシー改善
    /// (UIA クライアントは本文丸ごと読めるためセキュリティ利得はない=設計 §PR-G (2) 参照)。
    /// 判定は Windows のパス比較セマンティクスに合わせ <see cref="StringComparison.OrdinalIgnoreCase"/>。
    /// 完全一致 or 直後がパス区切り (Path.DirectorySeparatorChar) でなければ縮退しない
    /// (例: home = "%USERPROFILE%" 直下の隣接プロファイル風パスを "~Friend\..." に化かさない)。
    /// kxEdit は net9.0-windows 単一 target で <see cref="Path.GetDirectoryName(string?)"/> は
    /// AltDirectorySeparatorChar ('/') を返さないため、境界判定は '\\' のみで十分(untested な
    /// alt-sep 分岐は M-1 fixup で除去済)。
    /// ホームプロファイル外のパスはそのまま返す。テスト都合で <c>internal</c>。
    /// </summary>
    internal static string HomeRelative(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return string.Empty;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return dir;
        if (!dir.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return dir;
        // 完全一致 = home 直下の bare ケース(dir がそのままユーザプロファイル)。
        if (dir.Length == home.Length)
            return "~";
        char boundary = dir[home.Length];
        if (boundary == Path.DirectorySeparatorChar)
            return "~" + dir[home.Length..];
        // 「同名プレフィックス」の隣接プロファイル風パスは縮退しない(false positive 回避)。
        return dir;
    }

    /// <summary>BK-M-3: path-only fallback record の Describe 末尾に付ける marker。
    /// Content=null (32M chars 超過で本文を保存できなかった record) をユーザに明示する。
    /// テストからの assertion 用途で internal 公開(文字列直書きの複製を避ける)。</summary>
    internal const string PathOnlyMarker =
        "(サイズ超過のため本文は保存されていません — 元ファイルから開き直してください)";

    /// <summary>長すぎるディレクトリパスを「先頭…末尾」に省略する。maxLen 以下ならそのまま返す。</summary>
    private static string ElideMiddle(string s, int maxLen)
    {
        if (s.Length <= maxLen)
            return s;
        int keep = (maxLen - 3) / 2;
        return s[..keep] + "..." + s[^keep..];
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var info = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Text =
                "前回 kxEdit が正常に終了しなかったため、未保存の変更が残っています。復元するファイルを選んでください。",
        };
        // ダイアログ自体の説明にも載せ、SR が開いた直後に文脈を読めるようにする。
        AccessibleName = Text;
        AccessibleDescription = info.Text;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
        };
        var restore = new Button { Text = "選択を復元(&R)", AutoSize = true };
        var discard = new Button { Text = "すべて破棄(&D)", AutoSize = true };
        var later = new Button { Text = "あとで(&L)", AutoSize = true };
        restore.Click += (_, _) =>
        {
            Action = RestoreAction.Restore;
            Checked = CheckedRecords();
            DialogResult = DialogResult.OK;
        };
        discard.Click += (_, _) =>
        {
            Action = RestoreAction.DiscardAll;
            DialogResult = DialogResult.OK;
        };
        later.Click += (_, _) =>
        {
            Action = RestoreAction.Later;
            DialogResult = DialogResult.Cancel;
        };
        buttons.Controls.AddRange(restore, discard, later);

        root.Controls.Add(info, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = restore;
    }

    private List<BackupRecord> CheckedRecords()
    {
        var list = new List<BackupRecord>();
        foreach (int i in _list.CheckedIndices)
            list.Add(_records[i]);
        return list;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Action = RestoreAction.Later;
            DialogResult = DialogResult.Cancel;
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
