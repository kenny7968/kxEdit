// PreviewUserDataSweeper.cs
// M-1 の付随修正(2026-08-29 最終レビュー 脆弱性パス M-V1):
// Environment.Exit はフォームの Dispose を走らせないため、プレビューを開いた状態で
// クラッシュすると PreviewUserDataFolder が回収されない。起動時 sweep で拾う。
using System.Diagnostics;
using System.IO;

namespace kxEdit.App;

/// <summary>
/// 起動時に <see cref="PreviewUserDataFolder"/> の残骸を掃除する。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PreviewUserDataFolder"/> は Dispose でディレクトリごと消すが、
/// M-1 で導入した <c>Environment.Exit(1)</c> はフォームの Dispose を走らせないため、
/// プレビューを開いたままクラッシュすると残骸が<b>単調増加し、回収の当てがなくなる</b>。
/// このディレクトリには WebView2 のプロファイル(Code Cache / Local Storage 等)が入り、
/// プレビューは文書のディレクトリを base URI に持つ(A-2)ので、
/// 相対参照で取得した外部リソースのキャッシュも入りうる=単なるディスク消費ではない。
/// <see cref="PreviewUserDataFolder"/> の doc が「次回起動時 sweep は v0.12 以降候補」と
/// 書いていたものを、M-1 が残骸を systematic にしたためここで回収する。
/// </para>
/// <para>
/// <b>並行インスタンスへの配慮が本 sweep の要</b>: 素朴に消すと、別プロセスの kxEdit が
/// 使用中のプロファイルを消しにいく。WebView2 はプロファイルを掴んでいるので途中で
/// <see cref="IOException"/> になるが、<b>ロックに当たる前に一部が消えて相手のプロファイルを
/// 壊しうる</b>。そのため「自分以外の kxEdit プロセスが 1 つも居ないとき」だけ実行する
/// (<see cref="SweepIfSoleInstance"/>)。判定は best-effort で、失敗したら掃除しない側に倒す。
/// </para>
/// </remarks>
internal static class PreviewUserDataSweeper
{
    /// <summary>`preview-` で始まるディレクトリだけを対象にする(誤爆防止)。</summary>
    private const string Pattern = "preview-*";

    /// <summary>既定の親ディレクトリ(<c>%LOCALAPPDATA%\kxEdit\WebView2</c>)。</summary>
    internal static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kxEdit",
            "WebView2"
        );

    /// <summary>
    /// 自分以外の kxEdit プロセスが居ないときだけ <see cref="Sweep"/> する。
    /// 起動経路から 1 回だけ呼ぶ(失敗しても起動は続ける)。
    /// </summary>
    internal static void SweepIfSoleInstance()
    {
        try
        {
            if (!IsSoleInstance())
                return;
            Sweep(DefaultRoot);
        }
        catch (Exception ex)
        {
            // 掃除は best-effort。ここで起動を止めない(残骸が残るだけ)。
            Trace.TraceWarning($"preview sweep skipped: {ex.Message}");
        }
    }

    /// <summary>自分以外に同名プロセスが居ないか。取得に失敗したら false(=掃除しない側)。</summary>
    private static bool IsSoleInstance()
    {
        using var self = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(self.ProcessName);
        try
        {
            return others.Length <= 1;
        }
        finally
        {
            foreach (var p in others)
                p.Dispose();
        }
    }

    /// <summary>
    /// <paramref name="root"/> 直下の <c>preview-*</c> ディレクトリを削除する。
    /// </summary>
    /// <returns>削除できた数(テスト用)。</returns>
    /// <remarks>
    /// 1 件の失敗で残り全部を諦めないよう、削除は 1 件ずつ try で囲む。
    /// root が無ければ 0 件で正常終了する(プレビューを一度も開いていない環境)。
    /// </remarks>
    internal static int Sweep(string root)
    {
        if (!Directory.Exists(root))
            return 0;
        int deleted = 0;
        foreach (string dir in Directory.EnumerateDirectories(root, Pattern))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 使用中 / 権限なし。次回起動でまた試す。
                Trace.TraceWarning($"preview sweep failed: {ex.Message} ({dir})");
            }
        }
        return deleted;
    }
}
