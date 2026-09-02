using System.IO;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>
/// 起動時の設定読込(M-11・設計 2026-09-02 §5.4)。判定と文言の組み立てをここへ寄せる
/// —— <c>Program.Main</c> は STAThread + <c>Application.Run</c> のため自動テストから叩けないので、
/// テストできる場所へ出す。通知(<c>MessageBox</c>)の配線は Task 9 の担当。
/// </summary>
internal static class SettingsStartup
{
    /// <summary>
    /// 3 文言で共有する導入部。<b>何が起きたか</b>と<b>次に何をすればよいか</b>を、長いパスより
    /// 前に置く —— SR は線形に読むので、案内をパスの後ろに置くと数百文字のパス朗読を聞き終える
    /// まで到達できない(設計 §10.7 指摘 3)。
    /// </summary>
    private const string CorruptHead =
        "設定ファイルが壊れていたため、既定の設定で起動しました。"
        + "以前の設定は失われているので、必要な項目は設定し直してください。";

    /// <summary>
    /// 「設定を変更すると上書きされる」では<b>弱すぎる</b>ため、実際の書込契機を書く。
    /// <c>MainForm.OnFormClosing</c> は終了のたびに <c>MainForm.SaveSettingsSafe</c> を呼び、
    /// <c>FileController.RegisterRecent</c> はファイルを開く/保存するたびに呼ぶ。つまり
    /// <b>ユーザーが設定を何も変えなくても</b>上書きされる。設計 §5.4 の文言案(「設定を変更すると
    /// 上書きされます」)はここが実物と食い違っていた(§10.15)。
    /// <para>
    /// 参照は<b>シンボル名で書く</b>(仕様レビュー M-6)。以前あった行番号の引用
    /// (<c>MainForm.cs:665</c> / <c>FileController.cs:1575</c>)は、B5 が同じ保存経路へ
    /// 手を入れたことで漂流していた。
    /// </para>
    /// </summary>
    private const string RewriteReason =
        "kxEdit はファイルを開いたときや終了するときに設定を書き直すので、";

    /// <summary>退避の試行(テスト seam)。既定は <see cref="SettingsStore.TryQuarantineCorrupt"/>。
    /// <para>
    /// <b>seam が要るのは L-3(多重起動の後着)が実ファイルでは決定的に作れないため。</b>
    /// 「退避に失敗し、かつ原本も既に無い」状態は、<see cref="SettingsStore.Load"/> が
    /// 読めた<b>後</b>に原本が消えていないと成立しない = 単一スレッドの <see cref="Prepare"/>
    /// 内では競合そのものを再現するしかない。設計 §6.1 が <c>File.Replace</c> の部分失敗で
    /// 採ったのと同じ形(注入で「起こせない状態」を作る)。
    /// </para></summary>
    private static (bool Moved, string QuarantinePath) QuarantineCorrupt(string path)
    {
        bool moved = SettingsStore.TryQuarantineCorrupt(path, out string quarantinePath);
        return (moved, quarantinePath);
    }

    /// <summary>設定を読み、必要なら破損ファイルを退避し、起動後に出す警告文言を返す
    /// (警告不要なら null)。<paramref name="quarantineOverrideForTest"/> は上記 seam
    /// (null=実物)。
    /// <para>
    /// <c>QuarantineBeforeFirstSave</c> = <b>このセッションの最初の設定保存の直前に、原本を
    /// <c>.bak</c> へ退避するか</b>(B5・設計 §6.3 = B4 申し送りの回収)。立つのは
    /// <see cref="SettingsLoadStatus.Unreadable"/> の枝<b>だけ</b>で、実行するのは
    /// <c>MainForm.TrySaveSettings</c> —— ここで改名しないのは、<b>この時点では上書きするか
    /// どうかまだ分からない</b>からである(B4 §5.2)。
    /// </para></summary>
    internal static (AppSettings Settings, string? Warning, bool QuarantineBeforeFirstSave) Prepare(
        string path,
        Func<string, (bool Moved, string QuarantinePath)>? quarantineOverrideForTest = null
    )
    {
        var settings = SettingsStore.Load(path, out var status);

        switch (status)
        {
            case SettingsLoadStatus.Corrupt:
            {
                // 退避の呼出はソリューション中ここ 1 か所だけ = 「Corrupt のときだけ改名する」は
                // この分岐の位置で保たれている(設計 §5.2 / §10.15)。
                var (moved, quarantined) = (quarantineOverrideForTest ?? QuarantineCorrupt)(path);
                if (moved)
                    return (
                        settings,
                        CorruptHead
                            + "\n\n壊れたファイルは次の場所へ退避しました。不要になったら削除してください:\n  "
                            // 退避先は切り詰めない。ユーザーがこの場所を他所から知る手段は無く、
                            // 切れば案内そのものが失われる(設計 §10.6 の「切ってよい側と
                            // いけない側」の非対称)。無害化(OneLine)は外さない。
                            + SanitizeForDisplay.OneLine(quarantined),
                        // Corrupt はここで退避済み = 保存直前の退避は要らない。
                        QuarantineBeforeFirstSave: false
                    );

                // L-3(設計 §10.17 指摘 2)の回収。退避に失敗したうえ原本も消えている場合がある
                // —— kxEdit を 2 つ同時に起動すると、後着は先着が改名し終えた後に File.Move を
                // 呼ぶので FileNotFound で false を受け取る。このとき下の「原本をコピーして
                // ください」は<実在しないファイル>を案内する = 設計 §10.6 (c) で潰したのと
                // 同型の欠陥になる。
                // 弁別は File.Exists 一本。例外の型(FileNotFoundException)で分けると、同じ結果に
                // 至る別の事由(外部ツールが消した・親ごと消えた)を取りこぼす —— 前置の列挙は
                // 原理的に漏れる(監査 §9 V-7)。
                // 消えた先がどこかは書かない。先着の .bad があったとしても、それがこの破損の
                // コピーである保証は無い(前回起動の残骸かもしれない)= 推測になる。
                if (!File.Exists(path))
                    return (
                        settings,
                        CorruptHead
                            + "\n\n壊れたファイルは退避できず、元の場所にも残っていません。"
                            + "壊れた内容の在りかは分かりません。",
                        QuarantineBeforeFirstSave: false
                    );

                return (
                    settings,
                    CorruptHead
                        + "\n\n壊れたファイルは退避できませんでした。"
                        + RewriteReason
                        + "このまま使うと上書きされます。"
                        + "壊れた内容を残しておきたい場合は、先に次のファイルをコピーしてください:\n  "
                        // 案内するのは「実在しない退避先」ではなく原本。これから上書きされる
                        // 当のファイルであり、%APPDATA% 配下なのでユーザーには判らない。
                        + SanitizeForDisplay.OneLine(path),
                    // 退避に失敗した Corrupt でも、保存直前の退避へは倒さない。倒すと上書きの直前に
                    // <壊れた内容>が .bak へ落ち、「読めなかっただけの以前の設定」を意味する
                    // 名前を汚す(.bad と .bak を分けた理由そのものが消える)。
                    QuarantineBeforeFirstSave: false
                );
            }

            case SettingsLoadStatus.Unreadable:
                // 起動時は退避しない: 中身が正常なファイルを改名してしまうため(B4 設計 §5.2)。
                // 代わりに<b>最初の保存の直前</b>へ予約する(QuarantineBeforeFirstSave・B5 設計 §6.3)
                // —— そこまで来れば中身はどのみち失われるので、退避は厳密に増える側にしか働かない。
                // B4 の申し送りはここの回収である: 下の「先にコピーしてください」は、ユーザーが
                // 対処する前に OnFormClosing / RegisterRecent が上書きすると<案内した当のファイル>を
                // 失わせていた。
                // 保存は止めない: 止めると「設定を適用しました」が虚偽になり、B5 が潰す欠陥を
                // ここで新設することになる(B4 設計 §5.5)。
                //
                // L-3 の File.Exists ガードは<b>ここには入れていない</b>(設計 §10.19 指摘 E)。
                // 残余は実在する —— 多重起動の後着が File.Exists→true と ReadAllText の間に
                // 先着の File.Move を踏むと Unreadable になり、下の案内はもう存在しないパスを指す。
                // 入れない理由は<b>「網が張れず、残余が極小だから」</b>である:
                //   ・この状態は決定的に作れない(Corrupt 側と違い退避の seam も通らない)ので、
                //     ガードを足しても無網の分岐を 1 本増やすことになる。
                //   ・窓は File.Exists と ReadAllText の間だけで、レビュアーも再現できていない。
                // <b>「誤爆するから入れない」ではない</b>(その理由は成立しない)。Unreadable は
                // File.Exists が true を返した後にしか出ず、権限起因は §10.14 のとおり Missing へ
                // 落ちるので、ここで Exists を再評価しても誤爆の余地はほぼ無い。
                // 決定的に作る手段ができたら対称化してよい。
                return (
                    settings,
                    "設定ファイルを読み取れなかったため、既定の設定で起動しました。\n\n"
                        + RewriteReason
                        + "このまま使うと、読み取れなかったファイルは既定の設定で上書きされます。"
                        // 退避の<b>成功は約束しない</b>。改名が落ちる事由は実在する ——
                        // (R) / (GR) のようにまとめて拒否する DENY ACE と FileShare.None の
                        // ロックでは File.Move も落ちる(2026-09-03 実測。読み取り権だけを拒否する
                        // ACE や FileShare.Delete のロックでは通る)。「退避しました」と書けば
                        // 到達しうる状態で偽になる = B5 が潰しに来た欠陥そのものになる。
                        // 失敗の事由を「読み取れない原因」に限定しない(仕様レビュー M-4)——
                        // 宛先が同名のディレクトリ・+".bak" で MAX_PATH 超過でも落ちる。
                        + "上書きの直前に '.bak' を付けた名前への退避も試みますが、失敗することがあります。"
                        // .bak は掃除しない(設計 §6.4 / B4 §9: 消すのはユーザーの判断)。
                        // ただし「退避できたファイル」に掛ける —— 無条件に書くと、退避が落ちた
                        // ユーザーへ実在しないファイルの後始末を指示することになる。
                        + "退避できたファイルは自動では消さないので、不要になったら削除してください。"
                        // 即時の行動指針は残す。退避が効かない事由では、これが唯一の手段になる。
                        + "以前の設定を残したい場合は、先に次のファイルをコピーしてください:\n  "
                        + SanitizeForDisplay.OneLine(path),
                    QuarantineBeforeFirstSave: true
                );

            case SettingsLoadStatus.Ok:
            case SettingsLoadStatus.Missing:
            default:
                // Missing = 初回起動。ここで警告すると全ユーザーが初回に読まされる(設計 §5.2)。
                // 将来 status が増えたときもここへ落ちる = 退避も通知もしない安全側。
                // Ok を退避側へ倒すと<正常な設定>を .bak へ改名して既定値で上書きすることになり、
                // M-11 が直しに来た無音リセットをより強い形で新設する。
                return (settings, null, QuarantineBeforeFirstSave: false);
        }
    }
}
