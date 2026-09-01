using System.Text;
using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

public class AtomicFileStreamWriteTests
{
    [Fact]
    public void Write_Stream_CreatesFileWithWrittenBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            AtomicFile.Write(
                path,
                stream =>
                {
                    var bytes = Encoding.UTF8.GetBytes("hello");
                    stream.Write(bytes, 0, bytes.Length);
                }
            );
            Assert.Equal("hello", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_Stream_AtomicReplaceOverwritesExisting()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            AtomicFile.Write(
                path,
                stream =>
                {
                    var bytes = Encoding.UTF8.GetBytes("new");
                    stream.Write(bytes, 0, bytes.Length);
                }
            );
            Assert.Equal("new", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_Stream_WriterThrows_LeavesOriginalUntouched()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            Assert.Throws<InvalidOperationException>(() =>
                AtomicFile.Write(path, _ => throw new InvalidOperationException("boom"))
            );
            Assert.Equal("original", File.ReadAllText(path, Encoding.UTF8));
            // tmp が残っていない(同ディレクトリに *.tmp が無い)
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            string leftover =
                Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp").FirstOrDefault() ?? "";
            Assert.True(string.IsNullOrEmpty(leftover), $"leftover tmp: {leftover}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_Stream_NewFile_UsesMove()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.False(File.Exists(path));
            AtomicFile.Write(
                path,
                stream =>
                {
                    var bytes = Encoding.UTF8.GetBytes("fresh");
                    stream.Write(bytes, 0, bytes.Length);
                }
            );
            Assert.Equal("fresh", File.ReadAllText(path, Encoding.UTF8));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_Stream_TargetLocked_ThrowsShareViolation_KeepsOriginal_CleansTmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "locked-original");
            // ターゲットを FileShare.None で握って File.Replace を失敗させる
            // (assertion で path を再読みするため FileShare.None 解除後に検証する
            //  =existing byte[] 版テスト AtomicFileTests.Write_to_fully_locked_target_… と同型)。
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() =>
                    AtomicFile.Write(path, s => s.WriteByte(0x39))
                );
                Assert.True(AtomicFile.IsShareOrLockViolation(ex));
            }
            // 原本は不変・tmp 残骸なし。
            Assert.Equal("locked-original", File.ReadAllText(path));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var leftover = Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp");
            Assert.Empty(leftover);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// Stream 版の差替も <c>CommitStaged</c>(= seam)を通ること、かつその catch が catch-all で
    /// あること(非 IOException でも tmp を掃除する)を同時に固定する。
    /// <para>
    /// <b>Stream 版であることが重要</b>: 本番の主保存経路 <c>TextFileService.Save(string,
    /// TextBuffer, …)</c> が使うのはこちら。ここが seam から静かに外れると、M-12 の復旧
    /// ロジックが主経路に効いていなくても全テストが緑になる(設計 2026-09-02 §10.2)。
    /// byte[] 版だけを通る網では、この退行を検出できない。
    /// </para>
    /// </summary>
    [Fact]
    public void Write_Stream_ReplaceStepFailureWithNonIoException_StillCleansTmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "stream-original");
            var scope = AtomicFile.OverrideReplaceStepForTest(
                (_, _, _) => throw new InvalidOperationException("boom")
            );
            using (scope)
            {
                // フックへ到達しない実装(インライン差替へ戻す等)なら書込は成功してしまい、
                // この Assert.Throws が落ちる。
                Assert.Throws<InvalidOperationException>(() =>
                    AtomicFile.Write(path, s => s.WriteByte(0x39))
                );
            }
            // Stream 版が実際に seam を通ったこと(不発なら 0)。
            Assert.Equal(1, scope.Invocations);
            // 原本は不変(フックは差替先に触っていない)・tmp 残骸なし。
            Assert.Equal("stream-original", File.ReadAllText(path, Encoding.UTF8));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Assert.Empty(Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
