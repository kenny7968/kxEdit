# UIA のスレッド境界・申し送りの回収 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `TryRunOnUi` の約束(UI スレッドで実行できないときは body を走らせず、縮退値を返す)の穴を 2 つ塞ぐ。1 つは UI スレッドの終了(項目 1)、もう 1 つは自分の Handle だけが破棄された窓(項目 3)である。

**Architecture:** `UiaTextHostAdapter.TryRunOnUi` の Invoke の経路だけを変える。body を到達時のガードで包み、`InvalidAsynchronousStateException` を catch する。テスト用に、ガードの直後に呼ぶフック `TestHook_AfterUiBoundCheck` を置く。

**Tech Stack:** C# / .NET WinForms / xUnit(Editor.Tests)

**Spec:** `docs/plans/2026-10-01-uia-thread-guard-followups-design.md`

## Global Constraints

- 0 warning(`-warnaserror`)。CSharpier 整形(pre-commit フック)を `--no-verify` で飛ばさない。
- テストで `Thread.Sleep` を使わない(Sonar S2925)。待つときは `SpinWait.SpinUntil` を使う。
- 通常時の応答は変えない。意図的な挙動差は設計書 §3.1 の 2 つだけ。
- 変異検証は行わない。陰性対照(修正前に FAIL すること)で代える。
- 簡略化の基準に沿い、1 タスク・1 commit にする。

## ファイル構成

| ファイル | 変更 |
|---|---|
| `src/kxEdit.Editor/UiaTextHostAdapter.cs` | `TryRunOnUi` の Invoke 経路・remarks・テストフック `TestHook_AfterUiBoundCheck` |
| `src/kxEdit.Editor/EditorControl.Uia.cs` | テストフックの転送 `TestHook_UiaAfterUiBoundCheck` |
| `tests/kxEdit.Editor.Tests/UiaThreadGuardTests.cs` | 2 件を追加 |
| `docs/plans/2026-10-01-uia-thread-guard-followups-design.md` | §6 申し送り・実施記録の追記(最後) |

---

### Task 1: TryRunOnUi の穴を塞ぐ

- [ ] **Step 1: テストフックを足す**(修正の前。テストがコンパイルできるように)

`UiaTextHostAdapter.cs` のテストフック節:

```csharp
    /// <summary>
    /// テスト専用: <see cref="TryRunOnUi{T}"/> が Handle のガード(<see cref="IsUiBound"/>)を通った直後、
    /// InvokeRequired を見る前に呼ぶ。ガードの後で自分の Handle だけが破棄される窓を、実際の順序のまま
    /// テストで再現するため。製品コードからは設定しない(既定 null)。
    /// </summary>
    internal Action? TestHook_AfterUiBoundCheck { get; set; }
```

`TryRunOnUi` の `if (!IsUiBound) return fallback;` の直後に `TestHook_AfterUiBoundCheck?.Invoke();` を置く。

`EditorControl.Uia.cs` に転送を足す。setter を持つので、WFO1000 を避けるために `[Browsable(false)]` と `[DesignerSerializationVisibility(Hidden)]` を付ける(`TestHook_UiaAssumeHandleCreated` と同じ形)。

- [ ] **Step 2: テストを 2 件書く**(`UiaThreadGuardTests.cs`)

- `SyncQuery_OwnHandleDestroyedAfterGuardWithParentAlive_FallsBackOnUiThread`(項目 3)
  - 親の Form と EditorControl の両方に Handle を作り、折り返し 2 にする。
  - 陽性対照: UI スレッドで `LineStartOf(15)` を呼び、11 より大きい値(視覚行の先頭)が返ることを確かめる。
  - `WrapColumns` を 3 → 2 にして、行キャッシュを捨てる。
  - フックで worker だけを止める。止めている間に、UI スレッドで EditorControl の Handle だけを `DestroyHandle`(リフレクション)で破棄する。前提として、親が残っていて `InvokeRequired=true` になることを確かめる。
  - UI スレッドで汲みながら待つ。結果が 11(縮退値)であること、行キャッシュが空のままであること、Handle が作り直されていないことを確かめる。
- `SyncQuery_UiThreadExitsWhileInvokePending_FallsBackWithoutThrowing`(項目 1)
  - `Sta.Run` のスレッドを UI スレッドにする。worker(`Thread`)で `LineStartOf(15)` を呼ぶ。
  - `TestHook_LineSegsInvokeCount == 1` になり、さらに worker が `WaitSleepJoin` になるまで待つ。そのあと、汲まずに `Sta.Run` を抜ける(UI スレッドの終了)。
  - worker が例外を出さずに 11 を返すことを確かめる。

- [ ] **Step 3: 修正前に FAIL することを確かめる(陰性対照)**

```powershell
dotnet test tests/kxEdit.Editor.Tests --filter "FullyQualifiedName~UiaThreadGuardTests"
```

期待: 新しい 2 件が FAIL する。項目 1 は `InvalidAsynchronousStateException`、項目 3 は `Expected: 11 / Actual: 15` で落ちる。既存の 5 件は合格する。

- [ ] **Step 4: 修正する**

```csharp
                // 届いた時点で、UI スレッドでもう一度ガードを見る(TryPostToUi と対称)。
                // 自分の Handle が無く親の Handle があると、InvokeRequired は親で判定されて true になり、
                // Handle の無い状態の body がここへ届くため。
                return _host.Invoke(() => IsUiBound ? body() : fallback);
            }
            // ... 既存の 2 つの catch ...
            catch (System.ComponentModel.InvalidAsynchronousStateException)
            {
                return fallback;
            } // Invoke を待つ間に UI スレッドが終了した(ArgumentException の派生なので別に捕まえる)
```

`TryRunOnUi` の remarks の 2 番目の項目を、到達時の再判定と UI スレッドの終了を含む形に直す。

- [ ] **Step 5: 合格を確かめ、フレークも確かめる**

同じコマンドで 7 件すべてが合格すること。スレッドのタイミングに依存するので、`--no-build` で 15 回繰り返し、失敗が 0 であることを確かめる。

- [ ] **Step 6: commit**

`fix(uia): 同期の問い合わせで UI スレッドの終了と自分の Handle だけの破棄を縮退値にする`

---

### 仕上げ

- 最終レビュー: 別エージェント 1 回(コード品質+脆弱性。簡略化の基準)。
- `tools/pre-merge-check.ps1` が EXIT 0。
- L5: `tools/sr-regression.ps1` が EXIT 0。NVDA の実機で、行の移動(折り返し ON / OFF)とタブを閉じる操作を確かめる(ユーザーに依頼)。
- 設計書の §6 に実施記録と申し送りを追記する。
