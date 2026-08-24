# 「すべて破棄」が孤児バックアップを消さない(E-2)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 復元ダイアログの「すべて破棄」が、一覧に提示した孤児バックアップを実際に削除するようにする。

**Architecture:** 削除の単位を「dir」から「record」へ移す。Core に `BackupStore.DeleteByIds(baseDir, ids)`
を新設し、`LoadAll` と同じ範囲(flat + 全 `session-*`)を横断して指定 Id の `<Id>.json` だけを消す。
App 側は `IBackupWriter.DeleteAll()` を `DeleteAcrossSessions(baseDir, ids)` へ置換し、
`BackupCoordinator` が「提示した record の Id − 自セッションが保護中の Id」を渡す。

**Tech Stack:** .NET 8 / C# / xUnit / CSharpier(pre-commit)/ Husky.Net

**設計書:** `docs/plans/2026-08-24-discard-all-orphan-backups-design.md`(ブランチ `feature/discard-all-orphan-backups` の `ae68185`)

---

## 設計書からの精密化(実装時に確定した差分)

設計書 §3.1 は「id を `BackupIdValidator.IsValid` で検証し、不正なら skip」としていた。
実装では **`ids` をパスへ連結しない**方式に変える:

- 各 dir を `Directory.EnumerateFiles(dir, "*.json")` で列挙し、`Path.GetFileName` と
  `<id>.json` の**完全一致**(`OrdinalIgnoreCase`)で照合してから削除する。
- したがって `..\..\evil` のような Id は「どのファイル名にも一致しない」に落ちるだけで、
  `Path.Combine` 経由のトラバーサル入口が**構造的に存在しない**。ワイルドカード(`*`)も
  パターンではなく文字列等価で比較するため無効化される。
- `BackupIdValidator` 呼び出しは**置かない**。置いても観測可能な効果が無く
  (=どのテストでも殺せない行になる)、CLAUDE.md §4 の網の基準に反するため。
  `Write` / `Delete` の validator は「Id を `Path.Combine` へ流す」設計ゆえに必要な防御であり、
  ここでは同じ防御をより強い形(連結しない)で満たしている。

**この差分は PR description に記載すること**(設計書は策定時スナップショットなので書き換えない)。

---

## Task 1: Core — `BackupStore.DeleteByIds`

**Files:**
- Modify: `src/kxEdit.Core/Backup/BackupStore.cs`
- Test: `tests/kxEdit.Core.Tests/Backup/BackupStoreTests.cs`

**レビュー:** 仕様レビュー + **脆弱性レビュー**(CLAUDE.md §3.4 前倒し条件: パス操作・ファイル削除)

### Step 1: 失敗するテストを書く

`tests/kxEdit.Core.Tests/Backup/BackupStoreTests.cs` の末尾(クラス閉じ括弧の直前)へ追加する。
既存の `TempDir` / `HashId` / `Rec(label, path, content)` ヘルパをそのまま使う。

```csharp
    // ===== E-2: DeleteByIds(復元ダイアログ「すべて破棄」の実体) =====
    // 一覧(LoadAll)は flat + 全 session-* を集めるのに、削除(DeleteSessionDir)が自セッション
    // dir 限定だったため「すべて破棄」が事実上 no-op だった(監査 E-2)。DeleteByIds は削除範囲を
    // 「ユーザーに提示した Id」へ合わせ、一覧と削除の範囲を一致させる。

    [Fact]
    public void DeleteByIds_RemovesFlatRecord()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("flat", null, "flat-content"));

        int deleted = BackupStore.DeleteByIds(t.Root, new[] { HashId("flat") });

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(t.Root, HashId("flat") + ".json")));
    }

    [Fact]
    public void DeleteByIds_RemovesRecordInOtherSessionDir()
    {
        using var t = new TempDir();
        // 前回クラッシュ由来の孤児 session dir(自セッションではない=E-2 で消えなかった側)。
        var orphan = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(orphan, Rec("orphan", null, "orphan-content"));

        int deleted = BackupStore.DeleteByIds(t.Root, new[] { HashId("orphan") });

        Assert.Equal(1, deleted);
        Assert.Empty(BackupStore.LoadAll(t.Root)); // 次回起動で再提案されない
    }

    [Fact]
    public void DeleteByIds_KeepsRecordsNotListed()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(dir, Rec("offered", null, "offered-content"));
        // 一覧に出していない = ダイアログ表示後に他インスタンスが書いたライブ backup 相当。
        BackupStore.Write(dir, Rec("not-offered", null, "live-content"));

        int deleted = BackupStore.DeleteByIds(t.Root, new[] { HashId("offered") });

        Assert.Equal(1, deleted);
        Assert.Equal("live-content", Assert.Single(BackupStore.LoadAll(t.Root)).Content);
    }

    [Fact]
    public void DeleteByIds_RemovesEmptiedSessionDir_WithTmpResiduals()
    {
        using var t = new TempDir();
        var orphan = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(orphan, Rec("orphan", null, "orphan-content"));
        // 書込中クラッシュで残った平文の部分本文。dir が空になるなら一緒に消す。
        File.WriteAllText(Path.Combine(orphan, "residual.tmp"), "partial plaintext");

        BackupStore.DeleteByIds(t.Root, new[] { HashId("orphan") });

        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void DeleteByIds_KeepsSessionDirAndTmp_WhenJsonRemains()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(dir, Rec("offered", null, "offered-content"));
        BackupStore.Write(dir, Rec("not-offered", null, "live-content"));
        string tmp = Path.Combine(dir, "inflight.tmp");
        File.WriteAllText(tmp, "in-flight write of a live instance");

        BackupStore.DeleteByIds(t.Root, new[] { HashId("offered") });

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(tmp)); // 書込中の別インスタンスを壊さない
    }

    [Fact]
    public void DeleteByIds_DoesNotTouchSessionDirsWithoutTargets()
    {
        using var t = new TempDir();
        var target = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        var untouched = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(target, Rec("orphan", null, "orphan-content"));
        // *.json をまだ一つも持たない dir(=他インスタンスの初回書込中)。*.json が無いことを
        // 根拠に掃除すると、この tmp を壊してしまう。掃除は「削除が発生した dir」限定であること。
        Directory.CreateDirectory(untouched);
        string tmp = Path.Combine(untouched, "inflight.tmp");
        File.WriteAllText(tmp, "first write of a live instance (no .json yet)");

        BackupStore.DeleteByIds(t.Root, new[] { HashId("orphan") });

        Assert.False(Directory.Exists(target)); // 対象を含んだ dir は消える
        Assert.True(File.Exists(tmp)); // 含まない dir には触れない
    }

    [Fact]
    public void DeleteByIds_RemovesDuplicateIdFromBothLocations()
    {
        using var t = new TempDir();
        var session = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        // adopt-move 失敗の残骸で同一 Id が 2 箇所に居ることがある(TryMoveToSessionDir の xmldoc)。
        BackupStore.Write(t.Root, Rec("dup", null, "flat-copy"));
        BackupStore.Write(session, Rec("dup", null, "session-copy"));

        int deleted = BackupStore.DeleteByIds(t.Root, new[] { HashId("dup") });

        Assert.Equal(2, deleted);
        Assert.Empty(BackupStore.LoadAll(t.Root));
    }

    // 以下 2 本は「将来の実装変更に対するアンカー」。現行実装(パスへ連結せずファイル名の
    // 完全一致で照合)では自明に緑だが、Path.Combine 方式へ書き換えられた瞬間に red 化する。
    [Fact]
    public void DeleteByIds_InvalidId_DoesNotThrow_AndKeepsDeletingOthers()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("good", null, "good-content"));

        int deleted = 0;
        var ex = Record.Exception(
            () => deleted = BackupStore.DeleteByIds(t.Root, new[] { @"..\..\evil", HashId("good") })
        );

        // Write / Delete は ArgumentException を投げる契約だが、一括削除で 1 件の不正が
        // 全破棄を巻き添えにするのは安全側ではない。
        Assert.Null(ex);
        Assert.Equal(1, deleted);
    }

    [Fact]
    public void DeleteByIds_InvalidId_CannotEscapeSearchedDirectories()
    {
        using var t = new TempDir();
        var session = Path.Combine(t.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(session, Rec("orphan", null, "orphan-content"));
        string victim = Path.Combine(t.Root, "victim.json");
        File.WriteAllText(victim, "must survive");

        BackupStore.DeleteByIds(t.Root, new[] { @"..\victim", HashId("orphan") });

        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void DeleteByIds_OnMissingBaseDir_IsHarmless()
    {
        using var t = new TempDir();
        var missing = Path.Combine(t.Root, "does-not-exist");

        int deleted = -1;
        var ex = Record.Exception(
            () => deleted = BackupStore.DeleteByIds(missing, new[] { HashId("x") })
        );

        Assert.Null(ex);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void DeleteByIds_NeverDeletesBaseDirItself()
    {
        using var t = new TempDir();
        BackupStore.Write(t.Root, Rec("flat", null, "flat-content"));

        BackupStore.DeleteByIds(t.Root, new[] { HashId("flat") });

        Assert.True(Directory.Exists(t.Root)); // flat が空になっても base dir は残す
    }
```

### Step 2: テストが「コンパイルできない」ことを確認する

```
dotnet build tests/kxEdit.Core.Tests -c Release
```

Expected: `CS0117: 'BackupStore' に 'DeleteByIds' の定義が含まれていません` で FAIL。

### Step 3: `TryDelete` を bool 返しにする

`src/kxEdit.Core/Backup/BackupStore.cs` の末尾 `TryDelete` を置き換える(既存 4 箇所の
呼び出し元は戻り値を捨てる=式文のままで変更不要)。

```csharp
    /// <summary>ファイルを削除し、実際に削除できたら true(<see cref="DeleteByIds"/> の件数集計用)。
    /// 失敗・不在は false。例外は握り潰す(残骸は実害小)。</summary>
    private static bool TryDelete(string p)
    {
        try
        {
            if (!File.Exists(p))
                return false;
            File.Delete(p);
            return true;
        }
        catch
        { /* 残骸は実害小 */
            return false;
        }
    }
```

### Step 4: `DeleteByIds` を実装する

`SweepOldSessions` の直前(= `DeleteSessionDir` の直後)へ挿入する。

```csharp
    /// <summary>E-2: 指定した Id 群のバックアップを <paramref name="baseDir"/> **全体を横断して**
    /// 削除する(復元ダイアログ「すべて破棄」の実体)。探索範囲は <see cref="LoadAll"/> と同じ
    /// = <paramref name="baseDir"/> 直下(flat 後方互換)+ 配下の <c>session-*</c> 全部。
    /// BK-M-2 の <see cref="DeleteSessionDir(string)"/> は自セッション dir しか消さないため、
    /// LoadAll が提示した孤児が一件も消えず毎回再提案されていた(監査 E-2)。
    ///
    /// <paramref name="ids"/> は **パスへ連結しない**。各 dir を列挙して得たファイル名と
    /// <c>&lt;id&gt;.json</c> の完全一致(<see cref="StringComparer.OrdinalIgnoreCase"/>)で
    /// 照合してから削除するため、悪意ある Id(<c>..\..\evil</c>・ワイルドカード)は
    /// 「どれにも一致しない」に落ちるだけで、Path.Combine 経由のトラバーサル入口が構造的に
    /// 存在しない(<see cref="Write"/> / <see cref="Delete"/> の <see cref="BackupIdValidator"/>
    /// 検証は Id を Path.Combine へ流す設計ゆえに必要な防御であり、ここでは同じ防御を
    /// より強い形で満たしている)。
    ///
    /// 掃除の範囲: **実際に削除が発生した <c>session-*</c> dir** に <c>*.json</c> が残らなければ、
    /// その dir の <c>*.tmp</c>(書込中残骸=平文の部分本文)も消して dir 自体を削除する。
    /// 削除対象を含まなかった dir には一切触れない(他インスタンスが書込中の <c>*.tmp</c> を
    /// 壊さないため)。<paramref name="baseDir"/> 自体とその直下の <c>*.tmp</c> は対象外
    /// (後者は起動時の <see cref="SweepTempFiles(string)"/> が担当)。
    /// 失敗は握り潰す(残骸は次回起動の 30 日 sweep で回収)。戻り値 = 実際に削除した件数。</summary>
    public static int DeleteByIds(string baseDir, IReadOnlyCollection<string> ids)
    {
        if (!Directory.Exists(baseDir))
            return 0;

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
            targets.Add(id + ".json");

        // flat 配置(v0.3.0-sec 由来)。baseDir 自体は消さない。
        int deleted = DeleteTargetsIn(baseDir, targets);

        foreach (string sub in Directory.EnumerateDirectories(baseDir, "session-*"))
        {
            int n = DeleteTargetsIn(sub, targets);
            if (n == 0)
                continue; // 破棄対象を含まなかった dir には触れない(他インスタンスの書込中 *.tmp 保護)
            deleted += n;
            if (!Directory.EnumerateFiles(sub, "*.json").Any())
            {
                SweepTempFiles(sub);
                TryDeleteEmptySessionDir(sub);
            }
        }
        return deleted;
    }

    /// <summary><paramref name="dir"/> 直下の <c>*.json</c> のうち、ファイル名が
    /// <paramref name="fileNames"/> と完全一致するものを削除し、実際に消えた件数を返す
    /// (<see cref="DeleteByIds"/> の内部)。</summary>
    private static int DeleteTargetsIn(string dir, HashSet<string> fileNames)
    {
        int n = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            if (fileNames.Contains(Path.GetFileName(file)) && TryDelete(file))
                n++;
        return n;
    }
```

### Step 5: 陳腐化した xmldoc を直す

`SerialBackupWriter.DeleteAll` は Task 2 で消えるため、それを参照している 3 箇所を更新する。

1. `LoadAll` の xmldoc(`BackupStore.cs`)— 次の行を置換:

```
    /// (削除は <see cref="DeleteSessionDir(string)"/> 経由で自セッション限定に切り替える)。
```

```
    /// (BK-M-2 では削除を <see cref="DeleteSessionDir(string)"/> で自セッション限定に切り替えたが、
    /// それでは提示した孤児が一件も消えなかった=E-2。現在「すべて破棄」は
    /// <see cref="DeleteByIds(string, IReadOnlyCollection{string})"/> 経由で
    /// 「提示した Id」限定=一覧と削除の範囲が一致する)。
```

2. `DeleteAll(string dir)` の xmldoc — 次の行を置換:

```
    /// BK-M-2 以降、SerialBackupWriter.DeleteAll は代わりに <see cref="DeleteSessionDir(string)"/> を
    /// 呼ぶが、本メソッドは flat 後方互換の呼び出し元(将来の import 経路等)のために残す。</summary>
```

```
    /// E-2 以降「すべて破棄」は <see cref="DeleteByIds(string, IReadOnlyCollection{string})"/> が担う。
    /// 本メソッドは flat 後方互換の呼び出し元(将来の import 経路等)のために残す
    /// (現在 src 側からの呼び出し元は無い)。</summary>
```

3. `DeleteSessionDir` の xmldoc — 次の行を置換:

```
    /// (SerialBackupWriter.DeleteAll の実体)。失敗は握り潰す(残骸は次回起動の 30 日 sweep で回収)。</summary>
```

```
    /// (BK-M-2 では SerialBackupWriter.DeleteAll の実体だった。E-2 で「すべて破棄」は
    /// <see cref="DeleteByIds(string, IReadOnlyCollection{string})"/> へ移り、現在 src 側からの
    /// 呼び出し元は無い)。失敗は握り潰す(残骸は次回起動の 30 日 sweep で回収)。</summary>
```

### Step 6: テストが通ることを確認する

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.Core.Tests -c Release --no-build --filter FullyQualifiedName~DeleteByIds
```

Expected: build 0 warning、11 tests PASS。

もし `TryDelete` の戻り値破棄で警告が出た場合(CA1806 等)は、既存 4 箇所の呼び出しを
`_ = TryDelete(...);` へ変える。**警告を抑止設定で消さない**。

### Step 7: Core 全体の回帰を確認する

```
dotnet test tests/kxEdit.Core.Tests -c Release --no-build
```

Expected: 全 PASS(既存の `DeleteSessionDir_*` / `SweepOldSessions_*` は無変更で緑のまま)。

### Step 8: commit

```bash
git add src/kxEdit.Core/Backup/BackupStore.cs tests/kxEdit.Core.Tests/Backup/BackupStoreTests.cs
git commit -F - <<'EOF'
feat(core): 指定 Id のバックアップを base dir 横断で削除する DeleteByIds を追加(E-2)

LoadAll は flat + 全 session-* を集めるのに DeleteSessionDir は自セッション dir
しか消さないため、「すべて破棄」で提示した孤児が一件も消えなかった。削除範囲を
「提示した Id」に合わせる Core API を追加する。ids はパスへ連結せず、列挙した
ファイル名との完全一致で照合するためトラバーサル入口を持たない。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

### Step 9: レビュー(仕様 + 脆弱性)

別エージェントを 2 つ起動する(混載しない)。

- 仕様レビュー: 設計書 §3.1 / §4 と実装・テストの一致。特に「削除が発生した dir だけ掃除する」
  条件と、`LoadAll` の探索範囲との一致。
- 脆弱性レビュー: パストラバーサル(Id がパスへ流れないこと)・TOCTOU(列挙中の削除)・
  他インスタンスの `*.tmp` 破壊・例外による中断で部分削除が起きたときの残留。

指摘は 3 択(fixup / PR 記載で受容 / 理由付き却下)で明示する。

---

## Task 2: App — 「すべて破棄」を新 API へ配線する

**Files:**
- Modify: `src/kxEdit.App/Abstractions/IBackupWriter.cs:22`
- Modify: `src/kxEdit.App/SerialBackupWriter.cs:14-15, 68-78`
- Modify: `src/kxEdit.App/BackupCoordinator.cs:289-290`
- Modify: `tests/kxEdit.App.Tests/Fakes/FakeBackupWriter.cs:21-22, 68-72`
- Modify: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs:40-72, 939-975`
- Modify: `tests/kxEdit.App.Tests/SerialBackupWriterTests.cs:109-127, 285`

**レビュー:** 仕様レビュー

### Step 1: `IBackupWriter.DeleteAll()` を置き換える

`src/kxEdit.App/Abstractions/IBackupWriter.cs` の `void DeleteAll();` を次で置換:

```csharp
    /// <summary>E-2: 復元ダイアログ「すべて破棄」の実体。<paramref name="ids"/> のバックアップを
    /// <paramref name="baseDir"/> 配下(flat + 全 <c>session-*</c>)を横断して削除する。
    ///
    /// <see cref="Write"/> / <see cref="Delete"/> が ctor で受けた自セッション dir に束縛されるのに対し、
    /// 本 API は**意図的にそのスコープを外れる**(名前で明示している)。旧 <c>DeleteAll()</c> は
    /// 自セッション dir だけを消していたため、提示した孤児が一件も消えなかった。
    ///
    /// 契約: 呼び出し側は「**ユーザーに提示した record の Id**」だけを渡すこと。一覧に出していない
    /// Id を渡すと、同時起動している別インスタンスのライブバックアップを消し得る。
    /// <paramref name="ids"/> は背景スレッドが後で読むため、呼び出し側で不変のスナップショットを渡す。</summary>
    void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids);
```

### Step 2: `SerialBackupWriter` を追従させる

クラス xmldoc の該当行(`SerialBackupWriter.cs:14-15`):

```
/// (<c>%APPDATA%\kxEdit\backups\session-{Guid.N}\</c>)。<see cref="DeleteAll"/> は
/// <see cref="BackupStore.DeleteSessionDir(string)"/> 経由で自セッション dir のみを掃除する
/// ため、他インスタンスのライブは無傷。base dir 側の LoadAll / SweepOldSessions は
```

を次で置換:

```
/// (<c>%APPDATA%\kxEdit\backups\session-{Guid.N}\</c>)。<see cref="Write"/> / <see cref="Delete"/> は
/// この dir に束縛される。「すべて破棄」だけは例外で、<see cref="DeleteAcrossSessions"/> が
/// 引数の base dir を横断する(E-2)。base dir 側の LoadAll / SweepOldSessions は
```

`DeleteAll()`(`:68-78`)を次で置換:

```csharp
    public void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids) =>
        Enqueue(() =>
        {
            try
            {
                // E-2: 自セッション dir(_dir)ではなく引数の base dir を横断する。
                // BK-M-2 の DeleteSessionDir では、一覧に出した孤児が一件も消えなかった。
                BackupStore.DeleteByIds(baseDir, ids);
            }
            catch
            { /* 一括削除失敗は致命でない・無音 */
            }
        });
```

`Enqueue` の xmldoc にある「Write/Delete/DeleteAll/WriteLayout/DeleteLayout の 5 箇所」は
「Write/Delete/DeleteAcrossSessions/WriteLayout/DeleteLayout の 5 箇所」へ直す。

### Step 3: `BackupCoordinator` の DiscardAll 分岐を置き換える

`src/kxEdit.App/BackupCoordinator.cs:289-290`:

```csharp
            case RestoreAction.DiscardAll:
                _writer?.DeleteAll();
                return 0;
```

を次で置換:

```csharp
            case RestoreAction.DiscardAll:
                // E-2: 自セッション dir ではなく base dir を横断し、提示した record を実削除する。
                // `?.` は引数式も短絡するため、writer 未生成時に集合を組み立てない。
                _writer?.DeleteAcrossSessions(_dir, DiscardTargets(ordered));
                return 0;
```

さらに `OfferRestoreOnStartup` の直後(`LoadAllForRestore` の直前)へヘルパを追加:

```csharp
    /// <summary>E-2: 「すべて破棄」で実削除する Id を決める。提示した record から、
    /// **自セッションが現在保護中**の Id を除く。
    ///
    /// 除外の理由: 実ファイルだけ消えても <see cref="_map"/> は <c>HasBackup=true</c> のまま残るため、
    /// 次に内容が変わるまで再書込が走らず無保護窓ができる(A-1 / M-31 で潰したのと同型)。
    /// ダイアログ表示中に自分が書いた分は LoadAll 時点で存在せず元から対象外なので、
    /// ここで守るのは「LoadAll の直前に Reconcile が走って書かれた分」。
    ///
    /// 戻り値は背景スレッドへ渡すため、呼び出し時点で確定した独立リストにする。</summary>
    private List<string> DiscardTargets(IReadOnlyList<BackupRecord> offered)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in _map.Values)
            if (info.HasBackup)
                live.Add(info.Id);

        var ids = new List<string>(offered.Count);
        foreach (var rec in offered)
            if (!live.Contains(rec.Id))
                ids.Add(rec.Id);
        return ids;
    }
```

`OfferRestoreOnStartup` の xmldoc の「明示的に消すのは「すべて破棄」のみ」の直後に一文足す:

```
    /// (E-2: 「すべて破棄」は提示した record を base dir 横断で実削除する。一覧に出していない
    /// バックアップ=表示後に他インスタンスが書いた分・自セッションが保護中の分は消さない。)
```

### Step 4: ビルドして「Fake が interface を満たさない」ことを確認する

```
dotnet build kxEdit.sln -c Release
```

Expected: `CS0535: 'FakeBackupWriter' は interface メンバー 'IBackupWriter.DeleteAcrossSessions' を実装しません` で FAIL。

### Step 5: `FakeBackupWriter` を更新する

`DeleteAllCount` の宣言(`:21-22`)を次で置換:

```csharp
    /// <summary>E-2: DeleteAcrossSessions の呼び出し回数(旧 DeleteAllCount)。</summary>
    public int DiscardCalls;

    /// <summary>E-2: DeleteAcrossSessions に渡された base dir(最後の 1 回)。
    /// 自セッション dir を渡す退行(=E-2 そのもの)を検出する証人。</summary>
    public string? LastDiscardBaseDir;

    /// <summary>E-2: DeleteAcrossSessions に渡された Id 群(最後の 1 回)。件数だけの assert では
    /// 「どの Id を渡したか」の変異が生き残るため中身を保持する。</summary>
    public List<string> LastDiscardIds { get; } = new();
```

`DeleteAll()`(`:68-72`)を次で置換:

```csharp
    public void DeleteAcrossSessions(string baseDir, IReadOnlyList<string> ids)
    {
        DiscardCalls++;
        LastDiscardBaseDir = baseDir;
        LastDiscardIds.Clear();
        LastDiscardIds.AddRange(ids);
        // 旧 DeleteAll は Store.Clear() だったが、実装は「渡された Id だけ」を消す。
        foreach (string id in ids)
            Store.Remove(id);
    }
```

### Step 6: `Host` に writer factory の差替口を足す

`tests/kxEdit.App.Tests/BackupCoordinatorTests.cs` の `Host` ctor 引数へ 1 つ追加する:

```csharp
        /// <param name="writerFactory">null(既定)なら共有の <see cref="Writer"/>(Fake)を返す。
        /// E-2 の統合テストだけが実 SerialBackupWriter を注入する。</param>
        public Host(
            bool enabled = true,
            int intervalSeconds = 30,
            int? maxBackupCharsOverride = null,
            bool restoreSessionEnabled = false,
            Func<string, IBackupWriter>? writerFactory = null
        )
```

ctor 内の factory ラムダを次で置換:

```csharp
                sessionDir =>
                {
                    WriterFactoryCalls++;
                    CapturedSessionDir = sessionDir;
                    return writerFactory is null ? Writer : writerFactory(sessionDir);
                },
```

### Step 7: App のテストを更新・追加する

`BackupCoordinatorTests.cs:939-957` の `OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll`
を次の 3 本で置換する。

```csharp
    [Fact]
    public void OfferRestore_ConfirmTrue_DiscardAll_PassesOfferedIdsAndBaseDir() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            PlantBackup(host.TempDir, Rec("r1", "one")); // flat 後方互換
            var orphanDir = Path.Combine(host.TempDir, "session-" + Guid.NewGuid().ToString("N"));
            PlantBackup(orphanDir, Rec("r2", "two")); // 前回クラッシュ由来の孤児
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            );

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(1, host.Writer.DiscardCalls);
            // E-2 の核心: 自セッション dir ではなく base dir を渡す。
            Assert.Equal(host.TempDir, host.Writer.LastDiscardBaseDir);
            Assert.NotEqual(host.CapturedSessionDir, host.Writer.LastDiscardBaseDir);
            // 一覧に出した全件(flat + 孤児 session-*)を渡す。
            Assert.Equal(2, host.Writer.LastDiscardIds.Count);
            Assert.Contains(HashId("r1"), host.Writer.LastDiscardIds);
            Assert.Contains(HashId("r2"), host.Writer.LastDiscardIds);
        });

    [Fact]
    public void OfferRestore_DiscardAll_WithRealWriter_DeletesOrphanFilesOnDisk() =>
        Sta.Run(() =>
        {
            // E-2 の本命。Fake writer は in-memory で「どの dir を消したか」を持たないため、
            // 自セッション dir だけを消していた欠陥を旧テストは検出できなかった
            // (OfferRestore_ConfirmTrue_DiscardAll_InvokesWriterDeleteAll は E-2 の存在下で緑)。
            // 実 SerialBackupWriter + 実ディスクで「消えること」自体を固定する。
            SerialBackupWriter? real = null;
            using var host = new Host(writerFactory: dir => real = new SerialBackupWriter(dir));
            var orphanDir = Path.Combine(host.TempDir, "session-" + Guid.NewGuid().ToString("N"));
            PlantBackup(orphanDir, Rec("orphan", "前回セッションの未保存本文"));
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            );

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.NotNull(real);
            // 背景直列ワーカーの末尾バリアで完了を確定させる(Sleep もリトライも使わない)。
            Assert.True(real!.WaitForPendingJobs(TimeSpan.FromSeconds(10)));
            Assert.Empty(BackupStore.LoadAll(host.TempDir)); // 次回起動で再提案されない
            Assert.False(Directory.Exists(orphanDir)); // 平文本文が dir ごと消える
        });

    [Fact]
    public void OfferRestore_DiscardAll_ExcludesIdsProtectedByThisSession() =>
        Sta.Run(() =>
        {
            // ダイアログ表示直前の Reconcile で自セッションが書いた backup は、一覧に載っていても
            // 消さない(消すと _map は HasBackup=true のまま実体だけ消え、次に内容が変わるまで
            // 無保護窓ができる=A-1 / M-31 と同型)。
            using var host = new Host();
            host.NewDoc("自セッションの未保存本文");
            host.Backup.Reconcile();
            string mine = Assert.Single(host.Writer.Writes).Id;
            // Fake は書かないので、LoadAll が同じ Id を提示する状況を実ファイルで作る
            // (これが無いと「除外できている」assertion が空虚になる)。
            PlantBackup(host.CapturedSessionDir!, Rec("mine", "自セッションの未保存本文") with
            {
                Id = mine,
            });
            var orphanDir = Path.Combine(host.TempDir, "session-" + Guid.NewGuid().ToString("N"));
            PlantBackup(orphanDir, Rec("orphan", "前回の本文"));
            host.Prompt.NextOutcome = new RestoreOutcome(
                RestoreAction.DiscardAll,
                Array.Empty<BackupRecord>()
            );

            host.Backup.OfferRestoreOnStartup(
                host.Form,
                r => throw new Xunit.Sdk.XunitException("restore must not be called"),
                confirm: true
            );

            Assert.Equal(1, host.Writer.DiscardCalls);
            Assert.Contains(HashId("orphan"), host.Writer.LastDiscardIds);
            Assert.DoesNotContain(mine, host.Writer.LastDiscardIds);
        });
```

`OfferRestore_ConfirmTrue_Later_DoesNothing`(`:973`)の `DeleteAllCount` を `DiscardCalls` へ直す:

```csharp
            Assert.Equal(0, host.Writer.DiscardCalls);
```

### Step 8: `SerialBackupWriterTests` を更新する

`DeleteAll_RemovesEverything`(`:109-127`)を次で置換:

```csharp
    /// <summary>
    /// DeleteAcrossSessions ジョブが投入順に BackupStore.DeleteByIds に到達し、
    /// **ctor で受けた自セッション dir の外**(flat + 他 session-*)の指定 Id まで実削除する。
    /// 責務=「復元ダイアログの『すべて破棄』分岐」に対する統合パイプ担保(E-2)。
    /// </summary>
    [Fact]
    public void DeleteAcrossSessions_RemovesListedRecordsOutsideOwnSessionDir()
    {
        using var tmp = new SbwTempDir();
        var own = Path.Combine(tmp.Root, "session-" + Guid.NewGuid().ToString("N"));
        var orphan = Path.Combine(tmp.Root, "session-" + Guid.NewGuid().ToString("N"));
        BackupStore.Write(tmp.Root, Rec("flat", "1")); // flat 後方互換
        BackupStore.Write(orphan, Rec("orphan", "2")); // 他セッション(孤児)
        BackupStore.Write(orphan, Rec("keep", "3")); // 一覧に出ていない=残る

        using (var w = new SerialBackupWriter(own))
        {
            w.DeleteAcrossSessions(tmp.Root, new[] { HashId("flat"), HashId("orphan") });
        } // Dispose で投入順に消化

        var left = BackupStore.LoadAll(tmp.Root);
        Assert.Equal("3", Assert.Single(left).Content);
    }
```

`Enqueue_AfterDispose_DoesNotPropagateException`(`:285`)の 1 行を直す:

```csharp
        var deleteAllEx = Record.Exception(
            () => w.DeleteAcrossSessions(tmp.Root, new[] { HashId("y") })
        );
```

### Step 9: ビルドとテスト

```
dotnet build kxEdit.sln -c Release -warnaserror
dotnet test tests/kxEdit.App.Tests -c Release --no-build
```

Expected: 0 warning、App 全 PASS。

### Step 10: commit

```bash
git add src/kxEdit.App tests/kxEdit.App.Tests
git commit -F - <<'EOF'
fix(app): 「すべて破棄」が提示した孤児バックアップを実削除するようにする(E-2)

IBackupWriter.DeleteAll() を DeleteAcrossSessions(baseDir, ids) へ置換し、
BackupCoordinator が「提示した record の Id − 自セッションが保護中の Id」を渡す。
自セッション dir 限定だった削除範囲が一覧(LoadAll)と一致し、孤児が毎回
再提案される問題と平文本文の 30 日残留が解消する。

Fake writer は「どの dir を消したか」を持たず欠陥を覆い隠していたため、
実 SerialBackupWriter + 実ディスクの統合テストを追加した。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

### Step 11: 仕様レビュー

別エージェントで実施。焦点: `_writer?.` の短絡と `DiscardTargets` の副作用有無・
Fake と実 writer の契約一致・新テストが空虚になっていないか(特に
`ExcludesIdsProtectedByThisSession` の実ファイル植え込み)。

---

## Task 3: 最終ブランチレビュー(2 パス)と統合

### Step 1: コード品質パス

別エージェントで実施。ミューテーション検証をスポットチェックで行う(設計書 §6.3):

| 変異 | 期待 |
|---|---|
| `DeleteByIds` の flat 削除行(`DeleteTargetsIn(baseDir, targets)`)を消す | `DeleteByIds_RemovesFlatRecord` が赤 |
| `EnumerateDirectories(baseDir, "session-*")` のループを消す | `DeleteByIds_RemovesRecordInOtherSessionDir` が赤 |
| `fileNames.Contains(...)` を `true` にする | `DeleteByIds_KeepsRecordsNotListed` が赤 |
| `SweepTempFiles(sub); TryDeleteEmptySessionDir(sub);` を消す | `DeleteByIds_RemovesEmptiedSessionDir_WithTmpResiduals` が赤 |
| `if (n == 0) continue;` を消す | `DeleteByIds_DoesNotTouchSessionDirsWithoutTargets` が赤 |
| `DiscardTargets` の `if (!live.Contains(rec.Id))` を無条件 add にする | `OfferRestore_DiscardAll_ExcludesIdsProtectedByThisSession` が赤 |
| `DeleteAcrossSessions(_dir, ...)` を `DeleteAcrossSessions(_sessionDir, ...)` にする(E-2 の再導入) | `PassesOfferedIdsAndBaseDir` と `WithRealWriter_DeletesOrphanFilesOnDisk` が赤 |

変異は必ず復元する。台帳は**エージェントの報告をそのまま写す**(要約して転記しない)。

### Step 2: 脆弱性パス

別エージェントで実施。焦点: パストラバーサル・TOCTOU・他インスタンスへの副作用・
削除失敗時の平文残留・`SanitizeForDisplay` を要する新しい表示/ログ面が無いこと。

### Step 3: 指摘の反映

3 択(fixup commit / PR 記載で受容 / 理由付き却下)で明示し、fixup は**別 commit** で積む。

### Step 4: 品質ゲート

```
pwsh -File tools/pre-merge-check.ps1
```

Expected: EXIT 0。

### Step 5: 手動スモーク(L5 の代替ではない)

1. kxEdit を起動 → 無題タブに何か入力(保存しない)。
2. タスクマネージャーで kxEdit を強制終了。
3. `%APPDATA%\kxEdit\backups` に `session-*\<guid>.json` が残っていることを確認。
4. kxEdit を再起動 → 復元ダイアログで「すべて破棄」。
5. `%APPDATA%\kxEdit\backups` から当該 `session-*` が消えていることを確認。
6. もう一度起動 → **復元ダイアログが出ない**ことを確認。

### Step 6: PR

```bash
git push -u origin feature/discard-all-orphan-backups
gh pr create --base main
```

PR description(日本語)に必ず含める:

- 目的(E-2)と、BK-M-2 の「一覧は広く・削除は狭く」を「一覧と削除の範囲を一致させる」へ変えたこと。
- **設計書からの精密化**: `BackupIdValidator` を使わず「パスへ連結しない」構造的防御にした理由。
- **受容したトレードオフ**(設計書 §5): 同時起動している別インスタンスのライブが一覧に載っていれば消える。
- **申し送り** S-E2-1(lock ファイルによる生存判定)/ S-E2-2(`*.tmp` だけの孤児 dir)。
- `BackupStore.DeleteAll` / `DeleteSessionDir` が src 未使用になったが、Core の store API として
  残置し xmldoc のみ更新したこと。
- L5 は SR 経路不変のため省略。代わりに Step 5 の手動スモーク結果を記載。
