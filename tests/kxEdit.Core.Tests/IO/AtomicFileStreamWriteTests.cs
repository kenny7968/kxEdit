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
}
