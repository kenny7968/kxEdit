using System.IO;
using kxEdit.Core.Backup;

namespace kxEdit.App.Tests;

/// <summary>
/// BK-L-4 回帰: <see cref="RestoreDialog.Describe(BackupRecord)"/> が攻撃者 JSON 由来の
/// U+202E RLO 等の BiDi/format 制御文字と改行を 1 行表示に載せないことを固定する。
/// 実 UI(CheckedListBox/Form)には触れずヘルパの戻り値のみを検証する(App.Tests の
/// SmokeTests 以外での慣例)。
///
/// 制御文字はソース line terminator 解釈(U+2028 等)と csharpier の意図せぬ再整形の
/// 双方を避けるため <c>\uXXXX</c> エスケープで統一する(Task 1 SanitizeForDisplayTests は
/// RLO のみインライン直書き=Task 4 は一段厳しめで RLO も含めて全制御文字をエスケープに
/// 寄せる)。
/// また U+202E は UnicodeCategory.Format のため culture-sensitive な Contains/IndexOf では
/// 常に "見つかる" 側に倒れる。RLO の実在有無を厳密に問うために
/// <c>StringComparison.Ordinal</c> を明示する。
/// </summary>
public class RestoreDialogTests
{
    private static BackupRecord Rec(string? path, int untitled = 0) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: untitled,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: "",
            TimestampUtc: new DateTime(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc)
        );

    // ---------- 通常経路(挙動不変) ----------

    [Fact]
    public void Describe_UntitledRecord_ReturnsUntitledLabel()
    {
        var rec = Rec(path: null, untitled: 5);
        var d = RestoreDialog.Describe(rec);
        Assert.StartsWith("無題 5", d);
        Assert.Contains("2026-07-19", d);
    }

    [Fact]
    public void Describe_UntitledRecord_NoNumber_ReturnsUntitledOnly()
    {
        // UntitledNumber が 0 以下なら "無題" のみ(番号なし)
        var rec = Rec(path: null, untitled: 0);
        var d = RestoreDialog.Describe(rec);
        Assert.StartsWith("無題", d);
        Assert.DoesNotContain("無題 ", d);
    }

    [Fact]
    public void Describe_PathRecord_ReturnsFilenameAndDir()
    {
        var rec = Rec(@"C:\docs\hello.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.Contains("hello.txt", d);
        Assert.Contains(@"C:\docs", d);
    }

    // ---------- BK-L-4 core regression tests ----------

    [Fact]
    public void Describe_StripsRloOverride_InFilename()
    {
        // 攻撃者 JSON に U+202E RLO を含むファイル名を植えた場合:
        //   実体: evil-{RLO}txt.exe(表示上 evil-exe.txt に化ける)
        //   → Describe は RLO を drop して "evil-txt.exe" として 1 行表示する
        var rec = Rec("C:\\docs\\evil-\u202Etxt.exe");
        var d = RestoreDialog.Describe(rec);
        // Ordinal 明示の理由はクラス header 参照。生の RLO がダイアログテキストに載らない。
        Assert.DoesNotContain("\u202E", d, StringComparison.Ordinal);
        Assert.Contains("evil-txt.exe", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_StripsRloOverride_InDirectory()
    {
        var rec = Rec("C:\\\u202Emalicious\\file.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("\u202E", d, StringComparison.Ordinal);
        Assert.Contains("file.txt", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReplacesLineBreaks_InPath()
    {
        // OneLine は CR/LF を単一空白へ置換する(BK-L-5 と同族の防御)
        var rec = Rec("C:\\docs\\evil\r\ninjected\\file.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("\r", d, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", d, StringComparison.Ordinal);
        // 内容は 1 行として存在(改行崩壊しない)
        Assert.Single(d.Split('\n'));
    }

    // ---------- BK-M-3: path-only fallback marker ----------

    private static BackupRecord RecWithContent(string? path, string? content, int untitled = 0) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            OriginalPath: path,
            UntitledNumber: untitled,
            CodePage: 65001,
            HasBom: false,
            LineEndingId: 0,
            Content: content,
            TimestampUtc: new DateTime(2026, 7, 19, 12, 34, 56, DateTimeKind.Utc)
        );

    [Fact]
    public void Describe_ShowsPathOnlyMarker_WhenContentIsNull_ForPathRecord()
    {
        // BK-M-3: 上限超過で Content=null にフォールバックした record は、Describe 末尾に
        // 「本文なし=元ファイルから開き直してください」旨の marker を付ける契約を pin。
        // OriginalPath 有りの record は fileName/dir/timestamp の後に marker が来る。
        var rec = RecWithContent(@"C:\docs\huge.csv", content: null);
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.EndsWith(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.Contains("huge.csv", d, StringComparison.Ordinal); // ファイル名は通常通り表示
    }

    [Fact]
    public void Describe_ShowsPathOnlyMarker_WhenContentIsNull_ForUntitledRecord()
    {
        // BK-M-3: 無題タブの path-only fallback も marker を付ける(OriginalPath=null 経路)。
        var rec = RecWithContent(path: null, content: null, untitled: 7);
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.EndsWith(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
        Assert.Contains("無題 7", d, StringComparison.Ordinal); // 無題ラベルは通常通り
    }

    [Fact]
    public void Describe_DoesNotShowPathOnlyMarker_WhenContentIsPresent()
    {
        // 通常経路 regression: Content が非 null(=通常サイズ) の record には marker は付かない。
        var recPath = RecWithContent(@"C:\docs\normal.txt", content: "本文あり");
        Assert.DoesNotContain(
            RestoreDialog.PathOnlyMarker,
            RestoreDialog.Describe(recPath),
            StringComparison.Ordinal
        );
        var recUntitled = RecWithContent(path: null, content: "本文あり", untitled: 3);
        Assert.DoesNotContain(
            RestoreDialog.PathOnlyMarker,
            RestoreDialog.Describe(recUntitled),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Describe_DoesNotShowPathOnlyMarker_WhenContentIsEmpty()
    {
        // Content="" (空文字) は「意図した空ファイルの通常保存」= marker 対象外。
        // marker は Content is null=path-only fallback のみに付く契約を pin。
        var rec = RecWithContent(@"C:\docs\empty.txt", content: "");
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain(RestoreDialog.PathOnlyMarker, d, StringComparison.Ordinal);
    }

    // ---------- UIA-M-3: HomeRelative path display (設定なし・UX 改善) ----------
    // 設計 §PR-G (2): UIA クライアントは本文丸ごと読めるため縮退にセキュリティ利得はない。
    // 位置づけは「SR 発話が短くなり、スピーカー越しの周囲漏れも減る」純粋 UX/プライバシー改善。
    // ホーム下は "~\..." 縮退・home 外はフルパスのまま・home 判定は case-insensitive を pin する。
    // ローカルパス literal は tools/check-no-local-paths.ps1 に引っかかるため、home は必ず
    // Environment.GetFolderPath から取り、Path.Combine で組み立てる(literal を書かない)。

    [Fact]
    public void Describe_ReplacesUserProfilePrefix_WithTilde()
    {
        // %USERPROFILE%\Documents\a.txt を Describe に食わせると、ディレクトリ部分は
        // "~\Documents" に縮退し、生の home 文字列は含まれない。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home)); // 前提: 開発/CI で必ず取得できる
        var rec = Rec(Path.Combine(home, "Documents", "a.txt"));
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(@"~\Documents", d, StringComparison.Ordinal);
        Assert.DoesNotContain(home, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UserProfilePrefixMatch_IsCaseInsensitive()
    {
        // Windows のパス比較セマンティクスに合わせ home 判定は StringComparison.OrdinalIgnoreCase。
        // home と case が異なる path でも縮退される invariant を pin する。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home));
        string cased = home.ToUpperInvariant();
        if (cased == home)
        {
            // home が既に全 upper(まれ)の環境では lower に振る。invariant は「case が違うこと」。
            cased = home.ToLowerInvariant();
        }
        Assert.NotEqual(home, cased); // case を確実に反転させたことの sanity
        var rec = Rec(Path.Combine(cased, "Documents", "a.txt"));
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(@"~\Documents", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_PathOutsideUserProfile_IsUnchanged()
    {
        // home 外(別ドライブ)のパスはフルパス表示のまま。誤って tilde 化しない invariant を pin。
        var rec = Rec(@"D:\Data\file.txt");
        var d = RestoreDialog.Describe(rec);
        Assert.Contains(@"D:\Data", d, StringComparison.Ordinal);
        Assert.DoesNotContain("~", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UserProfilePrefixMatch_RequiresPathBoundary()
    {
        // false positive 回避 pin: home が "%USERPROFILE%" のとき、末尾に文字を継ぎ足した
        // 「隣接プロファイル」風のパス (home + "Friend" のような形) を "~Friend\..." に
        // 縮退しない。home 直後は パス区切り or 文字列終端でなければマッチしない invariant。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home));
        // home 末尾に文字を足しただけの「隣接プロファイル」風パスを合成する。
        // Path.Combine では境界を跨げないので、末尾 basename を suffix で膨らませる方式を使う。
        string sibling = home + "Friend";
        var rec = Rec(Path.Combine(sibling, "Documents", "a.txt"));
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("~", d, StringComparison.Ordinal);
        // ディレクトリ部分に sibling がそのまま載る(縮退しない)
        Assert.Contains(sibling, d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UntitledRecord_DoesNotContainTilde()
    {
        // regression pin: OriginalPath=null(無題)経路は Describe の dir 変換を通らないので
        // "~" が混入しない。UIA-M-3 で dir 変換を追加した後もこの invariant を維持する。
        var rec = Rec(path: null, untitled: 3);
        var d = RestoreDialog.Describe(rec);
        Assert.DoesNotContain("~", d, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ExactUserProfilePath_YieldsBareTilde()
    {
        // %USERPROFILE% 直下に保存されたファイル → dir は home 完全一致 → 表示は "~"。
        // HomeRelative の dir.Length == home.Length 分岐を機械固定 (境界チェック実装後の
        // "" 返しへの regression を防ぐ)。
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return; // 環境依存で home が空のときは skip 相当 (assert しない)
        var rec = Rec(Path.Combine(home, "readme.txt"));
        var d = RestoreDialog.Describe(rec);
        // ElideMiddle は maxLen=60 以下ならそのまま返すので dir="~" が保持される。
        Assert.Contains("— ~", d, StringComparison.Ordinal);
    }

    // ---------- ボタン → RestoreAction のマッピング(E-2 で判明した未被覆リンク) ----------
    //
    // 「すべて破棄」を押したときに Action が DiscardAll になることは、これまでどのテストでも
    // 検証されていなかった(このクラスは Describe だけを見ていた)。E-2 の欠陥は下流
    // (BackupCoordinator → writer → BackupStore)にあったが、この入口が壊れても症状は同じ
    // 「押しても何も消えない」になる。E-2 の下流を実 I/O で固めた以上、入口も固定しておく。
    //
    // 実 UI に触れるが ShowDialog はしない(モーダルループを起こすとテストが返らない)。
    // ただし Button.PerformClick は CanSelect(=可視な親チェーン)を要求するため、
    // Show していないフォームでは何も起きずに緑になる。DocumentInfoDialogTests と同じ
    // 「画面外で Show する」流儀に合わせる(この一手が無いと 3 本とも空虚なテストになる)。

    /// <summary>ダイアログを画面外に可視化する(PerformClick を機能させるために必要)。</summary>
    private static RestoreDialog ShowOffScreen(params BackupRecord[] records)
    {
        var dlg = new RestoreDialog(records)
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        dlg.Show();
        return dlg;
    }

    private static T? FindControlOrNull<T>(Control root, Func<T, bool>? match = null)
        where T : Control
    {
        foreach (Control c in root.Controls)
        {
            if (c is T t && (match is null || match(t)))
                return t;
            var found = FindControlOrNull(c, match);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static Button FindButton(Control root, string text)
    {
        var b = FindControlOrNull<Button>(root, x => x.Text == text);
        Assert.NotNull(b); // 文言を変えたらこのテスト群が探せなくなる=ここで気づく
        return b!;
    }

    [Fact]
    public void DiscardButton_SetsDiscardAllAction_IgnoringChecks() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen(Rec(path: null, untitled: 1));
            // チェックを付けた状態から押す。既定(未チェック)から始めると
            // 「Checked が空のまま」が既定値と区別できず空虚な assertion になる。
            var list = FindControlOrNull<CheckedListBox>(dlg);
            Assert.NotNull(list);
            list!.SetItemChecked(0, true);

            FindButton(dlg, "すべて破棄(&D)").PerformClick();

            Assert.Equal(RestoreDialog.RestoreAction.DiscardAll, dlg.Action);
            Assert.Empty(dlg.Checked); // 破棄は選択に依らない(Checked を拾わない)
        });

    [Fact]
    public void RestoreButton_SetsRestoreAction_AndCapturesOnlyCheckedRecords() =>
        Sta.Run(() =>
        {
            var kept = Rec(path: null, untitled: 1);
            var skipped = Rec(path: null, untitled: 2);
            using var dlg = ShowOffScreen(kept, skipped);
            var list = FindControlOrNull<CheckedListBox>(dlg);
            Assert.NotNull(list);
            // ダイアログは既定で全件チェック済み。1 件を**外して**部分選択にする
            // (チェックを付ける方向だと no-op になり、全選択と区別できない fixture になる)。
            list!.SetItemChecked(1, false);

            FindButton(dlg, "選択を復元(&R)").PerformClick();

            Assert.Equal(RestoreDialog.RestoreAction.Restore, dlg.Action);
            Assert.Same(kept, Assert.Single(dlg.Checked));
        });

    [Fact]
    public void LaterButton_SetsLaterAction_FromNonDefaultState() =>
        Sta.Run(() =>
        {
            using var dlg = ShowOffScreen(Rec(path: null, untitled: 1));
            // Action の既定値が Later なので、まず DiscardAll へ倒してから押す
            // (既定のままでも緑になる空虚な assertion を避ける)。
            FindButton(dlg, "すべて破棄(&D)").PerformClick();
            Assert.Equal(RestoreDialog.RestoreAction.DiscardAll, dlg.Action);

            FindButton(dlg, "あとで(&L)").PerformClick();

            Assert.Equal(RestoreDialog.RestoreAction.Later, dlg.Action);
        });
}
