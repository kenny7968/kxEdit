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
    /// (<c>MainForm.cs:594</c>)、<c>FileController.RegisterRecent</c> はファイルを開く/保存する
    /// たびに呼ぶ(<c>FileController.cs:1575</c>)。つまり<b>ユーザーが設定を何も変えなくても</b>
    /// 上書きされる。設計 §5.4 の文言案(「設定を変更すると上書きされます」)はここが実物と
    /// 食い違っていた(§10.15)。
    /// </summary>
    private const string RewriteReason =
        "kxEdit はファイルを開いたときや終了するときに設定を書き直すので、";

    /// <summary>設定を読み、必要なら破損ファイルを退避し、起動後に出す警告文言を返す
    /// (警告不要なら null)。</summary>
    internal static (AppSettings Settings, string? Warning) Prepare(string path)
    {
        var settings = SettingsStore.Load(path, out var status);

        switch (status)
        {
            case SettingsLoadStatus.Corrupt:
            {
                // 退避の呼出はソリューション中ここ 1 か所だけ = 「Corrupt のときだけ改名する」は
                // この分岐の位置で保たれている(設計 §5.2 / §10.15)。
                bool moved = SettingsStore.TryQuarantineCorrupt(path, out string quarantined);
                return (
                    settings,
                    moved
                        ? CorruptHead
                            + "\n\n壊れたファイルは次の場所へ退避しました。不要になったら削除してください:\n  "
                            // 退避先は切り詰めない。ユーザーがこの場所を他所から知る手段は無く、
                            // 切れば案内そのものが失われる(設計 §10.6 の「切ってよい側と
                            // いけない側」の非対称)。無害化(OneLine)は外さない。
                            + SanitizeForDisplay.OneLine(quarantined)
                        : CorruptHead
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
