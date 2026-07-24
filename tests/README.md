# yEdit テスト開発者向けガイド

新規テスト(特に Controller / Fake / Host フィクスチャ)を書く開発者(将来の Claude セッションを含む)のための正典。**戦略設計と Phase 実施記録は `docs/plans/2026-07-13-test-strategy-design.md` を参照**。ここは「使い方」に絞る。

- テスト数の「正」は `tools/pre-merge-check.ps1` の実行結果(文書に数値を書かない=CLAUDE.md §5)・0 警告を維持
- ゲート: ローカルは `tools/pre-merge-check.ps1`(フィルタなし・全数)・CI は `--filter "Category!=LocalOnly"`

---

## §1 5 層ピラミッドと責務

| 層 | プロジェクト | 責務 | 実行 |
|---|---|---|---|
| L1 単体 | `tests/yEdit.Core.Tests` | 純ロジック(Buffer/Layout/Search/Csv/Text/Settings/Backup/Editing/IO)+ yEdit.Accessibility の UIA プロバイダ契約 | 毎マージ(自動) |
| L2 コンポーネント | `tests/yEdit.Editor.Tests` | EditorControl 実生成(STA)での編集・キャレット・選択・UIA イベント・IME・クリップボード | 毎マージ(自動) |
| L3 App 統合 | `tests/yEdit.App.Tests` | Controller 群・DocumentManager・Speech 経路選択のロジック(Form 境界・OS 境界を Fake 化) | 毎マージ(自動) |
| L4 性能ゲート | `tests/yEdit.Core.Bench` / `tests/yEdit.Editor.Smoke --bench --bench-save` | 1GB 級文書・タイピング応答 5µs 等の EXIT コード判定 | 手動(Buffer/Layout/IO 大変更時) |
| L5 実機 SR | 手動チェックリスト + `tools/verify-uia-editor.ps1` ほか | NVDA / ナレーターでの実発声確認・a11y 最終ゲート | リリース前・a11y 変更時 |

### 「どこに書くか」の判断基準

1. **下層優先**: 表現できる限り L1 に置く。L1 で書けない結合部分だけ上層へ持ち上げる。
2. **Editor 統合 は L2**: EditorControl の実生成(キャレット・選択・UIA)は L2 で完結。L3 で `new EditorControl()` は許容だが、EditorControl 単体の挙動再検証は L2 に集約。
3. **Form / OS 境界の抽象化を含むテストは L3**: FakePrompt / FakeFileDialogService / FakeAnnouncer / TimeProvider などで Form・ファイルダイアログ・時計・SR 稼働判定・実クリップボード を偽装できるロジックは L3 に置く。実 WinForms ダイアログや実 SR 発声は L5 手動へ。
4. **実発声確認は L5 手動**: 自動化しない(UIA 呼び出しパターンの Phase 3 半自動化はあくまで前段フィルタ)。
5. **Accessibility の契約テスト**: yEdit.Accessibility 専用テストプロジェクトは作らない=プロバイダ契約は Core.Tests・UIA 統合は Editor.Tests に置く。

---

## §2 コピペ元の正典(現行完成形)

### 新規 Controller テスト = `GrepControllerTests`

L3 の新テストを書くときはまず `tests/yEdit.App.Tests/GrepControllerTests.cs` を読み、以下の三本柱を写す:

1. **Host フィクスチャ**(冒頭 `private sealed class Host : IDisposable`)= Form + DocumentManager + Fake 群 + 被テスト Controller をまとめた IDisposable。各テストは `using var host = new Host();` で開始する。
2. **`HostForm.CreateWithDocs()` 呼び出し**(`tests/yEdit.App.Tests/TestHost.cs`)= 画面外・非アクティブ・タスクバー非表示で「可視状態」まで作る共通ヘルパ。TabControl の Selected/Deselecting 系は可視で同期発火するため、これを Host 内から必ず経由する。
3. **`FakeGrepSearchFn`(TCS 駆動)+ `Sta.Run`** = 非同期 `RunAsync` を決定的にテストするパターン(TCS 規律は `Sta.cs` の remarks を必ず読む=`RunContinuationsAsynchronously` 禁止・`searchFn` 内に `Task.Run` を挟まない・でないと WinForms SC に Post された継続が誰にも拾われずハングする)。

Host フィクスチャの型は 6 以上の Controller テストで共通:

```csharp
private sealed class Host : IDisposable
{
    public Form Form { get; }
    public DocumentManager Docs { get; }
    public FakeXxx Xxx { get; } = new();
    public XxxController Ctl { get; }
    public Host()
    {
        var (form, docs) = HostForm.CreateWithDocs();
        Form = form;
        Docs = docs;
        Ctl = new XxxController(docs: Docs, owner: Form, xxx: Xxx, ...);
    }
    public void Dispose() => Form.Dispose();
}
```

### Fake 群のパターン = 「応答事前登録 + 順序保持の記録」

すべての Fake が同型:

- `tests/yEdit.App.Tests/Fakes/FakeAnnouncer.cs` = `List<string> Said`
- `tests/yEdit.App.Tests/Fakes/FakePrompt.cs` = `List<(string Kind, string Text, string Caption)> Log` + `OkCancelResult` 事前登録
- `tests/yEdit.App.Tests/Fakes/FakeFileDialogService.cs` / `FakeBackupWriter.cs` / `FakeRestorePrompt.cs` / `FakeTimeProvider.cs` / `FakeCellPicker.cs` / `FakeGrepView.cs` / `FakeGrepResultsView.cs` / `FakeFindReplaceView.cs` / `FakeGrepSearchFn.cs` = すべて同じ「戻り値/応答は事前登録・呼ばれた引数を List/Queue で順序保持」

新規に Fake が要るときはこれらのどれかをコピペ改変する(独自パターンを作らない)。

### `Sta.cs` の STA 化ヘルパ

- **App.Tests**: `tests/yEdit.App.Tests/Sta.cs`(TCS 規律込みの remarks を必ず読む)
- **Editor.Tests**: `tests/yEdit.Editor.Tests/Sta.cs`(同型)

使い方は `[Fact] public void X() => Sta.Run(() => { /* 本体 */ });`。async テストで TCS を使う場合の規律は `Sta.cs` remarks の TCS 節が正典。

### MainForm 直生成のコンポジションルート・スモーク

`tests/yEdit.App.Tests/MainFormSmokeTests.cs` を参照。ポイントは 2 点:

- **internal ctor の 4 引数 seam**: MainForm は `public MainForm(AppSettings)` →
  `internal MainForm(AppSettings, string settingsPath, string? backupDirectory = null, string? sessionLayoutPath = null)`
  にチェーン。テストは **必ず 4 引数すべてを TempDir 配下で指定**し、さらに
  `SetLastSessionBuffersPathForTest` も併用する(`ShowMainForm` ヘルパが一括で行う)。
- **`BackupEnabled = false` だけでは隔離にならない**(hot exit 統合以降): close 時の
  `Shutdown(keepForRestore:false)` は writer 未生成でも `SessionLayoutStore.Delete` を既定パスへ
  直接実行するため、`sessionLayoutPath` を渡さないテストは開発機の実
  `%APPDATA%\yEdit\session-state.json` を削除してしまう。MainForm を直生成するテストは
  必ず `ShowMainForm` ヘルパ経由で構築すること。

### ミューテーション検証(執筆時セルフチェック・必須)

新しいテスト 1 件ごとに、書いた本人が実施する:

1. 実装の 1 行を一時変異(例: `d.MatchCase, d.WholeWord` を swap / `>= 0` を `> 0` / 定数ずらし)
2. 対象テストが**赤化**するのを目視
3. 実装を復元し、`git status` で src がクリーンなことを確認
4. 該当テストが再度緑になることを目視

**Stage 3-8 の全 Task で標準として実施**した実績のある規律。生存変異(赤化しなかった変異)は commit / PR description に記録する。レビュー工程では最終ブランチレビューのコード品質パスが高価値テストに絞ったスポットチェックで再実施する(CLAUDE.md §3 工程 5・§4)。

---

## §3 レビュー標準チェックリスト(Stage 3-8 の学びの集約)

新規テスト提出前に自己レビューし、レビュアも同じ 6 点で見る。

1. **ミューテーション検証を実施したか**: 実装 1 行の一時変異で対象テストが赤化 → 復元 → 緑再確認 → src クリーン確認まで。省略不可。
2. **非既定位置から検証開始しているか**: 既定値と同値な空振り assert を避ける。例: `GoToCell` を `(0,0)` から呼んで `(0,0)` を assert すると、実装が no-op でも通ってしまう。**(2,2) など非既定位置から検証**する(Stage 6 追加標準)。
3. **partial-selection テストは prefix / suffix を除外する fixture か**: 選択範囲外の prefix / suffix が変更されていないことを assert する。「選択範囲だけが変わった」の証明には全体一致 assert では足りない(Stage 8 Task B 追加標準)。
4. **Cancel / Guard 系テストは assertion 前提と guard 発火条件が一致しているか**: 「Cancel の効果を assert」と書いた時、その guard が実際に発火する状況を fixture が満たしているか。`Cancel()` が `_cts?.Cancel()` しか呼ばないなら overtake guard は発火しない=真の未被覆分岐は別条件(Stage 8 Task D-1 pivot の教訓)。テスト名が `Cancel_...` なら本当に Cancel 経路の効果を検証すること。
5. **カウンタ assert は `>=` 許容か**: `TabControl.Deselecting` のような「二重発火があり得る」発火点は素朴な `== 1` ではなく `>= 1`。必要ならベースライン差分方式(例: `fired >= deselecting + 1`・Task 1-5 由来)。
6. **文言 assert は Core 定数参照か**: 例 `CsvAnnounceFormatter.Cell(...)` / `CsvAnnounceFormatter.LeftEdge` / `CsvAnnounceFormatter.ModeOn` 等の `src/yEdit.Core/Csv/CsvAnnounceFormatter.cs` に定義済み定数を参照する。**リテラル手書き禁止**=文言の正は Core に一元化。

補助:

- テスト名は実挙動と一致(名前と assertion が乖離していないか)。
- Theory の入力表は「swap / off-by-one / 引数取り違え」で赤化するように設計(件数 assert だけで済ませない=対称な変異を検出できないため)。

---

## §4 LocalOnly 方針

### 何を LocalOnly にするか

- **プロセス横断の実グローバル資源に触るテスト**(実クリップボード・実 UIA フォーカスイベント・実描画依存)は CI ホステッドランナーの他プロセスと衝突する可能性が高い=候補。
- 判断は **CI 実測後の実績ベース**。予防的な広い付与はしない。

### 付与方法(素 Trait)

**カスタム属性の xUnit v2 プラミングは YAGNI**。素の Trait を正とする:

```csharp
[Trait("Category", "LocalOnly")]
public class ClipboardTests { ... }
```

### 現在の付与

- `tests/yEdit.Editor.Tests/ClipboardTests.cs`(クラスレベル・2026-07-15 ブランチ 4 で付与)

### 追加候補(CI 初回 push 実測後に判断)

- `UiaFocusEventTests` / `UiaHostTests`(実フォーカス依存)
- `BoundingRects` / `OffsetFromPoint`(実描画依存)
- ほか、CI 初回 push で赤化・不安定化したテスト

初回 push 前に予防的付与しない。CI で実際に不安定化したものだけを追加する。

### ゲートの二相運用

- **`tools/pre-merge-check.ps1`(ローカル・main マージ前必須)**: Category フィルタ**なし**。全数実行し、ローカルでの網羅を保つ。
- **`.github/workflows/ci.yml` / `release.yml`(CI)**: `dotnet test yEdit.sln -c Release --no-build --filter "Category!=LocalOnly"` で隔離。

LocalOnly 隔離テストの回帰は**ローカルゲート + L5 実機 SR**で拾う設計。ゲート 3 ファイルは互いに相互参照コメントで同期する(テストプロジェクト追加時は 3 箇所同期)。
