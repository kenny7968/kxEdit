using System.IO;
using Xunit;
using yEdit.Core.Session;

namespace yEdit.Core.Tests.Session;

/// <summary>
/// レガシー移行(PR #22 形式の一回限り読み替え)で生きている Load/Delete のみを検証する。
/// Save 系テストは Task 7(hot exit 統合)の Save 退役と同時に削除し、fixture は
/// File.WriteAllText の生 JSON(旧 Save 出力と互換)で植える。
/// </summary>
public class LastSessionBuffersStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var map = LastSessionBuffersStore.Load(TempPath());
        Assert.Empty(map);
    }

    [Fact]
    public void Load_ValidJson_ReturnsEntries()
    {
        string path = TempPath();
        try
        {
            // 旧 Save 出力と互換の固定 fixture: 生 UTF-8 の日本語+エスケープ済み改行の両方を
            // 読めることを固定する(移行読取の契約)。
            File.WriteAllText(path, "{\"k1\":\"hello\",\"k2\":\"こんにちは\\n2行目\"}");
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("hello", loaded["k1"]);
            Assert.Equal("こんにちは\n2行目", loaded["k2"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ not valid json");
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, string.Empty);
            var map = LastSessionBuffersStore.Load(path);
            Assert.Empty(map);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Delete_MissingFile_IsNoOp()
    {
        string path = TempPath();
        // 例外にならず、存在しないファイルが依然として存在しないこと(no-op)を確認する。
        LastSessionBuffersStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Delete_ExistingFile_Removes()
    {
        string path = TempPath();
        File.WriteAllText(path, "{}");
        LastSessionBuffersStore.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_JsonWithNullValue_SkipsNullEntries()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"k1\":null,\"k2\":\"ok\"}");
            var map = LastSessionBuffersStore.Load(path);
            Assert.False(map.ContainsKey("k1")); // 明示 null は skip
            Assert.Equal("ok", map["k2"]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyJsonObject_ReturnsEmpty()
    {
        string path = TempPath();
        try
        {
            // 旧 Save が空マップを書いた形("{}")=移行パスが実際に踏むケース。
            File.WriteAllText(path, "{}");
            var loaded = LastSessionBuffersStore.Load(path);
            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
