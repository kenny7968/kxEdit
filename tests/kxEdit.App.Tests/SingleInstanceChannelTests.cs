using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;

namespace kxEdit.App.Tests;

/// <summary>
/// 引き渡しトランスポート(設計 2026-09-14 §4)。
/// <para>
/// 本テストは<b>実パイプを使う</b>。ウィンドウは作らないので STA も UI も不要。
/// パイプ名は <see cref="UniqueName"/> で毎回変える —— 実運用の kxEdit が同じ PC で
/// 動いていても干渉させないため。
/// </para>
/// </summary>
public class SingleInstanceChannelTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(800);

    private static string UniqueName() => $"kxEditTest.{Environment.ProcessId}.{Guid.NewGuid():N}";

    [Fact]
    public void HandOff_InvokesActivationOnServer_AndReportsSuccess()
    {
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void HandOff_ServesMultipleRequestsInSequence()
    {
        // 1 接続で待受ループが終わる変異を殺す(2 回目以降が NoResponse になる)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(2, activations);
    }

    [Fact]
    public void HandOff_WithoutServer_ReportsNoResponse()
    {
        Assert.Equal(
            HandoffResult.NoResponse,
            SingleInstanceClient.TryHandOff(UniqueName(), ShortTimeout)
        );
    }

    [Fact]
    public void HandOff_WhenActivationFails_ReportsNoResponse()
    {
        // 既存インスタンスの UI スレッドがハングしている状況(設計 D4)。
        // ACK を返さないので、B 側は「応答しない」と判定しなければならない。
        string pipe = UniqueName();
        using var server = new SingleInstanceServer(pipe, () => false);
        Assert.True(server.Start());

        Assert.Equal(HandoffResult.NoResponse, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
    }

    [Fact]
    public void Server_RejectsMalformedRequest_WithoutInvokingActivation()
    {
        // 他プロセスが書いた不正なバイト列で前面化を起こさせない(設計 §4 ★3)。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        using (
            var client = new NamedPipeClientStream(
                ".",
                pipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            )
        )
        {
            client.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] junk = Encoding.UTF8.GetBytes("KXEDIT1 QUIT\n");
            client.Write(junk, 0, junk.Length);
            client.Flush();
            // ACK は返らない。読もうとすると 0 バイトで切れる。
            Assert.Equal(0, client.Read(new byte[16], 0, 16));
        }

        Assert.Equal(0, activations);
        // 不正要求のあとも待受は生きている(1 件で死ぬ変異を殺す)。
        Assert.Equal(HandoffResult.Success, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void Server_RejectsOversizedRequest_WithoutInvokingActivation()
    {
        // 改行を送らずに延々と書き続ける相手でメモリを食わせない。
        string pipe = UniqueName();
        int activations = 0;
        using var server = new SingleInstanceServer(
            pipe,
            () =>
            {
                activations++;
                return true;
            }
        );
        Assert.True(server.Start());

        using (
            var client = new NamedPipeClientStream(
                ".",
                pipe,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            )
        )
        {
            client.Connect((int)ShortTimeout.TotalMilliseconds);
            byte[] flood = Encoding.UTF8.GetBytes(new string('x', 64 * 1024)); // 上限の 16 倍
            try
            {
                client.Write(flood, 0, flood.Length);
                client.Flush();
            }
            catch (IOException)
            { /* 上限到達でサーバが切る。切られること自体が期待動作 */
            }
        }

        Assert.Equal(0, activations);
    }

    [Fact]
    public void HandOff_WhenPeerAcceptsButNeverAnswers_ReportsNoResponse_WithinTimeout()
    {
        // ★重要: 接続だけ受けて黙り込む相手(ハングした kxEdit・悪意ある squatter)に対し、
        // クライアントが永久にブロックしないこと。同一プロセス内の生パイプなので
        // ★3 の実行ファイルパス検証は通過する = 検証の先にあるブロックを狙い撃ちできる。
        string pipe = UniqueName();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                sid,
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow
            )
        );
        using var mute = NamedPipeServerStreamAcl.Create(
            pipe,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security
        );
        // 接続だけは受け付ける(黙り込む相手の再現)。完了は待たないので破棄する。
        _ = mute.WaitForConnectionAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = SingleInstanceClient.TryHandOff(pipe, ShortTimeout);
        sw.Stop();

        Assert.Equal(HandoffResult.NoResponse, result);
        // 期限の 4 倍まで見る(CI の揺れを許容しつつ「無制限ブロック」は確実に落とす)。
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(ShortTimeout.TotalMilliseconds * 4),
            $"ACK 待ちが期限内に返らなかった: {sw.Elapsed}"
        );
    }

    [Fact]
    public void Start_ReturnsFalse_WhenPipeNameIsAlreadyTaken()
    {
        // 悪意あるプロセスが先回りして同名パイプを作った場合(設計 §4 ★3 の逆向き)。
        // A は待受なしで通常起動を続ける = 起動を止めない。
        string pipe = UniqueName();
        using var squatter = new SingleInstanceServer(pipe, () => true);
        Assert.True(squatter.Start());

        using var second = new SingleInstanceServer(pipe, () => true);
        Assert.False(second.Start());
    }

    [Fact]
    public void Start_ReturnsFalse_WhenSquatterAllowsUnlimitedInstances()
    {
        // 上のテストの squatter は maxNumberOfServerInstances: 1 なので
        // 2 本目は ERROR_PIPE_BUSY で弾かれる。本当に危ないのは、攻撃者が
        // 「無制限インスタンス」で先に名前を取った場合 —— ここで相乗りできてしまうと、
        // 接続が攻撃者と我々にランダムに振り分けられ、将来ファイルパスを載せた時点で
        // 情報漏洩になる(設計 §4 ★3)。
        // Win32 は全インスタンスで nMaxInstances の一致を要求するため、
        // maxNumberOfServerInstances: 1 のままで弾ける(FILE_FLAG_FIRST_PIPE_INSTANCE の
        // 明示は不要)。その事実をここで固定する。
        string pipe = UniqueName();
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow
            )
        );
        using var squatter = NamedPipeServerStreamAcl.Create(
            pipe,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security
        );

        using var ours = new SingleInstanceServer(pipe, () => true);
        Assert.False(ours.Start());
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        string pipe = UniqueName();
        var server = new SingleInstanceServer(pipe, () => true);
        Assert.True(server.Start());
        server.Dispose();

        Assert.Equal(HandoffResult.NoResponse, SingleInstanceClient.TryHandOff(pipe, ShortTimeout));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // IDisposable の契約。ガードが無いと 2 回目の Cancel() が
        // ObjectDisposedException を投げ、終了処理の後続が飛ぶ。
        // Start 済み / 未 Start の両方を見る(未 Start は _pipe / _loop が null の経路)。
        var started = new SingleInstanceServer(UniqueName(), () => true);
        Assert.True(started.Start());
        started.Dispose();
        started.Dispose();

        var neverStarted = new SingleInstanceServer(UniqueName(), () => true);
        neverStarted.Dispose();
        neverStarted.Dispose();
    }
}
