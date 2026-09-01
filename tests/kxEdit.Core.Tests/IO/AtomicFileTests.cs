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
