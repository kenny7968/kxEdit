using System.IO;
using yEdit.App.Tests.Fakes;

namespace yEdit.App.Tests;

/// <summary>
/// FileMetaProvider の到達性プローブ前置(HIGH-6 / CSV-M-1 と同じ 5 秒契約)を固定する。
/// FileInfo.Exists は GetFileAttributesExW を発火するため、切断済みリモートパスでは
/// SMB タイムアウト(約 60 秒)まで UI スレッドが返らない。ローカルパスでは
/// プローブのスレッド起動コストを払わないことも併せて pin する。
/// </summary>
public class FileMetaProviderTests
{
    [Fact]
    public void Unreachable_remote_path_returns_null_without_touching_the_file_system()
    {
        var probe = new FakeReachabilityProbe { Result = false }; // 到達不能
        var provider = new FileMetaProvider(probe);

        Assert.Null(provider.TryGet(@"\\unreachable-host\share\a.txt"));

        Assert.Equal(1, probe.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(5), probe.LastTimeout); // 5s → 5min 等の変異を殺す
    }

    /// <summary>ローカルパスはプローブを経由せず属性を返す(リモート判定ガードの kill)。</summary>
    [Fact]
    public void Local_path_skips_probe_and_reports_metadata()
    {
        var probe = new FakeReachabilityProbe();
        string dir = Directory.CreateTempSubdirectory("yEditFileMeta_").FullName;
        try
        {
            string path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "hello"); // 5 バイト(ASCII)

            var meta = new FileMetaProvider(probe).TryGet(path);

            Assert.NotNull(meta);
            Assert.Equal(5, meta!.Value.Length);
            Assert.Equal(0, probe.CallCount); // ローカルはプローブしない
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Null_path_returns_null_without_probing()
    {
        var probe = new FakeReachabilityProbe();
        Assert.Null(new FileMetaProvider(probe).TryGet(null));
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void Missing_local_file_returns_null()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "yEdit-no-such-file-" + Guid.NewGuid().ToString("N") + ".txt"
        );
        Assert.Null(new FileMetaProvider(new FakeReachabilityProbe()).TryGet(path));
    }

    /// <summary>不正なパス文字列でも例外を投げず null に落とす(catch-all の契約)。</summary>
    [Fact]
    public void Malformed_path_returns_null_instead_of_throwing()
    {
        var provider = new FileMetaProvider(new FakeReachabilityProbe());
        Assert.Null(provider.TryGet(""));
        Assert.Null(provider.TryGet("   "));
        Assert.Null(provider.TryGet("a\0b.txt"));
    }
}
