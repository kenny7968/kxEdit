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

    // ===== M-13: ステージングを File.WriteAllBytes から FileStream(CreateNew)+Flush(true) へ
    //              置き換えたことの挙動不変ネット(設計 2026-09-02 §4)。
    //
    // これらは「fsync が効いたこと」を検証していない。電源断を再現できないため、
    // Flush(flushToDisk: true) がディスクへ届いたことは自動テストでは観測できない(設計 §6.2)。
    // ここで固定しているのは<書き手を差し替えても書けるバイト列が変わらないこと>だけである。
    // CreateNew(既存 tmp を黙って上書きしない)を弁別する網は無い —— tmp 名が
    // Path.GetRandomFileName() 由来で、テストから同名ファイルを先に置けないため。
    // FileMode.Create へ退化させる変異は生存する(設計 §10.8 に実測を記録)。

    /// <summary>
    /// 空の payload は「0 バイトのファイル」になること。File.WriteAllBytes は空配列でも
    /// ファイルを作るが、置き換え後の実装が「書くものが無いなら書かない」形へ倒れると
    /// ステージングが失敗するか、内容の無いファイルが差し替わらなくなる。
    /// </summary>
    [Fact]
    public void Bytes_writes_an_empty_payload_as_an_empty_file()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            AtomicFile.Write(path, Array.Empty<byte>());
            Assert.True(File.Exists(path));
            Assert.Empty(File.ReadAllBytes(path));
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

    /// <summary>
    /// payload が null のときは <see cref="ArgumentNullException"/>(File.WriteAllBytes 時代と
    /// 同じ型)で、<b>tmp を作らずに</b>弾くこと。
    /// <para>
    /// FileStream 版は <c>payload.Length</c> に触れるため、入口ガードが無いと
    /// NullReferenceException になり、しかも空の tmp を作ってから消すことになる。
    /// </para>
    /// </summary>
    [Fact]
    public void Bytes_rejects_a_null_payload_without_creating_a_tmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            // キャストが要る: null リテラルだと byte[] 版と Action<Stream> 版が曖昧になる(CS0121)。
            Assert.Throws<ArgumentNullException>(() => AtomicFile.Write(path, (byte[])null!));
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

    /// <summary>
    /// FileStream の内部バッファ(既定 4KB)を大きく超える payload が<b>最後まで</b>書かれること。
    /// <para>
    /// 既存の byte[] 版テストは 1〜3 バイトしか書いておらず、書き手を
    /// <c>File.WriteAllBytes</c>(1 回で全部書く)から <c>FileStream.Write</c> へ替えたときに
    /// 部分書込へ退行しても検出できなかった。末尾も含めて内容を突き合わせる。
    /// </para>
    /// </summary>
    [Fact]
    public void Bytes_writes_a_payload_larger_than_the_stream_buffer_completely()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            byte[] payload = new byte[1024 * 1024 + 7]; // バッファ境界に揃わない長さ
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(i % 251);

            AtomicFile.Write(path, payload);

            byte[] actual = File.ReadAllBytes(path);
            Assert.Equal(payload.Length, actual.Length);
            Assert.Equal(payload, actual);
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

    /// <summary>
    /// seam は差替の 1 手だけを差し替え、スコープを抜けたら必ず既定実装へ戻ること。
    /// 戻らないと、並列実行される他のテストクラス(SettingsStoreTests / BackupStore 系)が
    /// 巻き添えになる。ThreadStatic なので他スレッドへは漏れないが、同一スレッド上での
    /// 後始末はこのテストでしか固定できない。
    /// </summary>
    [Fact]
    public void Replace_step_override_applies_only_inside_scope()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var scope = AtomicFile.OverrideReplaceStepForTest(
                (tmp, dest, _) => File.Move(tmp, dest, overwrite: true)
            );
            using (scope)
            {
                AtomicFile.Write(path, new byte[] { 1 });
            }
            // 「張ったのに不発(= 既定実装が走った)」を弾く。事後状態は既定実装でも同じになる。
            Assert.Equal(1, scope.Invocations);

            // スコープ外は既定実装(= フックは発火しない)。
            AtomicFile.Write(path, new byte[] { 2 });
            Assert.Equal(1, scope.Invocations);
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// 差替が<b>非 IOException</b> を投げても tmp 残骸を残さないこと(= CommitStaged の catch は
    /// catch-all でなければならない)。実例は保存先が ReadOnly 属性のときの
    /// UnauthorizedAccessException で、これは IOException ではない。
    /// <para>
    /// この網が無いと <c>catch</c> を <c>catch (IOException)</c> へ狭める変異が生存する。
    /// FileControllerTests の ReadOnly 系は「原本不変」と「Modified 復元」しか見ておらず、
    /// tmp 残骸を見ていないため素通しになる(設計 2026-09-02 §10.2)。
    /// </para>
    /// <para>
    /// フックは差替先を消さないので、M-12 の復旧で tmp 保持が入っても本テストは 0 個のまま
    /// (保持されるのは「原本が消えた」枝だけ——設計 2026-09-02 §3.1)。
    /// </para>
    /// </summary>
    [Fact]
    public void Replace_step_failure_with_non_io_exception_still_cleans_tmp()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllText(path, "original");
            var scope = AtomicFile.OverrideReplaceStepForTest(
                (_, _, _) => throw new InvalidOperationException("boom")
            );
            using (scope)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    AtomicFile.Write(path, new byte[] { 9 })
                );
            }
            // 投げるフックも発火として数える。0 なら既定実装が走っている。
            Assert.Equal(1, scope.Invocations);
            // 原本は不変(フックは差替先に触っていない)・tmp 残骸なし。
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

    /// <summary>
    /// スコープを入れ子にしたとき、内側の Dispose は<b>外側のフックへ</b>戻すこと(LIFO)。
    /// <para>
    /// 既存テストは 1 段しか張らず previous が常に null のため、
    /// <c>Dispose() =&gt; SetReplaceStepOverride(null)</c> への変異が等価になって生存する
    /// (設計 2026-09-02 §10.2)。previous を捕まえて戻すコードがある以上、網も要る。
    /// </para>
    /// </summary>
    [Fact]
    public void Replace_step_override_scopes_restore_in_lifo_order()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var outer = AtomicFile.OverrideReplaceStepForTest(
                (tmp, dest, _) => File.Move(tmp, dest, overwrite: true)
            );
            using (outer)
            {
                var inner = AtomicFile.OverrideReplaceStepForTest(
                    (tmp, dest, _) => File.Move(tmp, dest, overwrite: true)
                );
                using (inner)
                {
                    AtomicFile.Write(path, new byte[] { 1 });
                }
                // 内側スコープ中は内側だけが発火する。
                Assert.Equal(1, inner.Invocations);
                Assert.Equal(0, outer.Invocations);

                // 内側を抜けたら外側フックが復帰していること。
                // null へ戻す実装だと既定実装が走り outer は 0 のままになる。
                AtomicFile.Write(path, new byte[] { 2 });
                Assert.Equal(1, outer.Invocations);
                Assert.Equal(1, inner.Invocations);
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    /// <summary>
    /// <b>この網自身が「フック不発」を捕まえられること</b>の確認(網の網)。
    /// フックはスレッド親和なので、張ったスレッドと <c>Write</c> が走るスレッドがずれると
    /// 黙って既定実装が走る。BackupStore / SessionLayoutStore は SerialBackupWriter の専用
    /// ワーカースレッドで書くため、これは実際に起こりうる事故である。
    /// <para>
    /// 事後状態(内容・tmp 残骸)は既定実装が成功したときとまったく同じになるので区別できない。
    /// 区別できる唯一の観測点が <c>Invocations</c> であることを、ここで固定する。
    /// </para>
    /// </summary>
    [Fact]
    public void Replace_step_override_does_not_fire_on_another_thread()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            var scope = AtomicFile.OverrideReplaceStepForTest(
                (tmp, dest, _) => File.Move(tmp, dest, overwrite: true)
            );
            using (scope)
            {
                var worker = new Thread(() => AtomicFile.Write(path, new byte[] { 1 }));
                worker.Start();
                Assert.True(worker.Join(TimeSpan.FromSeconds(30)));
            }

            // 書込自体は成功し、事後状態はフックが効いた場合と区別が付かない。
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
            Assert.Empty(
                Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*tmp*")
            );
            // 区別できるのはここだけ。
            Assert.Equal(0, scope.Invocations);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
