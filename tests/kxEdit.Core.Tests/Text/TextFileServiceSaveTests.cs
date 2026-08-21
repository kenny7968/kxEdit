using System.Text;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class TextFileServiceSaveTests
{
    [Fact]
    public void Save_then_load_roundtrips_shift_jis()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            const string text = "保存テスト\r\nおわり\r\n";
            TextFileService.Save(path, text, EncodingCatalog.Get(932), hasBom: false);
            var doc = TextFileService.Load(path);
            Assert.Equal(text, doc.Text);
            Assert.Equal(932, doc.Encoding.CodePage);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_overwrites_existing_atomically()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            TextFileService.Save(path, "new", EncodingCatalog.Get(65001), hasBom: false);
            Assert.Equal("new", File.ReadAllText(path));
            // temp 残骸が無いこと
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
    public void Save_utf8_with_bom_writes_preamble()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            TextFileService.Save(path, "x", EncodingCatalog.Get(65001), hasBom: true);
            var bytes = File.ReadAllBytes(path);
            Assert.True(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            );
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_falls_back_to_inplace_when_replace_blocked_by_share_lock()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "old");
            // Delete 共有を許さない ReadWrite ロック → File.Replace は共有違反(0x80070020)で失敗するが、
            // in-place 上書き（FileShare.Read で open）は成立する。
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                TextFileService.Save(path, "new", EncodingCatalog.Get(65001), hasBom: false);
            }
            // File.Replace は塞がれているため、内容が "new" に変わったこと自体が in-place 経路を通った証拠。
            Assert.Equal("new", File.ReadAllText(path));
            // temp 残骸が無いこと。
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
    public void Save_does_not_truncate_original_when_unrecoverably_locked()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            const string original = "オリジナル本文\r\n壊れてはいけない\r\n";
            File.WriteAllBytes(path, EncodingCatalog.Get(932).GetBytes(original));
            long originalLen = new FileInfo(path).Length;

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 完全ロック: File.Replace も in-place 上書きも共有違反。Save は例外を投げるべきで、
                // かつ原本を 0 バイトに切り詰めてはならない（データ喪失バグの回帰ガード）。
                Assert.Throws<IOException>(() =>
                    TextFileService.Save(
                        path,
                        "破壊データ",
                        EncodingCatalog.Get(65001),
                        hasBom: false
                    )
                );
            }

            // 原本が保持されていること（長さ・内容とも）。
            Assert.Equal(originalLen, new FileInfo(path).Length);
            Assert.Equal(original, TextFileService.Load(path, forcedCodePage: 932).Text);
            // temp 残骸が無いこと。
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

    // ------------------------------------------------------------------
    // Task 1c: 旧 Save(string, string, Encoding, bool) は Buffer 版に集約し、
    // 共有違反フォールバック専用の internal 実装に閉じる。
    // ------------------------------------------------------------------

    [Fact]
    public void Save_StringText_IsInternalOnly()
    {
        // Task 1c: 旧版 Save(path, string, Encoding, hasBom) は共有違反 fallback 用の
        // internal 実装に閉じ、public API 面からは Buffer 版のみ露出する。
        var flags =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static;
        var methods = typeof(TextFileService)
            .GetMethods(flags)
            .Where(m => m.Name == "Save")
            .ToList();
        var stringOverload = methods.FirstOrDefault(m =>
            m.GetParameters().Length == 4 && m.GetParameters()[1].ParameterType == typeof(string)
        );
        Assert.NotNull(stringOverload);
        Assert.False(
            stringOverload!.IsPublic,
            "string 版 Save は public であってはならない(fallback 専用)"
        );
    }

    [Theory]
    [InlineData(65001, false)]
    [InlineData(65001, true)]
    [InlineData(932, false)]
    [InlineData(51932, false)]
    public void Save_BufferAndString_YieldSameBytes(int codePage, bool hasBom)
    {
        EncodingCatalog.EnsureRegistered();
        var enc = EncodingCatalog.Get(codePage);
        string text = "日本語混じり\nline2\n";

        string bufferPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string stringPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var buffer = TextBuffer.FromString(text);
            TextFileService.Save(bufferPath, buffer, enc, hasBom); // public Buffer 版
            TextFileService.Save(stringPath, text, enc, hasBom); // internal string 版
            // (InternalsVisibleTo 経由で呼び出し可能)
            Assert.Equal(File.ReadAllBytes(stringPath), File.ReadAllBytes(bufferPath));
        }
        finally
        {
            if (File.Exists(bufferPath))
                File.Delete(bufferPath);
            if (File.Exists(stringPath))
                File.Delete(stringPath);
        }
    }
}
