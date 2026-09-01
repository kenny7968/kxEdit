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
    /// ステージング済み tmp を path へ差し替える。<b>この時点の挙動は Task 2 では不変</b>
    /// (失敗したら tmp を掃除して伝播する)。Task 3 で事後条件による復旧を足す。
    /// </summary>
    private static void CommitStaged(string tmp, string path)
    {
        // destExists は差替の分岐条件そのもの。Task 3 の「原本が消えたか」の判定でも
        // この同じ値を使う(別途 File.Exists を採り直すと TOCTOU 窓が広がる)。
        bool destExists = File.Exists(path);
        try
        {
            Commit(tmp, path, destExists);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>差替の実処理。テストからのみ <see cref="OverrideCommitForTest"/> で置換できる。</summary>
    private static void Commit(string tmp, string path, bool destExists)
    {
        var hook = t_commitOverride;
        if (hook is not null)
        {
            hook(tmp, path, destExists);
            return;
        }
        if (destExists)
            File.Replace(tmp, path, destinationBackupFileName: null); // ACL/属性を保持・バックアップ無し
        else
            File.Move(tmp, path);
    }

    // ===== テスト専用 seam =====
    // File.Replace の部分失敗(差替先が消える)は実環境で決定的に起こせないため、差替段だけを
    // 差し替えられるようにする。production では常に null=既定実装しか走らない。
    //
    // [ThreadStatic] であることが必須: kxEdit.Core.Tests はテストクラスを並列実行する
    // (直列化しているのは kxEdit.App.Tests / kxEdit.Editor.Tests だけ)。素の static にすると、
    // フックを張っている間に別スレッドで走る SettingsStoreTests / BackupStore 系 /
    // TextFileService 系の書込まで差し替わり、無関係なテストが壊れる。
    [ThreadStatic]
    private static Action<string, string, bool>? t_commitOverride;

    /// <summary>差替段をテスト用に差し替える(<b>呼んだスレッドにのみ効く</b>)。
    /// 戻り値を Dispose するまで有効。</summary>
    internal static IDisposable OverrideCommitForTest(Action<string, string, bool> hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        var previous = t_commitOverride;
        SetCommitOverride(hook);
        return new CommitOverrideScope(previous);
    }

    /// <summary>
    /// t_commitOverride への<b>唯一の書込口</b>(張る側=OverrideCommitForTest と戻す側=
    /// CommitOverrideScope.Dispose の両方がここを通る)。static メソッドにしてあるのは、
    /// インスタンスメソッドから static フィールドを書き換えると S2696 になるため。
    /// </summary>
    private static void SetCommitOverride(Action<string, string, bool>? hook) =>
        t_commitOverride = hook;

    private sealed class CommitOverrideScope : IDisposable
    {
        private readonly Action<string, string, bool>? _previous;

        internal CommitOverrideScope(Action<string, string, bool>? previous) =>
            _previous = previous;

        public void Dispose() => SetCommitOverride(_previous);
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
