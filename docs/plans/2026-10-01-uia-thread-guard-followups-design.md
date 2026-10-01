# UIA のスレッド境界・申し送りの回収 設計書

**日付**: 2026-10-01
**ブランチ**: `feature/uia-thread-guard-followups`
**元**: `docs/plans/2026-09-27-perf-followups-design.md` §6.4 の申し送り(PR #102)

## 1. 目的

PR #102(perf-followups フェーズ 2)の申し送り 3 件について、対応の要否を判断する。必要なものだけを回収する。

対象のコードは `UiaTextHostAdapter.TryRunOnUi` / `TryPostToUi` だけである。Editor・Accessibility のプロジェクトで `Control.Invoke` / `BeginInvoke` を呼ぶのはこの 2 か所しかない。

## 2. 判断

| # | 申し送り | 判断 |
|---|---|---|
| 1 | 同期 Invoke を待つ間に UI スレッドが終了すると、`InvalidAsynchronousStateException` が catch されない | **対応する** |
| 2 | 書き込み系の投函に上限がない | **対応しない(閉じる)** |
| 3 | 自分の Handle が無く、親の Handle がある状態では、Invoke した body が UI スレッドで `IsUiBound` を判定し直さない | **対応する** |

### 2.1 項目 1: 対応する

- `TryRunOnUi` は「UI スレッドで実行できないときは body を走らせず、縮退値を返す」と約束している。UI スレッドの終了は、まさにその場面である。
- ところが、この例外は `ArgumentException` の派生なので、今の catch(`ObjectDisposedException` / `InvalidOperationException`)から漏れる。
- 実害は小さい(UIA の境界で HRESULT に変わるだけで、プロセスは落ちない)。それでも、catch を 1 つ足すだけで約束の穴を塞げる。
- WinForms の `Control.Invoke` は、待っている間に相手のスレッドが終わったかを約 1 秒ごとに確かめる(`WaitForWaitHandle` の `GetExitCodeThread`)。終わっていれば、呼び出し側のスレッドでこの例外を投げる。

### 2.2 項目 2: 閉じる

- 投函を積めるのは、UIA で同じデスクトップの同じ整合性レベルにいるプロセスだけである。信頼境界は越えない。そのようなプロセスは、投函を積むよりも直接的な手段(入力の合成など)を持っている。
- 実際の UIA クライアント(SR)は、人の操作の速さでしか Select / ScrollIntoView を呼ばない。
- 上限や合流を入れると、`TryPostToUi` の remarks にある前提が崩れる。前提とは、「invoke キューは FIFO なので、投函した書き込みは、SR が直後に呼ぶ同期の読み取りより先に走る」ことである。
- 費用に見合う利益がないので、閉じる。

### 2.3 項目 3: 対応する

- 今は無害である。ただしそれは、各 body がたまたま無害だからにすぎない。
  - `ComputeBoundingRectangles` / `ComputeOffsetFromScreenPoint` は `_hwnd==0` で先に抜ける。
  - `GetVisibleCharRange` は、Handle が無くても古い ClientSize で計算して値を返す。
  - 今後 body が `Control.Handle` に触れると、teardown 中に Handle を作り直す。
- `TryPostToUi` は、到達時にもう一度判定する。`TryRunOnUi` も同じにすれば、各 body の性質に頼らずに済む。

## 3. 変更

`src/kxEdit.Editor/UiaTextHostAdapter.cs` の `TryRunOnUi` だけを変える。

- **項目 3**: Invoke する body を `() => IsUiBound ? body() : fallback` で包む(`TryPostToUi` と対称)。
- **項目 1**: `catch (InvalidAsynchronousStateException)` を足して、縮退値を返す。
- **テスト用の seam**: `IsUiBound` の判定を通った直後に呼ぶテストフック `TestHook_AfterUiBoundCheck`(`Action?`。既定は null)を置く。
  - 項目 3 の窓(判定と `InvokeRequired` の間で、自分の Handle だけが破棄される)を、実際の順序のまま再現するためである。
  - 既存の `TestHook_AssumeHandleCreated` では区別できない。フックは UI スレッドでの判定し直しも通してしまうので、修正の有無でテストの結果が変わらない。

### 3.1 意図的な挙動差

通常時(Handle があり、UI スレッドが応答する)の応答は変わらない。競合時に縮退値を返す場面が、次の 2 つ増える。

- UIA の同期の問い合わせを待つ間に UI スレッドが終了した(以前は例外が UIA へ伝わった)。
- 問い合わせの判定の後、Invoke が届くまでの間に、自分の Handle だけが破棄された(以前は Handle の無い状態で body を計算した)。

## 4. テスト(L2・`UiaThreadGuardTests` に追加)

- **項目 3**
  - 親の Form と EditorControl の両方に Handle を作り、折り返し ON にする。
  - worker で `LineStartOf`(先頭ではない視覚行の位置)を呼ぶ。フックの中で worker を止め、そのあいだに UI スレッドで EditorControl の Handle だけを破棄する(親は残す)。すると、worker から見た `InvokeRequired` は親の Handle で判定されて true になる。
  - UI スレッドで汲みながら worker を待ち、縮退値(論理行の先頭)が返ることを確かめる。
  - 陰性対照: 修正前は、視覚行の先頭が返る(計算が走る)。
- **項目 1**
  - 専用の STA スレッドで Form と EditorControl を作り、Handle を作る。
  - worker で `LineStartOf` を呼んで Invoke で待たせ、STA スレッドを、メッセージを汲まずに終わらせる。
  - worker が例外を出さずに、縮退値を返すことを確かめる。
  - 陰性対照: 修正前は `InvalidAsynchronousStateException` が出る。
- 変異検証は行わない(perf-followups 設計書 §3.3 と同じ扱い)。陰性対照で代える。

## 5. プロセス

- CLAUDE.md §3 の簡略化の基準(単一ファイル・数十行)に沿う。実装は 1 タスク・1 commit とし、最終レビューの 2 パスは別エージェント 1 回に統合する。
- 品質ゲート: `tools/pre-merge-check.ps1` が EXIT 0。
- L5: UIA の経路に触れるので必須。`tools/sr-regression.ps1` が EXIT 0 で、NVDA で行の移動(折り返し ON / OFF)とタブを閉じる操作を簡易に確認する。

## 6. 実施記録(2026-10-01)

- **成果物**: §3 のとおり。実装計画は `docs/plans/2026-10-01-uia-thread-guard-followups.md`。
- **テスト**: `UiaThreadGuardTests` に 2 件を足した。
  - 陰性対照: 修正前に、項目 1 は `InvalidAsynchronousStateException`、項目 3 は `Expected: 11 / Actual: 15` で FAIL した。
  - スレッドのタイミングに依存するので、`--no-build` で 15 回繰り返した。失敗は 0 だった。
- **品質ゲート**: `tools/pre-merge-check.ps1` が EXIT 0。
- **L5**: `tools/sr-regression.ps1` が EXIT 0。NVDA の実機確認は、ユーザーの判断で省略した。
  - 変わるのは、Handle の破棄や UI スレッドの終了と UIA の問い合わせが競合したときの応答だけである。通常時の応答は変わらない。
- **レビュー**: 簡略化の基準に沿い、最終レビューの 2 パス(コード品質 / 脆弱性)を別エージェント 1 回に統合した。Critical・Important はなかった。
  - ① fixup で修正: M-3。項目 3 のテストで、前提の assert が落ちても `gate` を必ず開ける(try/finally)。
  - ② 受容: M-1。項目 1 のテストの前提(worker が `WaitSleepJoin` になった)は、まれに Invoke の投函より前に成り立ちうる。その場合、修正前のコードでも緑になりうる(偽の赤やフレークにはならない)。修正前に FAIL することは確かめた。
  - ② 申し送り: M-2(下記)。
  - M-4(本節が未記入)は、本節の追記で対応した。

## 7. 申し送り

- Invoke を呼ぶ前に UI スレッドが既に終了している場合は、本書の対象外のまま残る。
  - レビューの M-2 による。WinForms の内部挙動からの推定で、**未確認**である。
  - 推定では、例外が出ずに `null` が返る。T が値型の経路(`OffsetFromScreenPoint` / `GetVisibleRange`)では、`NullReferenceException` になりうる。
  - 起きるのはプロセス終了の間際だけである。UIA の境界で HRESULT に変わるので、プロセスは落ちない。
