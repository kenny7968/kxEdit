# バックアップ復元時に初期無題タブを閉じる Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** OFF 経路(`RestoreOpenFilesOnStartup=false`)+バックアップ有効+異常終了で復元されたとき、起動時の空無題1 タブを ON 経路と同様に自動で閉じる。

**Architecture:**
- `BackupCoordinator.OfferRestoreOnStartup` の返り値を 4 分岐全てで実復元件数に統一(現状は確認 ON+Restore のみ常に 0)。
- `MainForm.OnShown` OFF 経路で `restored > 0` なら `_startupEmptyDoc` を `TryClose`。ON 経路(`FileController.RestoreSession` :660-661)と対称。
- Announcer は `!ConfirmRestoreOnStartup` ゲートで従来無音を維持(SR ユーザーの体感を変えない)。

**Tech Stack:** C# / WinForms / xUnit(Sta.Run + PumpUntilShown で OnShown を発火)。既存の `FileForTest.DocsForTest` 経由でタブ列を観測、`form.Controls.OfType<Label>().Single(l => l.AccessibleName == "通知")` で announcer 発話を観測。

**Reference:** [design doc](./2026-07-24-restore-no-initial-untitled-design.md)

---

## Task 1: BackupCoordinator の返り値対称化(TDD)

**Files:**
- Test: `tests/yEdit.App.Tests/BackupCoordinatorTests.cs`(既存に追加)
- Modify: `src/yEdit.App/BackupCoordinator.cs:230-263`(Restore 分岐にカウンタ・末尾 return 0 を分岐内へ移動)

### Step 1: 3 件の失敗テストを追加

`tests/yEdit.App.Tests/BackupCoordinatorTests.cs` の `// ===== OfferRestoreOnStartup(4 分岐) =====` セクション内(既存の `OfferRestoreOnStartup_ConfirmTrue_*` テスト群と隣接する位置)に追記:

```csharp
[Fact]
public void OfferRestoreOnStartup_ConfirmTrue_Restore_ReturnsCountOfRestored() =>
    Sta.Run(() =>
    {
        // 設計 2026-07-24-restore-no-initial-untitled §1: 確認 ON+Restore 分岐の返り値が
        // 「常に 0」(現状)ではなく実復元件数になることを pin する。呼び出し側(MainForm)は
        // この値で _startupEmptyDoc の TryClose を判断する。
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("keep-a", "aaa"));
        PlantBackup(host.TempDir, Rec("keep-b", "bbb"));

        var recA = Rec("keep-a", "aaa");
        var recB = Rec("keep-b", "bbb");
        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { recA, recB });

        int calls = 0;
        int restored = host.Backup.OfferRestoreOnStartup(
            host.Form,
            r =>
            {
                calls++;
                var d = host.Docs.CreateNew();
                d.Editor.Text = r.Content ?? "";
                d.Editor.ClearSavePoint();
                return d;
            },
            confirm: true
        );

        Assert.Equal(2, calls);
        Assert.Equal(2, restored); // 現状: 常に 0(:263 の末尾 return 0)→ 修正後: 実件数
    });

[Fact]
public void OfferRestoreOnStartup_ConfirmTrue_Restore_OneThrows_ReturnsSuccessCountOnly() =>
    Sta.Run(() =>
    {
        // per-record catch(:236-251)で 1 件の失敗が他を巻き添えにしない既存契約を保存しつつ、
        // 成功件数のみを返すことを pin する(失敗分は含まない)。
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("bad", "boom"));
        PlantBackup(host.TempDir, Rec("good", "ok"));

        var badRec = Rec("bad", "boom");
        var goodRec = Rec("good", "ok");
        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, new[] { badRec, goodRec });

        int restored = host.Backup.OfferRestoreOnStartup(
            host.Form,
            r =>
            {
                if (r.Id == HashId("bad"))
                    throw new InvalidOperationException("restore failed");
                var d = host.Docs.CreateNew();
                d.Editor.Text = r.Content ?? "";
                d.Editor.ClearSavePoint();
                return d;
            },
            confirm: true
        );

        Assert.Equal(1, restored); // good のみ成功
    });

[Fact]
public void OfferRestoreOnStartup_ConfirmTrue_RestoreEmpty_ReturnsZero() =>
    Sta.Run(() =>
    {
        // 現状(:263 の共通末尾 return 0)と修正後(分岐内 return restored)を同点で担保する
        // 対称テスト。outcome.Checked が空のときはループを一度も回らず 0 を返す=呼び出し側は
        // _startupEmptyDoc を閉じない(=ユーザーがダイアログでチェックを外して確定した意図の尊重)。
        using var host = new Host();
        PlantBackup(host.TempDir, Rec("orphan-1", "aaa"));

        host.Prompt.NextOutcome = new RestoreOutcome(RestoreAction.Restore, Array.Empty<BackupRecord>());

        int restored = host.Backup.OfferRestoreOnStartup(
            host.Form,
            r => throw new Xunit.Sdk.XunitException("restore must not be called"),
            confirm: true
        );

        Assert.Equal(0, restored);
    });
```

### Step 2: 失敗を確認

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OfferRestoreOnStartup_ConfirmTrue_Restore_"
```

期待: 3 件全て赤(`Assert.Equal(2, restored)` / `Assert.Equal(1, restored)` は現状 0 が返るため失敗、`Assert.Equal(0, restored)` は現状も 0 で通ってしまうが Step 4 の実装後も 0 で通ることを確認する対称テスト=Step 2 では 1 件成功+2 件失敗の状態でも OK)。

### Step 3: 実装(BackupCoordinator.cs :230-263 修正)

```csharp
// 変更前:
var outcome = _restorePrompt.Prompt(owner, ordered);
switch (outcome.Action)
{
    case RestoreAction.Restore:
        foreach (var rec in outcome.Checked)
        {
            try
            {
                var doc = restore(rec);
                AdoptRestored(doc, rec);
            }
            catch (Exception ex)
            {
                _trace.Warn("restore-item", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
            }
        }
        break;

    case RestoreAction.DiscardAll:
        _writer?.DeleteAll();
        break;

    case RestoreAction.Later:
        break;
}
return 0;
```

```csharp
// 変更後:
// 設計 2026-07-24-restore-no-initial-untitled §1: 確認 ON+Restore の返り値を実復元件数に
// 統一する(確認 OFF+Restore と対称)。呼び出し側(MainForm)はこの値で _startupEmptyDoc の
// TryClose を判断する。
var outcome = _restorePrompt.Prompt(owner, ordered);
switch (outcome.Action)
{
    case RestoreAction.Restore:
        int restored = 0;
        foreach (var rec in outcome.Checked)
        {
            try
            {
                var doc = restore(rec);
                AdoptRestored(doc, rec);
                restored++;
            }
            catch (Exception ex)
            {
                _trace.Warn("restore-item", SanitizeForDisplay.OneLine(rec.Id, 200), ex);
            }
        }
        return restored;

    case RestoreAction.DiscardAll:
        _writer?.DeleteAll();
        return 0;

    case RestoreAction.Later:
    default:
        return 0;
}
```

**注意**: `case RestoreAction.Restore:` 内で `int restored = 0;` を宣言するため、他 case との名前空間衝突は起きない(C# の switch case は各 case でスコープ独立)。既存の確認 OFF+Restore 分岐(:206 の `int restored = 0;`)とも別スコープ。

### Step 4: テスト緑化を確認

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OfferRestoreOnStartup"
```

期待: 新規 3 件 PASS + 既存の `OfferRestoreOnStartup_*` テスト群も全て PASS(戻り値を assert していない既存テストは影響なし)。

### Step 5: Commit

```
git add tests/yEdit.App.Tests/BackupCoordinatorTests.cs src/yEdit.App/BackupCoordinator.cs
git commit -m "feat(app): OfferRestoreOnStartup 返り値を 4 分岐で実復元件数に統一"
```

---

## Task 2: MainForm OFF 経路の TryClose + Announcer ゲート(TDD)

**Files:**
- Test: `tests/yEdit.App.Tests/MainFormSmokeTests.cs`(既存に追加)
- Modify: `src/yEdit.App/MainForm.cs:242-249`(OFF 経路)

### Step 1: 3 件の失敗テストを追加

`tests/yEdit.App.Tests/MainFormSmokeTests.cs` の `// ===== hot exit 統合: OnShown の silent 統合復元(設計 §3.3/§8) =====` セクションの後(または末尾の適切な位置)に追記。既存の `PlantBackup`/`Rec`/`NewId`/`ShowMainForm_Unified` を利用。

**注意**: 既存の `Rec(string id, string? path, int untitledNumber, string content)` は 4 引数版。ここでも同シグネチャで呼ぶ。

```csharp
// ===== OFF 経路: バックアップ復元時に初期無題タブを閉じる(2026-07-24 設計) =====

// 現状バグの回帰テスト: OFF+ConfirmRestore=false+バックアップ 1 件 →
// 復元後は「復元 doc 1 個のみ」(起動時の initialEmpty は自動的に閉じる)。
[Fact]
public void OnShown_UnifiedOff_ConfirmFalse_BackupRestored_ClosesInitialEmptyTab() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string bkId = NewId();
        PlantBackup(tmp, Rec(bkId, path: null, untitledNumber: 2, "restored-body"));

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.BackupEnabled = true;
        settings.RestoreOpenFilesOnStartup = false; // OFF 経路
        settings.ConfirmRestoreOnStartup = false; // silent 自動復元

        using var form = ShowMainForm_Unified(settings, tmp);

        var docs = form.FileForTest.DocsForTest;
        var doc = Assert.Single(docs); // ← 現状バグでは 2 件(初期無題1+復元)になる=修正で 1 件へ
        Assert.Null(doc.State.Path);
        Assert.Equal("restored-body", doc.Editor.SnapshotText);
        Assert.True(doc.Editor.Modified);
    });

// 復元 0 件のとき初期無題タブは残る(復元失敗時にユーザーの作業台=空タブを消さない不変)。
[Fact]
public void OnShown_UnifiedOff_NoBackups_KeepsInitialEmptyTab() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        // バックアップ 0 件

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.BackupEnabled = true;
        settings.RestoreOpenFilesOnStartup = false;
        settings.ConfirmRestoreOnStartup = false;

        using var form = ShowMainForm_Unified(settings, tmp);

        var docs = form.FileForTest.DocsForTest;
        var doc = Assert.Single(docs); // 初期無題1 が残る
        Assert.Null(doc.State.Path);
        Assert.False(doc.Editor.Modified); // 初期空タブは Modified=false
    });

// Announcer 挙動の対称化 pin: 確認 OFF+復元成功=発話・確認 ON+復元成功=無音(既存挙動維持)。
// 前者は「N 件復元しました」文字列を通知 Label で観測、後者は Label が空のままを観測。
[Fact]
public void OnShown_UnifiedOff_ConfirmFalse_AnnouncesRestoredCount() =>
    Sta.Run(() =>
    {
        using var tmp = new TempDir();
        string bkId = NewId();
        PlantBackup(tmp, Rec(bkId, path: null, untitledNumber: 2, "restored-body"));

        var settings = NewSettings(csvAutoModeOnOpen: false);
        settings.BackupEnabled = true;
        settings.RestoreOpenFilesOnStartup = false;
        settings.ConfirmRestoreOnStartup = false; // silent 自動復元=発話する経路

        using var form = ShowMainForm_Unified(settings, tmp);

        var announce = form.Controls.OfType<Label>().Single(l => l.AccessibleName == "通知");
        Assert.Contains("バックアップを 1 件復元しました", announce.Text);
    });
```

**確認 ON+復元成功で発話しないこと**の pin テストは既存の `OnShown_UnifiedOn_NoLayout_OrphanBackup_RestoredAsExtra_NoOfferDialog`(MainFormSmokeTests.cs:280)が近い形で担保しているが、そちらは ON 経路のケース。OFF+ConfirmTrue+復元成功のケースは追加せず(実装コードで `!_settings.ConfirmRestoreOnStartup` ゲートを明示することでコード自体を診断可能に保つ)。**理由**: OFF+ConfirmTrue+復元成功の smoke test は `RestoreDialog` のモーダル発火を回避するために `IRestoreDialogPrompt` seam を注入する必要があるが、MainForm はその seam を持たない=新規 seam 追加は本修正のスコープを超える。Announcer ゲート反転変異(=`!confirm` を `confirm` にする)は Task 3 の別エージェントレビューでコード変異ミューテーション検証時に指摘してもらう(スポットチェック対象)。

### Step 2: 失敗を確認

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OnShown_UnifiedOff_"
```

期待:
- `_ClosesInitialEmptyTab`: 現状は docs.Count=2(初期無題+復元)で赤化(`Assert.Single` が失敗)。
- `_KeepsInitialEmptyTab`: 現状も docs.Count=1(復元 0 件なので初期タブがそのまま)で通ってしまう可能性→ Step 4 実装後も通ることを確認する対称テスト。
- `_AnnouncesRestoredCount`: 現状は 1 件成功で `restored=1` を返し発話するため通ってしまう→ Task 1 修正前は確認 OFF 経路の返り値仕様は変わらないので、両方で通る対称テスト。

**注記**: 3 件のうち赤化するのは `_ClosesInitialEmptyTab` のみ(1/3)。他 2 件は Task 1+Task 2 いずれの状態でも通る対称/現状 pin テスト。TDD の red-green サイクルはこの 1 件で成立する。

### Step 3: 実装(MainForm.cs :242-249 修正)

```csharp
// 変更前:
// OFF: 従来どおり異常終了バックアップの復元提案のみ。
int restored = _backup.OfferRestoreOnStartup(
    this,
    _file.RestoreFromBackup,
    _settings.ConfirmRestoreOnStartup
);
if (restored > 0)
    _announcer.Say($"バックアップを {restored} 件復元しました");
```

```csharp
// 変更後:
// OFF: 従来どおり異常終了バックアップの復元提案のみ。
// 設計 2026-07-24-restore-no-initial-untitled §1: 復元件数>0 なら ON 経路
// (FileController.RestoreSession の openedCount>0 で initialEmpty を TryClose)と対称に
// 起動時の空無題タブ (_startupEmptyDoc) を閉じる。Announcer は従来どおり silent 自動復元
// (!ConfirmRestoreOnStartup) のときのみ発話する(確認 ON はダイアログで件数を既知)。
int restored = _backup.OfferRestoreOnStartup(
    this,
    _file.RestoreFromBackup,
    _settings.ConfirmRestoreOnStartup
);
if (restored > 0)
{
    if (_startupEmptyDoc is not null)
        _docs.TryClose(_startupEmptyDoc, _ => true); // ON 経路と同じ「空無題は無条件破棄」
    if (!_settings.ConfirmRestoreOnStartup)
        _announcer.Say($"バックアップを {restored} 件復元しました");
}
```

### Step 4: テスト緑化を確認

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OnShown_UnifiedOff_"
```

期待: 3 件全て PASS。既存の `OnShown_UnifiedOn_*` テスト群も全て PASS(ON 経路には触っていない)。

### Step 5: 全 App.Tests を実行

```
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj
```

期待: 全件 PASS(Task 1 の BackupCoordinator 変更+Task 2 の MainForm 変更を合わせても回帰なし)。既存の `_startupEmptyDoc` を触るテスト・既存の OfferRestoreOnStartup を触るテストの assertion に影響しないことを目視で確認。

### Step 6: Commit

```
git add tests/yEdit.App.Tests/MainFormSmokeTests.cs src/yEdit.App/MainForm.cs
git commit -m "fix(app): OFF 経路のバックアップ復元後に起動時無題タブを閉じる"
```

---

## Task 3: 品質ゲート+別エージェントレビュー(2 パス統合)

**Files:** N/A(検証のみ)

### Step 1: pre-merge-check EXIT 0 確認

```
pwsh tools/pre-merge-check.ps1
```

期待: EXIT 0。0 warning 維持(`-warnaserror` 稼働)+CI と同種のゲート通過。

### Step 2: 別エージェントレビュー(統合 2 パス=コード品質+脆弱性)

`superpowers:code-reviewer` 相当の別エージェントに以下の依頼を行う:

- **対象**: `feature/restore-no-initial-untitled` ブランチの main からの差分(設計書 commit 除く)。
- **設計**: `docs/plans/2026-07-24-restore-no-initial-untitled-design.md`(採用方針=案 A+Announcer は !confirm ゲート維持)。
- **観点(統合 2 パス)**:
  - **コード品質**: (a) ON 経路(`FileController.RestoreSession` の `openedCount>0 && initialEmpty is not null` で TryClose)との対称性が保たれているか。(b) `int restored` のスコープが case 内に閉じ、他 case へ leak しないか。(c) Announcer ゲート `!_settings.ConfirmRestoreOnStartup` の位置・条件が正しいか(returnedCount ゼロ経路との干渉なし)。(d) `_startupEmptyDoc` が null の場合の分岐が正しいか(既に閉じられた場合の TryClose 冪等性含む)。
  - **脆弱性**: 該当なし判定(設計書 §6)。ただしレビュー時に外部入力→復元件数の経路で予期しない副作用がないか二次確認する。
- **ミューテーション検証(スポットチェック)**:
  1. `OfferRestoreOnStartup` :234 相当の `restored++` 除去変異 → `OfferRestoreOnStartup_ConfirmTrue_Restore_ReturnsCountOfRestored` が赤化することを確認。
  2. `MainForm.OnShown` OFF 経路の `_startupEmptyDoc?.TryClose` 除去変異 → `OnShown_UnifiedOff_ConfirmFalse_BackupRestored_ClosesInitialEmptyTab` が赤化することを確認。
  3. Announcer の `!_settings.ConfirmRestoreOnStartup` ゲートを `_settings.ConfirmRestoreOnStartup` に反転変異 → 該当テストが存在すれば赤化(現状はコード読みでのみ確認・レビュアの目視 sweep)。

### Step 3: 指摘対応の 3 択判断

各指摘に対し ① fixup commit で修正 / ② PR description に記載して受容 / ③ 理由付き却下 を明示。

### Step 4: fixup 反映(必要なら)

```
git add <files>
git commit -m "fixup(app): <指摘の要約>"
```

---

## Task 4: PR 作成

**Files:** N/A(GitHub PR 作成のみ)

### Step 1: push+PR 作成

```
git push -u origin feature/restore-no-initial-untitled
gh pr create --title "fix(app): バックアップ復元時に起動時の空無題タブを閉じる" --body "$(cat <<'EOF'
## 概要

「起動時に前回開いていたファイルを開く」が **無効**、かつバックアップが **有効** な状態で異常終了した場合、
復元後に「起動時の空無題1 + 復元された無題1」の 2 つの同名タブが並び、保存確認ダイアログで
「無題1」がどちらを指すか識別できない問題を修正する。

ユーザー報告 (2026-07-24):
- 前提: 無題1(未保存)+ 無題2(未保存)+ abc.txt(既存・未保存変更あり)の 3 タブを異常終了
- 現状: 起動時に「起動時無題1 + 復元無題1 + 復元無題2 + 復元abc.txt」の 4 タブ
- 期待: 「復元無題1 + 復元無題2 + 復元abc.txt」の 3 タブ(起動時の空無題は消す)

## 修正

- `BackupCoordinator.OfferRestoreOnStartup` の返り値を 4 分岐全てで実復元件数に統一
  (従来: 確認 ON+Restore は常に 0 を返していた)。
- `MainForm.OnShown` OFF 経路で `restored > 0` なら `_startupEmptyDoc` を `TryClose`
  (ON 経路 `FileController.RestoreSession` の `openedCount>0 && initialEmpty is not null` と対称)。
- Announcer 発話は `!_settings.ConfirmRestoreOnStartup` ゲートで従来無音を維持
  (確認 ON はダイアログで既知のため再アナウンスしない)。

## レビュー経緯

- 設計書 `docs/plans/2026-07-24-restore-no-initial-untitled-design.md`
- 実装計画 `docs/plans/2026-07-24-restore-no-initial-untitled.md`
- 別エージェントによる 2 パス統合レビュー(コード品質+脆弱性)実施済み。
- ミューテーション検証スポットチェック: `restored++` 除去 / `TryClose` 除去 / Announcer ゲート反転。

## テスト

- L1/L2: 変更なし。
- L3: BackupCoordinatorTests に 3 件・MainFormSmokeTests に 3 件追加。
- L5: SR 経路変更なし=省略可判定。

## 申し送り

- 説明書「バックアップからの復元」節への一文追加(復元時に起動時の空タブは自動的に閉じる旨)は
  リリース時にたたき台をユーザー校閲前提で提示。
- OFF+ConfirmTrue+復元成功の Announcer 無音は本 PR で `!confirm` ゲートで明示化。将来
  発話有りに切り替える判断があれば設計書 §8 申し送りに追記。

EOF
)"
```

### Step 2: PR URL をユーザーに返す

---

## 依存関係

- Task 1 → Task 2:Task 1 で `OfferRestoreOnStartup` の返り値が実件数になっていないと、Task 2 の `_ClosesInitialEmptyTab` テストで確認 ON 経路も緑化させる際に一貫性が崩れる。Task 1 → Task 2 の順で進める。
- Task 3 は Task 1+Task 2 の commit 後に実施(diff レビューのため)。
- Task 4 は Task 3 の指摘対応後に push。

## 実装の総規模

- **本体コード**: BackupCoordinator.cs 約 8 行差分・MainForm.cs 約 4 行差分・合計 12 行程度。
- **テストコード**: BackupCoordinatorTests 3 件約 60 行+MainFormSmokeTests 3 件約 45 行・合計 105 行程度。
- **設計書**: 追加済み(166 行)。
- **CLAUDE.md §3 簡略化基準**: 該当(単一 commit まで縮められる規模だが、TDD の red-green サイクルを保つため 2 commit=2 タスクに分割)。

## 検証コマンド一覧

```
# 個別テスト実行
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OfferRestoreOnStartup_ConfirmTrue_Restore_"
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj --filter "FullyQualifiedName~OnShown_UnifiedOff_"

# 全 App.Tests
dotnet test tests/yEdit.App.Tests/yEdit.App.Tests.csproj

# 全ゲート(main マージ前必須)
pwsh tools/pre-merge-check.ps1
```
