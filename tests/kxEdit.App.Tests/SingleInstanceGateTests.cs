using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace kxEdit.App.Tests;

/// <summary>
/// 起動時の判定(設計 2026-09-14 §3「ゲートの 3 状態」/ §4「2 つのレース」/ §7「受容するリスク」)。
/// <para>
/// 本テストは<b>実 Mutex と実パイプを使う</b>。Mutex 名 / パイプ名は毎回一意にする ——
/// 実運用の kxEdit が同じ PC で動いていても干渉させないため。
/// </para>
/// <para>
/// 引き渡し(<c>handOff</c>)は<b>注入できる</b>ので、4 状態の写像・終了中レースは
/// タイミングに頼らず決定論的に固定する。逆に、注入で置き換えると
/// 「既定の束縛が期限を正しく渡しているか」が検証できなくなるため、
/// <c>UsesConnectTimeout</c> / <c>UsesAckTimeout</c> の 2 本だけは既定の束縛を通す。
/// </para>
/// </summary>
public class SingleInstanceGateTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(800);

    private readonly string _mutexName = $@"Local\kxEditTest.{Guid.NewGuid():N}";
    private readonly string _pipeName = $"kxEditTest.{Environment.ProcessId}.{Guid.NewGuid():N}";

    /// <summary>
    /// <see cref="SingleInstanceGate.Acquire"/> の薄いラッパ。
    /// 既定は「通常起動・短い期限・引き渡しは既定の束縛(実パイプ)」。
    /// </summary>
    private (SingleInstanceOutcome Outcome, SingleInstanceGate? Gate) Acquire(
        bool newInstance = false,
        Func<CancellationToken, bool>? onActivate = null,
        TimeSpan? connectTimeout = null,
        TimeSpan? ackTimeout = null,
        Func<string, HandoffResult>? handOff = null
    ) =>
        SingleInstanceGate.Acquire(
            new CommandLineOptions(newInstance),
            _mutexName,
            _pipeName,
            onActivate ?? (_ => true),
            connectTimeout ?? ShortTimeout,
            ackTimeout ?? ShortTimeout,
            handOff
        );

    /// <summary>呼ばれてはならない前面化ハンドラ。</summary>
    private static bool MustNotActivate(CancellationToken ct)
    {
        Assert.Fail("2 つ目のインスタンスは待受を始めてはならない");
        return false;
    }

    [Fact]
    public void Acquire_FirstCall_BecomesFirstInstance()
    {
        var (outcome, gate) = Acquire();
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
            Assert.NotNull(gate);
        }
    }

    [Fact]
    public void Acquire_SecondCall_HandsOffToFirst()
    {
        // 実 Mutex + 実パイプの往復。1 つ目が待受を始めていること・2 つ目が
        // 既定の束縛で引き渡せること・前面化が 1 回だけ起きることを同時に固定する。
        int activations = 0;
        var (first, gate) = Acquire(onActivate: _ =>
        {
            Interlocked.Increment(ref activations);
            return true;
        });
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, first);

            var (second, secondGate) = Acquire(onActivate: MustNotActivate);

            Assert.Equal(SingleInstanceOutcome.HandedOff, second);
            Assert.Null(secondGate);
            Assert.Equal(1, Volatile.Read(ref activations));
        }
    }

    [Fact]
    public void Acquire_WithNewInstanceSwitch_SkipsExclusionEntirely()
    {
        // 抜け道(設計 D3)。2 回続けて FirstInstance になり、引き渡しも待受も起きない。
        // 【待受を始めない点が本質】始めてしまうと、本来の 1 つ目が居ないときに
        // --new-instance で起動したプロセスが次の起動を引き受けてしまう。
        var (first, a) = Acquire(
            newInstance: true,
            onActivate: MustNotActivate,
            handOff: _ =>
            {
                Assert.Fail("--new-instance では引き渡しを試みてはならない");
                return HandoffResult.NoResponse;
            }
        );
        using (a)
        {
            var (second, b) = Acquire(
                newInstance: true,
                onActivate: MustNotActivate,
                handOff: _ =>
                {
                    Assert.Fail("--new-instance では引き渡しを試みてはならない");
                    return HandoffResult.NoResponse;
                }
            );
            using (b)
            {
                Assert.Equal(SingleInstanceOutcome.FirstInstance, first);
                Assert.Equal(SingleInstanceOutcome.FirstInstance, second);
                // 待受が無い = Mutex だけ取っていない。パイプ名は空いたままのはず。
                using var listener = new SingleInstanceServer(_pipeName, _ => true);
                Assert.True(listener.Start());
            }
        }
    }

    [Fact]
    public void Acquire_WhenMutexHeldButNobodyListens_ReportsNoResponse()
    {
        // 既存インスタンスがハングしている(設計 D4)。フォールバック起動はしない。
        // 期限は既定の束縛を通す —— connect 側に ackTimeout を渡す変異が生きていると
        // ここが 3 秒コースになるので、所要時間でも殺す。
        using var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);

        var sw = Stopwatch.StartNew();
        var (outcome, gate) = Acquire(
            connectTimeout: TimeSpan.FromMilliseconds(300),
            ackTimeout: TimeSpan.FromSeconds(3)
        );
        sw.Stop();

        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(1200),
            $"接続待ちに connectTimeout が使われていない: {sw.Elapsed}"
        );
    }

    [Fact]
    public void Acquire_WhenPeerAcceptsButNeverAnswers_ReportsNoResponse_WithinAckTimeout()
    {
        // 接続だけ受けて黙り込む相手。ACK 待ちに connectTimeout を渡す変異を殺す
        // (渡されると 3 秒コースになる)。
        using var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);

        using var mute = NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            CurrentUserOnlyAcl()
        );
        _ = mute.WaitForConnectionAsync(); // 接続だけ受け付ける。完了は待たない。

        var sw = Stopwatch.StartNew();
        var (outcome, gate) = Acquire(
            connectTimeout: TimeSpan.FromSeconds(3),
            ackTimeout: TimeSpan.FromMilliseconds(400)
        );
        sw.Stop();

        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(1600),
            $"ACK 待ちに ackTimeout が使われていない: {sw.Elapsed}"
        );
    }

    /// <summary>現在のユーザーだけを許可する ACL(サーバ実装と同じ形)。</summary>
    private static PipeSecurity CurrentUserOnlyAcl()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow
            )
        );
        return security;
    }

    [Fact]
    public void Acquire_WhenHandoffReportsForeignPeer_ReportsForeignPeer()
    {
        // 設計 §4「失敗理由を 2 つに分ける」。NoResponse へ潰す変異は、
        // 別フォルダの別ビルドが起動しているときに誤診(「応答しません」)を出す。
        // ここはゲートの<b>写像</b>を見るテストで、TryVerifyPeer の検証ロジック自体を
        // 見る SingleInstanceChannelTests とは別物(両方要る)。
        using var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);

        int calls = 0;
        var (outcome, gate) = Acquire(handOff: name =>
        {
            calls++;
            Assert.Equal(_pipeName, name);
            return HandoffResult.ForeignPeer;
        });

        Assert.Equal(SingleInstanceOutcome.ForeignPeer, outcome);
        Assert.Null(gate);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Acquire_WhenHandoffReportsNoResponse_AndMutexStaysHeld_ReportsNoResponse()
    {
        // 引き渡し失敗の再試行は <b>Mutex 取得だけ</b>を 1 回。
        // 引き渡し自体を繰り返す変異(ループ化)を殺す —— 繰り返すと、
        // ハングした既存インスタンスに対して起動が期限の倍数だけ待たされる。
        using var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);

        int calls = 0;
        var (outcome, gate) = Acquire(handOff: _ =>
        {
            calls++;
            return HandoffResult.NoResponse;
        });

        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Acquire_RetriesMutexOnce_WhenPeerExitedDuringHandoff()
    {
        // 終了中レース(設計 §4): A が終了処理中で Mutex はまだ保持・パイプは閉じた。
        // 引き渡し失敗のあと Mutex を取り直せたら、自分が 1 つ目になる。
        // これが無いと、kxEdit を閉じた直後の再起動が「応答しません」エラーになる。
        // 注入で「引き渡しが失敗した瞬間に A が Mutex を手放した」を決定論的に作る。
        var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);

        var (outcome, gate) = Acquire(handOff: _ =>
        {
            held.ReleaseMutex();
            held.Dispose();
            return HandoffResult.NoResponse;
        });
        using (gate)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
            Assert.NotNull(gate);
        }
    }

    [Fact]
    public void Acquire_DoesNotRetryMutex_WhenHandoffSucceeded()
    {
        // 引き渡しが成立したら、たとえ直後に Mutex が空いても 1 つ目にはならない。
        // 成功を無条件に再試行へ流す変異(HandedOff の early return 削除)を殺す ——
        // 流れると、既存ウィンドウを前面化したうえで 2 つ目も起動する。
        var held = new Mutex(initiallyOwned: true, _mutexName, out bool createdNew);
        Assert.True(createdNew);
        try
        {
            var (outcome, gate) = Acquire(
                onActivate: MustNotActivate,
                handOff: _ =>
                {
                    // ハンドルごと閉じて名前を空ける。ReleaseMutex だけでは
                    // 名前付きオブジェクトが残り、取り直しは元々失敗するので
                    // 「再試行しない」ことを確かめられない。
                    held.ReleaseMutex();
                    held.Dispose();
                    return HandoffResult.Success;
                }
            );
            Assert.Equal(SingleInstanceOutcome.HandedOff, outcome);
            Assert.Null(gate);
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public void Acquire_WhenMutexAccessIsDenied_ReportsNoResponse_WithoutThrowing()
    {
        // 昇格の順序(設計 §7 受容リスク)の代理再現。実際は高 IL プロセスが作った
        // Mutex を中 IL から開けない形だが、IL を跨がなくても「自分に権利を与えない
        // DACL の Mutex」で同じ UnauthorizedAccessException を起こせる。
        // ここで例外が漏れると、ゲートが D4 のエラーを出す前に起動時クラッシュし、
        // 「必ずエラーを出して終了する」という不変条件をゲート自身が破る。
        var deny = new MutexSecurity();
        deny.AddAccessRule(
            new MutexAccessRule(
                WindowsIdentity.GetCurrent().User!,
                MutexRights.FullControl,
                AccessControlType.Deny
            )
        );
        using var blocker = MutexAcl.Create(
            initiallyOwned: false,
            _mutexName,
            out _,
            mutexSecurity: deny
        );

        var (outcome, gate) = Acquire(handOff: _ => HandoffResult.NoResponse);

        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
    }

    [Fact]
    public void Acquire_WhenNameIsTakenByAnotherObjectType_ReportsNoResponse_WithoutThrowing()
    {
        // 同一セッションのコードが Mutex 名を「別の種類のカーネルオブジェクト」で
        // 先取りした場合(設計 §7 の受容リスク)。CreateMutex は ERROR_INVALID_HANDLE を
        // 返し、.NET は WaitHandleCannotBeOpenedException にする ——
        // UnauthorizedAccessException だけを捕まえる形だと、ここで起動時クラッシュする。
        using var squatter = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            _mutexName,
            out bool createdNew
        );
        Assert.True(createdNew);

        var (outcome, gate) = Acquire(handOff: _ => HandoffResult.NoResponse);

        Assert.Equal(SingleInstanceOutcome.NoResponse, outcome);
        Assert.Null(gate);
    }

    [Fact]
    public void Dispose_ReleasesMutex_SoNextLaunchBecomesFirstInstance()
    {
        var (_, gate) = Acquire();
        gate!.Dispose();

        var (outcome, second) = Acquire();
        using (second)
        {
            Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
            Assert.NotNull(second);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // 終了処理の途中で例外を出すと、後続の後始末が丸ごと飛ぶ(IDisposable の契約)。
        var (outcome, gate) = Acquire();
        Assert.Equal(SingleInstanceOutcome.FirstInstance, outcome);
        gate!.Dispose();
        gate.Dispose();
    }
}
