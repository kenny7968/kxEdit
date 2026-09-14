// SingleInstanceRequest.cs
// 設計 2026-09-14 §4: 引き渡しメッセージ。今日は空ペイロードだが、
// 将来ファイルパスが乗る器として型を置く(設計 §6)。
namespace kxEdit.App;

/// <summary>
/// 2 つ目のインスタンスが既存インスタンスへ送る要求。
/// </summary>
/// <remarks>
/// 1 行 1 メッセージのテキストプロトコル。行区切りは <c>\n</c>(送受信側が付与・除去する)。
/// <para>
/// <b>パースは厳格側に倒す</b>。ここは<b>他プロセスが書いたバイト列</b>を読む場所である。
/// 今日のペイロードは空なので緩くても実害はないが、将来ファイルパスを載せた時点で
/// 攻撃面になる(設計 §4 ★3)。緩いパーサを後から締めるのは、締めたまま始めるより難しい。
/// </para>
/// </remarks>
internal sealed record SingleInstanceRequest
{
    /// <summary>プロトコルタグ。互換性のない変更をしたら数字を上げる。</summary>
    internal const string ProtocolTag = "KXEDIT1";

    private const string ActivateVerb = "ACTIVATE";

    /// <summary>受理したことを返す応答。要求と別の文字列であること。</summary>
    internal const string Ack = ProtocolTag + " OK";

    /// <summary>既存ウィンドウの前面化要求(今日はこれ 1 種類だけ)。</summary>
    internal static SingleInstanceRequest Activate { get; } = new();

    // 将来ファイルパスが乗る器としてインスタンスメソッドで置く(設計 §6)。今日のペイロードは
    // 空なのでインスタンス状態を読まず、CA1822 / S2325(static にできる)が立つ。
    // 【削除トリガー】設計 §6 のファイル引数対応でペイロードを持たせると Serialize() は
    // インスタンス状態を読むようになり、この抑止は不要になる。そのとき必ず削除すること
    // (不要な #pragma warning disable は警告が出ないため、書いた側が回収しないと誰も気付かない)。
#pragma warning disable CA1822, S2325 // reason: 上記。static 化すると将来ペイロードを足すときに呼び出し側を全面改修することになる
    internal string Serialize() => $"{ProtocolTag} {ActivateVerb}";
#pragma warning restore CA1822, S2325

    /// <summary>
    /// 解釈できないものは <c>null</c>。呼び出し側は ACK を返さず切る。
    /// <para>
    /// 行区切り(<c>\n</c> / <c>\r</c>)を含む文字列は受け付けない —— 除去は受信側の責務である。
    /// 長さの上限もここでは見ない。入力長の上限は受信側(SingleInstanceServer 側)の責務。
    /// </para>
    /// </summary>
    internal static SingleInstanceRequest? TryParse(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire))
            return null;
        // 将来ファイルパスが乗ったらトークン数の上限は緩むが、その時も
        // 「タグが一致し、動詞が既知」は据え置く。
        string[] parts = wire.Split(' ');
        if (parts.Length != 2)
            return null;
        if (!string.Equals(parts[0], ProtocolTag, StringComparison.Ordinal))
            return null;
        if (!string.Equals(parts[1], ActivateVerb, StringComparison.Ordinal))
            return null;
        return Activate;
    }
}
