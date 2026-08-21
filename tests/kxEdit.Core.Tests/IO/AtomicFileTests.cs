using kxEdit.Core.IO;
using Xunit;

namespace kxEdit.Core.Tests.IO;

public class AtomicFileTests
{
    [Fact]
    public void Write_creates_new_file_with_payload()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            AtomicFile.Write(path, new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_overwrites_existing_and_leaves_no_tmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            AtomicFile.Write(path, new byte[] { 0x6E, 0x65, 0x77 }); // "new"
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*")
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Write_to_fully_locked_target_throws_share_violation_and_keeps_original()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<IOException>(() => AtomicFile.Write(path, new byte[] { 9 }));
                Assert.True(AtomicFile.IsShareOrLockViolation(ex));
            }
            // 原本は不変・tmp 残骸なし。
            Assert.Equal("original", File.ReadAllText(path));
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*")
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void IsShareOrLockViolation_is_false_for_generic_io_error() =>
        Assert.False(AtomicFile.IsShareOrLockViolation(new IOException("generic")));
}
