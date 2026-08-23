using System.Linq;
using System.Reflection;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

public class RecentFilesListTests
{
    [Fact]
    public void New_path_goes_to_front()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt" }, @"C:\c.txt", 10);
        Assert.Equal(new[] { @"C:\c.txt", @"C:\a.txt", @"C:\b.txt" }, r);
    }

    [Fact]
    public void Existing_path_moves_to_front_without_duplicate()
    {
        var r = RecentFilesList.Add(
            new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" },
            @"C:\b.txt",
            10
        );
        Assert.Equal(new[] { @"C:\b.txt", @"C:\a.txt", @"C:\c.txt" }, r);
    }

    [Fact]
    public void Caps_at_max()
    {
        var r = RecentFilesList.Add(
            new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt" },
            @"C:\d.txt",
            2
        );
        Assert.Equal(new[] { @"C:\d.txt", @"C:\a.txt" }, r);
    }

    [Fact]
    public void Cap_one_returns_only_new()
    {
        var r = RecentFilesList.Add(new[] { @"C:\a.txt", @"C:\b.txt" }, @"C:\c.txt", 1);
        Assert.Equal(new[] { @"C:\c.txt" }, r); // max==1 で超過しない
    }

    // ===== dedup の契約(Issue #48 / 設計書 §3.4)=====
    // Add は PathKey.ForNormalized(= ToLowerInvariant のみ・ファイルシステム非接触)で照合する。
    // 以前は両辺に PathKey.For(= GetFullPath)を打っており、1 回の Add で 1 + 履歴件数(最大 10)回
    // の実 I/O を打ちうる形だった(S-15: 正規化後に `~` が残ると GetLongPathName を呼び、
    // 不達共有で約 21 秒 UI が止まる)。以下 3 本は「片側だけを For へ戻す」変異まで
    // 独立に kill できるよう、照会側 / 既存側それぞれに区切り差を置いてある。

    [Fact]
    public void Dedup_is_case_insensitive()
    {
        // 同一ファイルの大小違いは 1 件に集約される(ForNormalized の ToLowerInvariant)。
        var r = RecentFilesList.Add(new[] { @"C:\Dir\A.TXT" }, @"c:\dir\a.txt", 10);
        Assert.Single(r);
        Assert.Equal(@"c:\dir\a.txt", r[0]); // 新規入力が先頭
    }

    [Fact]
    public void Dedup_does_not_normalize_separators_in_existing_entry_accepted_degradation()
    {
        // Issue #48 / 設計書 §3.4 の**受容**を明示的に固定する(既存 current 側)。
        // 本バージョンが書き込むエントリーは正規化済みなのでこの経路には入らない。既存
        // settings.json に残るレガシーエントリーだけが、1 度開き直すまで重複して並びうる。
        // データ損失は無い。
        // この向き(既存側に `/`)は「既存側だけを PathKey.For へ戻す」変異を kill する。
        var r = RecentFilesList.Add(new[] { "c:/dir/a.txt" }, @"C:\Dir\a.txt", 10);
        Assert.Equal(new[] { @"C:\Dir\a.txt", "c:/dir/a.txt" }, r); // 吸収しない = 2 件並ぶ
    }

    [Fact]
    public void Dedup_does_not_normalize_separators_in_new_path_accepted_degradation()
    {
        // 上と対の向き(照会側に `/`)。「照会側だけを PathKey.For へ戻す」変異を kill する。
        // 両辺は独立に変異しうるので、片側ずつ網を張らないと変異が通り抜ける
        // (Task 5 の FindByPath で実測済み)。
        var r = RecentFilesList.Add(new[] { @"C:\Dir\a.txt" }, "c:/dir/a.txt", 10);
        Assert.Equal(new[] { "c:/dir/a.txt", @"C:\Dir\a.txt" }, r); // 吸収しない = 2 件並ぶ
    }

    [Fact]
    public void Add_tolerates_legacy_and_hostile_entries_without_throwing()
    {
        // settings.json 由来のレガシー / 攻撃エントリー(未正規化・相対・null・無効文字)が
        // 来ても例外にしない。以前は GetFullPath の例外を PathKey.For の catch が空文字へ
        // 落として吸収していたが、ForNormalized は I/O も解析もしないので投げる元が無い。
        // ここが縛るのは「投げないこと」と「新規と一致しないものは全件残ること」だけ。
        // Task 6 レビュー m-1: 以前ここには「件数は max で頭打ちなので増幅は起きない」と
        // 書いていたが、この fixture は max=MaxItems に対し 6 件しか作らず cap に一度も
        // 当たらない。cap の網は Caps_at_max / Cap_one_returns_only_new が持つ
        // (Add の `result.Count >= max` は key の比較より前に効くので、invalid 項目でも
        // cap は迂回できない)。
        var legacy = new[] { "..\\rel.txt", "c:/dir/a.txt", null!, "a\0b", @"C:\Dir\a.txt" };
        var r = RecentFilesList.Add(legacy, @"C:\new.txt", RecentFilesList.MaxItems);
        Assert.Equal(@"C:\new.txt", r[0]);
        Assert.Equal(legacy.Length + 1, r.Count); // どれも新規と一致しないので全件残る
    }

    /// <summary>
    /// S-15 の主犯(<c>PathKey.For</c> = <c>GetFullPath</c>)が本当に消えたことを IL で直接固定する。
    /// 上の挙動テストは「<b>結果に効く</b> GetFullPath」しか捕まえられず、結果を捨てる呼出
    /// (挙動不変・コストだけが残る形)を見逃す。S-15 はコストの問題なので、
    /// 「呼出が 1 つも無い」ことをここで見る。
    /// 陽性対照(<c>ForNormalized</c> を拾えること)を同時に置くのは、走査が空を返しただけで
    /// 緑になる vacuous 化を防ぐため。
    /// <para>
    /// <b>この網の射程(Task 6 レビュー I-1・実測で生存を確認)</b>: 走査するのは
    /// <see cref="RecentFilesList.Add"/> の<b>直接の</b>呼出だけで、推移的な呼出は見ない。
    /// 結果を捨てる <c>GetFullPath</c> を private ヘルパ 1 段越しに置く変異は、この網を含めて
    /// 全緑のまま生存する。つまり本テストは「<c>Add</c> の本体に FS 接触の呼出が直接は無い」
    /// ことしか言っておらず、「この関数から FS に到達しない」ことは保証しない
    /// (クラス doc の「ファイルシステムには一切触れない」は実装の性質の宣言であって、
    /// 本テストがそこまで機械固定しているわけではない)。
    /// ただし抜けるのは<b>片側だけをヘルパへ切り出した形</b>に限る: <c>ForNormalized</c> の
    /// 呼出 2 本を<b>両方</b>ヘルパへ移すと陽性対照の <c>Assert.Contains</c> が落ちて赤になるので、
    /// 本体をまるごとヘルパへ移す形は検出できる。
    /// </para>
    /// </summary>
    [Fact]
    public void Add_DoesNotCallFileSystemTouchingPathKeyFor()
    {
        var callees = CalleesOf(typeof(RecentFilesList).GetMethod(nameof(RecentFilesList.Add))!);
        // 陽性対照: 走査が実際に呼出を拾えている(拾えないなら以下の 2 本は無意味)。
        Assert.Contains(
            callees,
            m => m.DeclaringType == typeof(PathKey) && m.Name == nameof(PathKey.ForNormalized)
        );
        Assert.DoesNotContain(
            callees,
            m => m.DeclaringType == typeof(PathKey) && m.Name == nameof(PathKey.For)
        );
        // PathKey を経由しない直接呼び(Path.GetFullPath / Path.GetLongPathName 相当)も塞ぐ。
        Assert.DoesNotContain(callees, m => m.DeclaringType == typeof(Path));
    }

    /// <summary>
    /// method の IL から <c>call</c> / <c>callvirt</c> の対象として解決できたメソッドを集める。
    /// オペランドを誤読した偽陽性はメタデータテーブル種別(MethodDef / MemberRef / MethodSpec)と
    /// 解決可否で捨てる。残る偽陽性は「呼んでいないものが混ざる」方向にしか働かないので、
    /// 「呼んでいない」の assert が偽陽性で<b>緑になることはない</b>
    /// (逆に、将来の本体変更で偽陽性が当たれば赤で気付ける)。
    /// </summary>
    private static List<MethodBase> CalleesOf(MethodInfo method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        var typeArgs = method.DeclaringType!.GetGenericArguments();
        var methodArgs = method.GetGenericArguments();
        var result = new List<MethodBase>();
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F) // call / callvirt(いずれも 4 バイトのトークンを伴う)
                continue;
            int token = BitConverter.ToInt32(il, i + 1);
            byte table = (byte)((uint)token >> 24);
            if (table != 0x06 && table != 0x0A && table != 0x2B) // MethodDef/MemberRef/MethodSpec
                continue;
            try
            {
                var m = method.Module.ResolveMethod(token, typeArgs, methodArgs);
                if (m is not null)
                    result.Add(m);
            }
            catch (Exception e) when (e is ArgumentException or BadImageFormatException)
            {
                // 解決できないトークン=オペランドの誤読。呼出ではないので捨てる。
            }
        }
        return result;
    }

    [Fact]
    public void Max_zero_or_negative_returns_empty()
    {
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", 0));
        Assert.Empty(RecentFilesList.Add(new[] { @"C:\a.txt" }, @"C:\b.txt", -1));
    }

    [Fact]
    public void Empty_current_yields_single() =>
        Assert.Equal(
            new[] { @"C:\a.txt" },
            RecentFilesList.Add(System.Array.Empty<string>(), @"C:\a.txt", 10)
        );

    // CSV-L-4: settings.json 側で 10 万件の RecentFiles を投入された場合、Deserialize は O(N) を避けられない
    // (System.Text.Json 側の仕様)が、Deserialize 直後に本ヘルパを通せば後段(Add / メニュー再構築 / 各所の走査)
    // を O(MaxItems) に固定できる。null 耐性も持たせ SettingsStore.Normalize と二重防御にする。
    [Fact]
    public void Truncate_caps_to_max_items()
    {
        var source = Enumerable.Range(0, 100_000).Select(i => $@"C:\a{i}.txt");
        var r = RecentFilesList.Truncate(source);
        Assert.Equal(RecentFilesList.MaxItems, r.Count);
        Assert.Equal(@"C:\a0.txt", r[0]);
        Assert.Equal($@"C:\a{RecentFilesList.MaxItems - 1}.txt", r[^1]);
    }

    [Fact]
    public void Truncate_short_list_is_unchanged()
    {
        var source = new[] { @"C:\a.txt", @"C:\b.txt", @"C:\c.txt", @"C:\d.txt", @"C:\e.txt" };
        var r = RecentFilesList.Truncate(source);
        Assert.Equal(source, r);
    }

    [Fact]
    public void Truncate_null_returns_empty_list()
    {
        var r = RecentFilesList.Truncate(null!);
        Assert.NotNull(r);
        Assert.Empty(r);
    }

    // 定数値の pin: FileController から参照される single-source-of-truth を回帰保護する。
    [Fact]
    public void MaxItems_is_10() => Assert.Equal(10, RecentFilesList.MaxItems);
}
