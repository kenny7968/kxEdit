namespace kxEdit.App.Tests;

/// <summary>
/// 引き渡しメッセージの直列化とパース(設計 2026-09-14 §4)。
/// 今日のペイロードは空だが、将来ファイルパスが乗る器としてプロトコルタグを持つ。
/// <b>パースは厳格側に倒す</b>: 受信側は他プロセスからの入力を読む場所であり、
/// 緩めると将来ファイルパスを載せたときにそのまま攻撃面になる(設計 §4 ★3)。
/// </summary>
public class SingleInstanceRequestTests
{
    [Fact]
    public void Serialize_RoundTripsThroughTryParse()
    {
        string wire = SingleInstanceRequest.Activate.Serialize();
        Assert.NotNull(SingleInstanceRequest.TryParse(wire));
    }

    [Fact]
    public void Serialize_StartsWithProtocolTag()
    {
        Assert.StartsWith(
            SingleInstanceRequest.ProtocolTag,
            SingleInstanceRequest.Activate.Serialize(),
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ACTIVATE")] // タグ無し
    [InlineData("KXEDIT1")] // 動詞無し
    [InlineData("KXEDIT2 ACTIVATE")] // 別バージョン
    [InlineData("kxedit1 ACTIVATE")] // タグの大小(プロトコルは Ordinal)
    [InlineData("KXEDIT1 activate")] // 動詞の大小
    [InlineData("KXEDIT1 QUIT")] // 未定義の動詞
    [InlineData("KXEDIT1 ACTIVATE EXTRA")] // 余分なトークン
    [InlineData("KXEDIT1  ACTIVATE")] // 空白 2 つ
    public void TryParse_RejectsMalformedInput(string? wire)
    {
        Assert.Null(SingleInstanceRequest.TryParse(wire));
    }

    [Fact]
    public void Ack_DiffersFromRequest()
    {
        // ACK を要求と同じ文字列にする変異を殺す(自分の送信をエコーバックと
        // 取り違える形になり、引き渡し失敗が成功に見える)。
        // 引数順は xUnit2000 / S3415 に従い「定数が expected」。NotEqual なので意味は変わらない。
        Assert.NotEqual(SingleInstanceRequest.Ack, SingleInstanceRequest.Activate.Serialize());
    }
}
