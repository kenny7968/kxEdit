// CommandLineOptions.cs
// 設計 2026-09-14 §3「引数の扱い」。
using System.Diagnostics;

namespace kxEdit.App;

/// <summary>
/// kxEdit のコマンドライン引数。
/// </summary>
/// <remarks>
/// <b>未知の引数は無視する</b>のが本型の要点である。将来 <c>kxEdit.exe file.txt</c> で
/// ファイルを開けるようにする予定があり(設計 §6)、今日「未知の引数はエラー」の形にすると
/// そのとき後方非互換になる。無視した引数は Trace に残して post-mortem の手掛かりにする。
/// </remarks>
internal sealed record CommandLineOptions(bool NewInstance)
{
    /// <summary>
    /// 単一インスタンス制御を回避する開発・検証用スイッチ(設計 D3)。
    /// <b>説明書には載せない</b>——使うと複数インスタンス競合が復活する(設計 §7)。
    /// </summary>
    internal const string NewInstanceSwitch = "--new-instance";

    internal static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        bool newInstance = false;
        foreach (string arg in args)
        {
            // 完全一致で見る。前方一致にすると、将来のファイルパスが偶然前方一致したときに
            // 排他が黙って外れる(テスト Parse_DoesNotMatchNearMissSwitches)。
            if (string.Equals(arg, NewInstanceSwitch, StringComparison.OrdinalIgnoreCase))
            {
                newInstance = true;
                continue;
            }
            Trace.TraceInformation($"kxEdit args: ignored '{arg}'");
        }
        return new CommandLineOptions(newInstance);
    }
}
