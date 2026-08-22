using Directory = System.IO.Directory;
using IOException = System.IO.IOException;

namespace kxEdit.App.Tests;

/// <summary>テスト毎に使い捨ての一時フォルダ(実ファイル I/O 用)。</summary>
internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("kxEditAppTests_").FullName;

    public string File(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { /* 掃除失敗はテスト失敗にしない(読み取り専用属性等は UnauthorizedAccessException) */
        }
    }
}
