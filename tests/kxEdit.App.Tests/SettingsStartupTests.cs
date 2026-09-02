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
    /// <para>
    /// <b>MAX_PATH(260)を越える長さも作れる。</b>.NET ランタイムがパスへ <c>\\?\</c> を自動付与する
    /// (<c>PathInternal.EnsureExtendedPrefixIfNeeded</c>)ためで、<b>OS の長パス設定に依存しない</b>
    /// —— レビュアー実測では <c>LongPathsEnabled</c> 未設定のまま 271 文字の作成・<c>File.Exists</c>・
    /// <c>ReadAllText</c>・<c>File.Move</c> がすべて成功している。同じ .NET ランタイムで走る CI でも
    /// 成立する(設計 §10.16)。
    /// </para>
    /// </summary>
    private static string MakeLongSettingsPath(TempDir tmp, int quarantineLength)
    {
        // Root + "\" + dirName + "\" + Leaf + BadSuffix == quarantineLength
        int dirNameLength = quarantineLength - tmp.Root.Length - 2 - Leaf.Length - BadSuffix.Length;
        // fixture が空振りしていないことの検算(一時ディレクトリが極端に深い環境では
        // 静かに短いパスへ縮退させず、ここで落とす)。上限は 1 パス構成要素の限界(255)未満で、
        // TEMP が短い環境(CI)ほど大きくなる ——「短くなって網が緩む」向きには倒れない。
        Assert.InRange(dirNameLength, 8, 250);
        string dirName = new string('a', dirNameLength - 3) + "  z";
        string dir = Path.Combine(tmp.Root, dirName);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Leaf);
    }

    /// <summary>
    /// 「いつ上書きされるか」を<b>実物どおりに</b>述べていることの網(設計 §10.15 (1) / §10.16)。
    /// <para>
    /// 計画案の文言は「設定を変更すると上書きされます」だったが、実物は違う ——
    /// <c>MainForm.OnFormClosing</c>(<c>MainForm.cs:594</c>)が<b>終了のたび</b>に、
    /// <c>FileController.RegisterRecent</c>(<c>FileController.cs:1575</c> / <c>:276</c>)が
    /// <b>ファイルを開く・切り替える・保存するたび</b>に設定を書き直す。つまり
    /// <b>ユーザーが何も操作しなくても</b>上書きされる。
    /// </para>
    /// <para>
    /// 計画案へ戻すと「設定を変えなければ大丈夫」と読める = <b>案内文の側で、より静かな喪失を
    /// 作る</b>ことになる。<b>この網が無い間、文言を計画案へ戻す変異は App 743 全 PASS のまま
    /// 生存していた</b>(実測・§10.16 指摘 2)——「計画の文言は事実として弱かった」という
    /// Task 8 最大の主張が、緑では 1 ビットも守られていなかった。
    /// </para>
    /// </summary>
    /// <summary>
    /// <b>fixture 検算</b>: このパスに対して無害化(<c>OneLine</c>)が<b>実際に何かを変える</b>こと。
    /// <para>
    /// これが成り立たない短いパスでは <c>Assert.Contains(OneLine(path), warning)</c> が
    /// <b>恒真</b>になり、<c>OneLine</c> を外す変異がそのまま生存する ——
    /// §10.16 が退避先の枝で発見して <see cref="MakeLongSettingsPath"/> で潰した罠が、
    /// 残り 2 枝(Corrupt の退避失敗 / Unreadable)にそのまま残っていた(実測・§10.19)。
    /// </para>
    /// </summary>
    private static void AssertSanitizationIsObservable(string path)
    {
        string shown = SanitizeForDisplay.OneLine(path);
        Assert.Contains("  ", path, StringComparison.Ordinal); // 畳み込む対象がある
        Assert.DoesNotContain("  ", shown, StringComparison.Ordinal); // 畳み込まれる
        Assert.NotEqual(path, shown); // ★ 恒真 assertion にならない
    }

    private static void AssertNamesTheRealRewriteTrigger(string warning)
    {
        Assert.Contains("終了するとき", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("設定を変更すると", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// ファイルが無い = 初回起動。<b>警告を出さない</b>(設計 §5.2 の <c>Missing</c>)。
    /// ここが警告側へ倒れると、全ユーザーが初回起動で警告を読まされる。
    /// <para>
    /// <b><c>Missing</c> では「退避を呼ばないこと」は観測できない。</b>原本が存在しないので、
    /// 呼んでも <c>File.Move</c> が落ちて <c>false</c> を返し、<c>.bad</c> は生えない ——
    /// 下の <c>.bad</c> 不在 assertion が見ているのは「呼ばないこと」ではなく<b>「呼んでも失敗する
    /// こと」</b>である(<c>FileShare.None</c> の fixture で踏んだ欠陥と同型・§10.16 指摘 3)。
    /// 退避が <c>Corrupt</c> に限られることの網は <c>Ok</c> 側(原本のバイト列不変)と
    /// <c>Unreadable</c> 側(改名可能なロックで固定)が持つ。ここでは
    /// <b><c>Prepare</c> が設定ファイルを作らない</b>ことだけを押さえる。
    /// </para>
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
        // .bad が生えないことは「退避を呼ばない」の網ではない(呼んでも失敗するため。上の xmldoc)。
        // ここでは「Prepare が余計なファイルを置いていかない」ことだけを見ている。
        Assert.False(File.Exists(path + BadSuffix));
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
        string path = MakeLongSettingsPath(tmp, quarantineLength: 275);
        string quarantined = path + BadSuffix;
        File.WriteAllText(path, CorruptJson);

        var (settings, warning) = SettingsStartup.Prepare(path);

        Assert.NotNull(warning);
        Assert.False(File.Exists(path)); // ①原本は退避された
        Assert.Equal(CorruptJson, File.ReadAllText(quarantined));
        Assert.Equal(new AppSettings().FontName, settings.FontName); // 既定で続行

        // fixture の検算: 生のパスは 275 文字で連続空白を含み、無害化後はそれが畳まれて 1 文字短い。
        // この 3 行が緑にならない fixture では、下の Contains は切り詰め/無害化のどちらも弁別できない。
        // 275 は MAX_PATH(260)超 —— 計画案の上限 260 を付け直す変異まで殺せる長さである
        // (260 以下の fixture ではその変異が生存する。設計 §10.16)。
        string shown = SanitizeForDisplay.OneLine(quarantined);
        Assert.Equal(275, quarantined.Length);
        Assert.Contains("  ", quarantined, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", shown, StringComparison.Ordinal);

        // ②③退避先は「丸ごと」「無害化された形で」載る。無害化を外す変異と、
        // 274 未満のあらゆる上限を付け直す変異(計画案の 260 を含む)がここで落ちる。
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
        // 長いパス(連続空白入り)にする理由は AssertSanitizationIsObservable を参照。
        // 短いパスでは下の Contains(OneLine(path)) が恒真になり、無害化を外す変異が生存する。
        string path = MakeLongSettingsPath(tmp, quarantineLength: 275);
        string quarantined = path + BadSuffix;
        AssertSanitizationIsObservable(path);
        File.WriteAllText(path, CorruptJson);
        Directory.CreateDirectory(quarantined); // 退避を確実に失敗させる
        string marker = Path.Combine(quarantined, "keep.txt");
        File.WriteAllText(marker, "別物");

        var (_, warning) = SettingsStartup.Prepare(path);

        Assert.NotNull(warning);
        // 実在しない場所を案内しない。生と無害化後の<b>両方</b>を見る —— 片方だけだと、
        // もう一方の形で出力する実装が素通りする。
        Assert.DoesNotContain(quarantined, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SanitizeForDisplay.OneLine(quarantined),
            warning,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("退避しました", warning, StringComparison.Ordinal); // 成功側の文言ではない
        Assert.Contains("退避できませんでした", warning, StringComparison.Ordinal);
        // ★ 無害化を通っている。fixture が長い(= OneLine(path) != path)ので、
        //   OneLine を外す変異はここで落ちる(§10.16 が退避先の枝で潰したのと同じ罠)。
        Assert.Contains(SanitizeForDisplay.OneLine(path), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(path, warning, StringComparison.Ordinal); // 生のパスは載せない
        AssertNamesTheRealRewriteTrigger(warning);
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
        // 長いパス(連続空白入り)にする理由は AssertSanitizationIsObservable を参照。
        string path = MakeLongSettingsPath(tmp, quarantineLength: 275);
        AssertSanitizationIsObservable(path);
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
        // ★ 無害化を通っている(fixture が長いので恒真にならない)。
        Assert.Contains(SanitizeForDisplay.OneLine(path), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(path, warning, StringComparison.Ordinal); // 生のパスは載せない
        AssertNamesTheRealRewriteTrigger(warning);

        // ロックが外れれば以前の設定へ戻れる = 改名してはいけない対象だった。
        var reread = SettingsStore.Load(path, out var afterUnlock);
        Assert.Equal(SettingsLoadStatus.Ok, afterUnlock);
        Assert.Equal("BIZ UDゴシック", reread.FontName);
    }

    /// <summary>
    /// L-3(設計 §10.17 指摘 2 = Task 9 への申し送り)の回収。<b>多重起動の後着</b>は、先着が
    /// 改名し終えた後に <c>File.Move</c> を呼ぶので <c>FileNotFound</c> で <c>false</c> を受け取る。
    /// このとき<b>原本はもう存在しない</b>ので、退避失敗側の文言(「先に次のファイルをコピー
    /// してください: &lt;原本&gt;」)は<b>実在しない場所を案内する</b>——設計 §10.6 (c) が潰した
    /// のと同型の欠陥である。
    /// <para>
    /// <b>弁別は <c>File.Exists</c> 一本</b>にしてある。例外の型(<c>FileNotFoundException</c>)で
    /// 分けると、同じ結果に至る別の事由(先着が消した・外部ツールが消した・親ごと消えた)を
    /// 取りこぼす —— 本ブランチが一貫して守っている「前置の列挙は原理的に漏れる」。
    /// </para>
    /// <para>
    /// <b>seam で注入するのは、この状態が実ファイルでは決定的に作れないから。</b>
    /// <c>Corrupt</c> は <c>ReadAllText</c> が成功した後にしか出ないので、「読めた・退避に失敗した・
    /// 原本も無い」を単一スレッドの <c>Prepare</c> 内で並べるには競合そのものが要る。
    /// 注入する動作は後着が見る世界と同じ(<b>原本を改名してから false を返す</b>)。
    /// </para>
    /// <para>
    /// 対照は <c>Prepare_does_not_point_at_a_quarantine_that_was_never_created</c>(原本が残る
    /// 退避失敗)。そちらは原本を案内し、こちらは案内しない = <b>「常に案内しない」実装では
    /// 対照が赤くなる</b>ので、この網は退化した実装で緑にならない。
    /// </para>
    /// </summary>
    [Fact]
    public void Prepare_does_not_point_at_the_source_file_when_it_is_already_gone()
    {
        using var tmp = new TempDir();
        // 長いパスにすることで、下の 2 本の DoesNotContain(生 / 無害化後)が別々の主張になる。
        // 短いパスでは両者が同一文字列で、片方の形だけで出力する実装を弁別できない。
        string path = MakeLongSettingsPath(tmp, quarantineLength: 275);
        string quarantined = path + BadSuffix;
        AssertSanitizationIsObservable(path);
        File.WriteAllText(path, CorruptJson);

        var (settings, warning) = SettingsStartup.Prepare(
            path,
            p =>
            {
                File.Move(p, p + BadSuffix); // 先着の退避が済んだ世界
                return (false, p + BadSuffix); // 後着の File.Move は FileNotFound=false
            }
        );

        Assert.NotNull(warning);
        Assert.Equal(new AppSettings().FontName, settings.FontName); // 既定で続行するのは同じ
        // 前提の検算: 原本は本当に消えていて、中身は先着の .bad にある。
        Assert.False(File.Exists(path));
        Assert.Equal(CorruptJson, File.ReadAllText(quarantined));

        // ★ 実在しない場所をひとつも案内しない。原本(path)は退避先(path + ".bad")の
        //   prefix なので、原本を落とせば退避先も自動的に落ちる。
        Assert.DoesNotContain(path, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(SanitizeForDisplay.OneLine(path), warning, StringComparison.Ordinal);
        Assert.DoesNotContain(BadSuffix, warning, StringComparison.Ordinal);
        Assert.DoesNotContain("退避しました", warning, StringComparison.Ordinal); // 成功側でもない
        // 起きた事実は伝える(既定値で起動したことを黙らせない = M-11 が直しに来た無音リセット)。
        Assert.Contains("既定の設定で起動しました", warning, StringComparison.Ordinal);
        Assert.Contains("設定し直して", warning, StringComparison.Ordinal);
        // 「コピーしてください」は案内先があるときの文言。案内先が無い枝に残っていたら誤誘導。
        Assert.DoesNotContain("コピーしてください", warning, StringComparison.Ordinal);
    }
}
