using System.IO;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// 起動時の設定読込(M-11・設計 2026-09-02 §5.4)。判定(退避するか)と文言の組み立てを
/// <see cref="SettingsStartup"/> へ寄せてあるので、ここが<b>退避してよい枝といけない枝</b>の
/// 境界を固定する唯一の網になる。<c>MessageBox</c> の配線は Task 9 の担当で、ここでは扱わない。
/// </summary>
public class SettingsStartupTests
{
    /// <summary>JSON として解釈できない内容(= <c>Corrupt</c>)。</summary>
    private const string CorruptJson = "{ これは JSON ではない —— 壊れた設定";

    private const string Leaf = "settings.json";
    private const string BadSuffix = ".bad";

    /// <summary>
    /// 退避先(= <c>&lt;path&gt;.bad</c>)が <paramref name="quarantineLength"/> 文字ちょうどに
    /// なるよう、中間ディレクトリ名で長さを合わせた settings.json のパスを作る。
    /// <para>
    /// 目的は 2 つ。(1) <b>切り詰めの有無を弁別できる長さ</b>にすること —— 短いパスでは
    /// 上限付きの <c>OneLine(path, N)</c> でも丸ごと収まるため、上限を付け直す変異が生存する
    /// (設計 §10.6 と同型の罠)。(2) ディレクトリ名の末尾寄りに<b>連続空白</b>を 1 か所置き、
    /// <c>OneLine</c> の空白畳み込みが効いていることを観測面にする —— Windows のパス構成要素は
    /// 途中に連続空白を持てる(設計 §10.7 で実測)ので、無害化だけ外す変異が落ちる。
    /// </para>
    /// </summary>
    private static string MakeLongSettingsPath(TempDir tmp, int quarantineLength)
    {
        // Root + "\" + dirName + "\" + Leaf + BadSuffix == quarantineLength
        int dirNameLength = quarantineLength - tmp.Root.Length - 2 - Leaf.Length - BadSuffix.Length;
        // fixture が空振りしていないことの検算(一時ディレクトリが極端に深い環境では
        // 静かに短いパスへ縮退させず、ここで落とす)。
        Assert.InRange(dirNameLength, 8, 200);
        string dirName = new string('a', dirNameLength - 3) + "  z";
        string dir = Path.Combine(tmp.Root, dirName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Leaf);
    }

    /// <summary>
    /// ファイルが無い = 初回起動。<b>警告を出さない</b>(設計 §5.2 の <c>Missing</c>)。
    /// ここが警告側へ倒れると、全ユーザーが初回起動で警告を読まされる。
    /// 併せて<b>退避が走らない</b>(= <c>.bad</c> が生えない)ことも押さえる —— 退避は
    /// <c>Corrupt</c> とだけ結び付いているという主張の網。
    /// </summary>
    [Fact]
    public void Prepare_warns_nothing_when_the_settings_file_is_missing()
    {
        using var tmp = new TempDir();
        string path = tmp.File(Leaf);
        Assert.False(File.Exists(path)); // 前提: 「無い」状態そのもの

        var (settings, warning) = SettingsStartup.Prepare(path);

        Assert.Null(warning);
        Assert.Equal(new AppSettings().FontName, settings.FontName); // 既定で続行
        Assert.False(File.Exists(path)); // 読むだけ(生成しない)
        Assert.False(File.Exists(path + BadSuffix)); // 退避は走らない
    }

    /// <summary>
    /// 正常なファイル = 警告なし。<b>設定値が読めている</b>ことまで見る(非既定の
    /// <c>FontName</c> / <c>WindowWidth</c> を使うので、既定値フォールバックと区別が付く)。
    /// 原本のバイト列が動いていないことも押さえる —— 正常なファイルを触る実装が落ちる。
    /// </summary>
    [Fact]
    public void Prepare_warns_nothing_and_reads_the_values_for_a_valid_file()
    {
        using var tmp = new TempDir();
        string path = tmp.File(Leaf);
        SettingsStore.Save(
            path,
            new AppSettings { FontName = "BIZ UDゴシック", WindowWidth = 1234 }
        );
        string original = File.ReadAllText(path);

        var (settings, warning) = SettingsStartup.Prepare(path);

        Assert.Null(warning);
        Assert.Equal("BIZ UDゴシック", settings.FontName);
        Assert.Equal(1234, settings.WindowWidth);
        Assert.Equal(original, File.ReadAllText(path)); // 正常なファイルは動かさない
        Assert.False(File.Exists(path + BadSuffix));
    }

    /// <summary>
    /// 壊れたファイルは <c>.bad</c> へ退避され、<b>退避先が警告に載る</b>。
    /// <para>
    /// 観測点は 4 つ。①原本が退避された(元のパスから消え、中身は <c>.bad</c> にある)
    /// ②退避先が<b>丸ごと</b>載る(切り詰めない) ③無害化(空白の畳み込み)を通っている
    /// ④<b>次に何をすればよいか</b>が長いパスより<b>前</b>に来る —— SR は線形に読むので、
    /// 案内をパスの後ろに置くと数百文字のパス朗読を聞き終えるまで到達できない(設計 §10.7 指摘 3)。
    /// </para>
    /// </summary>
    [Fact]
    public void Prepare_quarantines_a_corrupt_file_and_points_at_it_in_full()
    {
        using var tmp = new TempDir();
        string path = MakeLongSettingsPath(tmp, quarantineLength: 250);
        string quarantined = path + BadSuffix;
        File.WriteAllText(path, CorruptJson);

        var (settings, warning) = SettingsStartup.Prepare(path);

        Assert.NotNull(warning);
        Assert.False(File.Exists(path)); // ①原本は退避された
        Assert.Equal(CorruptJson, File.ReadAllText(quarantined));
        Assert.Equal(new AppSettings().FontName, settings.FontName); // 既定で続行

        // fixture の検算: 生のパスは 250 文字で連続空白を含み、無害化後はそれが畳まれて 1 文字短い。
        // この 2 行が緑にならない fixture では、下の Contains は切り詰め/無害化のどちらも弁別できない。
        string shown = SanitizeForDisplay.OneLine(quarantined);
        Assert.Equal(250, quarantined.Length);
        Assert.Contains("  ", quarantined, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", shown, StringComparison.Ordinal);

        // ②③退避先は「丸ごと」「無害化された形で」載る。無害化を外す変異と、249 未満の上限を
        // 付け直す変異はここで落ちる。ただし<b>計画案の 260 は落ちない</b>(実測・生存)——
        // 260 超の fixture は MAX_PATH を越えるパスにファイルを作る必要があり、長パス対応の
        // 有無で CI とローカルの結果が変わる。この限界は設計 §10.15 に記録してある。
        Assert.Contains(shown, warning, StringComparison.Ordinal);

        // ④語順: 次にすべきこと(設定し直し)が退避先パスより前にある。
        int guideAt = warning.IndexOf("設定し直して", StringComparison.Ordinal);
        int pathAt = warning.IndexOf(shown, StringComparison.Ordinal);
        Assert.InRange(guideAt, 0, pathAt - 1);
    }

    /// <summary>
    /// 退避に<b>失敗した</b>ときは文言が変わり、<b>退避先を案内しない</b>(実在しない場所を
    /// 案内するのは設計 §10.6 (c) で潰したのと同じ欠陥)。代わりに案内するのは<b>原本</b>のパス
    /// —— これから上書きされる当のファイルであり、<c>%APPDATA%</c> 配下なのでユーザーが
    /// 他所から知る手段が無い。
    /// <para>
    /// fixture は宛先を<b>ディレクトリ</b>にして <c>File.Move(overwrite: true)</c> を確実に
    /// 失敗させる。ロックと違って決定的で、<b><c>overwrite: true</c> が同名の別物を消さない</b>
    /// ことも同時に観測できる。
    /// </para>
    /// <para>
    /// <c>DoesNotContain(&lt;退避先&gt;)</c> が成立するのは、禁止したい方(<c>path + ".bad"</c>)が
    /// 載せたい方(<c>path</c>)を<b>含む</b>側だからである。逆向き
    /// (<c>DoesNotContain(&lt;原本&gt;)</c>)は正しい実装を落とす —— 設計 §10.7 指摘 1 で
    /// レビュアー提案の網が成立しなかったのと同じ prefix 関係の、安全な側。
    /// </para>
    /// </summary>
    [Fact]
    public void Prepare_does_not_point_at_a_quarantine_that_was_never_created()
    {
        using var tmp = new TempDir();
        string path = tmp.File(Leaf);
        string quarantined = path + BadSuffix;
        File.WriteAllText(path, CorruptJson);
        Directory.CreateDirectory(quarantined); // 退避を確実に失敗させる
        string marker = Path.Combine(quarantined, "keep.txt");
        File.WriteAllText(marker, "別物");

        var (_, warning) = SettingsStartup.Prepare(path);

        Assert.NotNull(warning);
        Assert.DoesNotContain(quarantined, warning, StringComparison.Ordinal); // 実在しない場所を案内しない
        Assert.DoesNotContain("退避しました", warning, StringComparison.Ordinal); // 成功側の文言ではない
        Assert.Contains("退避できませんでした", warning, StringComparison.Ordinal);
        Assert.Contains(SanitizeForDisplay.OneLine(path), warning, StringComparison.Ordinal);
        Assert.Equal(CorruptJson, File.ReadAllText(path)); // 原本は動いていない
        Assert.True(Directory.Exists(quarantined)); // overwrite: true が同名の別物を消していない
        Assert.Equal("別物", File.ReadAllText(marker));
    }

    /// <summary>
    /// 下の <c>Unreadable</c> テストの<b>fixture 検算</b>。<c>FileShare.Delete</c> のロックは
    /// 「読めない」を作るが「改名できない」は<b>作らない</b>ことを実測で示す。
    /// これが成り立たない fixture(例: <c>FileShare.None</c>)だと、<c>Unreadable</c> でも
    /// 退避を呼ぶ変異が<b>「どのみち失敗するから」で素通し</b>し、網は「呼ばないこと」ではなく
    /// 「呼んでも失敗すること」しか見ていないことになる。
    /// </summary>
    [Fact]
    public void A_delete_shared_lock_blocks_reading_but_not_renaming()
    {
        using var tmp = new TempDir();
        string path = tmp.File(Leaf);
        File.WriteAllText(path, CorruptJson);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.ThrowsAny<IOException>(() => File.ReadAllText(path)); // 読めない(= Unreadable を作れる)
            Assert.True(SettingsStore.TryQuarantineCorrupt(path, out string moved)); // でも改名はできる
            Assert.True(File.Exists(moved));
            Assert.False(File.Exists(path));
        }
    }

    /// <summary>
    /// <b>読めなかっただけのファイルは退避しない</b>(設計 §5.2)—— 本タスクで最も重要な網。
    /// <c>Unreadable</c> の実態は AV / 同期ソフト / 別プロセスの一時的なロックで、<b>中身は正常</b>
    /// であることが多い。ここを <c>Corrupt</c> と同じ扱いにすると、健全な設定を <c>.bad</c> へ改名して
    /// 既定値で上書きすることになり、M-11 が直しに来た無音リセットを<b>より強い形で新設</b>する。
    /// <para>
    /// 観測点は「<c>.bad</c> が無い」だけでは足りない。原本が<b>そのまま残り、中身も無傷</b>で、
    /// ロックが外れれば<b>以前の設定へ戻れる</b>ところまで見る(非既定の <c>FontName</c> なので
    /// 既定値で書き直された場合と区別が付く)。
    /// </para>
    /// <para>
    /// <b>ロックは <c>FileShare.None</c> ではなく <c>FileShare.Delete</c> で掛ける。</b>
    /// <c>None</c> だと <c>File.Move</c> 自身も共有違反で失敗するため、<c>Unreadable</c> でも
    /// 退避を呼ぶ変異が<b>「退避に失敗したから」という理由で素通しする</b> —— 網が守りたい
    /// 「呼ばない」ではなく「呼んでも失敗する」を観測することになる。<c>Delete</c> は読み取りを
    /// 拒否しつつ改名を許すので、<b>退避を呼んだら実際に改名が成立する</b>状態になる。
    /// </para>
    /// </summary>
    [Fact]
    public void Prepare_warns_but_never_renames_an_unreadable_file()
    {
        using var tmp = new TempDir();
        string path = tmp.File(Leaf);
        SettingsStore.Save(path, new AppSettings { FontName = "BIZ UDゴシック" });
        string original = File.ReadAllText(path);

        string? warning;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            var prepared = SettingsStartup.Prepare(path);
            warning = prepared.Warning;
            Assert.Equal(new AppSettings().FontName, prepared.Settings.FontName); // 既定で続行
        }

        Assert.NotNull(warning);
        Assert.False(File.Exists(path + BadSuffix)); // ★ 改名していない
        Assert.True(File.Exists(path)); // ★ 原本がそのまま残っている
        Assert.Equal(original, File.ReadAllText(path)); // ★ 中身も無傷
        Assert.DoesNotContain(BadSuffix, warning, StringComparison.Ordinal); // 退避を案内しない
        Assert.DoesNotContain("壊れて", warning, StringComparison.Ordinal); // Corrupt の文言ではない
        Assert.Contains(SanitizeForDisplay.OneLine(path), warning, StringComparison.Ordinal);

        // ロックが外れれば以前の設定へ戻れる = 改名してはいけない対象だった。
        var reread = SettingsStore.Load(path, out var afterUnlock);
        Assert.Equal(SettingsLoadStatus.Ok, afterUnlock);
        Assert.Equal("BIZ UDゴシック", reread.FontName);
    }
}
