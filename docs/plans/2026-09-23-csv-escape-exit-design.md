# CSVモード: Esc でモードを終了する（設計書）

策定日: 2026-09-23

## 背景・目的

CSV モードの終了手段は現在「モードメニュー > CSVモード」の再トグルのみで、キーボード
単独では抜けられない。CSV モード中は素キーがグリッドナビに横取りされる（`CsvCommands.ByKey`）
ため、「今いるモードから戻る」動線が無いのは SR ユーザーにとって特に負荷が高い。

`Esc` は一般に「取消 / 一段戻る」のキーであり、CSV モード中は `CsvCommands.ByKey` に
エントリが無く、エディタ側（`InputRouter`）にもハンドラが無いため**現在は無反応**である。
ここへ「CSV モード終了」を割り当てる。

## 決定事項

### キー割り当て（追加）

| キー | 呼び出すコマンド | 機能 |
|---|---|---|
| `Esc` | `CsvController.ExitMode()` | CSV モードを終了して通常編集へ戻る |

`CsvCommands.ByKey` に 1 エントリ追加（21 → 22 エントリ）。

### 誤操作対策は行わない（ブレストで合意）

Esc は押した時点で即終了する。確認ダイアログ・2 回押し・設定での ON/OFF は導入しない。

- 終了時は既存の「CSVモード終了」を発声するので、意図せず外れたことは SR ユーザーにも伝わる。
- 誤って抜けてもモードメニューの再トグルで戻せる（データは失われない）。
- 設定項目化は永続化・設定タブ UI・テストが増え、単一ファイル規模の変更に収まらない。
  必要になった時点で追加する方が安い（YAGNI）。

### 新規ロジックなし

終了処理は既存の `CsvController.ExitMode(Document)`（private）をそのまま使う。読取専用解除・
ハイライト解除・最終セルへのキャレット復帰・`RaiseUiaSelectionEvents` 復帰・パースキャッシュ
解放・`ModeOff` 発声は**すべて現状のまま**で、挙動は既存のモード再トグルと同一である。

### 公開 API は `ExitMode()` を新設する（`ToggleMode()` を流用しない）

`ByKey` 表は CSV モード中しか引かれないので `ToggleMode()` でも動作は同じだが、
「Esc は終了専用」という意図をコードに残すため、引数なしの公開オーバーロードを新設する。

```csharp
/// <summary>CSVモードを終了する（Esc）。モード外・F2 編集中は何もしない（冪等）。</summary>
public void ExitMode()
{
    var doc = _docs.Active;
    if (doc is null || !doc.State.CsvMode || _editor.IsEditing)
        return;
    ExitMode(doc);
}
```

## 既存挙動との関係（いずれも変更しない）

- **F2 編集中の Esc は「編集取消」のまま**。`MainForm.ProcessCmdKey` の `!_csv.IsEditing`
  ガードにより Esc は横取りされず、`CsvCellEditor` の Esc 処理へ届く。モードは維持される。
  公開 `ExitMode()` 側にも `IsEditing` ガードを置き、メニュー等の別経路が生えても
  F2 編集中にモードが落ちないようにする（二重防御）。
- **パース不能状態でも Esc で抜けられる**。`ExitMode` は `TryContext` を通らないため、
  CSV として壊れた本文を掴んだままモードに閉じ込められない（脱出ハッチ）。
- フォーカスがタブ列にあるときは従来どおり横取りしない（`activeDoc.Editor.ContainsFocus` ガード）。
- メニューがアクティブな間（Alt 等）も横取りしない（`_menuActive` ガード）。

## 実装

1. `src/kxEdit.App/CsvController.cs` — 引数なし公開 `ExitMode()` を追加。
2. `src/kxEdit.App/CsvCommands.cs` — `ByKey` に `{ Keys.Escape, c => c.ExitMode() }` を追加。
   `Add` 形式の初期化子なのでキー重複は静的初期化で例外になる（重複の黙殺防止）。
3. `tests/kxEdit.App.Tests/CsvControllerTests.cs`
   - `ByKey_HasExactly21Entries` を 22 エントリに更新（内訳コメントも更新）。
   - `ByKey_MapsAllEntriesToExpectedCommands` の switch に `Keys.Escape` の case を追加
     （`CsvMode == false` / `ReadOnly == false` / `ModeOff` 発声を検証）。Theory は
     `ByKey.Keys` 列挙 + default throw なのでエントリ追加漏れは機械的に落ちる。
   - `ExitMode()` 直呼びの単体テスト: モード中→終了 / モード外 no-op / F2 編集中 no-op。

CLAUDE.md §3 の簡略化基準（数十行・少数ファイル＋テスト）に乗せ、実装 1 タスク・
単一 commit・最終レビューは別エージェント 1 回に統合する。

## テスト戦略

- L1〜L3 の自動テストで足りる（新規ロジックが無く、検証対象はキー→コマンドの対応と
  終了ガードの成立）。
- ミューテーション検証は**行わない**。キーバインドのイベントマッピングは
  CLAUDE.md §4-A の禁止領域。
- **L5（実機 SR 検証）は必要**と判断する。読み上げ経路自体は不変だが、CSV モードは
  SR ユーザーの主要動線であり、Esc が実際に `ProcessCmdKey` で横取りされ発声まで届くかは
  実機でしか確認できない（判定に迷ったら必要に倒す方針）。確認項目:
  - CSV モードで `Esc` を押し、「CSVモード終了」が読み上げられ通常編集へ戻ること。
  - 終了後のキャレット復帰で現在行が二重に読まれないこと（`ExitMode` のコメントにある
    SCN_UPDATEUI 遅延配送の既知の留保を実機で確認する）。
  - `F2` 編集中の `Esc` は編集取消のみで、CSV モードが維持されること。
  - `Esc` 後に通常編集（文字入力・矢印移動）が普通にできること。

## 申し送り

- `説明書/kxEdit説明書.md` 8.1 のキー表への追記が必要。**ユーザー編集版が正**
  （CLAUDE.md §8）なので本ブランチでは書き換えず、差分案の提示のみ行う。案:

  ```
  | Esc | CSVモードを終了する |
  ```

- CSV モードのキー一覧を持つアプリ内ヘルプは未実装（`MainForm` のコメントに
  「キー一覧は将来のヘルプに記載する」とある）。実装時に本件の `Esc` も載せる。
