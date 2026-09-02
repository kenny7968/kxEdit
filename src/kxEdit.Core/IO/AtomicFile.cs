namespace kxEdit.Core.IO;

/// <summary>
/// 原子的ファイル書き込みの共通実装（TextFileService の保存と BackupStore の退避で共用）。
/// 同ディレクトリの temp（"ファイル名.乱数.tmp"）へステージングしてから File.Replace
/// （新規は File.Move）で差し替える。目的は<b>原本喪失の回避</b>。
/// <para>
/// 失敗時のポリシーは段階で異なる:
/// <list type="bullet">
/// <item>①（ステージング）の失敗 —— 原本に一切触れず、tmp を掃除して例外を伝播する。</item>
/// <item>②（差替）の失敗で<b>原本が残っている</b>とき —— 同様に tmp を掃除して伝播する。</item>
/// <item>②の失敗で<b>原本が失われていた</b>とき —— tmp は<b>掃除しない</b>。
/// リネームによる復旧を試み、それも失敗したときだけ
/// <see cref="AtomicReplaceFailedException"/> で残した tmp のパスを伝える。
/// この場合 tmp がディスク上の唯一のコピーであり、消すと内容が完全に失われる
/// （M-12・設計 2026-09-02 §3）。</item>
/// </list>
/// </para>
/// <para>
/// <b>電源断に対して保証すること／しないこと（M-13・設計 2026-09-02 §4.3）。</b>
/// ①のステージングは差し替える前に <c>Flush(flushToDisk: true)</c>（= Win32
/// <c>FlushFileBuffers</c>）を掛ける。これが保証するのは<b>そのファイルの中身がディスクに
/// 届いたこと</b>だけであり、<b>その後の rename が届いたことではない</b>
/// —— Windows にはディレクトリのメタデータを明示的に flush する API が .NET から無い。
/// <list type="bullet">
/// <item>消える失敗: 「差し替わったファイルの中身が不完全」（= rename は届いたのに中身が
/// 届いていない状態）。</item>
/// <item><b>残る失敗</b>: 「rename 自体が失われる」。この場合<b>原本は無傷のまま残る</b>
/// （= データ喪失ではなく、保存の取りこぼし）。</item>
/// </list>
/// したがって<b>「原子書込＋fsync だから電源断に強い」とは言えない</b>。言えるのは
/// 「差し替わったファイルの中身が不完全になることはない」までで、<b>保存そのものが
/// 無かったことになる可能性は残る</b>。
/// </para>
/// フォールバック（共有違反時の in-place 上書き等）を行うかは呼び出し側の責務で、
/// IsShareOrLockViolation で判定できる。
/// </summary>
public static class AtomicFile
{
    // Win32 共有/ロック違反（AV・同期ソフト等が一時的に掴んでいる）。
    private const int HResultSharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
    private const int HResultLockViolation = unchecked((int)0x80070021); // ERROR_LOCK_VIOLATION

    /// <summary>
    /// payload を path へ原子的に書き込む。失敗時は tmp を掃除して例外を伝播する
    /// (この規則が当てはまらない唯一の場合 = 差替で原本が失われたときは、tmp を掃除せず残す
    /// —— <see cref="CommitStaged"/> を参照)。
    /// <para>
    /// <b>実体は <see cref="Write(string, Action{Stream})"/> の薄いラッパである</b>
    /// (payload を 1 回書くだけの writer を渡している)。ステージングの実装を 1 つに保つのが目的で、
    /// <b>2 つあると片方だけが <c>Flush(flushToDisk: true)</c> を失っても気付けない</b>
    /// —— byte[] 版から flush 行を落とす変異が全テスト緑のまま生存していた
    /// (設計 2026-09-02 §10.8 の M-C / §10.9)。CreateNew・FileShare.None・tmp の命名・
    /// 失敗時ポリシーはすべて委譲先の 1 か所で決まる。
    /// </para>
    /// </summary>
    public static void Write(string path, byte[] payload)
    {
        // 旧実装の File.WriteAllBytes は null で ArgumentNullException を投げていた。
        // 委譲先の Stream 版は writer の中で payload.Length に触れるため、ここで止めないと
        // NullReferenceException になるうえ、空の tmp を作ってから消すことになる。
        //
        // 保たれるのは<例外の型>と<tmp を作らないこと>で、paramName は M-13 で変わっている:
        // File.WriteAllBytes は "bytes"、ここは "payload"(いずれも実測)。依存コードは無く、
        // 本メソッドの公開引数名と一致する方へ寄っている。ここで投げる以上、委譲先の
        // "writer" には化けない —— この 3 点は AtomicFileTests が網で固定している。
        ArgumentNullException.ThrowIfNull(payload);
        Write(path, stream => stream.Write(payload, 0, payload.Length));
    }

    /// <summary>
    /// P7 I-3: 大容量本文向けの Stream ベース原子書込。writer に tmp ファイルの
    /// FileStream を渡し、書き終えた後に <see cref="Write(string, byte[])"/> と同じ
    /// File.Replace / File.Move で差し替える。writer が例外を投げた場合は tmp を
    /// 掃除して例外を伝播する(原本に一切触れない=byte[] 版と同一契約)。
    /// <para>
    /// <b>writer は渡された Stream を閉じてはならない</b>(M-13 以降の契約)。戻ってきた後に
    /// <c>Flush(flushToDisk: true)</c> を掛けるため、閉じられていると ObjectDisposedException に
    /// なる。具体的には <c>using var sw = new StreamWriter(stream)</c> のように<b>下位ストリームごと
    /// Dispose するラッパ</b>を writer 内で使わないこと(必要なら
    /// <c>leaveOpen: true</c> を渡す)。閉じられた場合は例外が伝播して tmp が掃除され、
    /// 原本は無傷のまま残る —— <b>黙って fsync を飛ばす方には倒さない</b>
    /// (飛ばすと M-13 の保証が静かに消える)。
    /// </para>
    /// </summary>
    public static void Write(string path, Action<Stream> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(
            dir,
            Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp"
        );

        try
        {
            using (
                var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            )
            {
                writer(fs);
                // M-13: 差し替える前にディスクへ届かせる。using を抜ける前でなければならない
                // （Dispose 後では fs へ触れないうえ、Dispose の flush は OS キャッシュまで）。
                // 保証の範囲はクラス xmldoc を参照。
                fs.Flush(flushToDisk: true);
            }
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        // ② tmp は完全に書けている。原子的に差し替える。
        CommitStaged(tmp, path);
    }

    /// <summary>
    /// ex が Win32 の共有違反/ロック競合か。in-place フォールバックを許してよい唯一の条件
    /// （これ以外＝ディスクフル等でフォールバックすると原本を破壊し得る）の判定に使う。
    /// </summary>
    public static bool IsShareOrLockViolation(IOException ex) =>
        ex.HResult is HResultSharingViolation or HResultLockViolation;

    /// <summary>
    /// ステージング済み tmp を path へ差し替える。<b>差替段全体(= 失敗時ポリシーを含む)</b>。
    /// 差替が失敗したとき、原本が失われていれば復旧を試み、それも駄目なら tmp を残す
    /// (M-12・設計 2026-09-02 §3)。
    /// <para>
    /// <b>判定は事後条件で行う(エラーコードで分岐しない)。</b> Win32 の
    /// ERROR_UNABLE_TO_MOVE_REPLACEMENT 等を列挙して前置ガードにすると、列挙から漏れたエラーで
    /// 同じ状態(原本が消え tmp だけが残る)になったときに素通しする —— 前置の列挙は原理的に
    /// 漏れる(監査 §9 V-7 / 設計 2026-09-02 §3.1・§8)。「どのエラーで失敗したか」ではなく
    /// 「失敗後にディスクがどうなっているか」を見るので、未知のエラーでも効く。
    /// </para>
    /// <para>
    /// 事後条件だけでは足りず、差替<b>前</b>に採った destExists と組で判定する。新規作成の失敗でも
    /// <c>!File.Exists(path)</c> は真になるが、そこには失われた原本が無い。片方を落とすと
    /// 「残骸を残すだけの誤検出」か「唯一のコピーの削除」のどちらかに倒れる(設計 §3.1 の判定表)。
    /// </para>
    /// <para>
    /// <b>受容するトレードオフ —— 復旧は ACL / 属性 / 作成日時を引き継がない。</b>
    /// <c>File.Replace</c> は差替先のそれらを引き継ぐが、復旧に使う <c>File.Move</c> は引き継がず、
    /// 復旧後のファイルは置かれたディレクトリの継承 ACL を持つ。元ファイルに個別の(より厳しい)
    /// ACL があった場合、復旧は<b>権限を広げる方向へ倒す</b>。
    /// </para>
    /// <para>
    /// <b>受容の中心根拠は「新しい ACL 状態を作り出していない」ことである</b>(脆弱性レビュー実測・
    /// 設計 §3.3 / §10.5)。原本が消えた後にユーザーが自分で保存し直すと、その保存は
    /// <c>destExists == false</c> を通る = <c>File.Move</c> なので、<b>本修正の前でも継承 ACL に
    /// なる</b>。つまり復旧は<b>ユーザーの再試行を代行しているだけ</b>で、変更前が到達できなかった
    /// 権限状態を新しく生んではいない。「権限が広がったファイルが残る」対「ファイルが消える」の
    /// 比較衡量も成り立つが、決定的なのはこちら。
    /// </para>
    /// <para>
    /// <b>頻度を過小評価しないこと。</b> ACL が実際に置き換わるのは復旧が<b>成功</b>したときであり、
    /// これは稀ではない —— 差替の直前に別プロセス(AV の隔離・同期クライアント・ユーザー自身の
    /// 削除)が宛先を消せば、<b>単一の平凡な失敗</b>で復旧枝に入り <c>File.Move</c> はほぼ確実に
    /// 成功する(「二重障害が要る」のは tmp が<b>残る</b>ケースの方であって、ここではない)。
    /// 併せて<b>挙動が変わる</b>点も記録しておく: 別プロセスが消したファイルを kxEdit が黙って
    /// 復活させることになる(変更前は保存失敗ダイアログだった)。起点はユーザー自身の保存操作なので
    /// 攻撃者が駆動できるものではない。復旧成功時に無言で return するのも同様に受容している
    /// (保存は実際に成立しているので「保存しました」は虚偽ではない)。
    /// </para>
    /// <para>
    /// <b>保証が及ぶ範囲</b>(設計 §10.3 / §10.5): ここは <c>Write</c> の 4 呼出者すべての
    /// 通り道だが、「tmp を残して例外で伝える」が実際にユーザーへ届くのは<b>文書保存経路
    /// (<c>TextFileService.Save</c>)だけ</b>である。<c>BackupStore.Write</c> /
    /// <c>SessionLayoutStore.Save</c> は <c>SerialBackupWriter</c> のワーカーが catch で
    /// 握り潰すため、例外はユーザーへ届かない。
    /// </para>
    /// <para>
    /// 残した tmp の行方は、この 2 経路で<b>異なる</b>。
    /// <list type="bullet">
    /// <item><b>バックアップ</b>(<c>%APPDATA%\kxEdit\backups</c> 配下)—— 起動時に
    /// <c>BackupCoordinator</c> が自セッション dir と base dir へ
    /// <c>BackupStore.SweepTempFiles</c>(<c>*.tmp</c> を無差別削除)を掛けるので回収される
    /// = 静かに消える。</item>
    /// <item><b>セッションレイアウト</b>(<c>%APPDATA%\kxEdit\session-state.json</c>)—— その
    /// tmp は <c>%APPDATA%\kxEdit\</c> 直下に落ちる。<c>*.tmp</c> を消すコードは
    /// <c>BackupStore</c> にしか無く、<b>どれも <c>backups</c> 配下しか見ていない</b>ため
    /// <b>恒久残留する</b>。本文を含まない数 KB で、かつ差替失敗と復旧失敗の二重障害が要るので
    /// 実害は小さいが、「静かに消える」ではない。<c>%APPDATA%\kxEdit\</c> 直下へ書く経路を
    /// 増やすときは同じことが起きる。</item>
    /// </list>
    /// 握り潰しの解消は別ブランチ(B5 / M-20)の担当で、本修正の射程外。
    /// </para>
    /// </summary>
    private static void CommitStaged(string tmp, string path)
    {
        // destExists は差替の分岐条件そのもの。M-12 の「原本が消えたか」の判定
        // (設計 2026-09-02 §3.1)でもこの同じ値を使う
        // (別途 File.Exists を採り直すと TOCTOU 窓が広がる)。
        bool destExists = File.Exists(path);
        try
        {
            RunReplaceStep(tmp, path, destExists);
        }
        catch (Exception replaceError)
        {
            // 原本があったのに消えている = tmp が唯一のコピー。新規作成(destExists=false)の
            // 失敗はここに入らない(失われた原本が無いので、残骸を残すだけになる)。
            if (destExists && !File.Exists(path))
            {
                try
                {
                    File.Move(tmp, path);
                    return; // 復旧成功 = 保存は成立している
                }
                catch (Exception recoveryError)
                {
                    // tmp は掃除しない。ここで消すと内容が完全に失われる。
                    throw new AtomicReplaceFailedException(path, tmp, replaceError, recoveryError);
                }
            }
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// 差替の<b>1 手</b>(File.Replace / File.Move)だけを行う。失敗時ポリシーは呼出元
    /// <see cref="CommitStaged"/> 側にあるので、<b>ここを差し替えても Write の成否を
    /// 支配できるわけではない</b>(M-12 導入後は、フックが投げても外側の復旧が走って
    /// Write が成功 return し得る——設計 2026-09-02 §3.2)。
    /// </summary>
    private static void RunReplaceStep(string tmp, string path, bool destExists)
    {
        var scope = t_replaceStepOverride;
        if (scope is not null)
        {
            // 投げるフックも「発火した」と数えるため、呼び出しの前に記録する。
            scope.RecordInvocation();
            scope.Hook(tmp, path, destExists);
            return;
        }
        if (destExists)
            File.Replace(tmp, path, destinationBackupFileName: null); // ACL/属性を保持・バックアップ無し
        else
            File.Move(tmp, path);
    }

    // ===== テスト専用 seam =====
    // File.Replace の部分失敗(差替先が消える)は実環境で決定的に起こせないため、差替の 1 手だけを
    // 差し替えられるようにする。production コードは OverrideReplaceStepForTest を呼んでいないため
    // 実際に走るのは既定実装だけだが、これは<現在の観測>であって強制ではない
    // (kxEdit.Core.csproj が kxEdit.Editor / kxEdit.Core.Bench へ internal を可視化しているため、
    //  それらの production アセンブリからは呼べてしまう)。
    //
    // [ThreadStatic] であることが必須: kxEdit.Core.Tests はテストクラスを並列実行する
    // (直列化しているのは kxEdit.App.Tests / kxEdit.Editor.Tests だけ)。素の static にすると、
    // フックを張っている間に別スレッドで走る SettingsStoreTests / BackupStore 系 /
    // TextFileService 系の書込まで差し替わり、無関係なテストが壊れる。
    //
    // その裏返しの事故として「張ったスレッドと Write が走るスレッドがずれると、黙って既定実装が
    // 走る」がある(例: BackupStore / SessionLayoutStore は SerialBackupWriter の専用ワーカー
    // スレッドで書く)。事後状態だけを見るテストはこの不発に気付けない——既定実装が成功すると
    // 同じ事後状態になるため。そこで発火回数を ReplaceStepOverrideScope.Invocations で観測できる
    // ようにしてある。<b>フックを張るテストは必ず Invocations を assert すること。</b>
    [ThreadStatic]
    private static ReplaceStepOverrideScope? t_replaceStepOverride;

    /// <summary>
    /// 差替の 1 手だけをテスト用に差し替える(<b>呼んだスレッドにのみ効く</b>)。
    /// 戻り値を Dispose するまで有効で、<see cref="ReplaceStepOverrideScope.Invocations"/> で
    /// フックが実際に発火したかを確かめられる。
    /// </summary>
    internal static ReplaceStepOverrideScope OverrideReplaceStepForTest(
        Action<string, string, bool> hook
    )
    {
        ArgumentNullException.ThrowIfNull(hook);
        var scope = new ReplaceStepOverrideScope(hook, t_replaceStepOverride);
        SetReplaceStepOverride(scope);
        return scope;
    }

    /// <summary>
    /// t_replaceStepOverride への<b>唯一の書込口</b>(張る側=OverrideReplaceStepForTest と
    /// 戻す側=ReplaceStepOverrideScope.Dispose の両方がここを通る)。static メソッドに
    /// してあるのは、インスタンスメソッドから static フィールドを書き換えると S2696 になるため。
    /// </summary>
    private static void SetReplaceStepOverride(ReplaceStepOverrideScope? scope) =>
        t_replaceStepOverride = scope;

    /// <summary>
    /// 差替フックの有効範囲。入れ子にでき、Dispose で 1 つ外側へ戻る(LIFO)。
    /// </summary>
    internal sealed class ReplaceStepOverrideScope : IDisposable
    {
        private readonly ReplaceStepOverrideScope? _previous;
        private int _invocations;

        internal ReplaceStepOverrideScope(
            Action<string, string, bool> hook,
            ReplaceStepOverrideScope? previous
        )
        {
            Hook = hook;
            _previous = previous;
        }

        internal Action<string, string, bool> Hook { get; }

        /// <summary>
        /// このスコープのフックが実際に呼ばれた回数。<b>0 のままなら「張ったのに不発」</b>で、
        /// 既定実装が走っている(スレッドがずれた等)。事後状態だけでは区別できない。
        /// </summary>
        internal int Invocations => Volatile.Read(ref _invocations);

        internal void RecordInvocation() => Interlocked.Increment(ref _invocations);

        public void Dispose() => SetReplaceStepOverride(_previous);
    }

    private static void TryDelete(string p)
    {
        try
        {
            if (File.Exists(p))
                File.Delete(p);
        }
        catch
        { /* 残骸は実害小 */
        }
    }
}
