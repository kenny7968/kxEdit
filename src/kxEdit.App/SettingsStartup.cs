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
    /// <c>MainForm.OnFormClosing</c> は終了のたびに <c>SaveSettingsSafe</c> を呼び
    /// (<c>MainForm.cs:665</c>)、<c>FileController.RegisterRecent</c> はファイルを開く/保存する
    /// たびに呼ぶ(<c>FileController.cs:1575</c>)。つまり<b>ユーザーが設定を何も変えなくても</b>
    /// 上書きされる。設計 §5.4 の文言案(「設定を変更すると上書きされます」)はここが実物と
    /// 食い違っていた(§10.15)。
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
    /// (null=実物)。</summary>
    internal static (AppSettings Settings, string? Warning) Prepare(
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
                            + SanitizeForDisplay.OneLine(quarantined)
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
                            + "壊れた内容の在りかは分かりません。"
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
                        + SanitizeForDisplay.OneLine(path)
                );
            }

            case SettingsLoadStatus.Unreadable:
                // 退避しない: 中身が正常なファイルを改名してしまうため(設計 §5.2)。
                // 保存も止めない: 止めると「設定を適用しました」が虚偽になり、B5 が潰す欠陥を
                // ここで新設することになる(設計 §5.5)。先に伝えることで代える。
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
                        + "以前の設定を残したい場合は、先に次のファイルをコピーしてください:\n  "
                        + SanitizeForDisplay.OneLine(path)
                );

            case SettingsLoadStatus.Ok:
            case SettingsLoadStatus.Missing:
            default:
                // Missing = 初回起動。ここで警告すると全ユーザーが初回に読まされる(設計 §5.2)。
                // 将来 status が増えたときもここへ落ちる = 退避も通知もしない安全側。
                return (settings, null);
        }
    }
}
