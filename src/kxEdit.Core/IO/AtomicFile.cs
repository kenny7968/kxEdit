namespace kxEdit.Core.IO;

/// <summary>
/// 原子的ファイル書き込みの共通実装（TextFileService の保存と BackupStore の退避で共用）。
/// 同ディレクトリの temp（"ファイル名.乱数.tmp"）へステージングしてから File.Replace
/// （新規は File.Move）で差し替える。どの段階で失敗しても原本には一切触れず、tmp の掃除だけ
/// 試みて例外を伝播する（= 原本喪失の回避が目的）。フォールバック（共有違反時の in-place
/// 上書き等）を行うかは呼び出し側の責務で、IsShareOrLockViolation で判定できる。
/// </summary>
public static class AtomicFile
{
    // Win32 共有/ロック違反（AV・同期ソフト等が一時的に掴んでいる）。
    private const int HResultSharingViolation = unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
    private const int HResultLockViolation = unchecked((int)0x80070021); // ERROR_LOCK_VIOLATION

    /// <summary>payload を path へ原子的に書き込む。失敗時は tmp を掃除して例外を伝播する。</summary>
    public static void Write(string path, byte[] payload)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string tmp = Path.Combine(
            dir,
            Path.GetFileName(path) + "." + Path.GetRandomFileName() + ".tmp"
        );

        // ① tmp へステージング書き込み。ここで失敗（ディスクフル・権限・パス長等）したら
        //    原本に一切触れず、tmp 残骸の掃除だけ試みて例外を伝播する。
        try
        {
            File.WriteAllBytes(tmp, payload);
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
    /// P7 I-3: 大容量本文向けの Stream ベース原子書込。writer に tmp ファイルの
    /// FileStream を渡し、書き終えた後に <see cref="Write(string, byte[])"/> と同じ
    /// File.Replace / File.Move で差し替える。writer が例外を投げた場合は tmp を
    /// 掃除して例外を伝播する(原本に一切触れない=byte[] 版と同一契約)。
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
                writer(fs);
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
    /// ステージング済み tmp を path へ差し替える。<b>差替段全体(= 失敗時ポリシーを含む)</b>で
    /// あり、現状の挙動は「失敗したら tmp を掃除して伝播する」= 集約前と同一。
    /// M-12 の復旧(原本が消えていたら復旧を試み、駄目なら tmp を残す)はここへ入る
    /// ——設計 2026-09-02 §3。
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
        catch
        {
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
