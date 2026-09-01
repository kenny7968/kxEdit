using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.IO;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.IO;

/// <summary>
/// 設計 2026-09-02 §3 / §6.1(M-12)。File.Replace の部分失敗(差替先が消え、tmp だけが残る)は
/// 実環境で決定的に起こせないため、差替の 1 手の seam に「例外を投げ、かつ原本を消す」偽装を
/// 注入して事後条件の分岐を固定する。
/// <para>
/// seam が実物とずれていないことは、実失敗経路を使う既存テストが緑のままであることで見る
/// (<c>AtomicFileTests</c> の共有違反 / <c>SerialBackupWriterTests</c> の同名ディレクトリ /
/// <c>FileControllerTests</c> の ReadOnly 属性)。この 3 本は「原本が残る」側の分岐を実物で押さえている。
/// </para>
/// <para>
/// <b>フックを張るテストは必ず <c>Invocations</c> を assert すること</b>(設計 2026-09-02 §10.4 I-2)。
/// seam は <c>[ThreadStatic]</c> なので、張ったスレッドと <c>Write</c> が走るスレッドがずれると
/// 黙って既定実装が走る。復旧成功枝の事後状態は既定実装が成功した場合と<b>まったく同じ</b>に
/// なるため、事後状態だけを見ると復旧ロジックを一度も通さずに緑になる。
/// </para>
/// </summary>
public class AtomicFileRecoveryTests
{
    private static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private static string[] TmpLeftovers(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*");

    /// <summary>
    /// 差替先を消してから失敗する = ERROR_UNABLE_TO_MOVE_REPLACEMENT 相当の状態
    /// (tmp がディスク上の唯一のコピー)を作る。
    /// </summary>
    private static void DestroyDestinationThenFail(string tmp, string dest, bool destExists)
    {
        File.Delete(dest);
        throw new IOException(
            $"simulated partial replace failure: destroyed '{dest}' (destExists={destExists}); only copy is '{tmp}'"
        );
    }

    /// <summary>
    /// 差替先を消したうえで、復旧の <c>File.Move(tmp, dest)</c> も必ず失敗させる
    /// (同名ディレクトリ = <c>SerialBackupWriterTests</c> と同じ決定的失敗)。
    /// </summary>
    private static void DestroyDestinationAndBlockRecovery(string tmp, string dest, bool destExists)
    {
        File.Delete(dest);
        Directory.CreateDirectory(dest);
        throw new IOException(
            $"simulated partial replace failure with blocked recovery: destroyed '{dest}' (destExists={destExists}); only copy is '{tmp}'"
        );
    }

    /// <summary>差替先に触れずに失敗する = 従来どおり tmp を掃除してよい状態。</summary>
    private static void FailWithoutTouchingDestination(string tmp, string dest, bool destExists) =>
        throw new IOException(
            $"simulated replace failure: '{dest}' untouched (destExists={destExists}); staged copy is '{tmp}'"
        );

    // 後始末。assertion より後に走るので、ここでの失敗が本来の失敗理由を覆い隠さないようにする。
    private static void Cleanup(string path)
    {
        try
        {
            foreach (string f in TmpLeftovers(path))
                File.Delete(f);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        { /* 残骸は実害小 */
        }
        catch (UnauthorizedAccessException)
        { /* 同上 */
        }
    }

    // ===== byte[] 版 =====

    /// <summary>
    /// #1 原本あり・差替失敗・原本が消える・復旧成功 → <c>Write</c> は正常 return し、
    /// 新内容が path にある(設計 2026-09-02 §3.1 / §3.2)。
    /// </summary>
    [Fact]
    public void Bytes_recovers_when_replace_loses_the_original()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (var scope = AtomicFile.OverrideReplaceStepForTest(DestroyDestinationThenFail))
            {
                // 復旧(tmp → path のリネーム)が効くので、Write は成功として返る。
                AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 }); // "new"

                // フックが実際に発火したこと。これが無いと、フック不発で既定実装が成功しただけの
                // 「同じ事後状態」を復旧成功と取り違える。
                Assert.Equal(1, scope.Invocations);
            }

            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// #2 原本あり・差替失敗・原本が消える・復旧も失敗 → <c>AtomicReplaceFailedException</c> で
    /// <b>tmp を残す</b>(= ディスク上の唯一のコピー)。in-place フォールバックへは流れない(§3.4)。
    /// </summary>
    [Fact]
    public void Bytes_preserves_tmp_when_recovery_also_fails()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(
                    DestroyDestinationAndBlockRecovery
                )
            )
            {
                var ex = Assert.Throws<AtomicReplaceFailedException>(() =>
                    AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 })
                );
                Assert.Equal(1, scope.Invocations);

                Assert.Equal(path, ex.TargetPath);
                // tmp は消されていない = ディスク上の唯一のコピーが残っている。
                Assert.True(File.Exists(ex.PreservedTempPath));
                Assert.Equal(
                    new byte[] { 0x6E, 0x65, 0x77 },
                    File.ReadAllBytes(ex.PreservedTempPath)
                );
                // 差替そのものの失敗が inner・復旧の失敗が RecoveryError。
                Assert.IsType<IOException>(ex.InnerException);
                Assert.NotNull(ex.RecoveryError);
                // in-place フォールバックへ流れない(設計 §3.4)。
                Assert.False(AtomicFile.IsShareOrLockViolation(ex));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// #3 原本あり・差替失敗・原本は残る → 従来どおり例外を伝播し、tmp は消す。
    /// <para>
    /// <c>Assert.Throws&lt;IOException&gt;</c> は xUnit では<b>型完全一致</b>なので、
    /// この時点で「復旧枝(= <c>AtomicReplaceFailedException</c>)へ入っていない」ことも同時に
    /// 固定している。後続の <c>IsNotType</c> は、これを <c>ThrowsAny</c> へ緩めた場合でも
    /// 弁別を残すための重ね掛け。
    /// </para>
    /// </summary>
    [Fact]
    public void Bytes_still_deletes_tmp_when_original_survives()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(FailWithoutTouchingDestination)
            )
            {
                var ex = Assert.Throws<IOException>(() => AtomicFile.Write(path, new byte[] { 9 }));
                Assert.Equal(1, scope.Invocations);
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.Equal("old", File.ReadAllText(path)); // 原本は不変
            Assert.Empty(TmpLeftovers(path)); // 残骸を残さない(従来どおり)
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// #4 新規作成(差替先が元から無い)の失敗では tmp を残さない。事後条件だけで判定すると
    /// ここも <c>!File.Exists(path)</c> が真になり、失われた原本が無いのに残骸を残す
    /// = 誤検出になる(設計 2026-09-02 §3.1)。
    /// </summary>
    [Fact]
    public void Bytes_deletes_tmp_when_creating_a_new_file_fails()
    {
        string path = NewTempPath();
        try
        {
            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(FailWithoutTouchingDestination)
            )
            {
                var ex = Assert.Throws<IOException>(() => AtomicFile.Write(path, new byte[] { 9 }));
                Assert.Equal(1, scope.Invocations);
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.False(File.Exists(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ===== Stream 版 =====
    // 本番の主保存経路 TextFileService.Save(string, TextBuffer, …) が使うのはこちら。
    // byte[] 版だけに網を張ると、片方だけを直す(あるいは片方だけ壊す)変異が生存する。

    /// <summary>#1 の Stream 版。</summary>
    [Fact]
    public void Stream_recovers_when_replace_loses_the_original()
    {
        string path = NewTempPath();
        byte[] payload = new byte[] { 0x6E, 0x65, 0x77 }; // "new"
        try
        {
            File.WriteAllText(path, "old");

            using (var scope = AtomicFile.OverrideReplaceStepForTest(DestroyDestinationThenFail))
            {
                AtomicFile.Write(path, s => s.Write(payload, 0, payload.Length));
                Assert.Equal(1, scope.Invocations);
            }

            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>#2 の Stream 版。</summary>
    [Fact]
    public void Stream_preserves_tmp_when_recovery_also_fails()
    {
        string path = NewTempPath();
        byte[] payload = new byte[] { 0x6E, 0x65, 0x77 }; // "new"
        try
        {
            File.WriteAllText(path, "old");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(
                    DestroyDestinationAndBlockRecovery
                )
            )
            {
                var ex = Assert.Throws<AtomicReplaceFailedException>(() =>
                    AtomicFile.Write(path, s => s.Write(payload, 0, payload.Length))
                );
                Assert.Equal(1, scope.Invocations);

                Assert.Equal(path, ex.TargetPath);
                Assert.True(File.Exists(ex.PreservedTempPath));
                Assert.Equal(payload, File.ReadAllBytes(ex.PreservedTempPath));
                Assert.IsType<IOException>(ex.InnerException);
                Assert.NotNull(ex.RecoveryError);
                Assert.False(AtomicFile.IsShareOrLockViolation(ex));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>#3 の Stream 版。</summary>
    [Fact]
    public void Stream_still_deletes_tmp_when_original_survives()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(FailWithoutTouchingDestination)
            )
            {
                var ex = Assert.Throws<IOException>(() =>
                    AtomicFile.Write(path, s => s.WriteByte(9))
                );
                Assert.Equal(1, scope.Invocations);
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.Equal("old", File.ReadAllText(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>#4 の Stream 版。</summary>
    [Fact]
    public void Stream_deletes_tmp_when_creating_a_new_file_fails()
    {
        string path = NewTempPath();
        try
        {
            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(FailWithoutTouchingDestination)
            )
            {
                var ex = Assert.Throws<IOException>(() =>
                    AtomicFile.Write(path, s => s.WriteByte(9))
                );
                Assert.Equal(1, scope.Invocations);
                Assert.IsNotType<AtomicReplaceFailedException>(ex);
            }

            Assert.False(File.Exists(path));
            Assert.Empty(TmpLeftovers(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ===== 本番の保存経路から見た挙動(設計 2026-09-02 §3.4) =====
    // AtomicReplaceFailedException が TextFileService の in-place フォールバック
    // (`catch (IOException ex) when (AtomicFile.IsShareOrLockViolation(ex))`)へ<b>流れない</b>ことを、
    // IsShareOrLockViolation を直接呼ぶのではなく実際の呼出経路で固定する。
    // 流れてしまうと、原本が消えた後に in-place 上書きで書き直すことになり、
    // AtomicFile 側の復旧と同じ仕事を別経路で二重に行う(どちらが効いたか判らなくなる)。

    /// <summary>
    /// Stream 経路(<c>TextFileService.Save(string, TextBuffer, …)</c> = 本番の主保存経路)。
    /// フォールバックへ流れると byte[] 版 Save へ委譲されて seam をもう一度通るため、
    /// <c>Invocations</c> が 2 になる。1 であることが「流れていない」の観測点。
    /// </summary>
    [Fact]
    public void Save_buffer_propagates_recovery_failure_without_in_place_fallback()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");
            var buffer = TextBuffer.FromString("new");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(
                    DestroyDestinationAndBlockRecovery
                )
            )
            {
                var ex = Assert.Throws<AtomicReplaceFailedException>(() =>
                    TextFileService.Save(path, buffer, new UTF8Encoding(false), hasBom: false)
                );
                Assert.Equal(1, scope.Invocations);
                Assert.True(File.Exists(ex.PreservedTempPath));
                Assert.Equal("new", File.ReadAllText(ex.PreservedTempPath, Encoding.UTF8));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// byte[] 経路(<c>TextFileService.Save(string, string, …)</c> = 共有違反フォールバック専用の
    /// overload)。こちらのフォールバックは <c>File.WriteAllBytes</c> 直書きで seam を通らないため、
    /// 観測点は例外の型になる(流れていれば差替先がディレクトリなので
    /// <c>UnauthorizedAccessException</c> に変わる)。
    /// </summary>
    [Fact]
    public void Save_text_propagates_recovery_failure_without_in_place_fallback()
    {
        string path = NewTempPath();
        try
        {
            File.WriteAllText(path, "old");

            using (
                var scope = AtomicFile.OverrideReplaceStepForTest(
                    DestroyDestinationAndBlockRecovery
                )
            )
            {
                var ex = Assert.Throws<AtomicReplaceFailedException>(() =>
                    TextFileService.Save(path, "new", new UTF8Encoding(false), hasBom: false)
                );
                Assert.Equal(1, scope.Invocations);
                Assert.True(File.Exists(ex.PreservedTempPath));
                Assert.Equal("new", File.ReadAllText(ex.PreservedTempPath, Encoding.UTF8));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }
}
