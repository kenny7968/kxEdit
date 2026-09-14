// SingleInstanceWire.cs
// 設計 2026-09-14 §4: 引き渡しプロトコルの行フレーミング(送受で共有する唯一の実装)。
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App;

/// <summary>
/// 引き渡しプロトコルのワイヤ層。1 行 1 メッセージの読み出しを送受で共有する。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ送受で共有するのか</b> —— 送り側(<see cref="SingleInstanceClient"/> の ACK 読み)と
/// 受け側(<see cref="SingleInstanceServer"/> の要求読み)は、戻り値の使い道と上限バイト数が
/// 違うだけで、ループの形は完全に同一だった。差はパラメータであって構造ではない。
/// そのうえ共有している中身が<b>順序依存の非自明な不変条件</b>(下記)なので、
/// 同じ罠を 2 か所に置くと「片方だけ直される / 片方だけ壊される」未来が容易に起きる。
/// 特に設計 §6 でファイルパスを載せるときに見直すのは受け側の <c>MaxRequestBytes</c> だけで、
/// そのとき改修者は受け側のループしか読まない。
/// </para>
/// <para>
/// <b><c>ConfigureAwait(false)</c> を外してはならない(単一インスタンス機構の全 await の正)</b>。
/// 引き渡し経路には <see cref="SingleInstanceClient"/> の
/// <c>GetAwaiter().GetResult()</c> による同期待ち合わせがあり、そこは UI スレッドから
/// 呼ばれうる。1 か所でも同期コンテキストへ戻す設定にすると、継続が UI スレッドを待ち、
/// UI スレッドが結果を待つデッドロックになる(実測で追認済み)。
/// 受け側の待受ループも同じ経路に繋がるので、条件は両側で同じ。
/// </para>
/// </remarks>
internal static class SingleInstanceWire
{
    /// <summary>
    /// 改行(<c>\n</c>)までを 1 行として読む。改行が来ないまま
    /// <paramref name="maxBytes"/> に達した場合と、相手が改行前に閉じた場合は <c>null</c>。
    /// </summary>
    /// <param name="stream">読み出し元(接続済みのパイプ)。</param>
    /// <param name="maxBytes">
    /// 1 行の上限バイト数 = 確保するバッファ長。<c>StreamReader</c> を使わないのは、
    /// この上限を呼び出し側が決めるため(他プロセスが書いたバイト列を読む場所なので、
    /// 上限なしにするとメモリを食わせられる)。
    /// </param>
    /// <param name="ct">この接続の期限。名前付きパイプに読み書きのタイムアウトは効かない。</param>
    internal static async Task<string?> ReadLineAsync(
        Stream stream,
        int maxBytes,
        CancellationToken ct
    )
    {
        byte[] buffer = new byte[maxBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break; // 相手が改行を送らずに閉じた
            // 【この 2 行の順序が本質・入れ替えないこと】改行の探索長は「今回読んだ分まで」
            // = total + read でなければならない。total を先に進めて探索長を total(= 読み込み前の
            // 総数)で打ち切る形へ崩すと、【今回の読み込みに含まれる改行を取りこぼす】。
            // バッファクォータ 0 の運用(SingleInstanceServer.CreatePipe 参照)では相手が
            // 小分けに書くため、取りこぼしは机上の話ではなく実際に踏む —— 症状は
            // 「要求 / ACK が来なかった」= 期限切れになるので、原因から遠い所で壊れる。
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, total + read);
            total += read;
            if (newline >= 0)
                return Encoding.UTF8.GetString(buffer, 0, newline);
        }
        return null;
    }
}
