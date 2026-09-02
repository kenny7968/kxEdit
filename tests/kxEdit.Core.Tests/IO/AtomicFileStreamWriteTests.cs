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

    /// <summary>
    /// M-13 の契約: writer が渡された Stream を閉じてしまった場合、<b>安全側に倒れる</b>こと
    /// —— 例外が伝播し、tmp は掃除され、原本は無傷のまま残る(設計 2026-09-02 §4)。
    /// <para>
    /// <c>using var sw = new StreamWriter(stream)</c> は<b>下位ストリームごと Dispose する</b>ので、
    /// 将来の呼出側が素直に書くとこの形になる。閉じられた後は <c>Flush(flushToDisk: true)</c> を
    /// 掛けられないため、<b>黙って fsync を飛ばす(= M-13 の保証が静かに消える)</b>か、
    /// <b>失敗させる</b>かの二択になる。ここでは後者を選んだことを固定する。
    /// </para>
    /// <para>
    /// <b>この網が殺せる変異と殺せない変異</b>: Stream 版から <c>fs.Flush(...)</c> を丸ごと
    /// 落とす変異は殺せる(閉じられた fs に触らなくなるので例外が出ず、書込が成功してしまう)。
    /// 一方 <c>Flush(flushToDisk: true)</c> を <c>Flush()</c> へ退化させる変異は<b>殺せない</b>
    /// —— どちらも閉じられたストリームでは同じ例外になる。fsync が実際にディスクへ届いたことは
    /// 自動テストでは観測できない(設計 §6.2 / §10.8)。
    /// </para>
    /// </summary>
    [Fact]
    public void Write_Stream_WriterClosesTheStream_FailsSafely()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            Assert.Throws<ObjectDisposedException>(() =>
                AtomicFile.Write(
                    path,
                    stream =>
                    {
                        // leaveOpen を渡していない = Dispose で下位ストリームまで閉じる。
                        using var sw = new StreamWriter(stream, Encoding.UTF8);
                        sw.Write("closed by the writer");
                    }
                )
            );
            // 原本は無傷・tmp 残骸なし(= ステージング失敗時の既存ポリシーどおり)。
            Assert.Equal("original", File.ReadAllText(path, Encoding.UTF8));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Assert.Empty(Directory.GetFiles(dir, Path.GetFileName(path) + ".*.tmp"));
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

    /// <summary>
    /// ステージング中の tmp は <c>FileShare.None</c> で開かれている = <b>書いている間、他の誰も
    /// 開けない</b>(最終レビュー(品質パス)I-2)。
    /// <para>
    /// <b>この網が張れることは、§10.8 /§10.10 が「原理的に不可能」と書いていた主張の反例である。</b>
    /// あちらの論証は <c>CreateNew</c> のもので、「<c>FileStream</c> ctor <b>より前</b>に tmp 名を
    /// 知る手段が production に無い」ことに依拠していた。<c>FileShare</c> の差はそこには出ない ——
    /// 出るのは<b>ハンドルが開いている間に別ハンドルから開けるか</b>であり、そのハンドルは
    /// <c>writer(fs)</c> の実行中ずっと開いていて、writer は <c>((FileStream)stream).Name</c> で
    /// tmp パスを知れる。<b>ctor 前に名前を知る連鎖は要らない</b>ので、論証はここへは及ばない。
    /// production への seam 追加もゼロである。
    /// </para>
    /// <para>
    /// 網を張る前は <c>FileShare.None</c> → <c>FileShare.ReadWrite</c> の変異が
    /// <b>Core 1427 全 PASS で生存</b>していた(実測・§10.21)。
    /// </para>
    /// <para>
    /// byte[] 版は Stream 版へ委譲している(§10.9)ので、この 1 本が両経路のステージングを押さえる。
    /// </para>
    /// </summary>
    [Fact]
    public void Staging_handle_denies_a_concurrent_open()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            string? stagingPath = null;
            bool? openedWhileStaging = null;

            AtomicFile.Write(
                path,
                stream =>
                {
                    // production へ seam を足さずに tmp 名を採れる唯一の場所(writer の中)。
                    stagingPath = ((FileStream)stream).Name;
                    try
                    {
                        // 共有を要求する側 = FileShare.None なら必ず弾かれる。
                        using var probe = new FileStream(
                            stagingPath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite
                        );
                        openedWhileStaging = true;
                    }
                    catch (IOException ex) when (AtomicFile.IsShareOrLockViolation(ex))
                    {
                        openedWhileStaging = false;
                    }
                    // 「開けなかった」が tmp 不在に由来していないことの検算。FileNotFoundException は
                    // IOException だが共有違反ではないので上の when を通らず、ここへ来る前に伝播する。
                    stream.WriteByte(0x39);
                }
            );

            Assert.NotNull(stagingPath);
            Assert.EndsWith(".tmp", stagingPath, StringComparison.Ordinal);
            // ★ 本体。true(= 共有が緩んだ)なら赤。null は writer 未実行 = やはり赤。
            Assert.False(openedWhileStaging);
            // 差替まで通っていること(ステージングだけ見て終わる網にしない)。
            Assert.Equal(new byte[] { 0x39 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
