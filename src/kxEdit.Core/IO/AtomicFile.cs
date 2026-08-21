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

        // ② tmp は完全に書けている。原子的に差し替える（ACL/属性を保持・バックアップ無し）。
        //    失敗しても原本は不変のまま tmp を消して伝播する。
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
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

        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, destinationBackupFileName: null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// ex が Win32 の共有違反/ロック競合か。in-place フォールバックを許してよい唯一の条件
    /// （これ以外＝ディスクフル等でフォールバックすると原本を破壊し得る）の判定に使う。
    /// </summary>
    public static bool IsShareOrLockViolation(IOException ex) =>
        ex.HResult is HResultSharingViolation or HResultLockViolation;

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
